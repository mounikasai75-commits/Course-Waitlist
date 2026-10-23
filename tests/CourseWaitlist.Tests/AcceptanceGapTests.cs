using System.Net;
using System.Text;
using System.Text.Json;
using CourseWaitlist.Api.Data;
using CourseWaitlist.Api.Domain;
using CourseWaitlist.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CourseWaitlist.Tests;

// Fills coverage gaps found when mapping tests to the acceptance criteria (see TESTING.md).
public sealed class EnrollTimePromotionEligibilityTests : IDisposable
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

    private void AddWaiter(string learnerId, long sequence)
    {
        using var context = _database.CreateContext();
        context.Registrations.Add(new Registration
        {
            OfferingId = OfferingId,
            LearnerId = learnerId,
            Status = RegistrationStatus.Waitlisted,
            WaitlistSequence = sequence
        });
        var offering = context.Offerings.Single();
        offering.NextWaitlistSequence = Math.Max(offering.NextWaitlistSequence, sequence + 1);
        context.SaveChanges();
    }

    private void SetEligible(string learnerId, bool eligible)
    {
        using var context = _database.CreateContext();
        context.Learners.Single(l => l.Id == learnerId).PrerequisiteSatisfied = eligible;
        context.SaveChanges();
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

    [Fact]
    public async Task Open_seat_skips_an_older_ineligible_waiter_and_promotes_the_next_eligible_one()
    {
        Seed(1, ("ada", false), ("grace", true), ("linus", true));
        AddWaiter("ada", 1);
        AddWaiter("grace", 2);

        var result = await EnrollAsync("linus");

        Assert.Equal(EnrollmentOutcome.Waitlisted, result.Outcome);
        Assert.Equal(["grace"], result.PromotedLearnerIds);
        Assert.Equal(RegistrationStatus.Enrolled, Get("grace").Status);
        var ada = Get("ada");
        Assert.Equal(RegistrationStatus.Waitlisted, ada.Status);
        Assert.Equal(1, ada.WaitlistSequence);
        Assert.Equal(3, Get("linus").WaitlistSequence);
        Assert.Equal(1, Count(RegistrationStatus.Enrolled));
    }

    [Fact]
    public async Task Waiter_who_regained_eligibility_is_promoted_ahead_of_a_newcomer_on_an_open_seat()
    {
        Seed(1, ("ada", false), ("grace", true));
        AddWaiter("ada", 1);
        SetEligible("ada", true);

        var result = await EnrollAsync("grace");

        Assert.Equal(EnrollmentOutcome.Waitlisted, result.Outcome);
        Assert.Equal(["ada"], result.PromotedLearnerIds);
        Assert.Equal(RegistrationStatus.Enrolled, Get("ada").Status);
        Assert.Equal(RegistrationStatus.Waitlisted, Get("grace").Status);
        Assert.Equal(2, Get("grace").WaitlistSequence);
    }

    [Fact]
    public async Task Ineligible_waiter_is_never_enrolled_by_any_operation_while_ineligible()
    {
        Seed(2, ("ada", false), ("grace", true), ("linus", true));
        AddWaiter("ada", 1);

        await EnrollAsync("grace");
        await EnrollAsync("linus");

        var ada = Get("ada");
        Assert.Equal(RegistrationStatus.Waitlisted, ada.Status);
        Assert.Equal(1, ada.WaitlistSequence);
        Assert.Equal(2, Count(RegistrationStatus.Enrolled));
    }

    private int Count(RegistrationStatus status)
    {
        using var context = _database.CreateContext();
        return context.Registrations.Count(r => r.Status == status);
    }

    private void AddEnrolled(string learnerId)
    {
        using var context = _database.CreateContext();
        context.Registrations.Add(new Registration
        {
            OfferingId = OfferingId,
            LearnerId = learnerId,
            Status = RegistrationStatus.Enrolled
        });
        context.SaveChanges();
    }

    private async Task<DropResult> DropAsync(string learnerId)
    {
        using var context = _database.CreateContext();
        return await new RegistrationService(context, _gate).DropAsync(OfferingId, learnerId);
    }

    [Fact]
    public async Task Drop_fills_a_seat_left_open_earlier_once_waiters_regain_eligibility()
    {
        Seed(2, ("e1", true), ("e2", true), ("w1", false), ("w2", false));
        AddEnrolled("e1");
        AddEnrolled("e2");
        AddWaiter("w1", 1);
        AddWaiter("w2", 2);

        var first = await DropAsync("e1");
        Assert.Equal(DropOutcome.DroppedSeatAvailable, first.Outcome);
        Assert.Equal(1, Count(RegistrationStatus.Enrolled));
        Assert.Equal(2, Count(RegistrationStatus.Waitlisted));

        SetEligible("w1", true);
        SetEligible("w2", true);
        var second = await DropAsync("e2");

        Assert.Equal(DropOutcome.DroppedAndPromoted, second.Outcome);
        Assert.Equal(["w1", "w2"], second.PromotedLearnerIds);
        Assert.Equal(RegistrationStatus.Enrolled, Get("w1").Status);
        Assert.Equal(RegistrationStatus.Enrolled, Get("w2").Status);
        Assert.Equal(2, Count(RegistrationStatus.Enrolled));
        Assert.Equal(0, Count(RegistrationStatus.Waitlisted));
    }

    [Fact]
    public async Task Drop_promotes_only_as_many_waiters_as_there_are_open_seats_in_fifo_order()
    {
        Seed(2, ("e1", true), ("e2", true), ("w1", false), ("w2", false), ("w3", false), ("w4", false));
        AddEnrolled("e1");
        AddEnrolled("e2");
        AddWaiter("w1", 1);
        AddWaiter("w2", 2);
        AddWaiter("w3", 3);
        AddWaiter("w4", 4);

        await DropAsync("e1");
        Assert.Equal(1, Count(RegistrationStatus.Enrolled));
        SetEligible("w2", true);
        SetEligible("w3", true);
        SetEligible("w4", true);
        var result = await DropAsync("e2");

        Assert.Equal(["w2", "w3"], result.PromotedLearnerIds);
        Assert.Equal(2, Count(RegistrationStatus.Enrolled));
        Assert.Equal(1, Get("w1").WaitlistSequence);
        Assert.Equal(4, Get("w4").WaitlistSequence);
        Assert.Equal(RegistrationStatus.Waitlisted, Get("w1").Status);
        Assert.Equal(RegistrationStatus.Waitlisted, Get("w4").Status);
    }
}

// Each test gets its own host and therefore its own isolated in-memory SQLite database.
public sealed class CourseApiAcceptanceTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public CourseApiAcceptanceTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, string learnerId)
    {
        using var content = new StringContent($"{{\"learnerId\":\"{learnerId}\"}}", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(path, content);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Enroll(string id) => PostAsync("/course/enroll", id);

    private Task<(HttpStatusCode Status, JsonElement Body)> Drop(string id) => PostAsync("/course/drop", id);

    private async Task<JsonElement> StateAsync()
    {
        using var response = await _client.GetAsync("/course/state");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string[] Enrolled(JsonElement state) =>
        state.GetProperty("enrolled").EnumerateArray().Select(e => e.GetString()!).Order().ToArray();

    private static string[] Waitlist(JsonElement state) =>
        state.GetProperty("waitlist").EnumerateArray().Select(e => e.GetProperty("learnerId").GetString()!).ToArray();

    // Only called while no request is in flight, because it uses the shared connection outside the service gate.
    private void SetEligible(string learnerId, bool eligible)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CourseWaitlistDbContext>();
        context.Learners.Single(l => l.Id == learnerId).PrerequisiteSatisfied = eligible;
        context.SaveChanges();
    }

    [Fact]
    public async Task Api_skips_an_ineligible_waiter_keeps_its_place_and_restores_priority_when_eligible_again()
    {
        await Enroll("alice");
        await Enroll("bob");
        await Enroll("carol");
        await Enroll("dave");
        SetEligible("carol", false);

        var first = await Drop("alice");

        Assert.Equal("DroppedAndPromoted", first.Body.GetProperty("outcome").GetString());
        Assert.Equal("dave", first.Body.GetProperty("promotedLearnerId").GetString());
        var afterSkip = await StateAsync();
        Assert.Equal(["bob", "dave"], Enrolled(afterSkip));
        Assert.Equal(["carol"], Waitlist(afterSkip));
        Assert.Equal(1, afterSkip.GetProperty("waitlist")[0].GetProperty("sequence").GetInt64());

        SetEligible("carol", true);
        await Enroll("alice");
        var second = await Drop("bob");

        Assert.Equal("carol", second.Body.GetProperty("promotedLearnerId").GetString());
        var final = await StateAsync();
        Assert.Equal(["carol", "dave"], Enrolled(final));
        Assert.Equal(["alice"], Waitlist(final));
    }

    [Fact]
    public async Task Api_drop_with_only_ineligible_waiters_leaves_the_seat_open_and_queue_intact()
    {
        await Enroll("alice");
        await Enroll("bob");
        await Enroll("carol");
        SetEligible("carol", false);

        var drop = await Drop("alice");

        Assert.Equal(HttpStatusCode.OK, drop.Status);
        Assert.Equal("DroppedSeatAvailable", drop.Body.GetProperty("outcome").GetString());
        var state = await StateAsync();
        Assert.Equal(["bob"], Enrolled(state));
        Assert.Equal(1, state.GetProperty("availableSeats").GetInt32());
        Assert.Equal(["carol"], Waitlist(state));
    }

    [Fact]
    public async Task Concurrent_http_enroll_requests_never_over_enroll_or_duplicate()
    {
        var learners = new[] { "alice", "bob", "carol", "dave" };
        var requests = learners.SelectMany(id => Enumerable.Repeat(id, 5)).Concat(Enumerable.Repeat("erin", 3)).ToArray();

        var start = new TaskCompletionSource();
        var tasks = requests.Select(id => Task.Run(async () =>
        {
            await start.Task;
            return await Enroll(id);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(2, results.Count(r => r.Body.TryGetProperty("outcome", out var o) && o.GetString() == "Enrolled"));
        Assert.Equal(2, results.Count(r => r.Body.TryGetProperty("outcome", out var o) && o.GetString() == "Waitlisted"));
        Assert.Equal(16, results.Count(r => r.Status == HttpStatusCode.Conflict));
        Assert.Equal(3, results.Count(r => r.Status == HttpStatusCode.UnprocessableEntity));

        var state = await StateAsync();
        Assert.Equal(2, state.GetProperty("enrolledCount").GetInt32());
        var queue = state.GetProperty("waitlist").EnumerateArray().Select(w => w.GetProperty("sequence").GetInt64()).ToArray();
        Assert.Equal([1L, 2L], queue);
        Assert.Equal(learners.Order(), Enrolled(state).Concat(Waitlist(state)).Order());
    }

    [Fact]
    public async Task Concurrent_http_drops_promote_each_waiter_once_and_repeats_are_no_ops()
    {
        await Enroll("alice");
        await Enroll("bob");
        await Enroll("carol");
        await Enroll("dave");

        var start = new TaskCompletionSource();
        var tasks = new[] { "alice", "bob" }.SelectMany(id => Enumerable.Repeat(id, 5))
            .Select(id => Task.Run(async () =>
            {
                await start.Task;
                return await Drop(id);
            })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(2, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(8, results.Count(r => r.Status == HttpStatusCode.Conflict));
        Assert.Equal(2, results.Count(r => r.Status == HttpStatusCode.OK && r.Body.GetProperty("outcome").GetString() == "DroppedAndPromoted"));
        Assert.Equal(8, results.Count(r => r.Status == HttpStatusCode.Conflict && r.Body.GetProperty("code").GetString() == "NotEnrolled"));
        Assert.Equal(
            ["carol", "dave"],
            results.Where(r => r.Status == HttpStatusCode.OK)
                .Select(r => r.Body.GetProperty("promotedLearnerId").GetString()!)
                .Order());

        var state = await StateAsync();
        Assert.Equal(["carol", "dave"], Enrolled(state));
        Assert.Empty(Waitlist(state));
    }
}
