namespace JobAgent.Api.Contracts;

public sealed record AnalyzeJobRequest(string Title, string Company, string Location, string ApplyUrl, string Description, Guid CandidateProfileId);
public sealed record StartAutomationRequest(string ApplyUrl, Guid CandidateProfileId, Guid? JobPostingId);
public sealed record SubmitAutomationRequest(bool Approved);
public sealed record AddCompanyRequest(string CareersUrl, string? Name);
public sealed record SaveApiKeyRequest(string? ApiKey);
public sealed record TailorRequest(List<string>? ConfirmedSkills);
public sealed record AdzunaKeysRequest(string? AppId, string? AppKey);
public sealed record MatchResult(int Score, string Decision, IReadOnlyList<string> MatchedSkills, IReadOnlyList<string> MissingSkills, string Reason, int SkillScore, int ExperienceScore, int? RequiredYears, int TitleScore, int StackScore);
public sealed record AutomationSessionView(Guid SessionId, string ApplyUrl, string AtsProvider, string Status, IReadOnlyList<string> FilledFields, IReadOnlyList<string> UnknownFields, string? Message);
public sealed record RegisterRequest(string? Email, string? Password, string? FirstName, string? LastName, string? InviteCode);
public sealed record LoginRequest(string? Email, string? Password);
