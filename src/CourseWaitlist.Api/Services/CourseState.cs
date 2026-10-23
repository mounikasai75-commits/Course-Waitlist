namespace CourseWaitlist.Api.Services;

public sealed record WaitlistEntry(int Position, string LearnerId, long Sequence, DateTimeOffset JoinedAtUtc);

public sealed record CourseState(
    string OfferingId,
    int Capacity,
    int EnrolledCount,
    int AvailableSeats,
    IReadOnlyList<string> Enrolled,
    IReadOnlyList<WaitlistEntry> Waitlist);

public enum StateOutcome
{
    Found,
    OfferingNotFound,
    InvalidRequest
}

public sealed record StateResult(StateOutcome Outcome, CourseState? State = null);
