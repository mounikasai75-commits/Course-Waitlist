using CourseWaitlist.Api.Domain;

namespace CourseWaitlist.Api.Services;

public enum EnrollmentOutcome
{
    Enrolled,
    Waitlisted,
    AlreadyRegistered,
    PrerequisiteNotMet,
    OfferingNotFound,
    LearnerNotFound,
    InvalidRequest
}

public sealed record EnrollmentResult(
    EnrollmentOutcome Outcome,
    RegistrationStatus? Status = null,
    long? WaitlistSequence = null,
    IReadOnlyList<string>? PromotedLearnerIds = null)
{
    public bool Succeeded => Outcome is EnrollmentOutcome.Enrolled or EnrollmentOutcome.Waitlisted;
}

public enum DropOutcome
{
    DroppedAndPromoted,
    DroppedSeatAvailable,
    DroppedFromWaitlist,
    NotEnrolled,
    OfferingNotFound,
    LearnerNotFound,
    InvalidRequest
}

public sealed record DropResult(DropOutcome Outcome, IReadOnlyList<string>? PromotedLearnerIds = null)
{
    // Earliest promoted learner; a drop normally promotes one, more only if earlier seats were left open.
    public string? PromotedLearnerId => PromotedLearnerIds?.FirstOrDefault();

    public bool Succeeded => Outcome is DropOutcome.DroppedAndPromoted
        or DropOutcome.DroppedSeatAvailable
        or DropOutcome.DroppedFromWaitlist;
}
