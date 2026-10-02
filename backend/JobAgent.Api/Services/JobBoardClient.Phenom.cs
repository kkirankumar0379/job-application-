using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobAgent.Api.Services;

/// <summary>
/// Phenom career sites (TEKsystems, Aerotek, Actalent and many enterprises). The site's own search endpoint
/// ("/widgets", ddoKey refineSearch) returns jobs with exact posted dates. Token format:
/// "host/localePath#refNum#lang#country", e.g. "careers.teksystems.com/us/en#TESYUS#en_us#us".
/// </summary>
public sealed partial class JobBoardClient
{
    private const int PhenomPageSize = 100;

    /// <summary>Recognizes a Phenom site from its page config (refNum, locale) and builds its token.</summary>
    private static (string Provider, string Token)? DetectPhenom(Uri page, string html)
    {
        if (!html.Contains("phenom", StringComparison.OrdinalIgnoreCase)) return null;
        var refNum = PhenomRefNum().Match(html);
        if (!refNum.Success) return null;
        var lang = PhenomLocale().Match(html) is { Success: true } l ? l.Groups[1].Value : "en_us";
        var country = PhenomCountry().Match(html) is { Success: true } c ? c.Groups[1].Value : lang.Split('_').LastOrDefault() ?? "us";
        // Phenom URLs start with a locale path such as /us/en; keep it so job links resolve.
        var localePath = string.Concat(page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(s => Regex.IsMatch(s, "^[a-z]{2}$")).Take(2).Select(s => "/" + s));
        return ("Phenom", $"{page.Host}{localePath}#{refNum.Groups[1].Value}#{lang}#{country}");
    }

    private async Task<List<BoardJob>> FetchPhenomAsync(string token, string company, IReadOnlyList<string>? keywords, CancellationToken ct)
    {
        var parts = token.Split('#');
        if (parts.Length < 4) throw new InvalidOperationException("Invalid Phenom board token.");
        var (site, refNum, lang, country) = (parts[0], parts[1], parts[2], parts[3]);
        var host = site.Split('/')[0];

        // Without keywords Phenom's "Most recent" order isn't reliable, so search each role keyword instead.
        var searches = keywords is { Count: > 0 } ? keywords.Take(8).ToList() : [""];
        var jobs = new Dictionary<string, BoardJob>();
        foreach (var keyword in searches)
        {
            var body = new
            {
                lang, deviceType = "desktop", country, pageName = "search-results", ddoKey = "refineSearch", sortBy = "Most recent",
                subsearch = "", from = 0, jobs = true, counts = false, all_fields = Array.Empty<string>(), size = PhenomPageSize,
                clearAll = false, jdsource = "facets", isSliderEnable = false, pageId = "page20", siteType = "external",
                keywords = keyword, global = true, selected_fields = new { }, refNum, locationData = new { }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/widgets") { Content = JsonContent.Create(body) };
            req.Headers.UserAgent.ParseAdd(BrowserAgent);
            using var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var list = doc.RootElement.GetPropertyOrDefault("refineSearch").GetPropertyOrDefault("data").GetPropertyOrDefault("jobs");
            if (list.ValueKind != JsonValueKind.Array) continue;

            foreach (var j in list.EnumerateArray())
            {
                var jobId = Str(j, "jobId");
                if (jobId == "" || jobs.ContainsKey(jobId)) continue;
                var title = Str(j, "title").Trim();
                var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                // The job page carries full JobPosting data only when addressed by jobSeqNo, not the short jobId.
                var pageId = Str(j, "jobSeqNo") is { Length: > 0 } seq ? seq : jobId;
                var url = $"https://{site}/job/{Uri.EscapeDataString(pageId)}/{slug}";
                var locations = new List<string> { Str(j, "location") is { Length: > 0 } loc ? loc : Str(j, "cityStateCountry") };
                if (j.GetPropertyOrDefault("multi_location_array") is { ValueKind: JsonValueKind.Array } more)
                    locations.AddRange(more.EnumerateArray().Select(m => Str(m, "location")));
                var location = string.Join("; ", locations.Where(x => x != "").Distinct());
                var remote = location.Contains("remote", StringComparison.OrdinalIgnoreCase)
                             || Str(j, "remoteOnsite").Contains("remote", StringComparison.OrdinalIgnoreCase);
                jobs[jobId] = new BoardJob(url, title, company, location, remote, Date(Str(j, "postedDate")) ?? Date(Str(j, "dateCreated")),
                    url, Str(j, "descriptionTeaser"), NeedsDetail: true);
            }
        }
        return jobs.Values.ToList();
    }

    [GeneratedRegex(@"""refNum""\s*:\s*""([A-Za-z0-9]+)""")] private static partial Regex PhenomRefNum();
    [GeneratedRegex(@"""locale""\s*:\s*""([a-z]{2}_[a-z]{2})""")] private static partial Regex PhenomLocale();
    [GeneratedRegex(@"""country""\s*:\s*""([a-z]{2})""")] private static partial Regex PhenomCountry();
}
