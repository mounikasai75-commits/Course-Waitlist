using CourseWaitlist.Api.Domain;

namespace CourseWaitlist.Api.Data;

public static class CourseSeeder
{
    // Idempotent: an existing offering or learner is left untouched.
    public static void Seed(CourseWaitlistDbContext context, CourseSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Id))
        {
            throw new InvalidOperationException("Course:Id must not be empty.");
        }

        if (settings.Capacity < 0)
        {
            throw new InvalidOperationException("Course:Capacity must not be negative.");
        }

        if (settings.Learners.Any(l => string.IsNullOrWhiteSpace(l.Id))
            || settings.Learners.Select(l => l.Id).Distinct().Count() != settings.Learners.Count)
        {
            throw new InvalidOperationException("Course:Learners must have unique, non-empty ids.");
        }

        if (!context.Offerings.Any(o => o.Id == settings.Id))
        {
            context.Offerings.Add(new CourseOffering { Id = settings.Id, Capacity = settings.Capacity });
        }

        var existing = context.Learners.Select(l => l.Id).ToHashSet();
        context.Learners.AddRange(settings.Learners
            .Where(l => !existing.Contains(l.Id))
            .Select(l => new Learner { Id = l.Id, PrerequisiteSatisfied = l.PrerequisiteSatisfied }));

        context.SaveChanges();
    }
}
