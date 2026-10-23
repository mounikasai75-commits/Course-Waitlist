using CourseWaitlist.Api.Domain;
using CourseWaitlist.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CourseWaitlist.Tests;

// FIFO key: the monotonic sequence. The enqueue timestamp is recorded and exposed but never affects order.
public sealed class WaitlistOrderingTests : IDisposable
{
    private const string OfferingId = "offering-1";

    private readonly TestDatabase _database = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SettableClock _clock = new();

    public void Dispose()
    {
        _database.Dispose();
        _gate.Dispose();
    }

    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private void Seed(int capacity, params string[] learnerIds)
    {
        using var context = _database.CreateContext();
        context.Offerings.Add(new CourseOffering { Id = OfferingId, Capacity = capacity });
        context.Learners.AddRange(learnerIds.Select(id => new Learner { Id = id, PrerequisiteSatisfied = true }));
        context.SaveChanges();
    }

    private async Task<EnrollmentResult> EnrollAsync(string learnerId)
    {
        using var context = _database.CreateContext();
        return await new RegistrationService(context, _gate, _clock).EnrollAsync(OfferingId, learnerId);
    }

    private async Task<DropResult> DropAsync(string learnerId)
    {
        using var context = _database.CreateContext();
        return await new RegistrationService(context, _gate, _clock).DropAsync(OfferingId, learnerId);
    }

    private async Task<CourseState> StateAsync()
    {
        using var context = _database.CreateContext();
        return (await new RegistrationService(context, _gate, _clock).GetStateAsync(OfferingId)).State!;
    }

    private Registration Get(string learnerId)
    {
        using var context = _database.CreateContext();
        return context.Registrations.AsNoTracking().Single(r => r.LearnerId == learnerId);
    }

    [Fact]
    public async Task Enqueue_timestamp_is_recorded_and_shown_in_the_state()
    {
        Seed(1, "alice", "bob");
        await EnrollAsync("alice");

        await EnrollAsync("bob");

        Assert.Equal(_clock.Now.UtcTicks, Get("bob").EnqueuedAtTicks);
        var entry = Assert.Single((await StateAsync()).Waitlist);
        Assert.Equal("bob", entry.LearnerId);
        Assert.Equal(_clock.Now, entry.JoinedAtUtc);
        DatabaseInvariants.AssertConsistent(_database);
    }

    [Fact]
    public async Task A_clock_that_moves_backwards_does_not_change_queue_order()
    {
        Seed(1, "alice", "bob", "carol");
        await EnrollAsync("alice");
        await EnrollAsync("bob");

        _clock.Now = _clock.Now.AddHours(-1);
        await EnrollAsync("carol");

        Assert.True(Get("carol").EnqueuedAtTicks < Get("bob").EnqueuedAtTicks);
        Assert.Equal(["bob", "carol"], (await StateAsync()).Waitlist.Select(w => w.LearnerId));

        var drop = await DropAsync("alice");

        Assert.Equal(DropOutcome.DroppedAndPromoted, drop.Outcome);
        Assert.Equal("bob", drop.PromotedLearnerId);
        Assert.Equal(RegistrationStatus.Waitlisted, Get("carol").Status);
        DatabaseInvariants.AssertConsistent(_database);
    }

    [Fact]
    public async Task Waiters_queued_at_the_same_instant_are_ordered_by_sequence()
    {
        Seed(1, "alice", "bob", "carol", "dave");
        await EnrollAsync("alice");
        await EnrollAsync("bob");
        await EnrollAsync("carol");
        await EnrollAsync("dave");

        Assert.Equal(Get("bob").EnqueuedAtTicks, Get("dave").EnqueuedAtTicks);
        Assert.Equal(["bob", "carol", "dave"], (await StateAsync()).Waitlist.Select(w => w.LearnerId));

        Assert.Equal("bob", (await DropAsync("alice")).PromotedLearnerId);
        Assert.Equal("carol", (await DropAsync("bob")).PromotedLearnerId);
        Assert.Equal("dave", (await DropAsync("carol")).PromotedLearnerId);
        DatabaseInvariants.AssertConsistent(_database);
    }

    [Fact]
    public async Task Sequence_not_timestamp_decides_order_when_they_disagree()
    {
        Seed(1, "alice", "bob", "carol");
        await EnrollAsync("alice");
        using (var context = _database.CreateContext())
        {
            context.Registrations.AddRange(
                new Registration
                {
                    OfferingId = OfferingId, LearnerId = "bob", Status = RegistrationStatus.Waitlisted,
                    WaitlistSequence = 1, EnqueuedAtTicks = 2_000
                },
                new Registration
                {
                    OfferingId = OfferingId, LearnerId = "carol", Status = RegistrationStatus.Waitlisted,
                    WaitlistSequence = 2, EnqueuedAtTicks = 1_000
                });
            context.Offerings.Single().NextWaitlistSequence = 3;
            context.SaveChanges();
        }

        Assert.Equal(["bob", "carol"], (await StateAsync()).Waitlist.Select(w => w.LearnerId));

        var drop = await DropAsync("alice");

        Assert.Equal("bob", drop.PromotedLearnerId);
        Assert.Equal(RegistrationStatus.Enrolled, Get("bob").Status);
        Assert.Equal(RegistrationStatus.Waitlisted, Get("carol").Status);
        Assert.Equal(2, Get("carol").WaitlistSequence);
    }

    [Fact]
    public async Task A_learner_who_rejoins_after_dropping_gets_a_fresh_timestamp_and_sequence()
    {
        Seed(1, "alice", "bob", "carol");
        await EnrollAsync("alice");
        await EnrollAsync("bob");
        await DropAsync("bob");
        _clock.Now = _clock.Now.AddMinutes(5);
        await EnrollAsync("carol");
        _clock.Now = _clock.Now.AddMinutes(5);

        var rejoin = await EnrollAsync("bob");

        Assert.Equal(EnrollmentOutcome.Waitlisted, rejoin.Outcome);
        Assert.Equal(_clock.Now.UtcTicks, Get("bob").EnqueuedAtTicks);
        Assert.Equal(["carol", "bob"], (await StateAsync()).Waitlist.Select(w => w.LearnerId));
        DatabaseInvariants.AssertConsistent(_database);
    }
}
