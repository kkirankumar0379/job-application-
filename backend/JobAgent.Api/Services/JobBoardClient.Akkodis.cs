using System.Net.Http.Json;
using System.Text.Json;

namespace JobAgent.Api.Services;

/// <summary>
/// Akkodis (staffing) loads its US openings from its own JSON search API, not from a standard job board.
/// The listing is newest first, 10 jobs per page, with no descriptions (those come from the detail call).
/// </summary>
public sealed partial class JobBoardClient
{
    public const string AkkodisProvider = "Akkodis";
    public const string AkkodisCareersUrl = "https://www.akkodis.com/en-us/careers/job-results";
    private const string AkkodisApi = "https://www.akkodis.com/api/data/jobs";

    // Only the posted-date facet matters here; the page sends more facet ranges that this scan doesn't use.
    private const string AkkodisBaseQuery =
        "&facet.range=PostedDate&f.PostedDate.facet.range.start=NOW-30DAYS/DAY&f.PostedDate.facet.range.end=NOW&f.PostedDate.facet.range.gap=%2B1DAY/DAY";

    private async Task<List<BoardJob>> FetchAkkodisAsync(string company, CancellationToken ct)
    {
        var jobs = new List<BoardJob>();
        var recent = DateTimeOffset.UtcNow.AddDays(-2);
        var range = 0;
        // Newest first: stop once a whole page is older than the scan window (or after 60 pages as a safety stop).
        for (var page = 0; page < 60; page++)
        {
            using var res = await http.PostAsJsonAsync($"{AkkodisApi}/summarized", new
            {
                queryString = "&sort=PostedDate desc",
                baseSearchQuery = AkkodisBaseQuery,
                range,
                siteName = "akkodis", brand = "modis", countryCookie = "US", langCookie = "en", brandFromDictionary = "akkodis",
            }, ct);
            res.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var items = doc.RootElement.GetPropertyOrDefault("jobs") is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().ToList() : [];
            var recentOnPage = 0;
            foreach (var item in items)
            {
                var id = Str(item, "jobId");
                if (id == "") continue;
                var posted = DateTimeOffset.TryParse(Str(item, "postedDate"), out var d) ? d : (DateTimeOffset?)null;
                if (posted is { } p && p >= recent) recentOnPage++;
                var title = Str(item, "jobTitle").Trim();
                var location = Str(item, "jobLocation");
                var remote = item.GetPropertyOrDefault("isRemote").ValueKind == JsonValueKind.True
                             || location.Contains("remote", StringComparison.OrdinalIgnoreCase);
                jobs.Add(new BoardJob(id, title, company, location, remote, posted,
                    $"{AkkodisCareersUrl}?jobTitle={Uri.EscapeDataString(title)}&jobId={id.ToLowerInvariant()}", "", NeedsDetail: true));
            }
            var next = doc.RootElement.GetPropertyOrDefault("pagination").GetPropertyOrDefault("nextRange");
            if (items.Count == 0 || recentOnPage == 0 || next.ValueKind != JsonValueKind.Number || next.GetInt32() <= range) break;
            range = next.GetInt32();
        }
        return jobs;
    }

    private async Task<BoardJobDetail> FetchAkkodisDetailAsync(string jobId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{AkkodisApi}/job-description-details/{Uri.EscapeDataString(jobId)}/modis/US/en/job-details", ct);
        var root = doc.RootElement;
        var location = Str(root, "location");
        return new BoardJobDetail(StripHtml(Str(root, "jobDescription")), string.IsNullOrWhiteSpace(location) ? null : location, null);
    }
}
