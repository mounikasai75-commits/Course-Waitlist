using CourseWaitlist.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CourseWaitlist.Api.Data;

public sealed class CourseWaitlistDbContext(DbContextOptions<CourseWaitlistDbContext> options)
    : DbContext(options)
{
    public DbSet<CourseOffering> Offerings => Set<CourseOffering>();

    public DbSet<Learner> Learners => Set<Learner>();

    public DbSet<Registration> Registrations => Set<Registration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CourseOffering>(offering =>
        {
            offering.HasKey(o => o.Id);
            offering.Property(o => o.Id).ValueGeneratedNever();
            offering.ToTable(t =>
            {
                t.HasCheckConstraint("CK_CourseOfferings_Capacity", "\"Capacity\" >= 0");
                t.HasCheckConstraint("CK_CourseOfferings_NextWaitlistSequence", "\"NextWaitlistSequence\" >= 1");
            });
        });

        modelBuilder.Entity<Learner>(learner =>
        {
            learner.HasKey(l => l.Id);
            learner.Property(l => l.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<Registration>(registration =>
        {
            registration.HasKey(r => r.Id);
            registration.Property(r => r.Id).ValueGeneratedOnAdd();
            registration.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);

            registration.HasOne(r => r.Offering)
                .WithMany(o => o.Registrations)
                .HasForeignKey(r => r.OfferingId)
                .OnDelete(DeleteBehavior.Restrict);

            registration.HasOne(r => r.Learner)
                .WithMany(l => l.Registrations)
                .HasForeignKey(r => r.LearnerId)
                .OnDelete(DeleteBehavior.Restrict);

            // One row per learner and offering, so a learner never holds two active registrations.
            registration.HasIndex(r => new { r.OfferingId, r.LearnerId }).IsUnique();

            // No duplicate queue positions per offering; SQLite allows many NULLs for non-waitlisted rows.
            registration.HasIndex(r => new { r.OfferingId, r.WaitlistSequence }).IsUnique();

            registration.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_Registrations_Status",
                    "\"Status\" IN ('Enrolled', 'Waitlisted', 'Dropped')");
                t.HasCheckConstraint(
                    "CK_Registrations_WaitlistSequence",
                    "(\"Status\" = 'Waitlisted' AND \"WaitlistSequence\" IS NOT NULL) " +
                    "OR (\"Status\" <> 'Waitlisted' AND \"WaitlistSequence\" IS NULL)");
            });
        });
    }
}
