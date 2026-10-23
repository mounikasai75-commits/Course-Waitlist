using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CourseWaitlist.Tests;

// Each test gets its own host, and therefore its own in-memory database, seeded from appsettings.json
// (capacity 2; alice, bob, carol, dave eligible; erin ineligible).
public sealed class CourseEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new();
    private readonly HttpClient client;

    public CourseEndpointTests() => client = factory.CreateClient();

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string path,
        string? json = null,
        string contentType = "application/json")
    {
        using var response = json is null && path == "/course/state"
            ? await client.GetAsync(path)
            : await client.PostAsync(path, new StringContent(json ?? "", Encoding.UTF8, contentType));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Enroll(string learnerId) =>
        SendAsync("/course/enroll", $"{{\"learnerId\":\"{learnerId}\"}}");

    private Task<(HttpStatusCode Status, JsonElement Body)> Drop(string learnerId) =>
        SendAsync("/course/drop", $"{{\"learnerId\":\"{learnerId}\"}}");

    private Task<(HttpStatusCode Status, JsonElement Body)> State() => SendAsync("/course/state");

    [Fact]
    public async Task Swagger_document_lists_course_endpoints_and_enroll_request_body()
    {
        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/course/enroll", out var enroll));
        Assert.True(enroll.GetProperty("post").TryGetProperty("requestBody", out _));
        Assert.True(paths.TryGetProperty("/course/drop", out _));
        Assert.True(paths.TryGetProperty("/course/state", out _));
    }

    [Fact]
    public async Task Initial_state_is_an_empty_course_with_all_seats_available()
    {
        var (status, body) = await State();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("course-101", body.GetProperty("offeringId").GetString());
        Assert.Equal(2, body.GetProperty("capacity").GetInt32());
        Assert.Equal(0, body.GetProperty("enrolledCount").GetInt32());
        Assert.Equal(2, body.GetProperty("availableSeats").GetInt32());
        Assert.Equal(0, body.GetProperty("enrolled").GetArrayLength());
        Assert.Equal(0, body.GetProperty("waitlist").GetArrayLength());
    }

    [Fact]
    public async Task Eligible_learner_with_a_free_seat_is_enrolled()
    {
        var (status, body) = await Enroll("alice");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Enrolled", body.GetProperty("outcome").GetString());
        Assert.Equal("alice", body.GetProperty("learnerId").GetString());
        Assert.Equal("Enrolled", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("waitlistSequence").ValueKind);
    }

    [Fact]
    public async Task Learner_is_waitlisted_when_the_course_is_full()
    {
        await Enroll("alice");
        await Enroll("bob");

        var (status, body) = await Enroll("carol");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Waitlisted", body.GetProperty("outcome").GetString());
        Assert.Equal(1, body.GetProperty("waitlistSequence").GetInt64());
    }

    [Fact]
    public async Task Duplicate_enrollment_conflicts_and_changes_nothing()
    {
        await Enroll("alice");
        var before = (await State()).Body.GetRawText();

        var (status, body) = await Enroll("alice");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("AlreadyRegistered", body.GetProperty("code").GetString());
        Assert.Equal(before, (await State()).Body.GetRawText());
    }

    [Fact]
    public async Task Duplicate_waitlist_request_conflicts_and_keeps_one_queue_entry()
    {
        await Enroll("alice");
        await Enroll("bob");
        await Enroll("carol");

        var (status, body) = await Enroll("carol");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("AlreadyRegistered", body.GetProperty("code").GetString());
        Assert.Equal(1, (await State()).Body.GetProperty("waitlist").GetArrayLength());
    }

    [Fact]
    public async Task Learner_failing_the_prerequisite_is_rejected_and_not_queued()
    {
        var (status, body) = await Enroll("erin");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("PrerequisiteNotMet", body.GetProperty("code").GetString());
        var state = (await State()).Body;
        Assert.Equal(0, state.GetProperty("enrolledCount").GetInt32());
        Assert.Equal(0, state.GetProperty("waitlist").GetArrayLength());
    }

    [Fact]
    public async Task Unknown_learner_is_not_found_for_enroll_and_drop()
    {
        var enroll = await Enroll("nobody");
        var drop = await Drop("nobody");

        Assert.Equal(HttpStatusCode.NotFound, enroll.Status);
        Assert.Equal("LearnerNotFound", enroll.Body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, drop.Status);
        Assert.Equal("LearnerNotFound", drop.Body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"learnerId\":\"\"}")]
    [InlineData("{\"learnerId\":\"   \"}")]
    [InlineData("{\"learnerId\":null}")]
    [InlineData("{\"learnerId\":42}")]
    [InlineData("{not json")]
    public async Task Invalid_bodies_are_bad_requests_with_a_stable_error_shape(string json)
    {
        foreach (var path in new[] { "/course/enroll", "/course/drop" })
        {
            var (status, body) = await SendAsync(path, json);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("InvalidRequest", body.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        }

        Assert.Equal(0, (await State()).Body.GetProperty("enrolledCount").GetInt32());
    }

    [Fact]
    public async Task Non_json_content_type_is_a_bad_request()
    {
        var (status, body) = await SendAsync("/course/enroll", "learnerId=alice", "application/x-www-form-urlencoded");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("InvalidRequest", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Dropping_an_enrolled_learner_promotes_the_earliest_waiter()
    {
        await Enroll("alice");
        await Enroll("bob");
        await Enroll("carol");
        await Enroll("dave");

        var (status, body) = await Drop("alice");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("DroppedAndPromoted", body.GetProperty("outcome").GetString());
        Assert.Equal("carol", body.GetProperty("promotedLearnerId").GetString());

        var state = (await State()).Body;
        Assert.Equal(["bob", "carol"], state.GetProperty("enrolled").EnumerateArray().Select(e => e.GetString()).Order());
        var waitlist = state.GetProperty("waitlist").EnumerateArray().Single();
        Assert.Equal("dave", waitlist.GetProperty("learnerId").GetString());
        Assert.Equal(1, waitlist.GetProperty("position").GetInt32());
    }

    [Fact]
    public async Task Dropping_with_nobody_waiting_leaves_the_seat_available()
    {
        await Enroll("alice");

        var (status, body) = await Drop("alice");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("DroppedSeatAvailable", body.GetProperty("outcome").GetString());
        Assert.Equal(2, (await State()).Body.GetProperty("availableSeats").GetInt32());
    }

    [Fact]
    public async Task Dropping_from_the_waitlist_does_not_affect_enrolled_capacity()
    {
        await Enroll("alice");
        await Enroll("bob");
        await Enroll("carol");

        var (status, body) = await Drop("carol");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("DroppedFromWaitlist", body.GetProperty("outcome").GetString());
        var state = (await State()).Body;
        Assert.Equal(2, state.GetProperty("enrolledCount").GetInt32());
        Assert.Equal(0, state.GetProperty("waitlist").GetArrayLength());
    }

    [Fact]
    public async Task Repeated_drop_returns_conflict_and_changes_nothing()
    {
        await Enroll("alice");
        await Drop("alice");
        var before = (await State()).Body.GetRawText();

        var repeated = await Drop("alice");
        var neverRegistered = await Drop("bob");

        Assert.Equal(HttpStatusCode.Conflict, repeated.Status);
        Assert.Equal("NotEnrolled", repeated.Body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, neverRegistered.Status);
        Assert.Equal("NotEnrolled", neverRegistered.Body.GetProperty("code").GetString());
        Assert.Equal(before, (await State()).Body.GetRawText());
    }

    [Fact]
    public async Task Hosts_do_not_share_state()
    {
        await Enroll("alice");

        using var other = new WebApplicationFactory<Program>();
        using var otherClient = other.CreateClient();
        using var response = await otherClient.GetAsync("/course/state");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(0, document.RootElement.GetProperty("enrolledCount").GetInt32());
    }
}
