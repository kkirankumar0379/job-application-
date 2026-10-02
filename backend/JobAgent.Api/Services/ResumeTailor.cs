using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using JobAgent.Api.Domain;

namespace JobAgent.Api.Services;

public sealed record TailoredExperience(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("company")] string Company,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("dates")] string Dates,
    [property: JsonPropertyName("bullets")] List<string> Bullets);

public sealed record TailoredEducation(
    [property: JsonPropertyName("degree")] string Degree,
    [property: JsonPropertyName("school")] string School,
    [property: JsonPropertyName("dates")] string Dates);

public sealed record TailoredSkillGroup(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("items")] List<string> Items);

public sealed record TailoredProject(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bullets")] List<string> Bullets);

/// <summary>What the tailor changed. Only the summary is rewritten; the other fields stay empty so the UI shows just the summary.</summary>
public sealed record TailoredResumeContent(
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("skills")] List<TailoredSkillGroup> Skills,
    [property: JsonPropertyName("experience")] List<TailoredExperience> Experience,
    [property: JsonPropertyName("projects")] List<TailoredProject> Projects,
    [property: JsonPropertyName("education")] List<TailoredEducation> Education,
    [property: JsonPropertyName("certifications")] List<string> Certifications,
    [property: JsonPropertyName("changes")] List<string> Changes);

/// <summary>The model's raw output: the new summary points, the new skills lines and short notes on what changed.</summary>
public sealed record SummaryDraft(
    [property: JsonPropertyName("summary")] List<string> Summary,
    [property: JsonPropertyName("skills")] List<string> Skills,
    [property: JsonPropertyName("changes")] List<string> Changes);

public sealed record TailorResult(TailoredResumeContent Content, string PlainText, AtsReport Before, AtsReport After, int Passes);

/// <summary>
/// Rewrites only the summary and the skills (tech stack) lines of the candidate's own .docx for one job with Claude,
/// leaving the rest of the file (format and every other section) untouched, then scores it; one revision pass if under target.
/// </summary>
public sealed class ResumeTailor(AiSettings settings, ILogger<ResumeTailor> logger)
{
    public const int TargetScore = 90;
    private const string Model = "claude-haiku-4-5";

    private const string SystemPrompt = """
        You are an expert technical resume writer. You rewrite ONLY the summary and the skills (tech stack) lines of a
        resume so they match a specific job description and pass applicant tracking systems (ATS). Nothing else in
        the resume changes.

        Hard rules (a violation makes the resume unusable, because the candidate would be misrepresenting themselves):
        - Use only facts found in the ORIGINAL RESUME, plus skills listed under CONFIRMED SKILLS, which the candidate
          has confirmed they genuinely have. Never invent employers, job titles, dates, degrees, certifications,
          projects, metrics, years of experience or technologies.
        - You may rephrase, reorder and emphasize, and use the job description's exact wording for skills and
          responsibilities the candidate really has (e.g. write "RESTful APIs" if the job says so and the resume
          shows REST work).

        Summary rules:
        - Return exactly the same number of summary points as the ORIGINAL SUMMARY has (one string per point),
          each about the same length as the original point it replaces.
        - Work the job's most important keywords in naturally, and use the exact target job title if it fits.

        Skills rules:
        - Return exactly one string per ORIGINAL SKILLS line, in the same order, keeping the same label before the
          colon (e.g. "Languages: ..."). Never add, remove or rename lines.
        - Within each line, put the skills most relevant to the job first, use the job description's spelling, keep
          the candidate's real skills, and add confirmed skills where they fit that line's category.
        - If there are no ORIGINAL SKILLS lines, return an empty list.

        Plain text only: no bullet characters, numbering, markdown, emojis or first-person pronouns.
        - "changes": 2-5 short notes for the candidate describing what you changed and why.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = BuildSchema();

    public async Task<TailorResult> TailorAsync(CandidateProfile profile, JobPosting job, IReadOnlyList<string> confirmedSkills, string outputPath, CancellationToken ct)
    {
        if (settings.ApiKey is not { } apiKey) throw new InvalidOperationException("Add your Anthropic API key on the Profile tab to use resume tailoring.");
        if (string.IsNullOrWhiteSpace(profile.ResumeText) || !File.Exists(profile.ResumePath)) throw new InvalidOperationException("Upload your resume on the Profile tab first.");
        if (!profile.ResumePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("To keep your resume's format, tailoring edits your own file, which must be a .docx. Upload your resume as .docx on the Profile tab.");

        var originalSummary = DocxSummaryEditor.ReadSummary(profile.ResumePath)
            ?? throw new InvalidOperationException("Couldn't find a Summary section in your resume. Add a heading like \"SUMMARY\" and upload it again.");

        var originalSkills = DocxSummaryEditor.ReadSkills(profile.ResumePath) ?? [];

        var before = AtsScorer.Score(profile.ResumeText, job.Title, job.Description);
        var client = new AnthropicClient { ApiKey = apiKey };

        var request = new StringBuilder()
            .AppendLine($"TARGET JOB: {job.Title} at {job.Company}")
            .AppendLine().AppendLine("JOB DESCRIPTION:").AppendLine(job.Description)
            .AppendLine().AppendLine("ORIGINAL RESUME:").AppendLine(profile.ResumeText)
            .AppendLine().AppendLine($"ORIGINAL SUMMARY ({originalSummary.Count} point(s)):")
            .AppendLine(string.Join("\n", originalSummary.Select((s, i) => $"{i + 1}. {s}")))
            .AppendLine().AppendLine($"ORIGINAL SKILLS ({originalSkills.Count} line(s)):")
            .AppendLine(originalSkills.Count > 0 ? string.Join("\n", originalSkills.Select((s, i) => $"{i + 1}. {s}")) : "(none)")
            .AppendLine().AppendLine("CONFIRMED SKILLS (the candidate has these even if the resume doesn't show them):")
            .AppendLine(confirmedSkills.Count > 0 ? string.Join(", ", confirmedSkills) : "(none)")
            .AppendLine().AppendLine($"Keywords from the job description to cover where truthful: {string.Join(", ", before.MatchedKeywords.Concat(before.MissingKeywords.Where(k => confirmedSkills.Contains(k, StringComparer.OrdinalIgnoreCase))))}")
            .AppendLine($"Use the exact target title \"{before.TargetTitle}\" in the summary if it fits the candidate.")
            .ToString();

        var draft = await GenerateAsync(client, request, ct);
        var (text, after) = Render(profile, job, draft, outputPath);
        var passes = 1;

        // One revision pass for keywords that could truthfully appear but don't yet.
        var fixable = after.MissingKeywords
            .Where(k => confirmedSkills.Contains(k, StringComparer.OrdinalIgnoreCase) || SkillCatalog.Extract(profile.ResumeText).Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (after.Score < TargetScore && (fixable.Count > 0 || after.TitleScore < 100))
        {
            var revision = new StringBuilder(request)
                .AppendLine().AppendLine("YOUR PREVIOUS DRAFT (JSON):").AppendLine(JsonSerializer.Serialize(draft))
                .AppendLine().AppendLine("Revise the draft. Still follow every hard rule.")
                .AppendLine(fixable.Count > 0 ? $"These keywords are true for the candidate but missing from the draft; work them in naturally: {string.Join(", ", fixable)}." : "")
                .AppendLine(after.TitleScore < 100 ? $"Include the exact phrase \"{after.TargetTitle}\" in the summary." : "")
                .ToString();
            var revised = await GenerateAsync(client, revision, ct);
            var tempPath = outputPath + ".rev";
            try
            {
                var (revisedText, revisedScore) = Render(profile, job, revised, tempPath);
                passes = 2;
                if (revisedScore.Score >= after.Score)
                {
                    File.Move(tempPath, outputPath, overwrite: true);
                    (draft, text, after) = (revised, revisedText, revisedScore);
                }
            }
            finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
        }

        logger.LogInformation("Tailored resume summary for {Job}: ATS {Before} → {After} in {Passes} pass(es)", job.Id, before.Score, after.Score, passes);
        var groups = draft.Skills.Select(l => l.Split(':', 2)).Where(a => a.Length == 2)
            .Select(a => new TailoredSkillGroup(a[0].Trim(), a[1].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList())).ToList();
        var content = new TailoredResumeContent("", string.Join("\n", draft.Summary), groups, [], [], [], [], draft.Changes);
        return new TailorResult(content, text, before, after, passes);
    }

    /// <summary>Writes the candidate's .docx with the new summary to <paramref name="path"/> and scores the result.</summary>
    private static (string Text, AtsReport Report) Render(CandidateProfile profile, JobPosting job, SummaryDraft draft, string path)
    {
        DocxSummaryEditor.Replace(profile.ResumePath, path, draft.Summary, draft.Skills);
        var text = ResumeParser.ExtractText(path);
        return (text, AtsScorer.Score(text, job.Title, job.Description));
    }

    private static async Task<SummaryDraft> GenerateAsync(AnthropicClient client, string prompt, CancellationToken ct)
    {
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 6000,
            System = SystemPrompt,
            // Haiku 4.5 doesn't support the effort parameter or server-side fallbacks.
            OutputConfig = new BetaOutputConfig
            {
                Format = new BetaJsonOutputFormat { Schema = Schema },
            },
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, ct);

        if (response.StopReason == "refusal") throw new InvalidOperationException("The AI declined to rewrite this summary. Try again or edit the job description.");
        if (response.StopReason == "max_tokens") throw new InvalidOperationException("The rewritten summary was too long to finish. Try again.");

        var json = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return JsonSerializer.Deserialize<SummaryDraft>(json)
               ?? throw new InvalidOperationException("The AI returned an empty summary. Try again.");
    }

    private static Dictionary<string, JsonElement> BuildSchema()
    {
        object StrList() => new { type = "array", items = new { type = "string" } };
        var root = new
        {
            type = "object",
            properties = new Dictionary<string, object> { ["summary"] = StrList(), ["skills"] = StrList(), ["changes"] = StrList() },
            required = new[] { "summary", "skills", "changes" },
            additionalProperties = false,
        };
        return JsonSerializer.SerializeToElement(root).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
