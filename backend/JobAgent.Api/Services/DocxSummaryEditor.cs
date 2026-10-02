using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace JobAgent.Api.Services;

/// <summary>
/// Reads and rewrites only the Summary and Skills (tech stack) sections of the candidate's own .docx, so fonts,
/// spacing, bullets, labels and every other section stay exactly as uploaded.
/// </summary>
public static partial class DocxSummaryEditor
{
    [GeneratedRegex(@"^(professional|career|executive)?\s*(summary|profile|objective)(\s+of\s+qualifications)?\s*:?$", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryHeading();

    [GeneratedRegex(@"^(technical|core|key|professional)?\s*(skills|competencies|technologies|tech stack)(\s*(&|and)\s*\w+)?\s*:?$", RegexOptions.IgnoreCase)]
    private static partial Regex SkillsHeading();

    [GeneratedRegex(@"^(?:(?:technical|core|key|professional|work)\s+)?(?:skills|competencies|technologies|tech stack|summary|profile|objective|experience|employment|work history|education|projects?|certifications?|achievements|awards|publications|training|volunteer|references)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnyHeading();

    /// <summary>The non-empty paragraphs under the summary heading, or null when the resume has none.</summary>
    public static List<string>? ReadSummary(string docxPath) => Read(docxPath, SummaryHeading());

    /// <summary>The non-empty lines under the skills heading (usually "Category: a, b, c"), or null when the resume has none.</summary>
    public static List<string>? ReadSkills(string docxPath) => Read(docxPath, SkillsHeading());

    private static List<string>? Read(string path, Regex heading)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return Find(doc, heading)?.Select(p => p.InnerText.Trim()).ToList();
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destPath"/> with the summary paragraphs and (when given)
    /// the skills lines replaced. Skills keep their line count and category labels; only the items change.
    /// </summary>
    public static void Replace(string sourcePath, string destPath, IReadOnlyList<string> summary, IReadOnlyList<string>? skills = null)
    {
        File.Copy(sourcePath, destPath, overwrite: true);
        using var doc = WordprocessingDocument.Open(destPath, true);

        var oldSummary = Find(doc, SummaryHeading());
        var freshSummary = summary.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (oldSummary is not null && freshSummary.Count > 0)
        {
            for (var i = 0; i < freshSummary.Count; i++)
            {
                if (i < oldSummary.Count) { SetText(oldSummary[i], freshSummary[i]); continue; }
                // More points than the original had: clone the last one so bullet/indent formatting carries over.
                var clone = (Paragraph)oldSummary[^1].CloneNode(true);
                oldSummary[^1].InsertAfterSelf(clone);
                oldSummary.Add(clone);
                SetText(clone, freshSummary[i]);
            }
            for (var i = oldSummary.Count - 1; i >= freshSummary.Count; i--) oldSummary[i].Remove();
        }

        if (skills is { Count: > 0 } && Find(doc, SkillsHeading()) is { } oldSkills)
        {
            // Match by position; if the model returned a different number of lines, leave the rest untouched.
            for (var i = 0; i < Math.Min(oldSkills.Count, skills.Count); i++)
                if (!string.IsNullOrWhiteSpace(skills[i])) SetSkillLine(oldSkills[i], skills[i].Trim());
        }

        doc.MainDocumentPart!.Document!.Save();
    }

    private static List<Paragraph>? Find(WordprocessingDocument doc, Regex heading)
    {
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return null;
        var paras = body.Elements<Paragraph>().ToList();
        var start = paras.FindIndex(p => heading.IsMatch(p.InnerText.Trim()));
        if (start < 0) return null;

        var result = new List<Paragraph>();
        for (var i = start + 1; i < paras.Count; i++)
        {
            var text = paras[i].InnerText.Trim();
            if (text.Length == 0) continue;
            if (IsHeading(text)) break;
            result.Add(paras[i]);
        }
        return result.Count > 0 ? result : null;
    }

    private static bool IsHeading(string text)
    {
        if (text.Length > 45) return false;
        // "Languages: C#, Java" is a content line, not a heading.
        var colon = text.IndexOf(':');
        if (colon >= 0 && colon < text.Length - 1) return false;
        if (AnyHeading().IsMatch(text)) return true;
        var letters = text.Where(char.IsLetter).ToList();
        return letters.Count > 2 && letters.All(char.IsUpper);
    }

    /// <summary>Puts the text in the first run (keeping its font/size/bold) and drops the other runs.</summary>
    private static void SetText(Paragraph p, string text)
    {
        var runs = p.Elements<Run>().ToList();
        var first = runs.FirstOrDefault(r => r.Elements<Text>().Any()) ?? runs.FirstOrDefault();
        if (first is null) { p.Append(NewRun(text)); return; }

        foreach (var r in runs.Where(r => r != first)) r.Remove();
        ClearText(first);
        first.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    /// <summary>
    /// For a "Label: items" line, keeps the label (and its bold run) and replaces only the items. The new text is
    /// the model's full line; if it repeats the label it is stripped, and a line without a colon is replaced whole.
    /// </summary>
    private static void SetSkillLine(Paragraph p, string newLine)
    {
        var runs = p.Elements<Run>().ToList();
        var colonRun = runs.FindIndex(r => r.InnerText.Contains(':'));
        if (colonRun < 0) { SetText(p, newLine); return; }

        var oldColonText = runs[colonRun].InnerText;
        var colonPos = oldColonText.IndexOf(':');
        var label = oldColonText[..(colonPos + 1)];
        var items = newLine.Contains(':') ? newLine[(newLine.IndexOf(':') + 1)..].Trim() : newLine;

        var hasValueRun = colonRun + 1 < runs.Count;
        if (hasValueRun)
        {
            // Label run stays as is up to the colon; the first following run carries the items, the rest go.
            ClearText(runs[colonRun]);
            runs[colonRun].Append(new Text(label) { Space = SpaceProcessingModeValues.Preserve });
            var value = runs[colonRun + 1];
            ClearText(value);
            value.Append(new Text(" " + items) { Space = SpaceProcessingModeValues.Preserve });
            foreach (var r in runs.Skip(colonRun + 2)) r.Remove();
        }
        else
        {
            ClearText(runs[colonRun]);
            runs[colonRun].Append(new Text(label + " " + items) { Space = SpaceProcessingModeValues.Preserve });
        }
        // Runs before the label run (rare) keep their text.
    }

    private static void ClearText(Run r)
    {
        foreach (var child in r.ChildElements.Where(c => c is Text or Break or TabChar).ToList()) child.Remove();
    }

    private static Run NewRun(string text) => new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
}
