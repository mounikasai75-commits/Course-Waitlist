using CourseWaitlist.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CourseWaitlist.Tests;

public static class DatabaseInvariants
{
    // Inspects the committed state through a fresh context; call only after all operations have finished.
    public static void AssertConsistent(TestDatabase database)
    {
        using var context = database.CreateContext();
        var offering = context.Offerings.AsNoTracking().Single();
        var registrations = context.Registrations.AsNoTracking().Include(r => r.Learner).ToList();

        Assert.Equal(
            registrations.Count,
            registrations.Select(r => (r.OfferingId, r.LearnerId)).Distinct().Count());

        var enrolled = registrations.Count(r => r.Status == RegistrationStatus.Enrolled);
        Assert.True(enrolled <= offering.Capacity, $"{enrolled} enrolled exceeds capacity {offering.Capacity}.");

        var waiting = registrations.Where(r => r.Status == RegistrationStatus.Waitlisted).ToList();
        Assert.All(waiting, r => Assert.NotNull(r.WaitlistSequence));
        Assert.Equal(waiting.Count, waiting.Select(r => r.WaitlistSequence).Distinct().Count());
        Assert.All(
            registrations.Where(r => r.Status != RegistrationStatus.Waitlisted),
            r => Assert.Null(r.WaitlistSequence));

        if (waiting.Count > 0)
        {
            Assert.True(offering.NextWaitlistSequence > waiting.Max(r => r.WaitlistSequence));
        }

        // A free seat must never coexist with an eligible waiter once every operation has completed.
        if (enrolled < offering.Capacity)
        {
            Assert.DoesNotContain(waiting, r => r.Learner!.PrerequisiteSatisfied);
        }
    }
}
