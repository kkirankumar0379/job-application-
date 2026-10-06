namespace JobAgent.Api.Domain;

public enum ApplicationStatus { Discovered, Reviewing, Prepared, NeedsUserInput, ReadyForApproval, Submitted, Skipped, Failed }

/// <summary>A login. Each user owns their own profile(s), jobs, answers and tailored resumes; the first user is the admin.</summary>
public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CandidateProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // The login that owns this profile; null only for profiles created before logins existed (claimed by the first user).
    public Guid? UserId { get; set; }
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
    public int? YearsOfExperience { get; set; }
    // Plain text extracted from the uploaded resume; used for skill matching.
    public string ResumeText { get; set; } = "";
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
    // Set for jobs found by the daily discovery scan; empty for manually analyzed jobs.
    public Guid? CandidateProfileId { get; set; }
    // For jobs added by hand (no CandidateProfileId): the profile that added it, so other users never see it.
    public Guid? AddedByProfileId { get; set; }
    public string AtsProvider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public DateTimeOffset? PostedAt { get; set; }
    // The company's careers portal (all its openings), as opposed to ApplyUrl for this one job.
    public string CareersUrl { get; set; } = "";
    public bool IsRemote { get; set; }
    public bool IsDismissed { get; set; }
    public bool IsSaved { get; set; }
    public int SkillScore { get; set; }
    public int ExperienceScore { get; set; }
    public int TitleScore { get; set; }
    // How much the job is built on the candidate's primary stack (0-100).
    public int StackScore { get; set; }
    public int? RequiredYears { get; set; }
    public string MatchedSkillsCsv { get; set; } = "";
    public string MissingSkillsCsv { get; set; } = "";
}

public sealed class SearchPreferences
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public bool Enabled { get; set; } = true;
    public string RoleKeywordsCsv { get; set; } = "software engineer, software developer, full stack, backend, .net, c#";
    public string ExcludeKeywordsCsv { get; set; } = "manager, director, principal, intern";
    public string LocationsCsv { get; set; } = "remote, united states, usa, us";
    public bool RemoteOnly { get; set; }
    public string ExperienceLevel { get; set; } = "Any"; // Any | Entry | Mid | Senior
    public bool NeedsSponsorship { get; set; }
    // Skip federal-government / public-sector roles and jobs that require a security clearance.
    public bool ExcludeFederalJobs { get; set; } = true;
    // The candidate's primary stack: ranked first, and used for the "main stack" filter.
    public string PrimarySkillsCsv { get; set; } = "";
    // Employers whose jobs are never shown, from any source (company boards or job-search APIs).
    public string BlockedCompaniesCsv { get; set; } = DefaultBlockedCompanies;

    public const string DefaultBlockedCompanies =
        "Google, Alphabet, Meta, Facebook, Instagram, WhatsApp, Tesla, SpaceX, Amazon, AWS, Apple, Microsoft, Netflix, " +
        "NVIDIA, Salesforce, Adobe, Intel, OpenAI, Anthropic, Spotify, Airbnb, Stripe, Databricks, Snowflake, Pinterest, Reddit, " +
        "Lyft, Uber, DoorDash, Coinbase, Dropbox, Waymo, Twitch, Zoox, " +
        "Boeing, Lockheed Martin, Northrop Grumman, Raytheon, RTX, General Dynamics, L3Harris, BAE Systems, Leidos, SAIC, CACI, " +
        "Booz Allen Hamilton, Palantir, Anduril, Shield AI, Relativity Space, Peraton, ManTech, GDIT";
    public int MaxAgeHours { get; set; } = 24;
    public int ScanEveryHours { get; set; } = 3;
    public DateTimeOffset? LastRunAt { get; set; }
}

public sealed class CompanySource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string AtsProvider { get; set; } = ""; // Greenhouse | Lever | Ashby | SmartRecruiters | Workday
    // Board slug, or for Workday "host/site" (e.g. "nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite").
    public string BoardToken { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastScannedAt { get; set; }
    public string LastError { get; set; } = "";
    // Open jobs found on the board/site at the last scan (before any filtering); null until first scanned.
    public int? LastJobCount { get; set; }
}

/// <summary>A resume rewritten for one job; the .docx is what "Autofill & apply" uploads for that job.</summary>
public sealed class TailoredResume
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public Guid JobPostingId { get; set; }
    public string ContentJson { get; set; } = "";
    public string ConfirmedSkillsCsv { get; set; } = "";
    public int AtsScoreBefore { get; set; }
    public int AtsScoreAfter { get; set; }
    public string FilePath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DiscoveryRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int CompaniesScanned { get; set; }
    public int CompaniesFailed { get; set; }
    public int JobsSeen { get; set; }
    public int JobsMatched { get; set; }
    public int JobsSaved { get; set; }
    public string Error { get; set; } = "";
    // Per-step counts explaining the result (JSON); see ScanFunnel.
    public string FunnelJson { get; set; } = "";
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

/// <summary>
/// One question the user answered on an application, kept so the same (or a differently worded) question can be answered
/// from memory next time. See Services/AnswerMemory.
/// </summary>
public sealed class AnswerMemory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateProfileId { get; set; }
    public string OriginalQuestion { get; set; } = "";
    public string NormalizedQuestion { get; set; } = "";
    // What the question is really asking (e.g. "sponsorship", "years_experience"); empty when no known intent applies.
    public string Intent { get; set; } = "";
    // For per-skill questions ("years of C#"): the skills asked about, sorted, comma separated.
    public string Subject { get; set; } = "";
    // True when "yes" means the opposite of the usual reading ("Can you work without sponsorship?").
    public bool Inverted { get; set; }
    public string Answer { get; set; } = "";
    public string AnswerType { get; set; } = "text"; // text | textarea | choice | yesno | number | multi
    public string OptionsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
    public int TimesUsed { get; set; }
    // Where it was first answered.
    public string SourceCompany { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    // Preferred = the answer the user approved last; Superseded = replaced by a newer approved answer (kept for history).
    public string Status { get; set; } = "Preferred"; // Preferred | Superseded
    // Reliability: UserAnswered (typed or confirmed by the user).
    public string Reliability { get; set; } = "UserAnswered";
}
