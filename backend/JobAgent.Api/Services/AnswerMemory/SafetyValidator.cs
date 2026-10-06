using JobAgent.Api.Domain;

namespace JobAgent.Api.Services.Answers;

/// <summary>
/// Safety and context validator: decides whether the best match may be filled automatically, must be confirmed,
/// or must be asked. Nothing job-specific, legal, demographic or out of date is ever filled without the user.
/// </summary>
public static class SafetyValidator
{
    public const int HighConfidence = 90;
    public const int MediumConfidence = 70;

    public sealed record Decision(string Status, string Reason, string? Answer);

    public static Decision Decide(QuestionInput q, QuestionIntent intent, MemoryMatch best, DateTimeOffset now)
    {
        var inverted = QuestionNormalizer.IsInverted(q.Text);
        var answer = AnswerMatcher.AnswerFor(best.Record, inverted);
        var flipped = !string.Equals(answer, best.Record.Answer, StringComparison.Ordinal);

        // The stored answer has to be something this question can actually accept.
        if (q.Options is { Count: > 0 } options)
        {
            var option = AnswerText.PickOption(answer, options);
            if (option is null) return new("ask", $"Your saved answer \"{best.Record.Answer}\" isn't one of the choices here.", null);
            answer = option;
        }

        if (intent.Policy == ReusePolicy.Never) return new("skip", intent.Reason, null);
        if (intent.Policy == ReusePolicy.Confirm) return new(best.Score >= MediumConfidence ? "confirm" : "ask", intent.Reason, best.Score >= MediumConfidence ? answer : null);
        if (best.Score < MediumConfidence) return new("ask", "Not similar enough to a question you've answered.", null);
        if (best.Score < HighConfidence) return new("confirm", best.SubjectPartial ? "Covers some, not all, of the skills asked about." : $"Possible match ({best.Basis}).", answer);
        if (flipped) return new("confirm", "This question is worded the opposite way from the one you answered, so the answer was reversed.", answer);

        if (intent.Policy == ReusePolicy.Expiring && intent.ExpiresAfterDays > 0 && now - best.Record.UpdatedAt > TimeSpan.FromDays(intent.ExpiresAfterDays))
            return new("confirm", $"You answered this over {intent.ExpiresAfterDays} days ago, so it may be out of date.", answer);

        return new("auto", $"Same as a question you answered before ({best.Basis}).", answer);
    }
}
