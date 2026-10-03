using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using JobAgent.Api.Data;
using JobAgent.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobAgent.Api.Services;

public sealed class ScanProgress(int companiesTotal)
{
    public int CompaniesTotal { get; } = companiesTotal;
    public int CompaniesDone;
    public int JobsSeen;
    public int JobsSaved;
}

/// <summary>Per-step counts for one scan, plus why jobs were dropped after their job page was read.</summary>
public sealed class ScanFunnel
{
    private readonly ConcurrentDictionary<string, int> passed = new();
    private readonly ConcurrentDictionary<string, int> rejected = new();
    private readonly ConcurrentQueue<string> locationSamples = new();
    private readonly List<string> order = [];

    public void Add(string step)
    {
        lock (order) if (!order.Contains(step)) order.Add(step);
        passed.AddOrUpdate(step, 1, (_, n) => n + 1);
    }

    public void Reject(string reason) => rejected.AddOrUpdate(reason, 1, (_, n) => n + 1);

    public void SampleLocation(string location)
    {
        if (locationSamples.Count < 25 && !string.IsNullOrWhiteSpace(location)) locationSamples.Enqueue(location);
    }

    public string ToJson(int seen)
    {
        string[] stepOrder =
        [
            "posted within your time window", "title matches your job titles", "not skipped by level / skip words",
            "not a federal / clearance title", "not a blocked company", "new (not already in your feed)", "saved to your feed",
        ];
        var steps = new List<object> { new { step = "open roles checked", count = seen } };
        steps.AddRange(stepOrder.Select(st => (object)new { step = st, count = passed.GetValueOrDefault(st) }));
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            steps,
            droppedAfterReadingJobPage = rejected.OrderByDescending(r => r.Value).Select(r => new { reason = r.Key, count = r.Value }),
            locationSamples = locationSamples.Distinct().Take(15),
        });
    }
}

public interface IJobDiscoveryService
{
    Task<DiscoveryRun> RunAsync(Guid candidateProfileId, CancellationToken ct);
    Task<int> RescoreAsync(Guid candidateProfileId, CancellationToken ct);
}

/// <summary>Scans company career boards for fresh postings matching the candidate's search preferences.</summary>
public sealed partial class JobDiscoveryService(
    AppDbContext db, JobBoardClient boards, IJobAnalysisService analysis, ILogger<JobDiscoveryService> logger) : IJobDiscoveryService
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private const int MaxJobsPerRun = 1000;

    public static DateTimeOffset? RunningSince { get; private set; }

    /// <summary>Live counters for the scan in progress (null when idle), shown in the feed header.</summary>
    public static ScanProgress? Progress { get; private set; }

    // Leadership titles, including the officer titles banks give senior individual contributors
    // ("Assistant Vice President", "AVP") and Citi's VP-level grade bands (C13-C15).
    private static readonly string[] LeadershipTitles = ["vp", "vice president", "assistant vice president", "avp", "svp", "evp", "c13", "c14", "c15"];
    private static readonly string[] EntryExcludes = ["senior", "sr", "staff", "principal", "lead", "manager", "director", "head", "architect", "distinguished", .. LeadershipTitles];
    private static readonly string[] MidExcludes = ["staff", "principal", "director", "head", "distinguished", "intern", "new grad", .. LeadershipTitles];
    private static readonly string[] SeniorExcludes = ["junior", "jr", "intern", "internship", "new grad", "entry level", "entry-level", "apprentice"];

    public async Task<DiscoveryRun> RunAsync(Guid candidateProfileId, CancellationToken ct)
    {
        if (!await RunLock.WaitAsync(0, ct)) throw new InvalidOperationException("A job scan is already running. It usually takes a minute or two.");
        RunningSince = DateTimeOffset.UtcNow;
        try { return await RunCoreAsync(candidateProfileId, ct); }
        finally { RunningSince = null; Progress = null; RunLock.Release(); }
    }

    private async Task<DiscoveryRun> RunCoreAsync(Guid candidateProfileId, CancellationToken ct)
    {
        var profile = await db.CandidateProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == candidateProfileId, ct) ?? throw new KeyNotFoundException("Profile not found.");
        var candidateSkills = JobAnalysisService.CandidateSkills(profile);
        var prefs = await db.SearchPreferences.FirstOrDefaultAsync(x => x.CandidateProfileId == candidateProfileId, ct)
                    ?? new SearchPreferences { CandidateProfileId = candidateProfileId };
        // Workers read these on other threads, so they are untracked; scan results are written back at the end.
        var companies = await db.CompanySources.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);

        var run = new DiscoveryRun { CandidateProfileId = candidateProfileId };
        db.DiscoveryRuns.Add(run);
        var useAdzuna = boards.AdzunaConfigured;
        var progress = Progress = new ScanProgress(companies.Count + (useAdzuna ? 1 : 0) + JobBoardClient.FeedProviders.Length);

        var roles = Csv(prefs.RoleKeywordsCsv);
        var excludes = TitleExclusions(prefs);
        var locations = Csv(prefs.LocationsCsv);
        var primarySkills = PrimarySkills(prefs);
        // Sites that must be searched (Phenom, Adzuna) get the main stack first: ".net developer", "react developer", then the job titles.
        var searchKeywords = SkillCatalog.Normalize(primarySkills).Where(SkillCatalog.IsCore)
            .Select(s => $"{s.ToLowerInvariant()} developer").Concat(roles).Distinct().ToList();
        var blocked = Csv(prefs.BlockedCompaniesCsv);
        bool IsBlocked(string company) => blocked.Any(b => ContainsWord(company, b));
        var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(prefs.MaxAgeHours, 1, 24 * 14));

        // Already-saved and in-flight apply URLs, shared across workers so a job listed by two boards is kept once.
        var seenUrls = new ConcurrentDictionary<string, byte>(
            (await db.JobPostings.Where(x => x.CandidateProfileId == candidateProfileId).Select(x => x.ApplyUrl).ToListAsync(ct))
            .Distinct().Select(u => new KeyValuePair<string, byte>(u, 0)));
        var seenJobKeys = new ConcurrentDictionary<string, byte>(
            (await db.JobPostings.Where(x => x.CandidateProfileId == candidateProfileId).Select(x => new { x.Company, x.Title }).ToListAsync(ct))
            .Select(x => JobKey(x.Company, x.Title)).Distinct().Select(k => new KeyValuePair<string, byte>(k, 0)));
        // Roles you marked "Not interested" stay out of the feed, even when a scan finds a new posting of them (e.g. another city).
        var dismissedKeys = (await db.JobPostings.Where(x => x.CandidateProfileId == candidateProfileId && x.IsDismissed).Select(x => new { x.Company, x.Title }).ToListAsync(ct))
            .Select(x => JobKey(x.Company, x.Title)).ToHashSet();
        var sourceResults = new ConcurrentDictionary<Guid, (int? Count, string Error, string? Name)>();
        var candidatesTotal = 0;

        // Counts how many jobs survive each step, so a scan can explain why it found what it found.
        var funnel = new ScanFunnel();
        IEnumerable<BoardJob> Step(IEnumerable<BoardJob> jobs, string name, Func<BoardJob, bool> keep) =>
            jobs.Where(j => { var ok = keep(j); if (ok) funnel.Add(name); return ok; });

        // Final checks once the job page has been read (date, precise location, full description); null = keep.
        string? RejectReason(BoardJob j)
        {
            if (j.PostedAt is not { } posted || posted < cutoff) return "posted too long ago (after reading the job page)";
            if (excludes.Any(e => ContainsWord(j.Title, e))) return "title has a skipped word or level";
            if (prefs.RemoteOnly && !j.IsRemote) return "not remote";
            if (!LocationMatches(j, locations)) { funnel.SampleLocation(j.Location); return "location outside your locations"; }
            if (prefs.NeedsSponsorship && NoSponsorship().IsMatch(j.Description)) return "says no visa sponsorship";
            if (prefs.ExcludeFederalJobs && (FederalTitle().IsMatch(j.Title) || ClearanceRequired().IsMatch(j.Description))) return "federal / clearance required";
            if (IsBlocked(j.Company)) return "blocked company";
            return null;
        }

        // Workers push matching jobs into a channel; this method saves them in small batches as they arrive,
        // so the feed fills in while slower sites are still being read.
        var found = Channel.CreateUnbounded<JobPosting>();
        async Task ProcessSourceAsync(CompanySource source, CancellationToken token)
        {
            var isAdzuna = JobBoardClient.IsAggregator(source.AtsProvider);
            try
            {
                List<BoardJob> jobs;
                try
                {
                    jobs = JobBoardClient.FeedProviders.Contains(source.AtsProvider)
                        ? await boards.FetchFeedAsync(source.AtsProvider, token)
                        : source.AtsProvider == JobBoardClient.AdzunaProvider
                        ? await boards.FetchAdzunaAsync(searchKeywords, (int)Math.Ceiling(prefs.MaxAgeHours / 24.0), token)
                        : await boards.FetchAsync(source.AtsProvider, source.BoardToken, source.Name, token, searchKeywords);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    sourceResults[source.Id] = (null, ex.Message, null);
                    logger.LogInformation("Scan failed for {Provider}/{Token}: {Error}", source.AtsProvider, source.BoardToken, ex.Message);
                    return;
                }
                Interlocked.Add(ref progress.JobsSeen, jobs.Count);
                // Seeded names may be board tokens; replace them with the real company name once we see it.
                var realName = source.Name == source.BoardToken && jobs.FirstOrDefault()?.Company is { Length: > 0 } n && n != source.BoardToken ? n : null;
                sourceResults[source.Id] = (jobs.Count, "", realName);

                // Cheap filters first; descriptions and precise locations are only fetched for what survives.
                IEnumerable<BoardJob> stream = jobs.Where(j => !string.IsNullOrEmpty(j.ApplyUrl) && !AtsDetector.IsBlockedSource(j.ApplyUrl));
                // Website jobs only learn their date from the job page, so they pass here and are re-checked after details.
                stream = Step(stream, "posted within your time window", j => j.PostedAt is { } posted ? posted >= cutoff : j.NeedsDetail);
                stream = Step(stream, "title matches your job titles", j => (roles.Length == 0 || roles.Any(r => ContainsWord(j.Title, r))) && IsSoftwareTitle(j.Title));
                stream = Step(stream, "title fits your skills (not another language)", j => JobAnalysisService.TitleFitsCandidate(j.Title, candidateSkills));
                stream = Step(stream, "not skipped by level / skip words", j => !excludes.Any(e => ContainsWord(j.Title, e)));
                stream = Step(stream, "not a federal / clearance title", j => !prefs.ExcludeFederalJobs || !FederalTitle().IsMatch(j.Title));
                stream = Step(stream, "not a blocked company", j => !IsBlocked(j.Company.Length > 0 ? j.Company : source.Name));
                // Company boards record their jobs; aggregator copies of those jobs are dropped.
                stream = Step(stream, "new (not already in your feed)", j => !dismissedKeys.Contains(JobKey(j.Company, j.Title))
                                                && seenUrls.TryAdd(j.ApplyUrl, 0) & (seenJobKeys.TryAdd(JobKey(j.Company, j.Title), 0) || !isAdzuna));
                var candidates = stream.ToList();
                Interlocked.Add(ref candidatesTotal, candidates.Count);

                await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (job, innerToken) =>
                {
                    if (job.NeedsDetail)
                    {
                        try
                        {
                            var detail = await boards.FetchDetailAsync(source.AtsProvider, source.BoardToken, job.ExternalId, innerToken);
                            job = job with
                            {
                                // Keep the listing text (e.g. a Phenom teaser) if the job page yields no description.
                                Description = string.IsNullOrWhiteSpace(detail.Description) ? job.Description : detail.Description,
                                Location = string.IsNullOrWhiteSpace(detail.Location) ? job.Location : detail.Location,
                                IsRemote = detail.IsRemote ?? job.IsRemote,
                                PostedAt = detail.PostedAt ?? job.PostedAt,
                                Title = string.IsNullOrWhiteSpace(detail.Title) ? job.Title : detail.Title
                            };
                        }
                        catch (Exception ex) when (!innerToken.IsCancellationRequested)
                        {
                            logger.LogInformation("Detail fetch failed for {Url}: {Error}", job.ApplyUrl, ex.Message);
                        }
                    }
                    if (RejectReason(job) is { } reason) { funnel.Reject(reason); return; }
                    funnel.Add("saved to your feed");

                    var posting = new JobPosting
                    {
                        CandidateProfileId = candidateProfileId,
                        Title = job.Title, Company = job.Company, Location = job.Location, IsRemote = job.IsRemote,
                        SourceUrl = job.ApplyUrl, ApplyUrl = job.ApplyUrl, Description = job.Description,
                        AtsProvider = source.AtsProvider, ExternalId = job.ExternalId, PostedAt = job.PostedAt,
                        CareersUrl = JobBoardClient.BoardUrl(source.AtsProvider, source.BoardToken)
                    };
                    ApplyMatch(posting, profile, primarySkills);
                    found.Writer.TryWrite(posting);
                });
            }
            finally { Interlocked.Increment(ref progress.CompaniesDone); }
        }

        // Company boards first; the aggregator runs afterwards so jobs found on a company's own site win.
        var producer = Task.Run(async () =>
        {
            await Parallel.ForEachAsync(companies, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
                async (source, token) => await ProcessSourceAsync(source, token));
            if (useAdzuna)
                await ProcessSourceAsync(new CompanySource { Id = Guid.Empty, Name = "Adzuna", AtsProvider = JobBoardClient.AdzunaProvider }, ct);
            foreach (var feed in JobBoardClient.FeedProviders)
                await ProcessSourceAsync(new CompanySource { Id = Guid.Empty, Name = feed, AtsProvider = feed }, ct);
        }, ct);
        _ = producer.ContinueWith(t => found.Writer.TryComplete(t.Exception), TaskScheduler.Default);

        var saved = 0;
        var batch = new List<JobPosting>();
        while (await found.Reader.WaitToReadAsync(ct))
        {
            while (saved + batch.Count < MaxJobsPerRun && found.Reader.TryRead(out var posting)) batch.Add(posting);
            if (batch.Count == 0) { while (found.Reader.TryRead(out _)) { } continue; }
            db.JobPostings.AddRange(batch);
            await db.SaveChangesAsync(ct);
            saved += batch.Count;
            Interlocked.Exchange(ref progress.JobsSaved, saved);
            batch.Clear();
        }
        await producer;

        // Write scan results back onto the (now tracked) company rows.
        foreach (var source in await db.CompanySources.Where(x => x.Enabled).ToListAsync(ct))
        {
            if (!sourceResults.TryGetValue(source.Id, out var r)) continue;
            source.LastScannedAt = DateTimeOffset.UtcNow;
            source.LastError = r.Error;
            if (r.Count is { } count) source.LastJobCount = count;
            if (r.Name is { } name) source.Name = name;
        }

        run.CompaniesScanned = companies.Count;
        run.CompaniesFailed = sourceResults.Values.Count(r => r.Error != "");
        run.JobsSeen = progress.JobsSeen;
        run.JobsMatched = candidatesTotal;
        run.JobsSaved = saved;
        run.FunnelJson = funnel.ToJson(progress.JobsSeen);
        prefs.LastRunAt = DateTimeOffset.UtcNow;
        if (db.Entry(prefs).State == EntityState.Detached) db.SearchPreferences.Add(prefs);
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Discovery for {Profile}: {Seen} seen, {Matched} candidates, {Saved} saved in {Seconds:0}s",
            candidateProfileId, run.JobsSeen, run.JobsMatched, run.JobsSaved, (run.FinishedAt.Value - run.StartedAt).TotalSeconds);
        return run;
    }

    /// <summary>Recomputes match scores after the profile's skills, resume or experience change.</summary>
    public async Task<int> RescoreAsync(Guid candidateProfileId, CancellationToken ct)
    {
        var profile = await db.CandidateProfiles.FindAsync([candidateProfileId], ct) ?? throw new KeyNotFoundException("Profile not found.");
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var primary = PrimarySkills(await db.SearchPreferences.FirstOrDefaultAsync(x => x.CandidateProfileId == candidateProfileId, ct));
        var jobs = await db.JobPostings.Where(x => x.CandidateProfileId == candidateProfileId && x.CreatedAt >= since).ToListAsync(ct);
        foreach (var job in jobs) ApplyMatch(job, profile, primary);
        await db.SaveChangesAsync(ct);
        return jobs.Count;
    }

    public static string[] PrimarySkills(SearchPreferences? prefs) =>
        prefs is null ? [] : prefs.PrimarySkillsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void ApplyMatch(JobPosting posting, CandidateProfile profile, IReadOnlyCollection<string> primary)
    {
        var match = analysis.Analyze(profile, primary, posting.Title, posting.Description);
        posting.StackScore = match.StackScore;
        posting.MatchScore = match.Score;
        posting.MatchReason = match.Reason;
        posting.SkillScore = match.SkillScore;
        posting.ExperienceScore = match.ExperienceScore;
        posting.TitleScore = match.TitleScore;
        posting.RequiredYears = match.RequiredYears;
        posting.MatchedSkillsCsv = string.Join(", ", match.MatchedSkills);
        posting.MissingSkillsCsv = string.Join(", ", match.MissingSkills);
    }

    private static readonly HashSet<string> UsStateCodes = new(StringComparer.Ordinal)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA","HI","ID","IL","IN","IA","KS","KY","LA","ME","MD","MA","MI","MN","MS","MO","MT",
        "NE","NV","NH","NJ","NM","NY","NC","ND","OH","OK","OR","PA","RI","SC","SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC",
    };

    private static readonly Regex UsStateNames = new(
        @"\b(alabama|alaska|arizona|arkansas|california|colorado|connecticut|delaware|florida|georgia|hawaii|idaho|illinois|indiana|iowa|kansas|kentucky|louisiana|maine|maryland|massachusetts|michigan|minnesota|mississippi|missouri|montana|nebraska|nevada|new hampshire|new jersey|new mexico|new york|north carolina|north dakota|ohio|oklahoma|oregon|pennsylvania|rhode island|south carolina|south dakota|tennessee|texas|utah|vermont|virginia|washington|west virginia|wisconsin|wyoming)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Austin, TX", "Seattle, Washington", "US-CA-Remote", "USA.VA.Reston" and similar US-only location strings.</summary>
    private static bool LooksUS(string location)
    {
        if (NonUsPlace.IsMatch(location)) return false;
        if (UsStateNames.IsMatch(location)) return true;
        // Any standalone two-letter state code: "Boston MA", "CT - Hartford", "Austin, TX 78701", "USA.VA.Reston".
        return Regex.Split(location, @"[\s,;./()\-]+").Any(t => t.Length == 2 && UsStateCodes.Contains(t));
    }

    // Places whose names or codes could look like US states ("London, ON", "Pune, IN", "Paris, FR").
    private static readonly Regex NonUsPlace = new(
        @"\b(india|canada|ontario|british columbia|quebec|united kingdom|uk|england|london|ireland|dublin|germany|france|paris|spain|netherlands|poland|romania|singapore|australia|japan|china|philippines|mexico|brazil|argentina|costa rica|colombia|israel|europe|emea|apac|latam|bengaluru|bangalore|hyderabad|pune|chennai|toronto|vancouver|montreal)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The user's skip words plus the words implied by their experience level.</summary>
    public static string[] TitleExclusions(SearchPreferences prefs) =>
        Csv(prefs.ExcludeKeywordsCsv).Concat(prefs.ExperienceLevel switch
        {
            "Entry" => EntryExcludes, "Mid" => MidExcludes, "Senior" => SeniorExcludes, _ => []
        }).Distinct().ToArray();

    public static string[] BlockedCompanies(SearchPreferences prefs) => Csv(prefs.BlockedCompaniesCsv);

    /// <summary>
    /// Broad title words ("developer", "front end", "programmer") also appear in non-software jobs: retail
    /// "Front End Cashier", apparel "Product Developer", clinical "Statistical Programmer". A title must name
    /// software work and must not name one of those other fields.
    /// </summary>
    public static bool IsSoftwareTitle(string title) => SoftwareTitle().IsMatch(title) && !NonSoftwareTitle().IsMatch(title);

    [GeneratedRegex(@"\b(software|developer|engineer|engineering|programmer|full[\s-]?stack|back[\s-]?end|front[\s-]?end\s+(developer|engineer|software|web)|web|applications?|sde|swe|devops|api|\.net|c#|java|python|react|angular|node)\b|\.net|c#", RegexOptions.IgnoreCase)]
    private static partial Regex SoftwareTitle();

    [GeneratedRegex(@"\b(cashiers?|retail|store|stocker|merchandis\w*|associate|supervisor|footwear|apparel|fashion|garment|textile|product developer|statistical programmer|sas programmer|clinical|biostatistic\w*|training developer|instructional|curriculum|content developer|course developer|business developer|business development|real estate|land developer|property developer|sales|account executive|recruiter|nurse|technician|mechanic|warehouse|driver|electrical engineer|mechanical engineer|civil engineer|structural engineer|chemical engineer|process engineer|manufacturing engineer|field service|customer service|call center)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NonSoftwareTitle();

    /// <summary>
    /// Whether a saved job still fits the current preferences (level/skip words, blocked companies, federal titles).
    /// Checked when the feed loads, so preference changes also apply to jobs found by earlier scans.
    /// </summary>
    public static bool StillWanted(string title, string company, SearchPreferences prefs, string[] exclusions, string[] blocked, HashSet<string> candidateSkills) =>
        IsSoftwareTitle(title)
        && JobAnalysisService.TitleFitsCandidate(title, candidateSkills)
        && !exclusions.Any(e => ContainsWord(title, e))
        && !blocked.Any(b => ContainsWord(company, b))
        && (!prefs.ExcludeFederalJobs || !FederalTitle().IsMatch(title));

    private static string JobKey(string company, string title) =>
        Regex.Replace($"{company}|{title}".ToLowerInvariant(), "[^a-z0-9|]+", "");

    private static string[] Csv(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).Distinct().ToArray();

    // "remote" alone admits remote jobs anywhere; combined with places (e.g. "remote, united states") a remote job
    // must also be in one of those places, unless the posting just says "Remote" without naming a location.
    private static bool LocationMatches(BoardJob job, string[] locations)
    {
        if (locations.Length == 0) return true;
        // Some career websites don't state a location; keep those rather than silently dropping them.
        if (string.IsNullOrWhiteSpace(job.Location)) return true;
        var places = locations.Where(l => l != "remote").ToArray();
        if (places.Any(p => ContainsWord(job.Location, p))) return true;
        if (places.Any(p => p is "us" or "usa" or "united states" or "united states of america" or "america") && LooksUS(job.Location)) return true;
        if (!job.IsRemote || !locations.Contains("remote")) return false;
        return places.Length == 0 || !Regex.IsMatch(Regex.Replace(job.Location, "remote", "", RegexOptions.IgnoreCase), "[a-z]{3,}", RegexOptions.IgnoreCase);
    }

    // Whole-word match so "us" doesn't match "business" and "lead" doesn't match "leading".
    private static bool ContainsWord(string text, string term) =>
        Regex.IsMatch(text, $@"(^|[^a-z0-9]){Regex.Escape(term)}([^a-z0-9]|$)", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"(not|unable to|cannot|can't|won't|will not|do not|does not)\s+(be\s+able\s+to\s+)?(provide|offer|support|sponsor)[^.]{0,40}sponsor|without\s+(visa\s+)?sponsorship|sponsorship\s+(is\s+)?not\s+(available|offered|provided)|no\s+(visa\s+)?sponsorship", RegexOptions.IgnoreCase)]
    private static partial Regex NoSponsorship();

    // Federal-government / public-sector roles, recognizable from the title.
    [GeneratedRegex(@"(federal|government|gov|public sector|clearance|cleared|ts/sci|dod|defense|intelligence community)", RegexOptions.IgnoreCase)]
    public static partial Regex FederalTitle();

    // Descriptions that require (or say you must be able to get) a security clearance or public-trust suitability.
    [GeneratedRegex(@"security clearance|ts/sci|top secret|secret clearance|public trust|active clearance|clearance (is )?required|(obtain|maintain|eligible for|hold) (an? |a current |an active )?(u.?s.? )?(government |security )?clearance|federal (agency|agencies|government customer|clients?)", RegexOptions.IgnoreCase)]
    public static partial Regex ClearanceRequired();
}

/// <summary>Re-scans every few hours per profile so the feed always covers the last 24 hours.</summary>
public sealed class DiscoveryWorker(IServiceScopeFactory scopes, ILogger<DiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await RunDueAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "Scheduled discovery check failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var due = (await db.SearchPreferences.Where(x => x.Enabled).ToListAsync(ct))
            .Where(p => p.LastRunAt is null || now - p.LastRunAt.Value >= TimeSpan.FromHours(Math.Clamp(p.ScanEveryHours, 1, 24)))
            .Select(p => p.CandidateProfileId)
            .ToList();

        foreach (var profileId in due)
        {
            // Fresh scope per run so one profile's tracked entities don't leak into the next.
            using var runScope = scopes.CreateScope();
            try { await runScope.ServiceProvider.GetRequiredService<IJobDiscoveryService>().RunAsync(profileId, ct); }
            catch (InvalidOperationException) { /* a manual scan is already running */ }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Scheduled discovery failed for {Profile}", profileId);
                // Record the attempt so a persistent failure doesn't retry every minute. New scope: the failed
                // run's context still tracks its unsaved entities.
                using var markScope = scopes.CreateScope();
                await markScope.ServiceProvider.GetRequiredService<AppDbContext>().SearchPreferences
                    .Where(x => x.CandidateProfileId == profileId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunAt, DateTimeOffset.UtcNow), ct);
            }
        }
    }
}
