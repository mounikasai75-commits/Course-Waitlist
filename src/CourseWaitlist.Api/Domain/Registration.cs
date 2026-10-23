namespace CourseWaitlist.Api.Domain;

public class Registration
{
    public int Id { get; set; }

    public required string OfferingId { get; set; }

    public required string LearnerId { get; set; }

    public RegistrationStatus Status { get; set; }

    // Set only while Waitlisted; lower values are served first.
    public long? WaitlistSequence { get; set; }

    // UTC ticks when the learner joined the queue. Informational only: queue order is WaitlistSequence,
    // so a clock change can never affect FIFO. Meaningful only while Waitlisted.
    public long EnqueuedAtTicks { get; set; }

    public CourseOffering? Offering { get; set; }

    public Learner? Learner { get; set; }
}
