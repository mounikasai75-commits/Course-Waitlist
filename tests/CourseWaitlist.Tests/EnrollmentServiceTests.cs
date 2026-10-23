using CourseWaitlist.Api.Data;
using CourseWaitlist.Api.Domain;
using CourseWaitlist.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CourseWaitlist.Tests;

public sealed class EnrollmentServiceTests : IDisposable
{
    private const string OfferingId = "offering-1";

    private readonly TestDatabase _database = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose()
    {
        _database.Dispose();
        _gate.Dispose();
    }

    private void Seed(int capacity, params (string Id, bool Eligible)[] learners)
    {
        using var context = _database.CreateContext();
        context.Offerings.Add(new CourseOffering { Id = OfferingId, Capacity = capacity });
        context.Learners.AddRange(learners.Select(l => new Learner { Id = l.Id, PrerequisiteSatisfied = l.Eligible }));
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

    // A fresh context and service per call mirrors a request scope; only the connection and gate are shared.
    private async Task<EnrollmentResult> EnrollAsync(string offeringId, string learnerId)
    {
        using var context = _database.CreateContext();
        return await new RegistrationService(context, _gate).EnrollAsync(offeringId, learnerId);
    }

    private Task<EnrollmentResult> EnrollAsync(string learnerId) => EnrollAsync(OfferingId, learnerId);

    private List<Registration> Registrations()
    {
        using var context = _database.CreateContext();
        return context.Registrations.AsNoTracking().OrderBy(r => r.Id).ToList();
    }

    private void AssertCapacityNotExceeded()
    {
        using var context = _database.CreateContext();
        var capacity = context.Offerings.Single().Capacity;
        Assert.True(context.Registrations.Count(r => r.Status == RegistrationStatus.Enrolled) <= capacity);
    }

    [Fact]
    public async Task Eligible_learner_is_enrolled_when_a_seat_is_available()
    {
        Seed(2, ("ada", true));

        var result = await EnrollAsync("ada");

        Assert.Equal(EnrollmentOutcome.Enrolled, result.Outcome);
        Assert.Equal(RegistrationStatus.Enrolled, result.Status);
        Assert.Null(result.WaitlistSequence);
        Assert.True(result.Succeeded);
        var registration = Assert.Single(Registrations());
        Assert.Equal(RegistrationStatus.Enrolled, registration.Status);
        Assert.Null(registration.WaitlistSequence);
    }

    [Fact]
    public async Task Last_seat_is_filled_and_capacity_is_not_exceeded()
    {
        Seed(2, ("ada", true), ("grace", true), ("linus", true));

        Assert.Equal(EnrollmentOutcome.Enrolled, (await EnrollAsync("ada")).Outcome);
        Assert.Equal(EnrollmentOutcome.Enrolled, (await EnrollAsync("grace")).Outcome);
        Assert.Equal(EnrollmentOutcome.Waitlisted, (await EnrollAsync("linus")).Outcome);

        AssertCapacityNotExceeded();
    }

    [Fact]
    public async Task Eligible_learner_joins_the_waitlist_when_the_course_is_full()
    {
        Seed(1, ("ada", true), ("grace", true));
        await EnrollAsync("ada");

        var result = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.Waitlisted, result.Outcome);
        Assert.Equal(RegistrationStatus.Waitlisted, result.Status);
        Assert.Equal(1, result.WaitlistSequence);
        Assert.True(result.Succeeded);
        Assert.Equal(RegistrationStatus.Waitlisted, Registrations().Single(r => r.LearnerId == "grace").Status);
    }

    [Fact]
    public async Task Zero_capacity_offering_waitlists_everyone_eligible()
    {
        Seed(0, ("ada", true));

        var result = await EnrollAsync("ada");

        Assert.Equal(EnrollmentOutcome.Waitlisted, result.Outcome);
        AssertCapacityNotExceeded();
    }

    [Fact]
    public async Task Waitlist_positions_follow_request_order()
    {
        Seed(1, ("ada", true), ("grace", true), ("linus", true), ("tim", true));
        await EnrollAsync("ada");

        var grace = await EnrollAsync("grace");
        var linus = await EnrollAsync("linus");
        var tim = await EnrollAsync("tim");

        Assert.True(grace.WaitlistSequence < linus.WaitlistSequence);
        Assert.True(linus.WaitlistSequence < tim.WaitlistSequence);
    }

    [Fact]
    public async Task Duplicate_request_for_an_enrolled_learner_creates_no_record()
    {
        Seed(2, ("ada", true));
        await EnrollAsync("ada");

        var result = await EnrollAsync("ada");

        Assert.Equal(EnrollmentOutcome.AlreadyRegistered, result.Outcome);
        Assert.Equal(RegistrationStatus.Enrolled, result.Status);
        Assert.False(result.Succeeded);
        Assert.Single(Registrations());
    }

    [Fact]
    public async Task Duplicate_request_for_a_waitlisted_learner_keeps_the_original_position()
    {
        Seed(1, ("ada", true), ("grace", true), ("linus", true));
        await EnrollAsync("ada");
        var first = await EnrollAsync("grace");
        await EnrollAsync("linus");

        var duplicate = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.AlreadyRegistered, duplicate.Outcome);
        Assert.Equal(RegistrationStatus.Waitlisted, duplicate.Status);
        Assert.Equal(first.WaitlistSequence, duplicate.WaitlistSequence);
        Assert.Equal(3, Registrations().Count);
        Assert.Equal(first.WaitlistSequence, Registrations().Single(r => r.LearnerId == "grace").WaitlistSequence);
    }

    [Fact]
    public async Task Duplicate_request_from_an_ineligible_learner_is_reported_as_duplicate()
    {
        Seed(1, ("ada", true), ("grace", true));
        await EnrollAsync("ada");
        await EnrollAsync("grace");
        using (var context = _database.CreateContext())
        {
            context.Learners.Single(l => l.Id == "grace").PrerequisiteSatisfied = false;
            context.SaveChanges();
        }

        var result = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.AlreadyRegistered, result.Outcome);
        Assert.Equal(RegistrationStatus.Waitlisted, Registrations().Single(r => r.LearnerId == "grace").Status);
    }

    [Fact]
    public async Task Prerequisite_failure_prevents_enrollment_when_a_seat_is_available()
    {
        Seed(2, ("ada", false));

        var result = await EnrollAsync("ada");

        Assert.Equal(EnrollmentOutcome.PrerequisiteNotMet, result.Outcome);
        Assert.Null(result.Status);
        Assert.False(result.Succeeded);
        Assert.Empty(Registrations());
    }

    [Fact]
    public async Task Prerequisite_failure_prevents_joining_the_waitlist_when_full()
    {
        Seed(1, ("ada", true), ("grace", false));
        await EnrollAsync("ada");

        var result = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.PrerequisiteNotMet, result.Outcome);
        Assert.Single(Registrations());
        using var context = _database.CreateContext();
        Assert.Equal(1, context.Offerings.Single().NextWaitlistSequence);
    }

    [Fact]
    public async Task Newcomer_does_not_take_an_open_seat_ahead_of_an_older_eligible_waiter()
    {
        Seed(1, ("ada", true), ("grace", true));
        AddRegistration("ada", RegistrationStatus.Waitlisted, 1);

        var result = await EnrollAsync("grace");

        // The older waiter receives the open seat in the same transaction and the newcomer queues behind.
        Assert.Equal(EnrollmentOutcome.Waitlisted, result.Outcome);
        Assert.Equal(2, result.WaitlistSequence);
        Assert.Equal(["ada"], result.PromotedLearnerIds);
        var registrations = Registrations();
        Assert.Equal(RegistrationStatus.Enrolled, registrations.Single(r => r.LearnerId == "ada").Status);
        Assert.Null(registrations.Single(r => r.LearnerId == "ada").WaitlistSequence);
        Assert.Equal(RegistrationStatus.Waitlisted, registrations.Single(r => r.LearnerId == "grace").Status);
        AssertCapacityNotExceeded();
    }

    [Fact]
    public async Task Open_seats_go_to_older_eligible_waiters_in_order_before_the_newcomer()
    {
        Seed(2, ("ada", true), ("grace", true), ("linus", true), ("tim", true));
        AddRegistration("ada", RegistrationStatus.Waitlisted, 1);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 2);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 3);

        var result = await EnrollAsync("tim");

        Assert.Equal(EnrollmentOutcome.Waitlisted, result.Outcome);
        Assert.Equal(4, result.WaitlistSequence);
        Assert.Equal(["ada", "grace"], result.PromotedLearnerIds);
        Assert.Equal(
            RegistrationStatus.Waitlisted,
            Registrations().Single(r => r.LearnerId == "linus").Status);
        AssertCapacityNotExceeded();
    }

    [Fact]
    public async Task Newcomer_takes_a_seat_left_over_after_older_waiters_are_served()
    {
        Seed(3, ("ada", true), ("grace", true));
        AddRegistration("ada", RegistrationStatus.Waitlisted, 1);

        var result = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.Enrolled, result.Outcome);
        Assert.Equal(["ada"], result.PromotedLearnerIds);
        Assert.All(Registrations(), r => Assert.Equal(RegistrationStatus.Enrolled, r.Status));
    }

    [Fact]
    public async Task Newcomer_may_take_an_open_seat_when_every_older_waiter_is_ineligible()
    {
        Seed(1, ("ada", false), ("grace", true));
        AddRegistration("ada", RegistrationStatus.Waitlisted, 1);

        var result = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.Enrolled, result.Outcome);
        var ada = Registrations().Single(r => r.LearnerId == "ada");
        Assert.Equal(RegistrationStatus.Waitlisted, ada.Status);
        Assert.Equal(1, ada.WaitlistSequence);
    }

    [Fact]
    public async Task Dropped_learner_can_enroll_again_without_a_second_row()
    {
        Seed(1, ("ada", true));
        AddRegistration("ada", RegistrationStatus.Dropped);

        var result = await EnrollAsync("ada");

        Assert.Equal(EnrollmentOutcome.Enrolled, result.Outcome);
        Assert.Equal(RegistrationStatus.Enrolled, Assert.Single(Registrations()).Status);
    }

    [Fact]
    public async Task Dropped_learner_rejoining_a_full_course_gets_a_fresh_queue_position()
    {
        Seed(1, ("ada", true), ("grace", true), ("linus", true));
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("linus", RegistrationStatus.Waitlisted, 1);
        AddRegistration("grace", RegistrationStatus.Dropped);

        var result = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.Waitlisted, result.Outcome);
        Assert.Equal(2, result.WaitlistSequence);
        Assert.Equal(3, Registrations().Count);
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
        Seed(1, ("ada", true));

        var result = await EnrollAsync(offeringId!, learnerId!);

        Assert.Equal(EnrollmentOutcome.InvalidRequest, result.Outcome);
        Assert.Empty(Registrations());
    }

    [Fact]
    public async Task Unknown_learner_is_rejected_and_nothing_is_created()
    {
        Seed(1, ("ada", true));

        var result = await EnrollAsync("nobody");

        Assert.Equal(EnrollmentOutcome.LearnerNotFound, result.Outcome);
        Assert.Empty(Registrations());
    }

    [Fact]
    public async Task Unknown_offering_is_rejected_and_nothing_is_created()
    {
        Seed(1, ("ada", true));

        var result = await EnrollAsync("other-offering", "ada");

        Assert.Equal(EnrollmentOutcome.OfferingNotFound, result.Outcome);
        Assert.Empty(Registrations());
    }

    [Fact]
    public async Task Concurrent_requests_never_over_enroll_or_duplicate_a_registration()
    {
        var learners = Enumerable.Range(1, 12).Select(i => ($"learner-{i}", true)).ToArray();
        Seed(3, learners);

        // Duplicate each learner's request so the same learner races against itself as well.
        var requests = learners.Concat(learners).Select(l => Task.Run(() => EnrollAsync(l.Item1))).ToArray();
        var results = await Task.WhenAll(requests);

        Assert.Equal(3, results.Count(r => r.Outcome == EnrollmentOutcome.Enrolled));
        Assert.Equal(9, results.Count(r => r.Outcome == EnrollmentOutcome.Waitlisted));
        Assert.Equal(12, results.Count(r => r.Outcome == EnrollmentOutcome.AlreadyRegistered));

        var registrations = Registrations();
        Assert.Equal(12, registrations.Count);
        Assert.Equal(12, registrations.Select(r => r.LearnerId).Distinct().Count());
        Assert.Equal(3, registrations.Count(r => r.Status == RegistrationStatus.Enrolled));

        var sequences = registrations.Where(r => r.Status == RegistrationStatus.Waitlisted)
            .Select(r => r.WaitlistSequence!.Value)
            .OrderBy(s => s)
            .ToList();
        Assert.Equal(Enumerable.Range(1, 9).Select(i => (long)i), sequences);
    }
}
