using JobAgent.Api.Domain;

namespace JobAgent.Api.Services.Answers;

/// <summary>A stored answer scored against the current question. Score is 0-100.</summary>
public sealed record MemoryMatch(AnswerMemory Record, int Score, string Basis, bool SubjectPartial);

/// <summary>
/// Similarity / confidence engine. Compares the current question with stored ones using, in order: exact text,
/// normalized text, the question's intent (and subject skill), then concept-word similarity. Questions with
/// different known intents never match, however similar their words are.
/// </summary>
public static class AnswerMatcher
{
    public static List<MemoryMatch> Match(string questionText, IEnumerable<AnswerMemory> records)
    {
        var normalized = QuestionNormalizer.Normalize(questionText);
        var intent = QuestionIntentClassifier.Classify(questionText);
        var wantedSubject = QuestionIntentClassifier.SubjectList(intent.Subject);
        var matches = new List<MemoryMatch>();

        foreach (var rec in records.Where(r => r.Status == "Preferred"))
        {
            if (AnswerText.Norm(rec.OriginalQuestion) == AnswerText.Norm(questionText)) { matches.Add(new(rec, 100, "exact wording", false)); continue; }
            if (normalized.Length > 0 && rec.NormalizedQuestion == normalized) { matches.Add(new(rec, 97, "same question, different wording", false)); continue; }

            var similarity = QuestionNormalizer.Similarity(normalized, rec.NormalizedQuestion);
            if (intent.Intent != "" && rec.Intent != "")
            {
                if (intent.Intent != rec.Intent) continue; // related but different questions: separate answers
                if (intent.Intent is "years_experience" or "skill_experience")
                {
                    var have = QuestionIntentClassifier.SubjectList(rec.Subject);
                    if (have.Length == 0 != (wantedSubject.Length == 0)) continue; // "total experience" vs "experience with C#"
                    if (have.Length > 0)
                    {
                        var common = have.Intersect(wantedSubject, StringComparer.OrdinalIgnoreCase).Count();
                        if (common == 0) continue;
                        if (common < Math.Max(have.Length, wantedSubject.Length)) { matches.Add(new(rec, 78, "overlapping skills", true)); continue; }
                    }
                }
                matches.Add(new(rec, (int)Math.Round(92 + 5 * similarity), "same intent", false));
            }
            else if (intent.Intent == "" && rec.Intent == "")
            {
                // No known meaning for either: wording similarity alone can only ever suggest, never auto-fill.
                if (similarity >= 0.92) matches.Add(new(rec, 80, "very similar wording", false));
                else if (similarity >= 0.8) matches.Add(new(rec, 62, "similar wording", false));
            }
            // One side recognized and the other not: not treated as the same question.
        }
        return matches.OrderByDescending(m => m.Score).ThenByDescending(m => m.Record.UpdatedAt).ThenByDescending(m => m.Record.TimesUsed).ToList();
    }

    /// <summary>The stored answer re-expressed for the current question's yes/no reading.</summary>
    public static string AnswerFor(AnswerMemory rec, bool currentInverted) =>
        rec.Inverted != currentInverted && AnswerText.YesNo(rec.Answer) is not null ? AnswerText.Flip(rec.Answer) : rec.Answer;

    /// <summary>The answer in the standard reading (yes = the usual meaning), used to tell whether two answers really differ.</summary>
    public static string Canonical(AnswerMemory rec) => AnswerText.Norm(rec.Inverted && AnswerText.YesNo(rec.Answer) is not null ? AnswerText.Flip(rec.Answer) : rec.Answer);
}
