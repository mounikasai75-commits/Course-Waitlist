using CourseWaitlist.Api.Data;
using CourseWaitlist.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CourseWaitlist.Api.Services;

public sealed class RegistrationService(
    CourseWaitlistDbContext context,
    SemaphoreSlim gate,
    TimeProvider? timeProvider = null) : IRegistrationService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<EnrollmentResult> EnrollAsync(
        string offeringId,
        string learnerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(offeringId) || string.IsNullOrWhiteSpace(learnerId))
        {
            return new EnrollmentResult(EnrollmentOutcome.InvalidRequest);
        }

        // The gate serializes every use of the shared connection and this scoped DbContext for the whole operation.
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Tracked entities from an earlier call on a reused context could be stale; always read current rows.
            context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var offering = await context.Offerings
                .SingleOrDefaultAsync(o => o.Id == offeringId, cancellationToken);
            if (offering is null)
            {
                return new EnrollmentResult(EnrollmentOutcome.OfferingNotFound);
            }

            var learner = await context.Learners
                .SingleOrDefaultAsync(l => l.Id == learnerId, cancellationToken);
            if (learner is null)
            {
                return new EnrollmentResult(EnrollmentOutcome.LearnerNotFound);
            }

            var registration = await context.Registrations
                .SingleOrDefaultAsync(r => r.OfferingId == offeringId && r.LearnerId == learnerId, cancellationToken);
            if (registration is { Status: RegistrationStatus.Enrolled or RegistrationStatus.Waitlisted })
            {
                return new EnrollmentResult(
                    EnrollmentOutcome.AlreadyRegistered,
                    registration.Status,
                    registration.WaitlistSequence);
            }

            if (!learner.PrerequisiteSatisfied)
            {
                return new EnrollmentResult(EnrollmentOutcome.PrerequisiteNotMet);
            }

            var enrolledCount = await context.Registrations
                .CountAsync(r => r.OfferingId == offeringId && r.Status == RegistrationStatus.Enrolled, cancellationToken);
            var freeSeats = Math.Max(0, offering.Capacity - enrolledCount);

            // Older eligible waiters are served first. Ineligible waiters keep their place but never have priority.
            var promoted = freeSeats == 0
                ? []
                : await context.Registrations
                    .Where(r => r.OfferingId == offeringId
                        && r.Status == RegistrationStatus.Waitlisted
                        && r.Learner!.PrerequisiteSatisfied)
                    .OrderBy(r => r.WaitlistSequence)
                    .Take(freeSeats)
                    .ToListAsync(cancellationToken);

            foreach (var waiter in promoted)
            {
                waiter.Status = RegistrationStatus.Enrolled;
                waiter.WaitlistSequence = null;
            }

            // A dropped learner's row is reused, preserving the one-row-per-learner-and-offering constraint.
            if (registration is null)
            {
                registration = new Registration { OfferingId = offeringId, LearnerId = learnerId };
                context.Registrations.Add(registration);
            }

            if (freeSeats > promoted.Count)
            {
                registration.Status = RegistrationStatus.Enrolled;
                registration.WaitlistSequence = null;
            }
            else
            {
                registration.Status = RegistrationStatus.Waitlisted;
                registration.WaitlistSequence = offering.NextWaitlistSequence++;
                registration.EnqueuedAtTicks = _time.GetUtcNow().UtcTicks;
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new EnrollmentResult(
                registration.Status == RegistrationStatus.Enrolled
                    ? EnrollmentOutcome.Enrolled
                    : EnrollmentOutcome.Waitlisted,
                registration.Status,
                registration.WaitlistSequence,
                promoted.Select(w => w.LearnerId).ToList());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DropResult> DropAsync(
        string offeringId,
        string learnerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(offeringId) || string.IsNullOrWhiteSpace(learnerId))
        {
            return new DropResult(DropOutcome.InvalidRequest);
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            context.ChangeTracker.Clear();

            // Disposing without commit rolls back, so a failure never leaves a drop without its promotion.
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var offering = await context.Offerings
                .SingleOrDefaultAsync(o => o.Id == offeringId, cancellationToken);
            if (offering is null)
            {
                return new DropResult(DropOutcome.OfferingNotFound);
            }

            var learnerExists = await context.Learners.AnyAsync(l => l.Id == learnerId, cancellationToken);
            if (!learnerExists)
            {
                return new DropResult(DropOutcome.LearnerNotFound);
            }

            var registration = await context.Registrations
                .SingleOrDefaultAsync(r => r.OfferingId == offeringId && r.LearnerId == learnerId, cancellationToken);

            // Unregistered and already-dropped learners are the same no-op, which makes repeated drops safe.
            if (registration is null || registration.Status == RegistrationStatus.Dropped)
            {
                return new DropResult(DropOutcome.NotEnrolled);
            }

            var wasEnrolled = registration.Status == RegistrationStatus.Enrolled;
            registration.Status = RegistrationStatus.Dropped;
            registration.WaitlistSequence = null;

            // Leaving the queue frees no seat, so other waiters keep their sequences and nobody is promoted.
            if (!wasEnrolled)
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new DropResult(DropOutcome.DroppedFromWaitlist);
            }

            // The dropped row is still tracked as Enrolled in the database until saved, so exclude it explicitly.
            var enrolledCount = await context.Registrations
                .CountAsync(
                    r => r.OfferingId == offeringId
                        && r.Status == RegistrationStatus.Enrolled
                        && r.Id != registration.Id,
                    cancellationToken);

            var freeSeats = Math.Max(0, offering.Capacity - enrolledCount);

            // Fill every open seat, not just the one this drop freed: earlier drops may have left seats open
            // while all waiters were ineligible. Ineligible waiters are filtered out of the read, never modified.
            var promoted = freeSeats == 0
                ? []
                : await context.Registrations
                    .Where(r => r.OfferingId == offeringId
                        && r.Status == RegistrationStatus.Waitlisted
                        && r.Learner!.PrerequisiteSatisfied)
                    .OrderBy(r => r.WaitlistSequence)
                    .Take(freeSeats)
                    .ToListAsync(cancellationToken);

            foreach (var waiter in promoted)
            {
                waiter.Status = RegistrationStatus.Enrolled;
                waiter.WaitlistSequence = null;
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return promoted.Count == 0
                ? new DropResult(DropOutcome.DroppedSeatAvailable)
                : new DropResult(DropOutcome.DroppedAndPromoted, promoted.Select(w => w.LearnerId).ToList());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StateResult> GetStateAsync(string offeringId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(offeringId))
        {
            return new StateResult(StateOutcome.InvalidRequest);
        }

        // Reads use the same shared connection, so they take the gate too; every writer holds it, so this is a consistent snapshot.
        await gate.WaitAsync(cancellationToken);
        try
        {
            context.ChangeTracker.Clear();

            var offering = await context.Offerings
                .AsNoTracking()
                .SingleOrDefaultAsync(o => o.Id == offeringId, cancellationToken);
            if (offering is null)
            {
                return new StateResult(StateOutcome.OfferingNotFound);
            }

            var enrolled = await context.Registrations
                .AsNoTracking()
                .Where(r => r.OfferingId == offeringId && r.Status == RegistrationStatus.Enrolled)
                .OrderBy(r => r.Id)
                .Select(r => r.LearnerId)
                .ToListAsync(cancellationToken);

            var waiting = await context.Registrations
                .AsNoTracking()
                .Where(r => r.OfferingId == offeringId && r.Status == RegistrationStatus.Waitlisted)
                .OrderBy(r => r.WaitlistSequence)
                .Select(r => new { r.LearnerId, Sequence = r.WaitlistSequence!.Value, r.EnqueuedAtTicks })
                .ToListAsync(cancellationToken);

            var waitlist = waiting
                .Select((w, index) => new WaitlistEntry(
                    index + 1, w.LearnerId, w.Sequence, new DateTimeOffset(w.EnqueuedAtTicks, TimeSpan.Zero)))
                .ToList();

            return new StateResult(
                StateOutcome.Found,
                new CourseState(
                    offering.Id,
                    offering.Capacity,
                    enrolled.Count,
                    Math.Max(0, offering.Capacity - enrolled.Count),
                    enrolled,
                    waitlist));
        }
        finally
        {
            gate.Release();
        }
    }
}
