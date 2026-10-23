namespace CourseWaitlist.Api.Domain;

public class Learner
{
    public required string Id { get; set; }

    public bool PrerequisiteSatisfied { get; set; }

    public List<Registration> Registrations { get; set; } = [];
}
