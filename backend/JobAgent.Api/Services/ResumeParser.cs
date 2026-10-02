using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace JobAgent.Api.Services;

public sealed record ParsedResume(
    string FirstName, string LastName, string Email, string Phone, string City, string State, string Country,
    string LinkedInUrl, int? YearsOfExperience, string Summary, IReadOnlyList<string> Skills, string Text);

public static partial class ResumeParser
{
    /// <summary>Extracts plain text (one line per text line) from a .pdf or .docx resume; returns "" for formats it can't read.</summary>
    public static string ExtractText(string path)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".pdf":
                    using (var pdf = PdfDocument.Open(path))
                        return string.Join("\n", pdf.GetPages().Select(p => ContentOrderTextExtractor.GetText(p)));
                case ".docx":
                    using (var doc = WordprocessingDocument.Open(path, false))
                        return string.Join("\n", doc.MainDocumentPart?.Document?.Body?.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
                            .Select(p => p.InnerText) ?? []);
                default:
                    return "";
            }
        }
        catch (Exception)
        {
            // A corrupt or password-protected file shouldn't block the upload itself.
            return "";
        }
    }

    /// <summary>Best-effort extraction of contact details, experience and skills. Anything not found is left empty for the user.</summary>
    public static ParsedResume Parse(string text)
    {
        var lines = text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0).ToList();
        var top = lines.Take(15).ToList();

        var email = Email().Match(text).Value;
        var phone = Phone().Matches(text).Select(m => m.Value.Trim()).FirstOrDefault(p => p.Count(char.IsDigit) is >= 10 and <= 15) ?? "";
        // The domain is case-insensitive ("Linkedin.com"); normalize it but keep the profile handle as written.
        var linkedIn = LinkedIn().Match(text) is { Success: true } li
            ? "https://www.linkedin.com/in/" + li.Groups[2].Value.TrimEnd('/', '.', ',')
            : "";

        var (first, last) = GuessName(top, email);
        var (city, state) = GuessCityState(top);
        var country = state != "" ? "US" : "";

        return new ParsedResume(first, last, email, phone, city, state, country, linkedIn,
            GuessYears(text, lines), GuessSummary(lines), SkillCatalog.Extract(text), text);
    }

    private static (string First, string Last) GuessName(List<string> top, string email)
    {
        foreach (var line in top)
        {
            // Name lines are short, letters only, and not a heading or contact line.
            var candidate = line.Split('|', '•', '·', ',')[0].Trim();
            if (candidate.Length is < 3 or > 40 || candidate.Any(char.IsDigit) || candidate.Contains('@')) continue;
            var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is < 2 or > 4 || !words.All(w => NameWord().IsMatch(w))) continue;
            if (Heading().IsMatch(candidate) || NotAName().IsMatch(candidate)) continue;
            var cased = words.Select(w => w.All(char.IsUpper) ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.ToLowerInvariant()) : w).ToArray();
            // Middle names/initials ("Jane A. Doe") are dropped; application forms ask for first and last.
            return (cased[0], cased[^1]);
        }
        // Fall back to the email's local part, e.g. jane.doe@... → Jane Doe.
        var local = email.Split('@')[0].Split('.', '_', '-').Where(p => p.Length > 1 && p.All(char.IsLetter)).ToArray();
        return local.Length >= 2
            ? (CultureInfo.InvariantCulture.TextInfo.ToTitleCase(local[0]), CultureInfo.InvariantCulture.TextInfo.ToTitleCase(local[^1]))
            : ("", "");
    }

    private static (string City, string State) GuessCityState(List<string> top)
    {
        foreach (var line in top)
        {
            foreach (Match m in CityState().Matches(line))
            {
                var city = m.Groups[1].Value.Trim();
                var stateText = m.Groups[2].Value.Trim();
                var abbr = stateText.Length == 2 ? stateText.ToUpperInvariant() : States.FirstOrDefault(s => s.Value.Equals(stateText, StringComparison.OrdinalIgnoreCase)).Key;
                if (abbr is null || !States.ContainsKey(abbr) || Heading().IsMatch(city)) continue;
                // Headers often list credentials ("…, MCIS, AWS CCP, AZ-900"); a tech term or credential isn't a city.
                if (SkillCatalog.Extract(city).Count > 0 || Credential().IsMatch(city)) continue;
                return (city, abbr);
            }
        }
        return ("", "");
    }

    private static int? GuessYears(string text, List<string> lines)
    {
        // An explicit claim ("8+ years of experience") in the resume wins.
        if (ClaimedYears().Match(text) is { Success: true } claim && int.TryParse(claim.Groups[1].Value, out var claimed) && claimed is > 0 and < 50)
            return claimed;

        // Otherwise add up the date ranges in the experience section (overlaps counted once).
        var section = Section(lines, ExperienceHeading());
        var source = section.Count > 0 ? string.Join("\n", section) : text;
        var ranges = new List<(DateOnly Start, DateOnly End)>();
        foreach (Match m in DateRange().Matches(source))
        {
            if (ParseDate(m.Groups["start"].Value, false) is not { } start) continue;
            var endText = m.Groups["end"].Value;
            var end = PresentWord().IsMatch(endText) ? DateOnly.FromDateTime(DateTime.Today) : ParseDate(endText, true);
            if (end is { } e && e >= start && e.Year - start.Year < 50) ranges.Add((start, e));
        }
        if (ranges.Count == 0) return null;

        var months = 0;
        DateOnly? coveredUntil = null;
        foreach (var (start, end) in ranges.OrderBy(r => r.Start))
        {
            var from = coveredUntil is { } c && c > start ? c : start;
            if (end > from) months += (end.Year - from.Year) * 12 + end.Month - from.Month;
            if (coveredUntil is null || end > coveredUntil) coveredUntil = end;
        }
        return months >= 6 ? (int)Math.Round(months / 12.0) : 0;
    }

    private static DateOnly? ParseDate(string s, bool isEnd)
    {
        s = s.Trim().TrimEnd('.');
        if (Regex.Match(s, @"^(\d{1,2})[/\-.](\d{4})$") is { Success: true } mmYyyy)
            return int.Parse(mmYyyy.Groups[1].Value) is >= 1 and <= 12 and var mo ? new DateOnly(int.Parse(mmYyyy.Groups[2].Value), mo, 1) : null;
        if (Regex.Match(s, @"^([A-Za-z]{3,9})\.?\s+'?(\d{2,4})$") is { Success: true } monYear)
        {
            var mon = Array.FindIndex(Months, m => monYear.Groups[1].Value.StartsWith(m, StringComparison.OrdinalIgnoreCase)) + 1;
            var year = int.Parse(monYear.Groups[2].Value);
            if (year < 100) year += 2000;
            return mon > 0 ? new DateOnly(year, mon, 1) : null;
        }
        if (Regex.Match(s, @"^(\d{4})$") is { Success: true } y) return new DateOnly(int.Parse(y.Value), isEnd ? 12 : 1, 1);
        return null;
    }

    private static string GuessSummary(List<string> lines)
    {
        var section = Section(lines, SummaryHeading());
        // No "Summary" heading: many resumes open with an untitled paragraph between the contact block and the
        // first section heading. Take the first run of long prose lines there.
        if (section.Count == 0)
        {
            var firstHeading = lines.FindIndex(l => l.Length < 40 && Heading().IsMatch(l));
            section = lines.Take(firstHeading < 0 ? 15 : firstHeading)
                .SkipWhile(l => !IsProse(l))
                .TakeWhile(IsProse)
                .ToList();
        }
        var summary = string.Join(" ", section);
        return summary.Length > 800 ? summary[..800].TrimEnd() + "…" : summary;
    }

    // A sentence-like line: long, not a contact line, not a "LABEL | LABEL" title bar.
    private static bool IsProse(string line) =>
        line.Length >= 60 && !line.Contains('@') && !line.Contains('|') && !line.Contains("http", StringComparison.OrdinalIgnoreCase)
        && line.Count(char.IsLower) > line.Count(char.IsUpper);

    /// <summary>Lines after a heading that matches <paramref name="heading"/>, up to the next section heading.</summary>
    private static List<string> Section(List<string> lines, Regex heading)
    {
        var start = lines.FindIndex(l => l.Length < 40 && heading.IsMatch(l));
        if (start < 0) return [];
        return lines.Skip(start + 1).TakeWhile(l => !(l.Length < 40 && Heading().IsMatch(l))).ToList();
    }

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, string> States = new()
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California", ["CO"] = "Colorado",
        ["CT"] = "Connecticut", ["DE"] = "Delaware", ["FL"] = "Florida", ["GA"] = "Georgia", ["HI"] = "Hawaii", ["ID"] = "Idaho",
        ["IL"] = "Illinois", ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas", ["KY"] = "Kentucky", ["LA"] = "Louisiana",
        ["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts", ["MI"] = "Michigan", ["MN"] = "Minnesota", ["MS"] = "Mississippi",
        ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska", ["NV"] = "Nevada", ["NH"] = "New Hampshire", ["NJ"] = "New Jersey",
        ["NM"] = "New Mexico", ["NY"] = "New York", ["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma",
        ["OR"] = "Oregon", ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina", ["SD"] = "South Dakota",
        ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont", ["VA"] = "Virginia", ["WA"] = "Washington",
        ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming", ["DC"] = "District of Columbia",
    };

    [GeneratedRegex(@"[ \t ]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"(?<!\d)(\+?\d{1,3}[\s.\-]?)?\(?\d{3}\)?[\s.\-]?\d{3}[\s.\-]?\d{4}(?!\d)")] private static partial Regex Phone();
    [GeneratedRegex(@"(?:https?://)?(?:www\.)?(linkedin\.com/in/([A-Za-z0-9_\-%]+)/?)", RegexOptions.IgnoreCase)] private static partial Regex LinkedIn();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z'\-\.]*$")] private static partial Regex NameWord();
    [GeneratedRegex(@"\b(resume|curriculum|vitae|engineer|developer|manager|analyst|consultant|summary|profile|experience|education|skills|objective|contact|address|phone|email|linkedin|github|portfolio|software|senior|junior|full stack|remote)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAName();
    [GeneratedRegex(@"^(professional\s+|career\s+|executive\s+)?(summary|profile|objective|about me|experience|work experience|professional experience|employment|employment history|work history|education|skills|technical skills|core competencies|projects|certifications?|awards|publications|languages|interests|volunteer)\b[:\s]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();
    [GeneratedRegex(@"^(professional\s+|career\s+|executive\s+)?(summary|profile|objective|about me)\b", RegexOptions.IgnoreCase)] private static partial Regex SummaryHeading();
    [GeneratedRegex(@"^(work\s+|professional\s+|relevant\s+)?(experience|employment( history)?|work history)\b", RegexOptions.IgnoreCase)] private static partial Regex ExperienceHeading();
    // State must not run into "-" or a digit, so certification codes like "AZ-900" aren't read as Arizona.
    [GeneratedRegex(@"([A-Z][A-Za-z.\- ]{1,30}),\s*([A-Z]{2}|[A-Z][a-z]+(?: [A-Z][a-z]+)?)(?![A-Za-z\-\d])(?:\s+\d{5})?")] private static partial Regex CityState();
    [GeneratedRegex(@"\b(MCIS|MBA|MS|MSc|BS|BSc|BA|BE|BTech|MTech|PhD|CCP|PMP|CPA|CISSP|CISA|CSM|CKA|CKAD|OCP|MCP|MCSA|MCSD|ITIL|certified|certification)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Credential();
    [GeneratedRegex(@"(\d{1,2})\+?\s*(?:years|yrs)\.?\s+(?:of\s+)?(?:[\w\-]+\s+){0,2}?(?:experience|exp\b)", RegexOptions.IgnoreCase)] private static partial Regex ClaimedYears();
    [GeneratedRegex(@"(?<start>(?:[A-Za-z]{3,9}\.?\s+'?\d{2,4})|(?:\d{1,2}[/\-.]\d{4})|(?:\d{4}))\s*(?:-|–|—|to|until)\s*(?<end>(?:[A-Za-z]{3,9}\.?\s+'?\d{2,4})|(?:\d{1,2}[/\-.]\d{4})|(?:\d{4})|present|current|now|today)", RegexOptions.IgnoreCase)]
    private static partial Regex DateRange();
    [GeneratedRegex(@"^(present|current|now|today)$", RegexOptions.IgnoreCase)] private static partial Regex PresentWord();
}
