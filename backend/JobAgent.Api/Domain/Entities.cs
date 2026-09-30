namespace JobAgent.Api.Domain;

public enum ApplicationStatus { Discovered, Reviewing, Prepared, NeedsUserInput, ReadyForApproval, Submitted, Skipped, Failed }

public sealed class CandidateProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Country { get; set; } = "US";
    public string LinkedInUrl { get; set; } = "";
    public string ResumePath { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SkillsCsv { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SavedAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public string Key { get; set; } = "";
    public string QuestionPattern { get; set; } = "";
    public string Answer { get; set; } = "";
    public bool IsSensitive { get; set; }
    public bool RequiresReviewEveryTime { get; set; }
}

public sealed class JobPosting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Company { get; set; } = "";
    public string Location { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string ApplyUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public int MatchScore { get; set; }
    public string MatchReason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JobApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobPostingId { get; set; }
    public Guid CandidateProfileId { get; set; }
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Discovered;
    public string AtsProvider { get; set; } = "Unknown";
    public string ResumePathUsed { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
