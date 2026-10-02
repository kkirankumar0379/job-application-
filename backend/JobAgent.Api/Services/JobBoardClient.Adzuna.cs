using System.Net;
using System.Text.Json;

namespace JobAgent.Api.Services;

/// <summary>
/// Adzuna job-search API: jobs aggregated from many US employers and job boards. Searched per role keyword,
/// newest first. Descriptions are snippets, and apply links go through Adzuna to the original posting.
/// </summary>
public sealed partial class JobBoardClient
{
    public const string AdzunaProvider = "Adzuna";
    public bool AdzunaConfigured => adzuna.IsConfigured;
    private const int AdzunaPageSize = 50;
    private const int AdzunaPagesPerKeyword = 2;

    public async Task<List<BoardJob>> FetchAdzunaAsync(IReadOnlyList<string>? keywords, int maxDaysOld, CancellationToken ct)
    {
        if (adzuna.AppId is not { } appId || adzuna.AppKey is not { } appKey) return [];
        var jobs = new Dictionary<string, BoardJob>();
        foreach (var keyword in (keywords is { Count: > 0 } ? keywords : ["software engineer"]).Take(8))
        {
            for (var page = 1; page <= AdzunaPagesPerKeyword; page++)
            {
                var results = await SearchAdzunaAsync(appId, appKey, keyword, page, AdzunaPageSize, maxDaysOld, ct);
                foreach (var j in results)
                {
                    var id = Str(j, "id");
                    if (id == "" || jobs.ContainsKey(id)) continue;
                    var loc = j.GetPropertyOrDefault("location");
                    var location = Str(loc, "display_name");
                    if (loc.GetPropertyOrDefault("area") is { ValueKind: JsonValueKind.Array } area)
                        // Area runs country → state → county → city, e.g. ["US","Texas","Dallas County","Dallas"]; keep it whole
                        // so the country matches location filters like "united states" / "us".
                        location = string.Join(", ", area.EnumerateArray().Select(x => x.ToString()).Where(x => x != "")) is { Length: > 0 } full ? $"{location}; {full}" : location;
                    var title = WebUtility.HtmlDecode(Str(j, "title")).Trim();
                    var description = WebUtility.HtmlDecode(Str(j, "description")).Trim();
                    var remote = $"{title} {location} {description}".Contains("remote", StringComparison.OrdinalIgnoreCase);
                    jobs[id] = new BoardJob("adzuna:" + id, title, WebUtility.HtmlDecode(Str(j.GetPropertyOrDefault("company"), "display_name")).Trim(),
                        location, remote, Date(Str(j, "created")), Str(j, "redirect_url"), description);
                }
                if (results.Count < AdzunaPageSize) break;
            }
        }
        return jobs.Values.ToList();
    }

    /// <summary>One search call; also used to check credentials when they're saved.</summary>
    public async Task<List<JsonElement>> SearchAdzunaAsync(string appId, string appKey, string what, int page, int size, int maxDaysOld, CancellationToken ct)
    {
        var url = $"https://api.adzuna.com/v1/api/jobs/us/search/{page}?app_id={Uri.EscapeDataString(appId)}&app_key={Uri.EscapeDataString(appKey)}" +
                  $"&title_only={Uri.EscapeDataString(what)}&results_per_page={size}&max_days_old={Math.Clamp(maxDaysOld, 1, 30)}&sort_by=date&content-type=application/json";
        using var res = await http.GetAsync(url, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Adzuna rejected the App ID / App Key. Check them on the Profile tab.");
        if ((int)res.StatusCode == 429) throw new InvalidOperationException("Adzuna's rate limit was reached; it will be retried on the next scan.");
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetPropertyOrDefault("results") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Select(x => x.Clone()).ToList()
            : [];
    }
}
