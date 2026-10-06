using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobAgent.Api.Services;

/// <param name="NeedsDetail">The listing lacks the description (and for Workday, the precise location); fetch it with FetchDetailAsync.</param>
public sealed record BoardJob(
    string ExternalId, string Title, string Company, string Location, bool IsRemote,
    DateTimeOffset? PostedAt, string ApplyUrl, string Description, bool NeedsDetail = false);

/// <param name="PostedAt">Set when the detail page knows the posting date (Website sources); null keeps the listing's date.</param>
/// <param name="Title">Set when the detail page has a better title than the listing link text.</param>
public sealed record BoardJobDetail(string Description, string? Location, bool? IsRemote, DateTimeOffset? PostedAt = null, string? Title = null);

/// <summary>Reads public job-board APIs that companies use to power their own career pages.</summary>
public sealed partial class JobBoardClient(HttpClient http, AdzunaSettings adzuna)
{
    public static readonly string[] SupportedProviders = ["Greenhouse", "Lever", "Ashby", "SmartRecruiters", "Workday", "Website"];

    /// <summary>Recognizes a career-page URL hosted by a supported job board and returns its provider and board token.</summary>
    public static (string Provider, string Token)? ParseBoardUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = segments.FirstOrDefault();
        // Akkodis (US) careers pages: www.akkodis.com/en-us/careers/job-results?...
        if (host.EndsWith("akkodis.com") && uri.AbsolutePath.Contains("/careers/job-results", StringComparison.OrdinalIgnoreCase)) return (AkkodisProvider, "us");
        if (host.EndsWith("randstadusa.com") && uri.AbsolutePath.StartsWith("/jobs/internal", StringComparison.OrdinalIgnoreCase)) return (RandstadProvider, "internal");
        if (host.EndsWith("greenhouse.io"))
        {
            // boards.greenhouse.io/embed/job_board?for=token
            var forToken = System.Web.HttpUtility.ParseQueryString(uri.Query)["for"];
            if (!string.IsNullOrEmpty(forToken)) return ("Greenhouse", forToken);
            return first is null or "embed" ? null : ("Greenhouse", first);
        }
        if (host == "jobs.lever.co" && first is not null) return ("Lever", first);
        if (host == "jobs.ashbyhq.com" && first is not null) return ("Ashby", first);
        if (host is "jobs.smartrecruiters.com" or "careers.smartrecruiters.com" && first is not null) return ("SmartRecruiters", first);
        if (host.EndsWith(".myworkdayjobs.com"))
        {
            // https://nvidia.wd5.myworkdayjobs.com/en-US/NVIDIAExternalCareerSite/job/... → site is the first non-locale segment
            var site = segments.FirstOrDefault(s => !Locale().IsMatch(s));
            return site is null ? null : ("Workday", $"{host}/{site}");
        }
        return null;
    }

    /// <summary>Public careers portal listing all of a company's openings on the given board.</summary>
    public static string BoardUrl(string provider, string token) => provider switch
    {
        "Greenhouse" => $"https://job-boards.greenhouse.io/{token}",
        "Lever" => $"https://jobs.lever.co/{token}",
        "Ashby" => $"https://jobs.ashbyhq.com/{token}",
        "SmartRecruiters" => $"https://jobs.smartrecruiters.com/{token}",
        "Workday" => $"https://{token}",
        "Website" => token,
        "Phenom" => "https://" + token.Split('#')[0],
        AkkodisProvider => AkkodisCareersUrl,
        RandstadProvider => RandstadCareersUrl,
        _ => ""
    };

    /// <summary>
    /// Finds the job board behind a company's own careers page: either the URL redirects to a supported board,
    /// or the page links to or embeds one (e.g. a Greenhouse iframe or "View openings" link to Workday).
    /// </summary>
    public async Task<(string Provider, string Token)?> DiscoverBoardAsync(string url, CancellationToken ct)
    {
        if (ParseBoardUrl(url) is { } direct) return direct;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Many career sites refuse non-browser clients.
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        req.Headers.Accept.ParseAdd("text/html");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.RequestMessage?.RequestUri is { } final && ParseBoardUrl(final.ToString()) is { } redirected) return redirected;
        if (!res.IsSuccessStatusCode) return null;

        var html = await res.Content.ReadAsStringAsync(ct);
        if (DetectPhenom(res.RequestMessage?.RequestUri ?? new Uri(url), html) is { } phenom) return phenom;
        if (html.Length > 3_000_000) html = html[..3_000_000];
        foreach (Match m in EmbeddedBoard().Matches(WebUtility.HtmlDecode(html)))
        {
            var candidate = "https://" + m.Value.TrimEnd('\\', '"', '\'', '/');
            if (ParseBoardUrl(candidate) is { } found && !IgnoredTokens.Contains(found.Token.Split('/')[^1])) return found;
        }
        return null;
    }

    private static readonly HashSet<string> IgnoredTokens = new(StringComparer.OrdinalIgnoreCase) { "embed", "v1", "jobs", "api", "js", "static", "assets", "wday" };

    /// <summary>Guesses board names from a company name ("Sprout Social" → sproutsocial / sprout-social) and returns the first that exists.</summary>
    public async Task<(string Provider, string Token)?> ProbeByNameAsync(string companyName, CancellationToken ct)
    {
        var words = Regex.Matches(companyName.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value)
            .Where(w => w is not ("inc" or "llc" or "ltd" or "corp" or "corporation" or "co" or "company" or "the")).ToList();
        if (words.Count == 0) return null;
        var slugs = new[] { string.Concat(words), string.Join("-", words) }.Distinct().ToList();

        foreach (var (provider, slug) in slugs.SelectMany(s => new[] { ("Greenhouse", s), ("Lever", s), ("Ashby", s) }).DistinctBy(x => x))
        {
            try
            {
                if ((await FetchAsync(provider, slug, companyName, ct)).Count > 0) return (provider, slug);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException) { /* not on this board */ }
        }
        return null;
    }

    /// <param name="keywords">Role keywords; used by boards that must be searched rather than listed in full (Phenom).</param>
    public async Task<List<BoardJob>> FetchAsync(string provider, string token, string companyName, CancellationToken ct, IReadOnlyList<string>? keywords = null) => provider switch
    {
        "Greenhouse" => await FetchGreenhouseAsync(token, companyName, ct),
        "Lever" => await FetchLeverAsync(token, companyName, ct),
        "Ashby" => await FetchAshbyAsync(token, companyName, ct),
        "SmartRecruiters" => await FetchSmartRecruitersAsync(token, companyName, ct),
        "Workday" => await FetchWorkdayAsync(token, companyName, ct),
        "Website" => await FetchWebsiteAsync(token, companyName, ct),
        "Phenom" => await FetchPhenomAsync(token, companyName, keywords, ct),
        AkkodisProvider => await FetchAkkodisAsync(companyName, ct),
        RandstadProvider => await FetchRandstadAsync(companyName, ct),
        _ => throw new NotSupportedException($"Unsupported job board '{provider}'.")
    };

    public async Task<BoardJobDetail> FetchDetailAsync(string provider, string token, string externalId, CancellationToken ct)
    {
        if (provider == "SmartRecruiters")
        {
            using var doc = await GetJsonAsync($"https://api.smartrecruiters.com/v1/companies/{Uri.EscapeDataString(token)}/postings/{Uri.EscapeDataString(externalId)}", ct);
            var sections = doc.RootElement.GetPropertyOrDefault("jobAd").GetPropertyOrDefault("sections");
            var text = sections.ValueKind == JsonValueKind.Object
                ? string.Join("\n\n", sections.EnumerateObject().Select(s => Str(s.Value, "title") + "\n" + StripHtml(Str(s.Value, "text"))))
                : "";
            return new BoardJobDetail(text.Trim(), null, null);
        }
        if (provider == "Website") return await FetchWebsiteDetailAsync(externalId, ct);
        if (provider == AkkodisProvider) return await FetchAkkodisDetailAsync(externalId, ct);
        // Phenom listings have exact dates, locations and clean titles; the job page only adds the full description.
        if (provider == "Phenom") return (await FetchWebsiteDetailAsync(externalId, ct)) with { PostedAt = null, Location = null, Title = null };
        if (provider == "Workday")
        {
            var (host, tenant, site) = SplitWorkday(token);
            using var doc = await GetJsonAsync($"https://{host}/wday/cxs/{tenant}/{site}{externalId}", ct);
            var info = doc.RootElement.GetPropertyOrDefault("jobPostingInfo");
            var locations = new List<string> { Str(info, "location") };
            if (info.GetPropertyOrDefault("additionalLocations") is { ValueKind: JsonValueKind.Array } more) locations.AddRange(more.EnumerateArray().Select(x => x.ToString()));
            var location = string.Join("; ", locations.Where(x => x != "").Distinct());
            var remote = location.Contains("remote", StringComparison.OrdinalIgnoreCase) || Str(info, "remoteType").Contains("remote", StringComparison.OrdinalIgnoreCase);
            return new BoardJobDetail(StripHtml(Str(info, "jobDescription")), location, remote);
        }
        return new BoardJobDetail("", null, null);
    }

    private async Task<List<BoardJob>> FetchGreenhouseAsync(string token, string company, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://boards-api.greenhouse.io/v1/boards/{Uri.EscapeDataString(token)}/jobs?content=true", ct);
        var jobs = new List<BoardJob>();
        foreach (var j in doc.RootElement.GetProperty("jobs").EnumerateArray())
        {
            var locations = new List<string> { Str(j.GetPropertyOrDefault("location"), "name") };
            if (j.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Array)
                foreach (var m in meta.EnumerateArray().Where(m => Str(m, "name").Contains("location", StringComparison.OrdinalIgnoreCase)))
                    locations.AddRange(m.GetPropertyOrDefault("value") is { ValueKind: JsonValueKind.Array } arr
                        ? arr.EnumerateArray().Select(v => v.ToString()) : [Str(m, "value")]);
            var location = string.Join("; ", locations.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            var name = Str(j, "company_name");
            jobs.Add(new BoardJob(
                Str(j, "id"), Str(j, "title").Trim(), string.IsNullOrEmpty(name) ? company : name, location,
                location.Contains("remote", StringComparison.OrdinalIgnoreCase),
                Date(Str(j, "first_published")) ?? Date(Str(j, "updated_at")),
                Str(j, "absolute_url"), StripHtml(WebUtility.HtmlDecode(Str(j, "content")))));
        }
        return jobs;
    }

    private async Task<List<BoardJob>> FetchLeverAsync(string token, string company, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://api.lever.co/v0/postings/{Uri.EscapeDataString(token)}?mode=json", ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray().Select(j =>
        {
            var cats = j.GetPropertyOrDefault("categories");
            var location = Str(cats, "location");
            var lists = j.GetPropertyOrDefault("lists") is { ValueKind: JsonValueKind.Array } l
                ? string.Join("\n\n", l.EnumerateArray().Select(x => Str(x, "text") + "\n" + StripHtml(Str(x, "content")))) : "";
            var created = j.GetPropertyOrDefault("createdAt") is { ValueKind: JsonValueKind.Number } c
                ? DateTimeOffset.FromUnixTimeMilliseconds(c.GetInt64()) : (DateTimeOffset?)null;
            var applyUrl = Str(j, "applyUrl");
            return new BoardJob(
                Str(j, "id"), Str(j, "text").Trim(), company, location,
                Str(j, "workplaceType") == "remote" || location.Contains("remote", StringComparison.OrdinalIgnoreCase),
                created, string.IsNullOrEmpty(applyUrl) ? Str(j, "hostedUrl") : applyUrl,
                (Str(j, "descriptionPlain") + "\n\n" + lists + "\n\n" + Str(j, "additionalPlain")).Trim());
        }).ToList();
    }

    private async Task<List<BoardJob>> FetchAshbyAsync(string token, string company, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://api.ashbyhq.com/posting-api/job-board/{Uri.EscapeDataString(token)}", ct);
        return doc.RootElement.GetProperty("jobs").EnumerateArray()
            .Where(j => j.GetPropertyOrDefault("isListed").ValueKind != JsonValueKind.False)
            .Select(j =>
            {
                var locations = new List<string> { Str(j, "location") };
                if (j.GetPropertyOrDefault("secondaryLocations") is { ValueKind: JsonValueKind.Array } sec)
                    locations.AddRange(sec.EnumerateArray().Select(s => Str(s, "location")));
                var location = string.Join("; ", locations.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                var applyUrl = Str(j, "applyUrl");
                var html = Str(j, "descriptionHtml");
                return new BoardJob(
                    Str(j, "id"), Str(j, "title").Trim(), company, location,
                    j.GetPropertyOrDefault("isRemote").ValueKind == JsonValueKind.True || Str(j, "workplaceType") == "Remote",
                    Date(Str(j, "publishedAt")), string.IsNullOrEmpty(applyUrl) ? Str(j, "jobUrl") : applyUrl,
                    html != "" ? StripHtml(html) : Str(j, "descriptionPlain"));
            }).ToList();
    }

    private async Task<List<BoardJob>> FetchSmartRecruitersAsync(string token, string company, CancellationToken ct)
    {
        var jobs = new List<BoardJob>();
        // Postings come newest first; a few pages covers anything published in the last day or two.
        for (var offset = 0; offset < 500; offset += 100)
        {
            using var doc = await GetJsonAsync($"https://api.smartrecruiters.com/v1/companies/{Uri.EscapeDataString(token)}/postings?limit=100&offset={offset}", ct);
            var page = doc.RootElement.GetProperty("content").EnumerateArray().ToList();
            foreach (var j in page)
            {
                var loc = j.GetPropertyOrDefault("location");
                var location = string.Join(", ", new[] { Str(loc, "city"), Str(loc, "region"), Str(loc, "country") }.Where(x => x != ""));
                var remote = loc.GetPropertyOrDefault("remote").ValueKind == JsonValueKind.True;
                if (remote) location = string.IsNullOrEmpty(location) ? "Remote" : $"Remote; {location}";
                var id = Str(j, "id");
                jobs.Add(new BoardJob(id, Str(j, "name").Trim(), Str(j.GetPropertyOrDefault("company"), "name") is { Length: > 0 } n ? n : company,
                    location, remote, Date(Str(j, "releasedDate")), $"https://jobs.smartrecruiters.com/{token}/{id}", "", NeedsDetail: true));
            }
            if (page.Count < 100) break;
        }
        return jobs;
    }

    private async Task<List<BoardJob>> FetchWorkdayAsync(string token, string company, CancellationToken ct)
    {
        var (host, tenant, site) = SplitWorkday(token);
        var jobs = new List<BoardJob>();
        // Workday lists newest first and only says "Posted Today / Yesterday / N Days Ago"; stop once a page has nothing recent.
        for (var offset = 0; offset < 600; offset += 20)
        {
            using var res = await http.PostAsJsonAsync($"https://{host}/wday/cxs/{tenant}/{site}/jobs",
                new { appliedFacets = new { }, limit = 20, offset, searchText = "" }, ct);
            if (res.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Job board not found (check the careers link).");
            res.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var page = doc.RootElement.GetPropertyOrDefault("jobPostings") is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().ToList() : [];
            var recentOnPage = 0;
            foreach (var p in page)
            {
                var posted = WorkdayPostedOn(Str(p, "postedOn"));
                if (posted is { } d && d >= DateTimeOffset.UtcNow.AddDays(-2)) recentOnPage++;
                var path = Str(p, "externalPath");
                // "/job/US-CA-Santa-Clara/Title_JR123" — the location segment helps when locationsText is just "3 Locations".
                var pathLocation = path.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } seg ? seg[1].Replace('-', ' ') : "";
                var location = $"{Str(p, "locationsText")}; {pathLocation}".Trim(' ', ';');
                jobs.Add(new BoardJob(path, Str(p, "title").Trim(), company, location,
                    location.Contains("remote", StringComparison.OrdinalIgnoreCase), posted,
                    $"https://{host}/{site}{path}", "", NeedsDetail: true));
            }
            if (page.Count < 20 || recentOnPage == 0) break;
        }
        return jobs;
    }

    private static (string Host, string Tenant, string Site) SplitWorkday(string token)
    {
        var slash = token.IndexOf('/');
        var host = token[..slash];
        return (host, host.Split('.')[0], token[(slash + 1)..]);
    }

    private static DateTimeOffset? WorkdayPostedOn(string text)
    {
        var now = DateTimeOffset.UtcNow;
        if (text.Contains("today", StringComparison.OrdinalIgnoreCase)) return now;
        if (text.Contains("yesterday", StringComparison.OrdinalIgnoreCase)) return now.AddDays(-1);
        var m = DaysAgo().Match(text);
        return m.Success ? now.AddDays(-int.Parse(m.Groups[1].Value)) : null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Job board not found (check the careers link).");
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v.ToString() : "";

    // Npgsql only stores UTC offsets in timestamptz columns.
    private static DateTimeOffset? Date(string s) => DateTimeOffset.TryParse(s, out var d) ? d.ToUniversalTime() : null;

    /// <summary>HTML to readable plain text: block elements become line breaks, list items become bullets.</summary>
    private static string StripHtml(string html)
    {
        var text = WebUtility.HtmlDecode(html);
        text = ListItem().Replace(text, "\n• ");
        text = BlockBreak().Replace(text, "\n");
        text = WebUtility.HtmlDecode(Tags().Replace(text, ""));
        text = Whitespace().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return string.Join("\n", text.Split('\n').Select(l => l.Trim())).Trim();
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ListItem();
    [GeneratedRegex(@"<br\s*/?>|</(p|div|h[1-6]|ul|ol|li|tr)>|<(p|div|h[1-6]|ul|ol)[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex BlockBreak();
    [GeneratedRegex(@"[ \t\r\f\v ]+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"\n\s*\n(\s*\n)+")] private static partial Regex BlankLines();
    [GeneratedRegex(@"(\d+)\+?\s+days?\s+ago", RegexOptions.IgnoreCase)] private static partial Regex DaysAgo();
    [GeneratedRegex(@"^[a-z]{2}-[A-Z]{2}$")] private static partial Regex Locale();
    [GeneratedRegex(@"(?:boards\.greenhouse\.io/embed/job_board(?:/js)?\?for=[\w\-]+|(?:job-)?boards\.greenhouse\.io/[\w\-]+|jobs\.lever\.co/[\w.\-]+|jobs\.ashbyhq\.com/[\w.\-]+|(?:jobs|careers)\.smartrecruiters\.com/[\w\-]+|[\w\-]+\.wd\d+\.myworkdayjobs\.com/(?:[a-z]{2}-[A-Z]{2}/)?[\w\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedBoard();
}

internal static class JsonElementExtensions
{
    public static JsonElement GetPropertyOrDefault(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}
