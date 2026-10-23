using CourseWaitlist.Api.Data;
using CourseWaitlist.Api.Domain;
using CourseWaitlist.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CourseWaitlist.Tests;

public sealed class ConcurrencyTests
{
    private const string OfferingId = "offering-1";
    private const int Iterations = 25;

    // One isolated database and one gate per scenario; every operation gets its own DbContext and service.
    private sealed class Scenario : IDisposable
    {
        public Scenario(int capacity, params string[] learnerIds)
        {
            using var context = Database.CreateContext();
            context.Offerings.Add(new CourseOffering { Id = OfferingId, Capacity = capacity });
            context.Learners.AddRange(learnerIds.Select(id => new Learner { Id = id, PrerequisiteSatisfied = true }));
            context.SaveChanges();
        }

        public TestDatabase Database { get; } = new();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void Register(string learnerId, RegistrationStatus status, long? sequence = null)
        {
            using var context = Database.CreateContext();
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

        public void SetEligible(string learnerId, bool eligible)
        {
            using var context = Database.CreateContext();
            context.Learners.Single(l => l.Id == learnerId).PrerequisiteSatisfied = eligible;
            context.SaveChanges();
        }

        public async Task<EnrollmentResult> EnrollAsync(string learnerId, CancellationToken cancellationToken = default)
        {
            using var context = Database.CreateContext();
            return await new RegistrationService(context, Gate).EnrollAsync(OfferingId, learnerId, cancellationToken);
        }

        public async Task<DropResult> DropAsync(string learnerId)
        {
            using var context = Database.CreateContext();
            return await new RegistrationService(context, Gate).DropAsync(OfferingId, learnerId);
        }

        public List<Registration> All()
        {
            using var context = Database.CreateContext();
            return context.Registrations.AsNoTracking().OrderBy(r => r.Id).ToList();
        }

        public Registration Get(string learnerId) => All().Single(r => r.LearnerId == learnerId);

        public List<string> Queue() => All()
            .Where(r => r.Status == RegistrationStatus.Waitlisted)
            .OrderBy(r => r.WaitlistSequence)
            .Select(r => r.LearnerId)
            .ToList();

        public long NextSequence()
        {
            using var context = Database.CreateContext();
            return context.Offerings.AsNoTracking().Single().NextWaitlistSequence;
        }

        public void AssertConsistent() => DatabaseInvariants.AssertConsistent(Database);

        public void Dispose()
        {
            Database.Dispose();
            Gate.Dispose();
        }
    }

    // All operations wait on one signal so they contend at the same moment instead of running one after another.
    private static async Task<T[]> RunConcurrently<T>(IEnumerable<Func<Task<T>>> operations)
    {
        var start = new TaskCompletionSource();
        var tasks = operations
            .Select(operation => Task.Run(async () =>
            {
                await start.Task;
                return await operation();
            }))
            .ToArray();

        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static string[] Ids(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => $"{prefix}{i}").ToArray();

    [Fact]
    public async Task Two_learners_racing_for_the_last_seat_enroll_exactly_one()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(1, "ada", "grace");

            var results = await RunConcurrently<EnrollmentResult>(
                [() => scenario.EnrollAsync("ada"), () => scenario.EnrollAsync("grace")]);

            Assert.Equal(1, results.Count(r => r.Outcome == EnrollmentOutcome.Enrolled));
            var waitlisted = Assert.Single(results, r => r.Outcome == EnrollmentOutcome.Waitlisted);
            Assert.Equal(1, waitlisted.WaitlistSequence);

            var all = scenario.All();
            Assert.Equal(2, all.Count);
            Assert.Equal(1, all.Count(r => r.Status == RegistrationStatus.Enrolled));
            Assert.Equal(1, all.Count(r => r.Status == RegistrationStatus.Waitlisted));
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Many_learners_racing_for_one_seat_enroll_one_and_queue_the_rest_with_unique_positions()
    {
        using var scenario = new Scenario(1, Ids("learner", 20));

        var results = await RunConcurrently(Ids("learner", 20).Select(id => (Func<Task<EnrollmentResult>>)(() => scenario.EnrollAsync(id))));

        Assert.Equal(1, results.Count(r => r.Outcome == EnrollmentOutcome.Enrolled));
        Assert.Equal(19, results.Count(r => r.Outcome == EnrollmentOutcome.Waitlisted));
        var sequences = scenario.All()
            .Where(r => r.Status == RegistrationStatus.Waitlisted)
            .Select(r => r.WaitlistSequence!.Value)
            .Order()
            .ToList();
        Assert.Equal(Enumerable.Range(1, 19).Select(n => (long)n), sequences);
        Assert.Equal(20, scenario.NextSequence());
        scenario.AssertConsistent();
    }

    [Fact]
    public async Task Repeated_requests_for_one_learner_with_a_free_seat_create_one_enrollment()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(2, "ada");

            var results = await RunConcurrently(
                Enumerable.Range(0, 10).Select(_ => (Func<Task<EnrollmentResult>>)(() => scenario.EnrollAsync("ada"))));

            Assert.Equal(1, results.Count(r => r.Outcome == EnrollmentOutcome.Enrolled));
            Assert.Equal(9, results.Count(r => r.Outcome == EnrollmentOutcome.AlreadyRegistered));
            var registration = Assert.Single(scenario.All());
            Assert.Equal(RegistrationStatus.Enrolled, registration.Status);
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Repeated_requests_for_one_learner_on_a_full_course_create_one_queue_entry()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(1, "ada", "grace");
            scenario.Register("ada", RegistrationStatus.Enrolled);

            var results = await RunConcurrently(
                Enumerable.Range(0, 10).Select(_ => (Func<Task<EnrollmentResult>>)(() => scenario.EnrollAsync("grace"))));

            Assert.Equal(1, results.Count(r => r.Outcome == EnrollmentOutcome.Waitlisted));
            Assert.Equal(9, results.Count(r => r.Outcome == EnrollmentOutcome.AlreadyRegistered));
            Assert.Equal(1, scenario.Get("grace").WaitlistSequence);
            Assert.Equal(2, scenario.NextSequence());
            Assert.Equal(2, scenario.All().Count);
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Repeated_requests_from_an_ineligible_learner_create_nothing()
    {
        using var scenario = new Scenario(2, "ada");
        scenario.SetEligible("ada", false);

        var results = await RunConcurrently(
            Enumerable.Range(0, 10).Select(_ => (Func<Task<EnrollmentResult>>)(() => scenario.EnrollAsync("ada"))));

        Assert.All(results, r => Assert.Equal(EnrollmentOutcome.PrerequisiteNotMet, r.Outcome));
        Assert.Empty(scenario.All());
        Assert.Equal(1, scenario.NextSequence());
        scenario.AssertConsistent();
    }

    [Fact]
    public async Task Repeated_drops_of_one_learner_promote_the_next_waiter_exactly_once()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(1, "ada", "grace", "linus");
            scenario.Register("ada", RegistrationStatus.Enrolled);
            scenario.Register("grace", RegistrationStatus.Waitlisted, 1);
            scenario.Register("linus", RegistrationStatus.Waitlisted, 2);

            var results = await RunConcurrently(
                Enumerable.Range(0, 10).Select(_ => (Func<Task<DropResult>>)(() => scenario.DropAsync("ada"))));

            var promoted = Assert.Single(results, r => r.Outcome == DropOutcome.DroppedAndPromoted);
            Assert.Equal("grace", promoted.PromotedLearnerId);
            Assert.Equal(9, results.Count(r => r.Outcome == DropOutcome.NotEnrolled));
            Assert.Equal(RegistrationStatus.Dropped, scenario.Get("ada").Status);
            Assert.Equal(RegistrationStatus.Enrolled, scenario.Get("grace").Status);
            Assert.Equal(2, scenario.Get("linus").WaitlistSequence);
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Drop_racing_with_a_newcomer_always_ends_with_the_newcomer_enrolled()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(1, "ada", "linus");
            scenario.Register("ada", RegistrationStatus.Enrolled);

            await Task.WhenAll(
                RunConcurrently<object>(
                [
                    async () => await scenario.DropAsync("ada"),
                    async () => await scenario.EnrollAsync("linus")
                ]));

            // Enroll-first queues linus and the drop then promotes him; drop-first lets him take the seat.
            Assert.Equal(RegistrationStatus.Dropped, scenario.Get("ada").Status);
            Assert.Equal(RegistrationStatus.Enrolled, scenario.Get("linus").Status);
            Assert.Empty(scenario.Queue());
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Drop_racing_with_a_newcomer_never_lets_the_newcomer_jump_an_existing_waiter()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(1, "ada", "grace", "linus");
            scenario.Register("ada", RegistrationStatus.Enrolled);
            scenario.Register("grace", RegistrationStatus.Waitlisted, 1);

            await Task.WhenAll(
                RunConcurrently<object>(
                [
                    async () => await scenario.DropAsync("ada"),
                    async () => await scenario.EnrollAsync("linus")
                ]));

            Assert.Equal(RegistrationStatus.Enrolled, scenario.Get("grace").Status);
            var linus = scenario.Get("linus");
            Assert.Equal(RegistrationStatus.Waitlisted, linus.Status);
            Assert.Equal(2, linus.WaitlistSequence);
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Same_learner_dropping_and_enrolling_at_once_ends_in_a_valid_state_matching_the_results()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(1, "ada");
            scenario.Register("ada", RegistrationStatus.Enrolled);

            var drop = default(DropResult);
            var enroll = default(EnrollmentResult);
            await RunConcurrently<object>(
            [
                async () => drop = await scenario.DropAsync("ada"),
                async () => enroll = await scenario.EnrollAsync("ada")
            ]);

            Assert.Equal(DropOutcome.DroppedSeatAvailable, drop!.Outcome);
            var status = scenario.Get("ada").Status;
            if (enroll!.Outcome == EnrollmentOutcome.AlreadyRegistered)
            {
                Assert.Equal(RegistrationStatus.Dropped, status);
            }
            else
            {
                Assert.Equal(EnrollmentOutcome.Enrolled, enroll.Outcome);
                Assert.Equal(RegistrationStatus.Enrolled, status);
            }

            Assert.Single(scenario.All());
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Newcomers_racing_with_an_open_seat_and_an_older_eligible_waiter_are_served_in_fifo_order()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(1, "ada", "grace", "linus");
            scenario.Register("ada", RegistrationStatus.Waitlisted, 1);

            await RunConcurrently<EnrollmentResult>(
                [() => scenario.EnrollAsync("grace"), () => scenario.EnrollAsync("linus")]);

            Assert.Equal(RegistrationStatus.Enrolled, scenario.Get("ada").Status);
            Assert.Equal(RegistrationStatus.Waitlisted, scenario.Get("grace").Status);
            Assert.Equal(RegistrationStatus.Waitlisted, scenario.Get("linus").Status);
            Assert.Equal(
                new long[] { 2, 3 },
                scenario.All().Where(r => r.WaitlistSequence is not null).Select(r => r.WaitlistSequence!.Value).Order());
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Concurrent_waitlist_joins_get_unique_contiguous_positions()
    {
        using var scenario = new Scenario(1, [.. Ids("joiner", 30), "holder"]);
        scenario.Register("holder", RegistrationStatus.Enrolled);

        var results = await RunConcurrently(
            Ids("joiner", 30).Select(id => (Func<Task<EnrollmentResult>>)(() => scenario.EnrollAsync(id))));

        Assert.All(results, r => Assert.Equal(EnrollmentOutcome.Waitlisted, r.Outcome));
        Assert.Equal(
            Enumerable.Range(1, 30).Select(n => (long)n),
            results.Select(r => r.WaitlistSequence!.Value).Order());
        Assert.Equal(31, scenario.NextSequence());
        scenario.AssertConsistent();
    }

    [Fact]
    public async Task Drops_and_joins_racing_on_a_full_course_preserve_fifo_for_existing_waiters()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(2, [.. Ids("enrolled", 2), .. Ids("waiter", 4), .. Ids("newcomer", 3)]);
            foreach (var id in Ids("enrolled", 2))
            {
                scenario.Register(id, RegistrationStatus.Enrolled);
            }

            var sequence = 1;
            foreach (var id in Ids("waiter", 4))
            {
                scenario.Register(id, RegistrationStatus.Waitlisted, sequence++);
            }

            var operations = Ids("enrolled", 2)
                .Select(id => (Func<Task<object>>)(async () => await scenario.DropAsync(id)))
                .Concat(Ids("newcomer", 3).Select(id => (Func<Task<object>>)(async () => await scenario.EnrollAsync(id))));
            await RunConcurrently(operations);

            Assert.Equal(RegistrationStatus.Enrolled, scenario.Get("waiter1").Status);
            Assert.Equal(RegistrationStatus.Enrolled, scenario.Get("waiter2").Status);
            var queue = scenario.Queue();
            Assert.Equal(["waiter3", "waiter4"], queue.Take(2));
            Assert.Equal(Ids("newcomer", 3).Order(), queue.Skip(2).Order());
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Waitlist_drops_racing_with_joins_leave_survivors_in_order_and_ahead_of_joiners()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(1, [.. Ids("waiter", 5), .. Ids("joiner", 3), "holder"]);
            scenario.Register("holder", RegistrationStatus.Enrolled);
            var sequence = 1;
            foreach (var id in Ids("waiter", 5))
            {
                scenario.Register(id, RegistrationStatus.Waitlisted, sequence++);
            }

            var operations = new[] { "waiter2", "waiter4" }
                .Select(id => (Func<Task<object>>)(async () => await scenario.DropAsync(id)))
                .Concat(Ids("joiner", 3).Select(id => (Func<Task<object>>)(async () => await scenario.EnrollAsync(id))));
            await RunConcurrently(operations);

            Assert.Equal(RegistrationStatus.Dropped, scenario.Get("waiter2").Status);
            Assert.Equal(RegistrationStatus.Dropped, scenario.Get("waiter4").Status);
            Assert.Equal(RegistrationStatus.Enrolled, scenario.Get("holder").Status);
            var queue = scenario.Queue();
            Assert.Equal(["waiter1", "waiter3", "waiter5"], queue.Take(3));
            Assert.Equal(Ids("joiner", 3).Order(), queue.Skip(3).Order());
            Assert.All(
                scenario.All().Where(r => r.LearnerId.StartsWith("joiner")),
                r => Assert.True(r.WaitlistSequence > 5));
            scenario.AssertConsistent();
        }
    }

    [Fact]
    public async Task Concurrent_drops_skip_an_ineligible_waiter_and_leave_it_in_place()
    {
        for (var i = 0; i < Iterations; i++)
        {
            using var scenario = new Scenario(2, "enrolled1", "enrolled2", "waiter1", "waiter2", "waiter3");
            scenario.Register("enrolled1", RegistrationStatus.Enrolled);
            scenario.Register("enrolled2", RegistrationStatus.Enrolled);
            scenario.Register("waiter1", RegistrationStatus.Waitlisted, 1);
            scenario.Register("waiter2", RegistrationStatus.Waitlisted, 2);
            scenario.Register("waiter3", RegistrationStatus.Waitlisted, 3);
            scenario.SetEligible("waiter1", false);

            var results = await RunConcurrently<DropResult>(
                [() => scenario.DropAsync("enrolled1"), () => scenario.DropAsync("enrolled2")]);

            Assert.All(results, r => Assert.Equal(DropOutcome.DroppedAndPromoted, r.Outcome));
            Assert.Equal(["waiter2", "waiter3"], results.Select(r => r.PromotedLearnerId).Order());
            var skipped = scenario.Get("waiter1");
            Assert.Equal(RegistrationStatus.Waitlisted, skipped.Status);
            Assert.Equal(1, skipped.WaitlistSequence);
            scenario.AssertConsistent();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Mixed_concurrent_workload_keeps_every_invariant(int seed)
    {
        var random = new Random(seed);
        var learners = Ids("learner", 12);
        using var scenario = new Scenario(3, learners);

        var operations = Enumerable.Range(0, 200)
            .Select(_ =>
            {
                var id = learners[random.Next(learners.Length)];
                return random.Next(100) < 60
                    ? (Func<Task<string>>)(async () => (await scenario.EnrollAsync(id)).Outcome.ToString())
                    : async () => (await scenario.DropAsync(id)).Outcome.ToString();
            })
            .ToList();

        var outcomes = await RunConcurrently(operations);

        Assert.DoesNotContain(outcomes, o => o is "InvalidRequest" or "OfferingNotFound" or "LearnerNotFound");
        scenario.AssertConsistent();
    }

    [Fact]
    public async Task Operations_wait_for_the_shared_gate_before_touching_the_database()
    {
        using var scenario = new Scenario(1, "ada");
        await scenario.Gate.WaitAsync();

        var pending = scenario.EnrollAsync("ada");
        await Task.Delay(250);

        Assert.False(pending.IsCompleted);
        Assert.Empty(scenario.All());

        scenario.Gate.Release();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EnrollmentOutcome.Enrolled, result.Outcome);
        Assert.Equal(1, scenario.Gate.CurrentCount);
    }

    [Fact]
    public async Task Cancelled_request_waiting_for_the_gate_changes_nothing_and_does_not_leak_the_gate()
    {
        using var scenario = new Scenario(1, "ada");
        await scenario.Gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();

        var pending = scenario.EnrollAsync("ada", cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        scenario.Gate.Release();
        Assert.Empty(scenario.All());
        Assert.Equal(1, scenario.Gate.CurrentCount);

        var result = await scenario.EnrollAsync("ada");
        Assert.Equal(EnrollmentOutcome.Enrolled, result.Outcome);
        scenario.AssertConsistent();
    }

    [Fact]
    public async Task Reused_context_sees_a_learners_current_eligibility()
    {
        using var scenario = new Scenario(2, "ada");
        scenario.SetEligible("ada", false);
        using var context = scenario.Database.CreateContext();
        var service = new RegistrationService(context, scenario.Gate);

        Assert.Equal(EnrollmentOutcome.PrerequisiteNotMet, (await service.EnrollAsync(OfferingId, "ada")).Outcome);
        scenario.SetEligible("ada", true);

        Assert.Equal(EnrollmentOutcome.Enrolled, (await service.EnrollAsync(OfferingId, "ada")).Outcome);
        scenario.AssertConsistent();
    }

    [Fact]
    public async Task Reused_context_does_not_reuse_a_stale_waitlist_sequence()
    {
        using var scenario = new Scenario(1, "holder", "grace", "linus", "tim");
        scenario.Register("holder", RegistrationStatus.Enrolled);
        using var context = scenario.Database.CreateContext();
        var service = new RegistrationService(context, scenario.Gate);

        Assert.Equal(1, (await service.EnrollAsync(OfferingId, "grace")).WaitlistSequence);
        Assert.Equal(2, (await scenario.EnrollAsync("linus")).WaitlistSequence);

        // The shared context last saw the offering before linus joined, so a stale counter would collide here.
        Assert.Equal(3, (await service.EnrollAsync(OfferingId, "tim")).WaitlistSequence);
        scenario.AssertConsistent();
    }
}
