using System.Text.RegularExpressions;
using JobAgent.Api.Domain;

namespace JobAgent.Api.Services.Answers;

/// <summary>
/// When memory has no answer, the attached resume can still suggest one: total years, skills you list, years stated for a
/// skill, highest degree. These are only ever suggestions (you confirm them); a fact the resume doesn't state is never guessed.
/// Works for whatever resume is attached to the profile, so it applies to any user's resume.
/// </summary>
public static partial class ResumeAnswerSource
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C#"] = @"c#|c\s?sharp|csharp", [".NET"] = @"\.net|dotnet", ["Node.js"] = @"node\.?js|node", ["JavaScript"] = @"javascript|js",
        ["TypeScript"] = @"typescript|ts", ["PostgreSQL"] = @"postgresql|postgres", ["Kubernetes"] = @"kubernetes|k8s", ["AWS"] = @"aws|amazon web services",
        ["Azure"] = @"azure|microsoft azure", ["GCP"] = @"gcp|google cloud",
    };

    /// <summary>A suggested answer with where it came from, or null when the resume doesn't support one.</summary>
    public static (string Answer, string Reason)? Suggest(QuestionInput q, QuestionIntent intent, CandidateProfile profile)
    {
        var resume = profile.ResumeText ?? "";
        var skills = JobAnalysisService.CandidateSkills(profile);
        var asked = QuestionIntentClassifier.SubjectList(intent.Subject);

        switch (intent.Intent)
        {
            case "years_experience" when asked.Length == 0:
                return profile.YearsOfExperience is { } total
                    ? (total.ToString(), $"From your profile: {total} years of experience in total.") : null;

            case "years_experience":
            {
                var stated = asked.Select(s => YearsStated(resume, s)).Where(y => y > 0).ToList();
                return stated.Count > 0
                    ? (stated.Max().ToString(), $"Your resume states {stated.Max()} years with {string.Join("/", asked)}. Check it is still right.") : null;
            }

            case "skill_experience":
                // Having the skill on the resume supports "Yes". Not having it never produces "No": it may just be unlisted.
                return asked.All(a => skills.Contains(a, StringComparer.OrdinalIgnoreCase))
                    ? (Pick(q, "Yes"), $"Your resume lists {string.Join(", ", asked)}.") : null;

            case "education_level":
            {
                var degree = HighestDegree(resume);
                return degree is null || q.Options is not { Count: > 0 } ? null : AnswerText.PickOption(degree, q.Options) is { } opt ? (opt, $"Your resume mentions a {degree}.") : null;
            }
        }
        return null;
    }

    private static string Pick(QuestionInput q, string answer) =>
        q.Options is { Count: > 0 } ? AnswerText.PickOption(answer, q.Options) ?? answer : answer;

    /// <summary>Largest "N years ... skill" figure the resume states for the skill (0 when none).</summary>
    public static int YearsStated(string resume, string skill)
    {
        var alias = Aliases.TryGetValue(skill, out var a) ? a : Regex.Escape(skill);
        var best = 0;
        foreach (Match m in Regex.Matches(resume, $@"(\d{{1,2}})\s*\+?\s*(?:years?|yrs?)[^.\n]{{0,45}}?(?<!\w)(?:{alias})(?!\w)", RegexOptions.IgnoreCase))
            best = Math.Max(best, int.Parse(m.Groups[1].Value));
        foreach (Match m in Regex.Matches(resume, $@"(?<!\w)(?:{alias})(?!\w)[^.\n]{{0,30}}?\(?\s*(\d{{1,2}})\s*\+?\s*(?:years?|yrs?)", RegexOptions.IgnoreCase))
            best = Math.Max(best, int.Parse(m.Groups[1].Value));
        return best is > 0 and < 50 ? best : 0;
    }

    private static string? HighestDegree(string resume) =>
        Doctorate().IsMatch(resume) ? "Doctorate" : Masters().IsMatch(resume) ? "Master's degree" : Bachelors().IsMatch(resume) ? "Bachelor's degree" : null;

    [GeneratedRegex(@"\b(ph\.?d|doctorate|doctor of philosophy)\b", RegexOptions.IgnoreCase)] private static partial Regex Doctorate();
    [GeneratedRegex(@"\b(master'?s? (of|degree|in)|master of|m\.s\.|msc|mba|m\.tech|m\.eng)\b", RegexOptions.IgnoreCase)] private static partial Regex Masters();
    [GeneratedRegex(@"\b(bachelor'?s?|b\.s\.|bsc|b\.tech|b\.e\.|b\.eng|undergraduate degree)", RegexOptions.IgnoreCase)] private static partial Regex Bachelors();
}
