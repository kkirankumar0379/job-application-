using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobAgent.Api.Services;

/// <summary>
/// "Website" sources: career pages that don't use a supported job board (e.g. staffing firms on Bullhorn or
/// WordPress). The listing page is read for links to individual job pages, and each job page's schema.org
/// JobPosting data (the markup sites publish for Google Jobs) supplies title, date, location and description.
/// </summary>
public sealed partial class JobBoardClient
{
    private const int MaxWebsiteLinks = 80;
    private const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";

    // Job pages rarely change once posted; don't refetch them on every scan.
    private static readonly ConcurrentDictionary<string, (BoardJobDetail Detail, DateTimeOffset FetchedAt)> WebsiteDetailCache = new();

    private static readonly string[] JobPathWords = ["job", "jobs", "career", "careers", "position", "positions", "opening", "openings", "vacancy", "vacancies", "requisition", "posting", "postings", "opportunity", "opportunities", "role", "roles"];
    private static readonly HashSet<string> NonJobSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "login", "signin", "sign-in", "register", "signup", "privacy", "privacy-policy", "terms", "about", "about-us", "contact", "contact-us", "blog", "news",
        "events", "category", "categories", "tag", "tags", "page", "feed", "wp-json", "wp-content", "saved", "saved-jobs", "alerts", "job-alerts", "benefits",
        "culture", "faq", "faqs", "locations", "teams", "departments", "search", "search-jobs", "all-jobs", "submit-resume", "upload-resume", "employers", "hire",
        "clients", "resources", "insights", "press", "media", "sitemap", "cookie-policy", "accessibility", "life-at", "our-team", "leadership",
    };
    private static readonly HashSet<string> GenericLinkText = new(StringComparer.OrdinalIgnoreCase)
    {
        "apply", "apply now", "view", "view job", "view details", "details", "learn more", "read more", "more", "see more", "see job", "job details", "open", "click here",
    };

    private async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(BrowserAgent);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("That page was not found (404).");
        res.EnsureSuccessStatusCode();
        var html = await res.Content.ReadAsStringAsync(ct);
        return html.Length > 4_000_000 ? html[..4_000_000] : html;
    }

    /// <summary>Links on the listing page that look like individual job pages; titles come from the link text or URL slug.</summary>
    private async Task<List<BoardJob>> FetchWebsiteAsync(string listingUrl, string company, CancellationToken ct)
    {
        var listing = new Uri(listingUrl);
        var html = await GetHtmlAsync(listingUrl, ct);
        if (SiteName(html) is { Length: > 0 } siteName) SiteNames[listingUrl] = siteName;
        var jobs = new List<BoardJob>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Anchor().Matches(html))
        {
            if (!Uri.TryCreate(listing, WebUtility.HtmlDecode(m.Groups[1].Value.Trim()), out var link)) continue;
            if (link.Scheme is not ("http" or "https")) continue;
            var clean = new UriBuilder(link) { Fragment = "" }.Uri.ToString();
            var text = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(m.Groups[2].Value, " ")), " ").Trim();
            if (!IsLikelyJobLink(link, listing) || !seen.Add(clean)) continue;

            var title = text.Length is >= 4 and <= 140 && !GenericLinkText.Contains(text) ? text : TitleFromSlug(link);
            if (string.IsNullOrWhiteSpace(title)) continue;
            jobs.Add(new BoardJob(clean, title, company, "", false, null, clean, "", NeedsDetail: true));
            if (jobs.Count >= MaxWebsiteLinks) break;
        }
        return jobs;
    }

    /// <summary>
    /// When a careers link is wrong (404) or shows no jobs, look on that page and the site's home page for links
    /// like "Search jobs" / "Careers" (or straight to a job board) and return them, best first.
    /// </summary>
    public async Task<List<string>> FindCareerLinksAsync(string url, CancellationToken ct)
    {
        var start = new Uri(url);
        var pages = new List<Uri> { start };
        var home = new Uri($"{start.Scheme}://{start.Host}/");
        if (home != start) pages.Add(home);

        var scored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            string html;
            try { html = await GetHtmlAsync(page.ToString(), ct); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException) { continue; }
            foreach (Match m in Anchor().Matches(html))
            {
                if (!Uri.TryCreate(page, WebUtility.HtmlDecode(m.Groups[1].Value.Trim()), out var link) || link.Scheme is not ("http" or "https")) continue;
                var text = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(m.Groups[2].Value, " ")), " ").Trim().ToLowerInvariant();
                var href = link.ToString();
                var score = 0;
                if (ParseBoardUrl(href) is not null || href.Contains("myworkdayjobs.com") || href.Contains("/widgets")) score += 100;
                if (Regex.IsMatch(text, @"\b(search|find|browse|view|see|explore|current|open)\s+(all\s+)?(jobs|openings|positions|opportunities|roles)\b|\bjob search\b|\bjob openings\b")) score += 50;
                else if (Regex.IsMatch(text, @"^(jobs|careers|join us|work with us|job seekers|find a job|find work|for job seekers)$")) score += 30;
                if (Regex.IsMatch(link.AbsolutePath, @"/(jobs?|careers?|job-search|search-jobs|find-a-job|find-work|openings|opportunities)(/|$)", RegexOptions.IgnoreCase)) score += 15;
                if (score == 0 || !(SameSite(link.Host, start.Host) || score >= 100)) continue;
                var clean = new UriBuilder(link) { Fragment = "" }.Uri.ToString();
                if (clean.TrimEnd('/') == url.TrimEnd('/')) continue;
                scored[clean] = Math.Max(scored.GetValueOrDefault(clean), score);
            }
        }
        return scored.OrderByDescending(x => x.Value).Select(x => x.Key).Take(4).ToList();
    }

    private static readonly ConcurrentDictionary<string, string> SiteNames = new();

    /// <summary>The name a careers website gives itself, if it was seen while reading that listing page.</summary>
    public static string? WebsiteName(string listingUrl) => SiteNames.TryGetValue(listingUrl, out var n) ? n : null;

    private static string SiteName(string html)
    {
        var name = WebUtility.HtmlDecode(Meta(html, "og:site_name")).Trim();
        if (name.Length == 0)
        {
            // "Tech Jobs | Motion Recruitment" → the brand is usually the last part of the title.
            var title = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value;
            name = WebUtility.HtmlDecode(title).Split('|', '–', '—', '-').Select(p => p.Trim()).LastOrDefault(p => p.Length > 0) ?? "";
        }
        return name.Length is > 1 and <= 60 ? name : "";
    }

    private static bool IsLikelyJobLink(Uri link, Uri listing)
    {
        if (!SameSite(link.Host, listing.Host)) return false;
        var path = link.AbsolutePath.TrimEnd('/');
        if (path.Length == 0 || path.Equals(listing.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return false;
        if (Regex.IsMatch(path, @"\.(pdf|jpe?g|png|gif|svg|css|js|xml|zip|docx?|ico|webp)$", RegexOptions.IgnoreCase)) return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(NonJobSegments.Contains)) return false;
        var last = segments[^1];
        var underListing = listing.AbsolutePath.TrimEnd('/') is { Length: > 0 } lp && path.StartsWith(lp + "/", StringComparison.OrdinalIgnoreCase);
        var jobWordInPath = segments.Take(segments.Length - 1).Any(s => JobPathWords.Any(w => s.Equals(w, StringComparison.OrdinalIgnoreCase)))
                            || segments.Any(s => s.StartsWith("job", StringComparison.OrdinalIgnoreCase));
        if (!underListing && !jobWordInPath) return false;
        // A specific posting ends in an id ("…/888695") or a descriptive slug ("…/cloud-platform-engineer"), not a category word.
        return Regex.IsMatch(last, @"\d{3,}") || last.Count(c => c is '-' or '_') >= 1 && last.Length >= 8 && !JobPathWords.Contains(last.ToLowerInvariant());
    }

    private static bool SameSite(string a, string b)
    {
        static string Base(string host) => string.Join('.', host.ToLowerInvariant().Split('.').TakeLast(2));
        return Base(a) == Base(b);
    }

    private static string TitleFromSlug(Uri link)
    {
        var slug = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(s => !Regex.IsMatch(s, @"^\d+$")) ?? "";
        slug = Regex.Replace(slug, @"[-_]\d+$", "");
        var words = slug.Split('-', '_').Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(" ", words);
    }

    private async Task<BoardJobDetail> FetchWebsiteDetailAsync(string url, CancellationToken ct)
    {
        if (WebsiteDetailCache.TryGetValue(url, out var cached) && cached.FetchedAt > DateTimeOffset.UtcNow.AddHours(-12)) return cached.Detail;
        var html = await GetHtmlAsync(url, ct);
        var detail = ParseJobPage(html);
        WebsiteDetailCache[url] = (detail, DateTimeOffset.UtcNow);
        return detail;
    }

    /// <summary>Reads schema.org JobPosting JSON-LD; falls back to page publish date and meta tags for sites without it.</summary>
    internal static BoardJobDetail ParseJobPage(string html)
    {
        JsonElement? posting = null;
        DateTimeOffset? published = null;
        var docs = new List<JsonDocument>();
        try
        {
            foreach (Match m in JsonLd().Matches(html))
            {
                try
                {
                    var doc = JsonDocument.Parse(m.Groups[1].Value.Trim(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    docs.Add(doc);
                    foreach (var node in Walk(doc.RootElement))
                    {
                        if (posting is null && HasType(node, "JobPosting")) posting = node;
                        if (published is null && Date(Str(node, "datePublished")) is { } p) published = p;
                    }
                }
                catch (JsonException) { /* sites sometimes emit invalid JSON-LD; skip that block */ }
            }

            if (posting is { } j)
            {
                var locations = new List<string>();
                var remote = Str(j, "jobLocationType").Contains("TELECOMMUTE", StringComparison.OrdinalIgnoreCase);
                foreach (var loc in AsArray(j.GetPropertyOrDefault("jobLocation")))
                {
                    var addr = loc.GetPropertyOrDefault("address");
                    var country = addr.GetPropertyOrDefault("addressCountry") is { ValueKind: JsonValueKind.Object } c ? Str(c, "name") : Str(addr, "addressCountry");
                    var parts = new[] { Str(addr, "addressLocality"), Str(addr, "addressRegion"), country }.Where(x => x != "");
                    if (parts.Any()) locations.Add(string.Join(", ", parts));
                }
                foreach (var req in AsArray(j.GetPropertyOrDefault("applicantLocationRequirements")))
                    if (Str(req, "name") is { Length: > 0 } n) locations.Add(remote ? $"Remote ({n})" : n);
                if (remote && locations.Count == 0) locations.Add("Remote");
                return new BoardJobDetail(
                    StripHtml(WebUtility.HtmlDecode(Str(j, "description"))),
                    locations.Count > 0 ? string.Join("; ", locations.Distinct()) : null,
                    remote || locations.Any(l => l.Contains("remote", StringComparison.OrdinalIgnoreCase)),
                    Date(Str(j, "datePosted")) ?? published,
                    WebUtility.HtmlDecode(Str(j, "title")).Trim() is { Length: > 0 } t ? t : null);
            }
        }
        finally { docs.ForEach(d => d.Dispose()); }

        // No JobPosting markup: use what the page does say about itself.
        published ??= Date(Meta(html, "article:published_time"));
        var title = WebUtility.HtmlDecode(Meta(html, "og:title")).Trim();
        var description = EmbeddedDescription(html) ?? WebUtility.HtmlDecode(Meta(html, "og:description")).Trim();
        return new BoardJobDetail(description, null, null, published, title.Length > 0 ? title : null);
    }

    /// <summary>
    /// Career apps (e.g. Phenom) often leave the JSON-LD block empty for the browser to fill in, but embed the job
    /// in page data as "description":"&lt;p&gt;...". Take the longest such string, if it is long enough to be a real description.
    /// </summary>
    private static string? EmbeddedDescription(string html)
    {
        var longest = EmbeddedDescriptionJson().Matches(html).Select(m => m.Groups[1].Value).OrderByDescending(v => v.Length).FirstOrDefault();
        if (longest is not { Length: > 300 }) return null;
        try { return StripHtml(JsonSerializer.Deserialize<string>("\"" + longest + "\"") ?? ""); }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<JsonElement> Walk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            yield return e;
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    foreach (var x in Walk(p.Value)) yield return x;
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                foreach (var x in Walk(item)) yield return x;
    }

    private static bool HasType(JsonElement e, string type) =>
        e.GetPropertyOrDefault("@type") is var t && (t.ValueKind == JsonValueKind.String && t.GetString() == type
            || t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == type));

    private static IEnumerable<JsonElement> AsArray(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList() : e.ValueKind == JsonValueKind.Object ? [e] : [];

    private static string Meta(string html, string property)
    {
        var m = Regex.Match(html, $@"<meta[^>]+(?:property|name)=[""']{Regex.Escape(property)}[""'][^>]*content=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(html, $@"<meta[^>]+content=[""']([^""']*)[""'][^>]*(?:property|name)=[""']{Regex.Escape(property)}[""']", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    [GeneratedRegex(@"<a\s[^>]*?href\s*=\s*[""']([^""'#][^""']*)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();
    [GeneratedRegex(@"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLd();

    // A JSON string value for a "description" key, allowing escaped characters inside it.
    [GeneratedRegex(@"""description""\s*:\s*""((?:[^""\\]|\\.){300,})""")]
    private static partial Regex EmbeddedDescriptionJson();
}
