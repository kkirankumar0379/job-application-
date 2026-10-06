using System.Text.RegularExpressions;

namespace JobAgent.Api.Services.Answers;

/// <summary>How far a stored answer to this kind of question can be trusted on a different application.</summary>
public enum ReusePolicy
{
    /// <summary>Same answer everywhere (work authorization, sponsorship).</summary>
    Stable,
    /// <summary>True when given but drifts over time (years of experience); older answers need confirming.</summary>
    Expiring,
    /// <summary>Depends on the job, company, place, date or wording: never filled without confirmation.</summary>
    Confirm,
    /// <summary>Never remembered or reused (identity numbers, passwords, signatures).</summary>
    Never,
}

/// <param name="Intent">What the question is asking; empty when it isn't one of the known kinds.</param>
/// <param name="Subject">Skills a per-skill question is about, sorted and comma separated.</param>
/// <param name="Reason">Why confirmation is required (for Confirm / Never policies).</param>
public sealed record QuestionIntent(string Intent, string Subject, ReusePolicy Policy, string Reason, int ExpiresAfterDays = 0);

/// <summary>
/// Works out what a question is really asking, so related-but-different questions are never mistaken for each other
/// ("authorized to work in the US" and "require sponsorship" are different intents with separate answers).
/// </summary>
public static partial class QuestionIntentClassifier
{
    private sealed record Rule(string Intent, Regex Pattern, ReusePolicy Policy, string Reason = "", int ExpiresAfterDays = 0);

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Checked in order; the first match wins, so more specific rules come before general ones.
    private static readonly Rule[] Rules =
    [
        new("secret", R(@"\b(ssn|social security|password|passcode|date of birth|dob|birth ?date|signature|captcha|bank|routing number|account number|driver'?s? licen[sc]e number|passport number)\b"), ReusePolicy.Never, "Never stored or reused."),
        new("eeo", R(@"\b(gender|sex\b|race|ethnic|hispanic|latino|veteran|military|disabilit|pronoun|sexual orientation|transgender|lgbt|religio|marital status|national origin|protected class|eeo)\b"), ReusePolicy.Confirm, "Voluntary demographic question: confirm each time."),
        new("legal_attestation", R(@"\b(i certify|i acknowledge|i agree|i attest|i understand|i declare|terms (and|&) conditions|privacy (policy|notice)|consent|electronic signature|true and (correct|complete)|accurate and complete)\b"), ReusePolicy.Confirm, "Legal attestation: confirm each time."),
        new("salary", R(@"\b(salary|compensation|pay (rate|range|expectation)|hourly rate|desired (pay|rate)|expected (pay|rate|salary)|rate expectation|wage|ctc|base pay)\b"), ReusePolicy.Confirm, "Compensation depends on the job: confirm."),
        new("start_date", R(@"\b(start date|available to start|earliest (start|you can start)|when (can|could) you (start|begin|join)|notice period|availability date|date available|how soon can you)\b"), ReusePolicy.Confirm, "Start date depends on today's date: confirm."),
        new("relocation", R(@"\b(relocat|move to|willing to move)\w*"), ReusePolicy.Confirm, "Depends on this job's location: confirm."),
        new("commute", R(@"\b(commute|on-?site|in[- ]office|in[- ]person|hybrid|work from (the )?office|days (a|per) week (in|on))\b"), ReusePolicy.Confirm, "Depends on this job's location and schedule: confirm."),
        new("location", R(@"\b(reside|live (in|within)|located (in|within)|within \d+ miles|current(ly)? (located|living)|which (city|state|office|location))\b"), ReusePolicy.Confirm, "Location-specific: confirm."),
        new("company_specific", R(@"\b(why (do you want|are you interested|would you like)|what (interests|excites|attracts) you|previously (worked|employed|applied)|(ever|currently) (worked|been employed|employed) (for|at|by|with)|former employee|current employee|relatives?|related to (an? )?(employee|anyone)|how did you (hear|find|learn)|referr(ed|al)|who referred)\b"), ReusePolicy.Confirm, "Specific to this company: confirm."),
        // Work authorization and sponsorship are separate intents. Sponsorship is tested first: a sponsorship question
        // often mentions being "authorized to work" as well.
        new("sponsorship", R(@"\b(sponsor\w*|visa|h-?1b|immigration)\b"), ReusePolicy.Stable),
        new("work_authorization", R(@"\b(authori[sz]ed|authori[sz]ation|eligible|legal(ly)? (right|permitted|allowed)|right to work|work permit|permitted to work)\b.{0,40}\bwork|\bwork (authori[sz]ation|eligibility)|\blegally (able|entitled)"), ReusePolicy.Stable),
        new("citizenship", R(@"\b(u\.?s\.? citizen|citizenship|citizen of|permanent resident|green card holder|us person)\b"), ReusePolicy.Stable),
        new("security_clearance", R(@"\b(security clearance|clearance level|active clearance|secret clearance|ts/sci|top secret|public trust)\b"), ReusePolicy.Expiring, "", 90),
        new("age_18", R(@"\b(at least 18|18 years|18 or older|over (the age of )?18|legal age|of legal working age)\b"), ReusePolicy.Stable),
        new("background_check", R(@"\b(background (check|screen|investigation)|drug (test|screen)|credit check|fingerprint)\b"), ReusePolicy.Stable),
        new("non_compete", R(@"\b(non-?compete|non-?solicit|restrictive covenant|confidentiality agreement|bound by any)\b"), ReusePolicy.Confirm, "Contract terms: confirm."),
        new("education_level", R(@"\b(highest (level of )?(education|degree)|level of education|degree (do you|have you)|bachelor|master'?s|phd|doctorate|graduated|gpa)\b"), ReusePolicy.Stable),
        new("years_experience", R(@"\b(years?|yrs?)\b.{0,40}\b(experience|worked|working|using|with|in|of)\b|\bexperience\b.{0,40}\b(years?|yrs?)\b|\bhow (many|long)\b.{0,50}\b(experience|worked|years?|yrs?|using)\b"), ReusePolicy.Expiring, "", 90),
        // "Do you have experience with Kubernetes?": a yes/no about a skill. Only counts when a skill is actually named.
        new("skill_experience", R(@"\b(experience|proficien\w*|familiar\w*|knowledge|skilled|expertise|worked|comfortable)\b.{0,30}\b(with|in|of|using)\b"), ReusePolicy.Stable),
        new("work_arrangement", R(@"\b(remote|work from home|telework|travel|willing to travel|overtime|weekends|shift|nights)\b"), ReusePolicy.Confirm, "Depends on this job: confirm."),
        new("links", R(@"\b(linkedin|github|portfolio|personal (website|site)|website url|twitter)\b"), ReusePolicy.Stable),
    ];

    public static QuestionIntent Classify(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return new("", "", ReusePolicy.Confirm, "No question text.");
        var text = question.Replace('’', '\'');
        foreach (var rule in Rules)
        {
            if (!rule.Pattern.IsMatch(text)) continue;
            var subject = rule.Intent is "years_experience" or "skill_experience" ? SubjectOf(text) : "";
            if (rule.Intent == "skill_experience" && subject == "") continue; // no skill named: not this kind of question
            return new QuestionIntent(rule.Intent, subject, rule.Policy, rule.Reason, rule.ExpiresAfterDays);
        }
        // Not a recognized kind: only reuse when the wording is essentially identical, and always confirm
        // (long free-text answers are usually job-specific).
        return new QuestionIntent("", "", ReusePolicy.Confirm, "Not a recognized standard question: confirm before reusing.");
    }

    /// <summary>The skills a "years of ..." question is about, canonical names, sorted ("C#,.NET").</summary>
    public static string SubjectOf(string question)
    {
        var skills = SkillCatalog.Extract(question).Where(s => s is not ("Go" or "C")).ToList();
        // "Go" and "C" are too ambiguous as words to treat as a subject.
        return string.Join(',', skills.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
    }

    public static string[] SubjectList(string subject) => subject.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
