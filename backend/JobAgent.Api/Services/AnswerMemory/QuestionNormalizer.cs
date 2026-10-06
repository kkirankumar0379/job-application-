using System.Text.RegularExpressions;

namespace JobAgent.Api.Services.Answers;

/// <summary>
/// Step 3 of the pipeline: turns a question into a normalized form so small wording differences don't matter.
/// "Do you require employment visa sponsorship now or in the future?" and "Will you now or in the future require
/// sponsorship?" both normalize to the same concept words ("require sponsorship future").
/// </summary>
public static partial class QuestionNormalizer
{
    // Phrases that mean one thing, replaced by a single concept word. Longest/most specific first.
    private static readonly (Regex Pattern, string Concept)[] Phrases =
    [
        (new(@"\b(legally )?(authori[sz]ed|eligible|permitted|allowed|legal right|right) to work\b|\bwork (authori[sz]ation|eligibility|permit)\b", RegexOptions.Compiled), " workauth "),
        (new(@"\b(employment |work )?visa\b|\bsponsor(ship|ed|ing)?\b|\bh-?1b\b|\bimmigration (support|sponsorship|assistance)\b|\bgreen card\b", RegexOptions.Compiled), " sponsorship "),
        (new(@"\b(now or )?(in|at any point in) the future\b|\bnow or later\b|\bcurrently or in the future\b|\bat any time\b|\bnow or in future\b", RegexOptions.Compiled), " future "),
        (new(@"\b(will|would|do|does) (you |the candidate )?(now )?(need|require)\b|\b(need|require[sd]?|requiring)\b", RegexOptions.Compiled), " require "),
        (new(@"\b(years?|yrs?)( of)?\b", RegexOptions.Compiled), " year "),
        (new(@"\b(hands[- ]on |professional(ly)? |work(ed|ing)? |experience[ds]?|worked|working)\b", RegexOptions.Compiled), " experience "),
        (new(@"\bat least 18\b|\b18 years\b|\b18 or older\b|\bover (the age of )?18\b|\blegal (working )?age\b", RegexOptions.Compiled), " age18 "),
        (new(@"\brelocat(e|ion|ing)\b|\bmove to\b", RegexOptions.Compiled), " relocate "),
        (new(@"\bsalary\b|\bcompensation\b|\bpay (rate|expectation)s?\b|\bhourly rate\b|\bexpected (pay|rate)\b", RegexOptions.Compiled), " salary "),
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a","an","the","do","does","did","you","your","yours","are","is","am","be","been","to","of","in","for","have","has","had","will","would",
        "can","could","should","may","might","now","or","and","with","any","please","if","that","this","our","we","us","i","me","my","it","on","at",
        "by","as","from","into","about","how","what","which","who","whether","are","there","than","then","so","such","other","more","most","some",
        "required","optional","select","choose","one","yes","no","answer","question",
    };

    /// <summary>Lowercase text with markup, required-markers and punctuation removed and wording variants collapsed to concept words.</summary>
    public static string Normalize(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return "";
        var t = Html().Replace(question, " ").ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');
        t = t.Replace("won't", "will not").Replace("can't", "cannot").Replace("don't", "do not").Replace("doesn't", "does not")
             .Replace("i'm", "i am").Replace("i've", "i have").Replace("c#/.net", "c# .net").Replace("c# / .net", "c# .net");
        t = Marker().Replace(t, " ");
        foreach (var (pattern, concept) in Phrases) t = pattern.Replace(t, concept);
        t = NonWord().Replace(t, " ");
        var tokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim('.'))
            .Where(x => x.Length > 0 && !StopWords.Contains(x))
            .Select(Stem);
        return string.Join(' ', tokens);
    }

    /// <summary>Whether the wording flips the usual yes/no reading ("work without sponsorship", "do not require").</summary>
    public static bool IsInverted(string? question) =>
        !string.IsNullOrWhiteSpace(question) && Inversion().IsMatch(question);

    public static string[] Tokens(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Cosine similarity of the two questions' concept words (0-1). Concept words weigh double.</summary>
    public static double Similarity(string normalizedA, string normalizedB)
    {
        var a = Vector(normalizedA);
        var b = Vector(normalizedB);
        if (a.Count == 0 || b.Count == 0) return 0;
        var dot = a.Where(kv => b.ContainsKey(kv.Key)).Sum(kv => kv.Value * b[kv.Key]);
        var norm = Math.Sqrt(a.Values.Sum(v => v * v)) * Math.Sqrt(b.Values.Sum(v => v * v));
        return norm == 0 ? 0 : dot / norm;
    }

    private static readonly HashSet<string> ConceptWords = new(StringComparer.Ordinal)
        { "workauth", "sponsorship", "future", "require", "year", "experience", "age18", "relocate", "salary" };

    private static Dictionary<string, double> Vector(string normalized)
    {
        var v = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var token in Tokens(normalized)) v[token] = v.GetValueOrDefault(token) + (ConceptWords.Contains(token) ? 2.0 : 1.0);
        return v;
    }

    // Light stemming so "locations"/"location", "applying"/"apply" compare equal.
    private static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ies")) return w[..^3] + "y";
        if (w.Length > 4 && w.EndsWith("es")) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss")) return w[..^1];
        return w;
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex Html();
    [GeneratedRegex(@"\(\s*(required|optional)\s*\)|\*|\brequired\b|\boptional\b|\[\s*required\s*\]")] private static partial Regex Marker();
    [GeneratedRegex(@"[^a-z0-9+#.]+")] private static partial Regex NonWord();
    [GeneratedRegex(@"\bwithout\b|\bnot (require|need)|\bdo(es)? not (require|need)|\bno need\b|\bnot (authori[sz]ed|eligible)\b|\bunable\b|\black\b|\bwithout (the )?need\b", RegexOptions.IgnoreCase)]
    private static partial Regex Inversion();
}
