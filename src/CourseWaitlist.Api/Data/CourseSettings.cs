namespace CourseWaitlist.Api.Data;

// The single course offering and its known learners; there are no endpoints to create or edit them.
public sealed class CourseSettings
{
    public string Id { get; set; } = "course-101";

    public int Capacity { get; set; } = 2;

    public List<LearnerSeed> Learners { get; set; } = [];
}

public sealed class LearnerSeed
{
    public string Id { get; set; } = "";

    public bool PrerequisiteSatisfied { get; set; } = true;
}
