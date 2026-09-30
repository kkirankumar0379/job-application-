namespace JobAgent.Api.Contracts;

public sealed record AnalyzeJobRequest(string Title, string Company, string Location, string ApplyUrl, string Description, Guid CandidateProfileId);
public sealed record StartAutomationRequest(string ApplyUrl, Guid CandidateProfileId, Guid? JobPostingId);
public sealed record SubmitAutomationRequest(bool Approved);
public sealed record MatchResult(int Score, string Decision, IReadOnlyList<string> MatchedSkills, IReadOnlyList<string> MissingSkills, string Reason);
public sealed record AutomationSessionView(Guid SessionId, string ApplyUrl, string AtsProvider, string Status, IReadOnlyList<string> FilledFields, IReadOnlyList<string> UnknownFields, string? Message);
