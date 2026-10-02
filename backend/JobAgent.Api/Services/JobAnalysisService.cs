using System.Text.RegularExpressions;
using JobAgent.Api.Contracts;
using JobAgent.Api.Domain;

namespace JobAgent.Api.Services;

public interface IJobAnalysisService
{
    /// <param name="primarySkills">The candidate's main stack (may be empty: then no stack component is used).</param>
    MatchResult Analyze(CandidateProfile profile, IReadOnlyCollection<string> primarySkills, string title, string description);
}

/// <summary>
/// Match score = skills (weighted: languages/frameworks 3x, platforms 2x, general practices 1x), title fit (does the
/// title name the candidate's stack, or a specialty they don't have?), experience (years asked vs. had) and, when the
/// candidate has set a main stack, main-stack fit (is the job built on it?). With a main stack:
/// 35% skills, 25% main stack, 25% title, 15% experience; without: 50% skills, 30% title, 20% experience.
/// </summary>
public sealed partial class JobAnalysisService : IJobAnalysisService
{
    public MatchResult Analyze(CandidateProfile profile, IReadOnlyCollection<string> primarySkills, string title, string description)
    {
        var candidateSkills = CandidateSkills(profile);
        var primary = SkillCatalog.Normalize(primarySkills).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var jobSkills = SkillCatalog.Extract(title + "\n" + description);
        var matched = jobSkills.Where(s => candidateSkills.Contains(s)).ToList();
        var missing = jobSkills.Where(s => !candidateSkills.Contains(s)).ToList();

        // Weighted coverage with a floor on the denominator, so a posting that names one or two skills can't score 100%.
        var totalWeight = jobSkills.Sum(SkillCatalog.Weight);
        var matchedWeight = matched.Sum(SkillCatalog.Weight);
        // A software posting always names some technology; one that names none gets no skill credit.
        var skillScore = jobSkills.Count > 0 ? (int)Math.Round(100d * matchedWeight / Math.Max(totalWeight, 6))
            : description.Trim().Length < 300 ? 50   // only a teaser was available: unknown, not "no skills"
            : 0;

        var titleScore = TitleFit(title, candidateSkills, primary);
        var stackScore = StackFit(title, jobSkills, primary);

        var requiredYears = RequiredYears(description);
        var experienceScore = (profile.YearsOfExperience, requiredYears) switch
        {
            (null, _) => 70,
            (_, null) => 100,
            var (have, need) when have >= need => 100,
            var (have, need) when need - have <= 1 => 70,
            var (have, need) when need - have <= 2 => 45,
            _ => 20
        };

        var score = Math.Clamp((int)Math.Round(primary.Count > 0
            ? 0.35 * skillScore + 0.25 * stackScore + 0.25 * titleScore + 0.15 * experienceScore
            : 0.5 * skillScore + 0.3 * titleScore + 0.2 * experienceScore), 0, 100);
        var decision = score >= 75 ? "StrongMatch" : score >= 50 ? "Review" : "Skip";
        var coreMissing = missing.Where(SkillCatalog.IsCore).ToList();
        var reason = jobSkills.Count == 0
            ? "Few recognizable technical skills were found in the posting; review it manually."
            : $"You have {matched.Count} of {jobSkills.Count} skills this posting mentions" +
              (coreMissing.Count > 0 ? $"; it's built on {string.Join(", ", coreMissing.Take(3))}, which isn't on your profile" : "") +
              (primary.Count > 0 && stackScore <= 10 ? "; it doesn't use your main stack" : "") +
              (requiredYears is { } y ? $"; it asks for {y}+ years of experience." : ".");
        return new MatchResult(score, decision, matched, missing, reason, skillScore, experienceScore, requiredYears, titleScore, primary.Count > 0 ? stackScore : 0);
    }

    /// <summary>
    /// 100 when the title names something in the candidate's stack (".NET Developer"); 15 when it names a specialty
    /// they don't have (embedded, iOS, ML, C++...); 70 for general software titles; 50 otherwise.
    /// </summary>
    public static int TitleFit(string title, HashSet<string> candidateSkills, HashSet<string> primary)
    {
        var titleSkills = SkillCatalog.Extract(title);
        if (titleSkills.Any(s => SkillCatalog.IsCore(s) && (primary.Count == 0 ? candidateSkills.Contains(s) : primary.Contains(s)))) return 100;
        if (titleSkills.Any(s => SkillCatalog.IsCore(s) && candidateSkills.Contains(s))) return 75;
        if (titleSkills.Any(s => SkillCatalog.IsCore(s) && !candidateSkills.Contains(s))) return 15;
        foreach (var (pattern, skill) in Specialties)
            if (pattern.IsMatch(title) && (skill is null || !candidateSkills.Contains(skill))) return 15;
        if (GeneralTitle().IsMatch(title)) return 70;
        return 50;
    }

    // Specialties that make a job a poor fit unless the candidate has the related skill (null = never a fit by skills alone).
    private static readonly (Regex Pattern, string? Skill)[] Specialties =
    [
        (Word("embedded|firmware|boot|bios|kernel|device driver|driver|fpga|asic|hardware|emulation|verification|silicon|rtl"), null),
        (Word("ios|android|mobile"), "iOS"),
        (Word("machine learning|ml|ai engineer|data scientist|deep learning|computer vision|perception"), "Machine Learning"),
        (Word("data engineer|data platform|etl|analytics engineer"), "ETL"),
        (Word("qa|quality assurance|test engineer|sdet|automation tester|test automation"), "Selenium"),
        (Word("site reliability|sre|network engineer|security engineer|infosec|penetration"), null),
        (Word("mainframe|cobol|cics|jcl|db2|as/400|rpg"), null),
        (Word("simulation|robotics|autonomy|autonomous|av|game engine|graphics|rendering|maya|unreal|unity"), null),
        (Word("salesforce|sap|workday|servicenow|oracle ebs|dynamics 365"), "ServiceNow"),
    ];

    private static Regex Word(string alternatives) => new($@"(?<![a-z0-9])({alternatives})(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"\b(software|full[\s-]?stack|back[\s-]?end|front[\s-]?end|web|application|applications|platform|api|cloud|developer|programmer|sde|swe)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GeneralTitle();

    /// <summary>
    /// How much the job is built on the main stack: 100 when the title names it; 55-100 by the share of the job's
    /// languages/frameworks that are in it; 50 when the posting names no language or framework; 10 when it is built
    /// entirely on other ones (e.g. a Java-only job for a .NET developer).
    /// </summary>
    public static int StackFit(string title, IReadOnlyList<string> jobSkills, HashSet<string> primary)
    {
        if (primary.Count == 0) return 0;
        if (SkillCatalog.Extract(title).Any(primary.Contains)) return 100;
        var jobCore = jobSkills.Where(SkillCatalog.IsCore).ToList();
        if (jobCore.Count == 0) return 50;
        var hits = jobCore.Count(primary.Contains);
        return hits == 0 ? 10 : (int)Math.Round(55 + 45.0 * hits / jobCore.Count);
    }

    /// <summary>
    /// The profile's skills, in canonical form, expanded with skills they imply. Resume skills are copied into the
    /// profile on upload, so the profile list is the single source of truth: a skill the user removes stops counting.
    /// </summary>
    public static HashSet<string> CandidateSkills(CandidateProfile profile)
    {
        var skills = SkillCatalog.Normalize(profile.SkillsCsv.Split(',')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (skill, implied) in Implies)
            if (skills.Contains(skill))
                foreach (var i in implied) skills.Add(i);
        return skills;
    }

    // Having the key skill means you have the value skills too: a React/Angular developer writes JavaScript and
    // TypeScript; an ASP.NET developer writes C#. This stops those from counting as "missing".
    private static readonly (string Skill, string[] Implied)[] Implies =
    [
        ("React", ["JavaScript", "TypeScript", "HTML", "CSS"]),
        ("Angular", ["JavaScript", "TypeScript", "HTML", "CSS"]),
        ("Vue", ["JavaScript", "TypeScript", "HTML", "CSS"]),
        ("Svelte", ["JavaScript", "TypeScript"]),
        ("Next.js", ["JavaScript", "TypeScript", "React"]),
        ("Node.js", ["JavaScript"]),
        ("Redux", ["JavaScript"]),
        ("jQuery", ["JavaScript"]),
        ("ASP.NET", ["C#"]),
        ("Entity Framework", ["C#"]),
        ("LINQ", ["C#"]),
        ("Blazor", ["C#"]),
        ("WPF", ["C#"]),
        (".NET", ["C#"]),
        ("Django", ["Python"]),
        ("Flask", ["Python"]),
        ("FastAPI", ["Python"]),
        ("Spring", ["Java"]),
        ("Rails", ["Ruby"]),
        ("Laravel", ["PHP"]),
    ];

    // Programming languages that define what a job is built in. JavaScript/TypeScript are left out on purpose:
    // they come with front-end frameworks (React, Angular) rather than signalling a different stack.
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Java", "Python", "Go", "Ruby", "Rails", "C++", "C", "Kotlin", "Scala", "PHP", "Rust", "Swift", "Objective-C", "Dart",
    };

    /// <summary>
    /// Whether the title's stack fits the candidate: false only when the title names a programming language the
    /// candidate doesn't have and names no language or framework they do ("Java Developer" for a .NET developer,
    /// but not "Python Fullstack (Angular) Developer", which keeps because Angular is theirs).
    /// </summary>
    public static bool TitleFitsCandidate(string title, HashSet<string> candidateSkills)
    {
        var titleSkills = SkillCatalog.Extract(title);
        var ownsOne = titleSkills.Any(s => SkillCatalog.IsCore(s) && candidateSkills.Contains(s));
        var foreignLanguage = titleSkills.Any(s => Languages.Contains(s) && !candidateSkills.Contains(s));
        return ownsOne || !foreignLanguage;
    }

    /// <summary>The first "N+ years" requirement in the posting, which is usually the headline one.</summary>
    public static int? RequiredYears(string description)
    {
        var m = YearsPattern().Match(description);
        return m.Success && int.TryParse(m.Groups[1].Value, out var y) && y is > 0 and <= 20 ? y : null;
    }

    [GeneratedRegex(@"(\d{1,2})\s*\+?\s*(?:(?:-|–|to)\s*\d{1,2}\s*)?\+?\s*years?(?:\s+of)?(?:\s+\w+){0,4}?\s+(?:experience|exp\b)", RegexOptions.IgnoreCase)]
    private static partial Regex YearsPattern();
}
