using System.Text.Json;
using CourseWaitlist.Api.Data;
using CourseWaitlist.Api.Services;
using Microsoft.AspNetCore.Diagnostics;

namespace CourseWaitlist.Api.Endpoints;

public sealed record LearnerRequest(string? LearnerId);

public sealed record ApiError(string Code, string Message);

public sealed record EnrollResponse(
    string Outcome,
    string LearnerId,
    string Status,
    long? WaitlistSequence,
    IReadOnlyList<string> PromotedLearnerIds,
    string Message);

public sealed record DropResponse(
    string Outcome,
    string LearnerId,
    string? PromotedLearnerId,
    IReadOnlyList<string> PromotedLearnerIds,
    string Message);

// Handlers only parse input and translate service outcomes to HTTP; every business decision lives in IRegistrationService.
public static class CourseEndpoints
{
    private const string BadBodyMessage = "Request body must be JSON in the form {\"learnerId\": \"<id>\"} with a non-empty learnerId.";

    public static IEndpointRouteBuilder MapCourseEndpoints(this IEndpointRouteBuilder app)
    {
        var course = app.MapGroup("/course");
        course.MapPost("/enroll", EnrollAsync)
            .Produces<EnrollResponse>()
            .Produces<ApiError>(StatusCodes.Status400BadRequest)
            .Produces<ApiError>(StatusCodes.Status404NotFound)
            .Produces<ApiError>(StatusCodes.Status409Conflict)
            .Produces<ApiError>(StatusCodes.Status422UnprocessableEntity);
        course.MapPost("/drop", DropAsync)
            .Produces<DropResponse>()
            .Produces<ApiError>(StatusCodes.Status400BadRequest)
            .Produces<ApiError>(StatusCodes.Status404NotFound)
            .Produces<ApiError>(StatusCodes.Status409Conflict);
        course.MapGet("/state", GetStateAsync);
        return app;
    }

    public static IApplicationBuilder UseApiExceptionHandler(this IApplicationBuilder app) =>
        app.UseExceptionHandler(errors => errors.Run(async context =>
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CourseWaitlist.Api");
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

            // A client that disconnected mid-request is not a server fault; state is safe because the transaction rolls back.
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                logger.LogInformation("Request {Method} {Path} was cancelled by the client.", context.Request.Method, context.Request.Path);
                context.Response.StatusCode = 499;
                return;
            }

            logger.LogError(exception, "Unhandled exception while processing {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ApiError("InternalError", "An unexpected error occurred."));
        }));

    private static async Task<IResult> EnrollAsync(
        HttpRequest request,
        CourseSettings course,
        IRegistrationService service,
        CancellationToken cancellationToken)
    {
        var learnerId = await ReadLearnerIdAsync(request, cancellationToken);
        if (learnerId is null)
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidRequest", BadBodyMessage);
        }

        var result = await service.EnrollAsync(course.Id, learnerId, cancellationToken);
        return result.Outcome switch
        {
            EnrollmentOutcome.Enrolled => Results.Json(new EnrollResponse(
                "Enrolled", learnerId, "Enrolled", null, result.PromotedLearnerIds ?? [],
                $"Learner '{learnerId}' is enrolled.")),
            EnrollmentOutcome.Waitlisted => Results.Json(new EnrollResponse(
                "Waitlisted", learnerId, "Waitlisted", result.WaitlistSequence, result.PromotedLearnerIds ?? [],
                $"The course is full; learner '{learnerId}' joined the waitlist.")),
            EnrollmentOutcome.AlreadyRegistered => Error(
                StatusCodes.Status409Conflict, "AlreadyRegistered",
                $"Learner '{learnerId}' already has an active registration ({result.Status}); nothing was changed."),
            EnrollmentOutcome.PrerequisiteNotMet => Error(
                StatusCodes.Status422UnprocessableEntity, "PrerequisiteNotMet",
                $"Learner '{learnerId}' does not meet the prerequisite and cannot enroll or join the waitlist."),
            EnrollmentOutcome.LearnerNotFound => Error(
                StatusCodes.Status404NotFound, "LearnerNotFound", $"Learner '{learnerId}' does not exist."),
            EnrollmentOutcome.OfferingNotFound => Error(
                StatusCodes.Status404NotFound, "OfferingNotFound", "The course offering does not exist."),
            _ => Error(StatusCodes.Status400BadRequest, "InvalidRequest", BadBodyMessage)
        };
    }

    private static async Task<IResult> DropAsync(
        HttpRequest request,
        CourseSettings course,
        IRegistrationService service,
        CancellationToken cancellationToken)
    {
        var learnerId = await ReadLearnerIdAsync(request, cancellationToken);
        if (learnerId is null)
        {
            return Error(StatusCodes.Status400BadRequest, "InvalidRequest", BadBodyMessage);
        }

        var result = await service.DropAsync(course.Id, learnerId, cancellationToken);
        return result.Outcome switch
        {
            DropOutcome.DroppedAndPromoted => Results.Json(new DropResponse(
                "DroppedAndPromoted", learnerId, result.PromotedLearnerId, result.PromotedLearnerIds ?? [],
                $"Learner '{learnerId}' dropped; {string.Join(", ", (result.PromotedLearnerIds ?? []).Select(id => $"'{id}'"))} promoted from the waitlist.")),
            DropOutcome.DroppedSeatAvailable => Results.Json(new DropResponse(
                "DroppedSeatAvailable", learnerId, null, [],
                $"Learner '{learnerId}' dropped; the seat is open because no eligible learner is waiting.")),
            DropOutcome.DroppedFromWaitlist => Results.Json(new DropResponse(
                "DroppedFromWaitlist", learnerId, null, [],
                $"Learner '{learnerId}' left the waitlist; enrolled capacity is unchanged.")),
            // A repeated or unnecessary drop changes nothing and is reported as a conflict.
            DropOutcome.NotEnrolled => Error(
                StatusCodes.Status409Conflict, "NotEnrolled",
                $"Learner '{learnerId}' has no active registration; nothing was changed."),
            DropOutcome.LearnerNotFound => Error(
                StatusCodes.Status404NotFound, "LearnerNotFound", $"Learner '{learnerId}' does not exist."),
            DropOutcome.OfferingNotFound => Error(
                StatusCodes.Status404NotFound, "OfferingNotFound", "The course offering does not exist."),
            _ => Error(StatusCodes.Status400BadRequest, "InvalidRequest", BadBodyMessage)
        };
    }

    private static async Task<IResult> GetStateAsync(
        CourseSettings course,
        IRegistrationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetStateAsync(course.Id, cancellationToken);
        return result.Outcome == StateOutcome.Found
            ? Results.Json(result.State)
            : Error(StatusCodes.Status404NotFound, "OfferingNotFound", "The course offering does not exist.");
    }

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new ApiError(code, message), statusCode: statusCode);

    // Anything other than a JSON object with a non-blank string learnerId is treated as invalid input.
    private static async Task<string?> ReadLearnerIdAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            var body = await request.ReadFromJsonAsync<LearnerRequest>(cancellationToken);
            return string.IsNullOrWhiteSpace(body?.LearnerId) ? null : body.LearnerId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
