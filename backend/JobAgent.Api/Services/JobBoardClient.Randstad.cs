using System.Net.Http.Json;
using System.Text.Json;

namespace JobAgent.Api.Services;

/// <summary>
/// Randstad USA's own openings (randstadusa.com/jobs/internal/) come from its site search API:
/// 30 jobs per page, with the full description included, so no per-job detail call is needed.
/// </summary>
public sealed partial class JobBoardClient
{
    public const string RandstadProvider = "Randstad";
    public const string RandstadCareersUrl = "https://www.randstadusa.com/jobs/internal/";

    private async Task<List<BoardJob>> FetchRandstadAsync(string company, CancellationToken ct)
    {
        var jobs = new List<BoardJob>();
        for (var page = 1; page <= 20; page++)
        {
            var slug = $"page-{page}";
            using var res = await http.PostAsJsonAsync("https://www.randstadusa.com/api/search/search-results", new
            {
                data = new
                {
                    currentRoute = new
                    {
                        path = "/jobs/internal/:searchParams*", url = $"/jobs/internal/{slug}/", isExact = true,
                        @params = new { searchParams = slug }, routeName = "internal-search",
                    },
                    currentLanguage = "en",
                    cookies = new { },
                },
            }, ct);
            res.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var results = doc.RootElement.GetPropertyOrDefault("searchResults");
            var hits = results.GetPropertyOrDefault("hits") is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().ToList() : [];
            if (hits.Count == 0) break;

            foreach (var hit in hits)
            {
                var title = Str(hit, "title").Trim();
                var applyUrl = Str(hit, "applyUrl") is { Length: > 0 } a ? a : Str(hit, "detailsUrl");
                if (title == "" || applyUrl == "") continue;
                var place = hit.GetPropertyOrDefault("jobLocation");
                var city = Str(place, "city");
                var state = Str(place, "stateAbbreviation");
                var location = city == "" ? "" : state == "" ? city : $"{city}, {state}";
                var created = hit.GetPropertyOrDefault("createdDate") is { ValueKind: JsonValueKind.Number } ms && ms.TryGetInt64(out var epoch)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : (DateTimeOffset?)null;
                var remote = hit.GetPropertyOrDefault("isRemote").ValueKind == JsonValueKind.True
                             || location.Contains("remote", StringComparison.OrdinalIgnoreCase);
                jobs.Add(new BoardJob(Str(hit, "id"), title, company, location, remote, created, applyUrl, StripHtml(Str(hit, "description"))));
            }
            if (hits.Count < 30) break;
        }
        return jobs;
    }
}
