using JobAgent.Api.Contracts;
using JobAgent.Api.Domain;

namespace JobAgent.Api.Services;

public interface IJobAnalysisService { MatchResult Analyze(CandidateProfile profile, string description); }

public sealed class JobAnalysisService : IJobAnalysisService
{
    private static readonly string[] CommonTechTerms = [".net","c#","asp.net","asp.net core","react","angular","typescript","javascript","sql server","postgresql","aws","azure","rest","microservices","docker","kubernetes","entity framework","linq"];

    public MatchResult Analyze(CandidateProfile profile, string description)
    {
        var haystack = description.ToLowerInvariant();
        var skills = profile.SkillsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).Distinct().ToArray();
        var matched = skills.Where(haystack.Contains).ToList();
        var requested = CommonTechTerms.Where(haystack.Contains).Distinct().ToList();
        var missing = requested.Where(x => !skills.Contains(x)).ToList();
        var score = Math.Clamp((int)Math.Round(100d * (requested.Count - missing.Count) / Math.Max(requested.Count, 1)), 0, 100);
        var decision = score >= 75 ? "StrongMatch" : score >= 50 ? "Review" : "Skip";
        var reason = requested.Count == 0 ? "Few recognizable technical requirements were found; manual review is recommended." : $"Matched {requested.Count - missing.Count} of {requested.Count} recognized technical requirements.";
        return new MatchResult(score, decision, matched, missing, reason);
    }
}
