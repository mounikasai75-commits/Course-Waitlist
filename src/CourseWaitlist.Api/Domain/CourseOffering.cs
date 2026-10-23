namespace CourseWaitlist.Api.Domain;

public class CourseOffering
{
    public required string Id { get; set; }

    public int Capacity { get; set; }

    // Monotonic, never reused: promoted or dropped learners clear their sequence, so it cannot be derived from current rows.
    public long NextWaitlistSequence { get; set; } = 1;

    public List<Registration> Registrations { get; set; } = [];
}
