using System.Net;
using System.Text.Json;

namespace JobAgent.Api.Services;

/// <summary>
/// Free public remote-job feeds (Remotive, Remote OK). No keys needed. Their terms require linking back to the
/// original listing, so the apply link is always the feed's own job page.
/// </summary>
public sealed partial class JobBoardClient
{
    public const string RemotiveProvider = "Remotive";
    public const string RemoteOkProvider = "RemoteOK";
    public static readonly string[] FeedProviders = [RemotiveProvider, RemoteOkProvider];

    /// <summary>True for sources that are searched or listed as a whole rather than one company's board.</summary>
    public static bool IsAggregator(string provider) => provider == AdzunaProvider || FeedProviders.Contains(provider);

    public Task<List<BoardJob>> FetchFeedAsync(string provider, CancellationToken ct) =>
        provider == RemotiveProvider ? FetchRemotiveAsync(ct) : FetchRemoteOkAsync(ct);

    private async Task<List<BoardJob>> FetchRemotiveAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://remotive.com/api/remote-jobs?category=software-dev&limit=500", ct);
        return doc.RootElement.GetPropertyOrDefault("jobs") is { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().Select(j => new BoardJob(
                "remotive:" + Str(j, "id"), WebUtility.HtmlDecode(Str(j, "title")).Trim(), WebUtility.HtmlDecode(Str(j, "company_name")).Trim(),
                FeedLocation(Str(j, "candidate_required_location")), true, Date(Str(j, "publication_date")),
                Str(j, "url"), StripHtml(Str(j, "description")))).ToList()
            : [];
    }

    private async Task<List<BoardJob>> FetchRemoteOkAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://remoteok.com/api");
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (JobAgent)");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        // The first element is a legal notice, not a job.
        return doc.RootElement.EnumerateArray().Where(j => j.ValueKind == JsonValueKind.Object && Str(j, "position") != "")
            .Select(j => new BoardJob(
                "remoteok:" + Str(j, "id"), WebUtility.HtmlDecode(Str(j, "position")).Trim(), WebUtility.HtmlDecode(Str(j, "company")).Trim(),
                FeedLocation(Str(j, "location")), true, Date(Str(j, "date")),
                Str(j, "url"), StripHtml(Str(j, "description")))).ToList();
    }

    // Remote feeds say "Worldwide" / "Anywhere" / "USA only"; spell out the country so location filters can match it.
    private static string FeedLocation(string location)
    {
        location = location.Trim();
        if (location == "") return "Remote; United States";
        var l = location.ToLowerInvariant();
        return l.Contains("worldwide") || l.Contains("anywhere") ? $"{location}; United States"
            : l is "usa" or "us" or "u.s." or "usa only" ? "United States" : location;
    }
}
