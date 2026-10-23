using CourseWaitlist.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CourseWaitlist.Tests;

public sealed class PersistenceTests : IDisposable
{
    private const string OfferingId = "offering-1";

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private void Seed(int capacity = 2, params string[] learnerIds)
    {
        using var context = _database.CreateContext();
        context.Offerings.Add(new CourseOffering { Id = OfferingId, Capacity = capacity });
        context.Learners.AddRange(learnerIds.Select(id => new Learner { Id = id, PrerequisiteSatisfied = true }));
        context.SaveChanges();
    }

    private void AddRegistration(string learnerId, RegistrationStatus status, long? sequence = null)
    {
        using var context = _database.CreateContext();
        context.Registrations.Add(new Registration
        {
            OfferingId = OfferingId,
            LearnerId = learnerId,
            Status = status,
            WaitlistSequence = sequence
        });
        context.SaveChanges();
    }

    private static SqliteException ConstraintViolation(Action action)
    {
        var exception = Assert.Throws<DbUpdateException>(action);
        var sqlite = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(19, sqlite.SqliteErrorCode);
        return sqlite;
    }

    [Fact]
    public void Entities_round_trip_with_relationships()
    {
        Seed(3, "ada");
        AddRegistration("ada", RegistrationStatus.Enrolled);

        using var context = _database.CreateContext();
        var registration = context.Registrations
            .Include(r => r.Offering)
            .Include(r => r.Learner)
            .Single();

        Assert.Equal(RegistrationStatus.Enrolled, registration.Status);
        Assert.Null(registration.WaitlistSequence);
        Assert.Equal(3, registration.Offering!.Capacity);
        Assert.Equal(1, registration.Offering.NextWaitlistSequence);
        Assert.True(registration.Learner!.PrerequisiteSatisfied);
    }

    [Fact]
    public void Status_is_stored_as_text()
    {
        Seed(1, "ada");
        AddRegistration("ada", RegistrationStatus.Dropped);

        using var context = _database.CreateContext();
        var status = context.Database
            .SqlQueryRaw<string>("SELECT \"Status\" AS \"Value\" FROM \"Registrations\"")
            .Single();

        Assert.Equal("Dropped", status);
    }

    [Fact]
    public void Data_persists_across_separate_contexts_on_the_same_connection()
    {
        Seed(1, "ada");

        using var first = _database.CreateContext();
        using var second = _database.CreateContext();

        Assert.Equal("ada", first.Learners.Single().Id);
        Assert.Equal("ada", second.Learners.Single().Id);
    }

    [Fact]
    public void Each_test_database_is_isolated()
    {
        Seed(1, "ada");

        using var other = new TestDatabase();
        using var otherContext = other.CreateContext();

        Assert.Empty(otherContext.Offerings);
        Assert.Empty(otherContext.Learners);
    }

    [Fact]
    public void Learner_can_have_only_one_registration_row_per_offering()
    {
        Seed(2, "ada");
        AddRegistration("ada", RegistrationStatus.Enrolled);

        ConstraintViolation(() => AddRegistration("ada", RegistrationStatus.Dropped));

        using var context = _database.CreateContext();
        Assert.Equal(1, context.Registrations.Count());
    }

    [Fact]
    public void Waitlist_sequence_must_be_unique_within_an_offering()
    {
        Seed(1, "ada", "grace");
        AddRegistration("ada", RegistrationStatus.Waitlisted, 1);

        ConstraintViolation(() => AddRegistration("grace", RegistrationStatus.Waitlisted, 1));
    }

    [Fact]
    public void Non_waitlisted_rows_may_share_a_null_sequence()
    {
        Seed(2, "ada", "grace", "linus");
        AddRegistration("ada", RegistrationStatus.Enrolled);
        AddRegistration("grace", RegistrationStatus.Enrolled);
        AddRegistration("linus", RegistrationStatus.Dropped);

        using var context = _database.CreateContext();
        Assert.Equal(3, context.Registrations.Count());
    }

    [Fact]
    public void Waitlisted_row_requires_a_sequence()
    {
        Seed(1, "ada");

        ConstraintViolation(() => AddRegistration("ada", RegistrationStatus.Waitlisted));
    }

    [Theory]
    [InlineData(RegistrationStatus.Enrolled)]
    [InlineData(RegistrationStatus.Dropped)]
    public void Non_waitlisted_row_cannot_have_a_sequence(RegistrationStatus status)
    {
        Seed(1, "ada");

        ConstraintViolation(() => AddRegistration("ada", status, 1));
    }

    [Fact]
    public void Registration_requires_an_existing_offering_and_learner()
    {
        Seed(1, "ada");

        ConstraintViolation(() => AddRegistration("missing-learner", RegistrationStatus.Enrolled));

        using var context = _database.CreateContext();
        context.Registrations.Add(new Registration
        {
            OfferingId = "missing-offering",
            LearnerId = "ada",
            Status = RegistrationStatus.Enrolled
        });
        ConstraintViolation(() => context.SaveChanges());
    }

    [Fact]
    public void Offering_cannot_be_deleted_while_registrations_reference_it()
    {
        Seed(1, "ada");
        AddRegistration("ada", RegistrationStatus.Enrolled);

        using var context = _database.CreateContext();
        context.Offerings.Remove(context.Offerings.Single());

        ConstraintViolation(() => context.SaveChanges());
    }

    [Fact]
    public void Capacity_cannot_be_negative()
    {
        using var context = _database.CreateContext();
        context.Offerings.Add(new CourseOffering { Id = OfferingId, Capacity = -1 });

        ConstraintViolation(() => context.SaveChanges());
    }

    [Fact]
    public void Next_waitlist_sequence_must_be_positive()
    {
        using var context = _database.CreateContext();
        context.Offerings.Add(new CourseOffering { Id = OfferingId, Capacity = 1, NextWaitlistSequence = 0 });

        ConstraintViolation(() => context.SaveChanges());
    }

    [Fact]
    public void Waitlist_orders_by_sequence_not_by_insertion_order()
    {
        Seed(1, "ada", "grace", "linus");
        AddRegistration("linus", RegistrationStatus.Waitlisted, 3);
        AddRegistration("ada", RegistrationStatus.Waitlisted, 1);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 2);

        using var context = _database.CreateContext();
        var queue = context.Registrations
            .Where(r => r.OfferingId == OfferingId && r.Status == RegistrationStatus.Waitlisted)
            .OrderBy(r => r.WaitlistSequence)
            .Select(r => r.LearnerId)
            .ToList();

        Assert.Equal(["ada", "grace", "linus"], queue);
    }

    [Fact]
    public void Sequence_is_retained_when_a_waiter_is_ineligible_and_order_is_unchanged()
    {
        Seed(1, "ada", "grace");
        AddRegistration("ada", RegistrationStatus.Waitlisted, 1);
        AddRegistration("grace", RegistrationStatus.Waitlisted, 2);

        using (var context = _database.CreateContext())
        {
            context.Learners.Single(l => l.Id == "ada").PrerequisiteSatisfied = false;
            context.SaveChanges();
        }

        using var verify = _database.CreateContext();
        var queue = verify.Registrations
            .OrderBy(r => r.WaitlistSequence)
            .Select(r => new { r.LearnerId, r.WaitlistSequence })
            .ToList();

        Assert.Equal([("ada", 1L), ("grace", 2L)], queue.Select(q => (q.LearnerId, q.WaitlistSequence!.Value)));
    }
}
