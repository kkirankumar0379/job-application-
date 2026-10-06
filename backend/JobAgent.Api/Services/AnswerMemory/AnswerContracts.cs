namespace JobAgent.Api.Services.Answers;

/// <summary>One question read off an application page (output of the page reader / question extractor).</summary>
public sealed record QuestionInput(string Id, string Text, string Type, List<string>? Options = null, string? Context = null, bool Required = false);

public sealed record ResolveRequest(string? PageUrl, string? Company, List<QuestionInput> Questions);

public sealed record Candidate(Guid MemoryId, string Question, string Answer, bool Preferred, DateTimeOffset UpdatedAt, int TimesUsed);

/// <param name="Status">auto = fill it; confirm = suggest and wait for the user; ask = no reliable answer, ask the user; conflict = stored answers disagree; skip = never remembered.</param>
public sealed record Resolution(
    string Id, string Status, int Confidence, string? Answer, string? MatchedQuestion, Guid? MemoryId,
    string Intent, string Reason, List<Candidate> Candidates);

public sealed record ResolveResponse(List<Resolution> Results);

public sealed record SaveRequest(string Question, string Answer, string? Type, List<string>? Options, string? SourceUrl, string? Company);

public sealed record SaveResult(Guid Id, bool Created, bool ReplacedAnswer, string? PreviousAnswer, bool Skipped, string Reason);

/// <param name="Origin">memory = filled automatically; confirmed = user accepted a suggestion; user = typed by the user; empty = no answer yet; existing = already filled when the page loaded.</param>
public sealed record ValidationItem(string Id, string Text, string Type, List<string>? Options, bool Required, string? Answer, string Origin, Guid? MemoryId);

public sealed record ValidateRequest(string? PageUrl, string? Company, List<ValidationItem> Items);

public sealed record ValidationLine(string Id, string Question, string? Answer, string Note);

/// <summary>The final pass before submitting: nothing may be guessed or low confidence.</summary>
public sealed record ValidationReport(
    List<ValidationLine> FromMemory, List<ValidationLine> NeedsConfirmation, List<ValidationLine> Unanswered,
    List<ValidationLine> Conflicts, bool CanSubmit);
