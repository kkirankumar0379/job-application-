using System.Text.RegularExpressions;

namespace JobAgent.Api.Services;

public sealed record AtsReport(
    int Score, int KeywordScore, int TitleScore, int SectionScore, int ContactScore,
    IReadOnlyList<string> MatchedKeywords, IReadOnlyList<string> MissingKeywords,
    IReadOnlyList<string> MissingSections, string TargetTitle);

/// <summary>
/// Transparent ATS-style score of a resume against a job posting: job-description keywords present (70%),
/// target title present (10%), standard sections (15%) and contact details (5%). Real ATS products differ,
/// but they all weight keyword coverage and parseable standard sections heavily.
/// </summary>
public static partial class AtsScorer
{
    private static readonly (string Name, Regex Pattern)[] Sections =
    [
        ("Summary", new Regex(@"^\s*(professional\s+|career\s+)?(summary|profile|objective|about me)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)),
        ("Skills", new Regex(@"^\s*(technical\s+|core\s+|key\s+)?(skills|competencies|technologies)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)),
        ("Experience", new Regex(@"^\s*(professional\s+|work\s+|relevant\s+)?(experience|employment|work history)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)),
        ("Education", new Regex(@"^\s*(education|academic)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)),
    ];

    public static AtsReport Score(string resumeText, string jobTitle, string jobDescription)
    {
        var keywords = SkillCatalog.Extract(jobTitle + "\n" + jobDescription);
        var have = SkillCatalog.Extract(resumeText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = keywords.Where(have.Contains).ToList();
        var missing = keywords.Where(k => !have.Contains(k)).ToList();
        var keywordScore = keywords.Count == 0 ? 100 : (int)Math.Round(100.0 * matched.Count / keywords.Count);

        var target = CoreTitle(jobTitle);
        var titleScore = TitleScore(resumeText, target);

        var missingSections = Sections.Where(s => !s.Pattern.IsMatch(resumeText)).Select(s => s.Name).ToList();
        var sectionScore = (int)Math.Round(100.0 * (Sections.Length - missingSections.Count) / Sections.Length);

        var contactScore = (Email().IsMatch(resumeText) ? 50 : 0) + (Phone().IsMatch(resumeText) ? 50 : 0);

        var score = (int)Math.Round(0.70 * keywordScore + 0.10 * titleScore + 0.15 * sectionScore + 0.05 * contactScore);
        return new AtsReport(score, keywordScore, titleScore, sectionScore, contactScore, matched, missing, missingSections, target);
    }

    /// <summary>"Senior Software Engineer II, Payments (Remote)" → "Software Engineer".</summary>
    public static string CoreTitle(string title)
    {
        var t = Regex.Replace(title, @"\(.*?\)|\[.*?\]", " ");
        t = Regex.Split(t, @"\s[-–—|/]\s|,|:")[0];
        t = Regex.Replace(t, @"\b(senior|sr\.?|junior|jr\.?|lead|staff|principal|associate|mid[- ]level|entry[- ]level|[IV]{1,3}|[1-4])\b", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static int TitleScore(string resume, string target)
    {
        if (target.Length == 0) return 100;
        if (resume.Contains(target, StringComparison.OrdinalIgnoreCase)) return 100;
        var words = target.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToList();
        if (words.Count == 0) return 100;
        var found = words.Count(w => Regex.IsMatch(resume, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase));
        return (int)Math.Round(100.0 * found / words.Count * 0.7);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"(\+?\d{1,3}[\s.\-]?)?\(?\d{3}\)?[\s.\-]?\d{3}[\s.\-]?\d{4}")] private static partial Regex Phone();
}
