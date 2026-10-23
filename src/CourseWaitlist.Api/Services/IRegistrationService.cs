namespace CourseWaitlist.Api.Services;

public interface IRegistrationService
{
    Task<EnrollmentResult> EnrollAsync(string offeringId, string learnerId, CancellationToken cancellationToken = default);

    Task<DropResult> DropAsync(string offeringId, string learnerId, CancellationToken cancellationToken = default);

    Task<StateResult> GetStateAsync(string offeringId, CancellationToken cancellationToken = default);
}
