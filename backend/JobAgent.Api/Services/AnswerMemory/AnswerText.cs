namespace JobAgent.Api.Services.Answers;

/// <summary>Helpers for comparing answers: yes/no polarity and mapping a stored answer onto a question's options.</summary>
public static class AnswerText
{
    public static string Norm(string? s) =>
        string.Join(' ', (s ?? "").ToLowerInvariant().Replace('’', '\'').Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '(', ')', '"', '*'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>true = yes, false = no, null = not a yes/no answer ("Yes", "No, I do not require sponsorship").</summary>
    public static bool? YesNo(string? answer)
    {
        var a = Norm(answer);
        if (a is "yes" or "y" or "true" or "i do" or "i am" or "i will") return true;
        if (a is "no" or "n" or "false" or "i do not" or "i am not" or "i will not" or "i don't" or "none") return false;
        if (a.StartsWith("yes ")) return true;
        if (a.StartsWith("no ") || a.StartsWith("no,")) return false;
        return null;
    }

    public static string Flip(string answer) => YesNo(answer) switch { true => "No", false => "Yes", _ => answer };

    /// <summary>The option that best expresses the stored answer, or null when none clearly does (never guesses).</summary>
    public static string? PickOption(string answer, IReadOnlyList<string> options)
    {
        if (options.Count == 0) return answer;
        var a = Norm(answer);
        var exact = options.Where(o => Norm(o) == a).ToList();
        if (exact.Count == 1) return exact[0];
        if (YesNo(answer) is { } yes)
        {
            var same = options.Where(o => YesNo(o) == yes).ToList();
            if (same.Count == 1) return same[0];
        }
        var contains = options.Where(o => a.Length >= 3 && (Norm(o).Contains(a) || a.Contains(Norm(o)) && Norm(o).Length >= 3)).ToList();
        return contains.Count == 1 ? contains[0] : null;
    }
}
