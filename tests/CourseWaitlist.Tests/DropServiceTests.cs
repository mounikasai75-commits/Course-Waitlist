using CourseWaitlist.Api.Data;
using CourseWaitlist.Api.Domain;
using CourseWaitlist.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CourseWaitlist.Tests;

public sealed class DropServiceTests : IDisposable
{
    private const string OfferingId = "offering-1";

    private readonly TestDatabase _database = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose()
    {
        _database.Dispose();
        _gate.Dispose();
    }

    private void Seed(int capacity, params string[] learnerIds)
    {
        using var context = _database.CreateContext();
        context.Offerings.Add(new CourseOffering { Id = OfferingId, Capacity = capacity });
        context.Learners.AddRange(learnerIds.Select(id => new Learner { Id = id, PrerequisiteSatisfied = true }));
        context.SaveChanges();
    }

    private void AddRegistration(string learnerId, RegistrationStatus status, long? sequence = null)
    {
        using var context = _database.CreateContext();
        context.Registrations.Add(new Registration
        {
            OfferingId = OfferingId,
            LearnerId = learnerId,
            Status = status,
            WaitlistSequence = sequence
        });
        if (sequence is not null)
        {
            var offering = context.Offerings.Single();
            offering.NextWaitlistSequence = Math.Max(offering.NextWaitlistSequence, sequence.Value + 1);
        }

        context.SaveChanges();
    }

    private void SetEligible(string learnerId, bool eligible)
    {
        using var context = _database.CreateContext();
        context.Learners.Single(l => l.Id == learnerId).PrerequisiteSatisfied = eligible;
        context.SaveChanges();
    }

    // A fresh context and service per call mirrors a request scope; only the connection and gate are shared.
    private async Task<DropResult> DropAsync(string learnerId, string offeringId = OfferingId)
    {
        using var context = _database.CreateContext();
        return await new RegistrationService(context, _gate).DropAsync(offeringId, learnerId);
    }

    private async Task<EnrollmentResult> EnrollAsync(string learnerId)
    {
        using var context = _database.CreateContext();
        return await new RegistrationService(context, _gate).EnrollAsync(OfferingId, learnerId);
    }

    private Registration Get(string learnerId)
    {
        using var context = _database.CreateContext();
        return context.Registrations.AsNoTracking().Single(r => r.LearnerId == learnerId);
    }

    private List<(string Learner, RegistrationStatus Status, long? Sequence)> Snapshot()
    {
        using var context = _database.CreateContext();
        return context.Registrations.AsNoTracking()
            .OrderBy(r => r.LearnerId)
            .AsEnumerable()
            .Select(r => (r.LearnerId, r.Status, r.WaitlistSequence))
            .ToList();
    }

    private List<string> Queue()
    {
        using var context = _database.CreateContext();
        return context.Registrations.AsNoTracking()
            .Where(r => r.Status == RegistrationStatus.Waitlisted)
            .OrderBy(r => r.WaitlistSequence)
            .Select(r => r.LearnerId)
            .ToList();
    }

    private void AssertCapacityNotExceeded()
    {
        using var context = _database.CreateContext();
        var capacity = context.Offerings.Single().Capacity;
        Assert.True(context.Registrations.Count(r => r.Status == RegistrationStatus.Enrolled) <= capacity);
    }

    [Fact]
    public async Task Dropping_an_enrolled_learner_promotes_the_earliest_waiter()
    {
        Seed(1, "ada", "grace", "linus");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 2);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);

        var result = await DropAsync("ada");

        Assert.Equal(DropOutcome.DroppedAndPromoted, result.Outcome);
        Assert.Equal("grace", result.PromotedLearnerId);
        Assert.True(result.Succeeded);
        Assert.Equal(RegistrationStatus.Dropped, Get("ada").Status);
        var grace = Get("grace");
        Assert.Equal(RegistrationStatus.Enrolled, grace.Status);
        Assert.Null(grace.WaitlistSequence);
        Assert.Equal(["linus"], Queue());
        AssertCapacityNotExceeded();
    }

    [Fact]
    public async Task Repeated_drops_promote_in_strict_fifo_order()
    {
        Seed(1, "ada", "grace", "linus", "tim");
        await EnrollAsync("ada");
        await EnrollAsync("grace");
        await EnrollAsync("linus");
        await EnrollAsync("tim");

        Assert.Equal("grace", (await DropAsync("ada")).PromotedLearnerId);
        Assert.Equal("linus", (await DropAsync("grace")).PromotedLearnerId);
        Assert.Equal("tim", (await DropAsync("linus")).PromotedLearnerId);

        Assert.Equal(RegistrationStatus.Enrolled, Get("tim").Status);
        Assert.Empty(Queue());
        AssertCapacityNotExceeded();
    }

    [Fact]
    public async Task Promotion_fills_only_one_seat_for_one_drop()
    {
        Seed(2, "ada", "grace", "linus", "tim");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Enrolled);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 1);
        AddRegistration("tim", RegistrationStatus.Waitlisted, 2);

        await DropAsync("ada");

        Assert.Equal(RegistrationStatus.Enrolled, Get("linus").Status);
        Assert.Equal(RegistrationStatus.Waitlisted, Get("tim").Status);
        Assert.Equal(2, Get("tim").WaitlistSequence);
        AssertCapacityNotExceeded();
    }

    [Fact]
    public async Task Ineligible_waiter_is_skipped_and_retains_its_position()
    {
        Seed(1, "ada", "grace", "linus", "tim");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 2);
        AddRegistration("tim", RegistrationStatus.Waitlisted, 3);
        SetEligible("grace", false);

        var result = await DropAsync("ada");

        Assert.Equal(DropOutcome.DroppedAndPromoted, result.Outcome);
        Assert.Equal("linus", result.PromotedLearnerId);
        var grace = Get("grace");
        Assert.Equal(RegistrationStatus.Waitlisted, grace.Status);
        Assert.Equal(1, grace.WaitlistSequence);
        Assert.Equal(3, Get("tim").WaitlistSequence);
        Assert.Equal(["grace", "tim"], Queue());
    }

    [Fact]
    public async Task Several_ineligible_waiters_are_skipped_in_order()
    {
        Seed(1, "ada", "grace", "linus", "tim");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 2);
        AddRegistration("tim", RegistrationStatus.Waitlisted, 3);
        SetEligible("grace", false);
        SetEligible("linus", false);

        var result = await DropAsync("ada");

        Assert.Equal("tim", result.PromotedLearnerId);
        Assert.Equal(["grace", "linus"], Queue());
        Assert.Equal(1, Get("grace").WaitlistSequence);
        Assert.Equal(2, Get("linus").WaitlistSequence);
    }

    [Fact]
    public async Task Eligibility_is_rechecked_at_promotion_time_not_at_join_time()
    {
        Seed(1, "ada", "grace", "linus");
        await EnrollAsync("ada");
        await EnrollAsync("grace");
        await EnrollAsync("linus");
        SetEligible("grace", false);

        var result = await DropAsync("ada");

        Assert.Equal("linus", result.PromotedLearnerId);
        Assert.Equal(RegistrationStatus.Waitlisted, Get("grace").Status);
    }

    [Fact]
    public async Task Waiter_who_regains_eligibility_keeps_original_priority()
    {
        Seed(1, "ada", "grace", "linus", "tim");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 2);
        AddRegistration("tim", RegistrationStatus.Waitlisted, 3);
        SetEligible("grace", false);

        Assert.Equal("linus", (await DropAsync("ada")).PromotedLearnerId);

        SetEligible("grace", true);
        var next = await DropAsync("linus");

        Assert.Equal("grace", next.PromotedLearnerId);
        Assert.Equal(["tim"], Queue());
    }

    [Fact]
    public async Task Seat_stays_open_when_no_waiter_is_eligible_and_queue_is_unchanged()
    {
        Seed(1, "ada", "grace", "linus");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 2);
        SetEligible("grace", false);
        SetEligible("linus", false);

        var result = await DropAsync("ada");

        Assert.Equal(DropOutcome.DroppedSeatAvailable, result.Outcome);
        Assert.Null(result.PromotedLearnerId);
        Assert.DoesNotContain(Snapshot(), r => r.Status == RegistrationStatus.Enrolled);
        Assert.Equal(["grace", "linus"], Queue());
        Assert.Equal(1, Get("grace").WaitlistSequence);
        Assert.Equal(2, Get("linus").WaitlistSequence);
    }

    [Fact]
    public async Task Dropping_with_an_empty_waitlist_just_frees_the_seat()
    {
        Seed(2, "ada");
        AddRegistration("ada", RegistrationStatus.Enrolled);

        var result = await DropAsync("ada");

        Assert.Equal(DropOutcome.DroppedSeatAvailable, result.Outcome);
        Assert.Equal(RegistrationStatus.Dropped, Get("ada").Status);
        Assert.Null(Get("ada").WaitlistSequence);
    }

    [Fact]
    public async Task Freed_seat_is_not_over_filled_when_capacity_is_still_used_elsewhere()
    {
        Seed(1, "ada", "grace", "linus");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Dropped);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 1);

        await DropAsync("grace");

        Assert.Equal(RegistrationStatus.Waitlisted, Get("linus").Status);
        AssertCapacityNotExceeded();
    }

    [Fact]
    public async Task Waitlisted_learner_dropping_leaves_the_queue_without_changing_capacity()
    {
        Seed(1, "ada", "grace", "linus", "tim");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 2);
        AddRegistration("tim", RegistrationStatus.Waitlisted, 3);

        var result = await DropAsync("linus");

        Assert.Equal(DropOutcome.DroppedFromWaitlist, result.Outcome);
        Assert.Null(result.PromotedLearnerId);
        Assert.True(result.Succeeded);
        Assert.Equal(RegistrationStatus.Enrolled, Get("ada").Status);
        var linus = Get("linus");
        Assert.Equal(RegistrationStatus.Dropped, linus.Status);
        Assert.Null(linus.WaitlistSequence);
        Assert.Equal(["grace", "tim"], Queue());
        Assert.Equal(1, Get("grace").WaitlistSequence);
        Assert.Equal(3, Get("tim").WaitlistSequence);
    }

    [Fact]
    public async Task Waitlisted_learner_dropping_does_not_promote_anyone_even_with_a_free_seat()
    {
        Seed(1, "ada", "grace");
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);

        var result = await DropAsync("grace");

        Assert.Equal(DropOutcome.DroppedFromWaitlist, result.Outcome);
        Assert.DoesNotContain(Snapshot(), r => r.Status == RegistrationStatus.Enrolled);
    }

    [Fact]
    public async Task Repeated_drop_is_a_no_op_and_changes_nothing()
    {
        Seed(1, "ada", "grace", "linus");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 2);

        var first = await DropAsync("ada");
        var after = Snapshot();
        var repeat = await DropAsync("ada");

        Assert.Equal(DropOutcome.DroppedAndPromoted, first.Outcome);
        Assert.Equal(DropOutcome.NotEnrolled, repeat.Outcome);
        Assert.False(repeat.Succeeded);
        Assert.Equal(after, Snapshot());
    }

    [Fact]
    public async Task Repeated_drop_of_a_waitlisted_learner_is_a_no_op()
    {
        Seed(1, "ada", "grace", "linus");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 2);

        await DropAsync("grace");
        var after = Snapshot();
        var repeat = await DropAsync("grace");

        Assert.Equal(DropOutcome.NotEnrolled, repeat.Outcome);
        Assert.Equal(after, Snapshot());
    }

    [Fact]
    public async Task Dropping_a_learner_with_no_registration_is_not_enrolled_and_creates_nothing()
    {
        Seed(1, "ada");

        var result = await DropAsync("ada");

        Assert.Equal(DropOutcome.NotEnrolled, result.Outcome);
        Assert.Empty(Snapshot());
    }

    [Fact]
    public async Task Unknown_learner_is_rejected_and_changes_nothing()
    {
        Seed(1, "ada");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        var before = Snapshot();

        var result = await DropAsync("nobody");

        Assert.Equal(DropOutcome.LearnerNotFound, result.Outcome);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Unknown_offering_is_rejected_and_changes_nothing()
    {
        Seed(1, "ada");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        var before = Snapshot();

        var result = await DropAsync("ada", "other-offering");

        Assert.Equal(DropOutcome.OfferingNotFound, result.Outcome);
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData(null, "ada")]
    [InlineData("", "ada")]
    [InlineData("   ", "ada")]
    [InlineData(OfferingId, null)]
    [InlineData(OfferingId, "")]
    [InlineData(OfferingId, "   ")]
    public async Task Missing_identifiers_are_invalid_requests_and_change_nothing(string? offeringId, string? learnerId)
    {
        Seed(1, "ada");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        var before = Snapshot();

        var result = await DropAsync(learnerId!, offeringId!);

        Assert.Equal(DropOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Dropped_learner_can_enroll_again_and_goes_to_the_back_of_a_full_queue()
    {
        Seed(1, "ada", "grace", "linus");
        await EnrollAsync("ada");
        await EnrollAsync("grace");
        await DropAsync("grace");

        var result = await EnrollAsync("linus");
        var rejoin = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.Waitlisted, result.Outcome);
        Assert.Equal(EnrollmentOutcome.Waitlisted, rejoin.Outcome);
        Assert.True(rejoin.WaitlistSequence > result.WaitlistSequence);
        Assert.Equal(["linus", "grace"], Queue());
    }

    [Fact]
    public async Task Failure_after_the_changes_are_written_rolls_back_both_drop_and_promotion()
    {
        Seed(1, "ada", "grace");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 1);
        var before = Snapshot();

        using (var failing = _database.CreateContext(new FailAfterSave()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new RegistrationService(failing, _gate).DropAsync(OfferingId, "ada"));
        }

        Assert.Equal(before, Snapshot());

        // The gate must have been released, and a retry must then succeed normally.
        var retry = await DropAsync("ada");
        Assert.Equal(DropOutcome.DroppedAndPromoted, retry.Outcome);
        Assert.Equal("grace", retry.PromotedLearnerId);
    }

    [Fact]
    public async Task Concurrent_drops_promote_each_waiter_once_and_never_over_enroll()
    {
        var learners = Enumerable.Range(1, 10).Select(i => $"learner-{i}").ToArray();
        Seed(3, learners);
        foreach (var learner in learners)
        {
            await EnrollAsync(learner);
        }

        var enrolled = Snapshot().Where(r => r.Status == RegistrationStatus.Enrolled).Select(r => r.Learner).ToList();
        Assert.Equal(3, enrolled.Count);

        // Each enrolled learner drops twice at the same time.
        var results = await Task.WhenAll(
            enrolled.Concat(enrolled).Select(id => Task.Run(() => DropAsync(id))));

        Assert.Equal(3, results.Count(r => r.Outcome == DropOutcome.DroppedAndPromoted));
        Assert.Equal(3, results.Count(r => r.Outcome == DropOutcome.NotEnrolled));
        Assert.Equal(
            3,
            results.Where(r => r.PromotedLearnerId is not null).Select(r => r.PromotedLearnerId).Distinct().Count());

        AssertCapacityNotExceeded();
        var snapshot = Snapshot();
        Assert.Equal(10, snapshot.Count);
        Assert.Equal(3, snapshot.Count(r => r.Status == RegistrationStatus.Enrolled));
        Assert.Equal(3, snapshot.Count(r => r.Status == RegistrationStatus.Dropped));
        Assert.Equal(4, Queue().Count);
    }

    // Throws once SaveChanges has written both changes but before the service commits, forcing a rollback.
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated failure before commit.");
    }
}
