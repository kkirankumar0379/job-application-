using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using JobAgent.Api.Contracts;
using JobAgent.Api.Data;
using JobAgent.Api.Domain;
using JobAgent.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Hosting platforms provide the database as DATABASE_URL (postgres://user:pass@host:port/db) and the port as PORT.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
static string PostgresConnection(IConfiguration config)
{
    if (Environment.GetEnvironmentVariable("DATABASE_URL") is not { Length: > 0 } url) return config.GetConnectionString("Postgres")!;
    var u = new Uri(url);
    var user = u.UserInfo.Split(':', 2);
    return new NpgsqlConnectionStringBuilder
    {
        Host = u.Host, Port = u.Port > 0 ? u.Port : 5432, Database = u.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(user[0]), Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : "",
        SslMode = SslMode.Prefer, TrustServerCertificate = true,
    }.ToString();
}

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(PostgresConnection(builder.Configuration)));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TokenService>((o, tokens) =>
    o.TokenValidationParameters = new TokenValidationParameters
    {
        IssuerSigningKey = tokens.Key, ValidateIssuer = false, ValidateAudience = false,
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
    });
builder.Services.AddAuthorization();
// Sign-up and login are limited per IP so passwords can't be guessed at speed.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddSingleton<IJobAnalysisService, JobAnalysisService>();
builder.Services.AddSingleton<IAutomationService, AutomationService>();
builder.Services.AddHttpClient<JobBoardClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("JobAgent/0.1 (personal job search)");
});
builder.Services.AddScoped<IJobDiscoveryService, JobDiscoveryService>();
builder.Services.AddScoped<CompanyImporter>();
builder.Services.AddSingleton<AiSettings>();
builder.Services.AddSingleton<AdzunaSettings>();
builder.Services.AddScoped<ResumeTailor>();
builder.Services.AddHostedService<DiscoveryWorker>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// The dev frontend runs on :5173. When hosted, the app serves the frontend itself (same origin), so no CORS is needed.
var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:5173").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    // Add any seed boards that are missing, so new entries reach existing databases too.
    var known = db.CompanySources.AsEnumerable().Select(c => (c.AtsProvider, c.BoardToken.ToLowerInvariant())).ToHashSet();
    var knownNames = db.CompanySources.AsEnumerable().Select(c => c.Name.ToLowerInvariant()).ToHashSet();
    var missing = CompanySeed.Boards.Where(b => !known.Contains((b.Provider, b.Token.ToLowerInvariant())) && !knownNames.Contains(b.Name.ToLowerInvariant()))
        .DistinctBy(b => (b.Provider, b.Token.ToLowerInvariant())).ToList();
    if (missing.Count > 0)
    {
        db.CompanySources.AddRange(missing.Select(b => new CompanySource { Name = b.Name, AtsProvider = b.Provider, BoardToken = b.Token }));
        db.SaveChanges();
    }
}

var uploadsRoot = Path.Combine(app.Environment.ContentRootPath, "uploads");
var sessionApplications = new ConcurrentDictionary<Guid, Guid>();
var sessionOwners = new ConcurrentDictionary<Guid, Guid>();

static bool IsHttpUrl(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

static ApplicationStatus ToApplicationStatus(string sessionStatus) =>
    Enum.TryParse<ApplicationStatus>(sessionStatus, out var s) ? s : ApplicationStatus.Prepared;

// Everything under /api needs a login, except health and the auth endpoints below.
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// Accounts
var auth = api.MapGroup("/auth").AllowAnonymous().RequireRateLimiting("auth");

static object AuthResult(TokenService tokens, AppUser user) =>
    new { token = tokens.Create(user), user = new { user.Id, user.Email, user.IsAdmin } };

auth.MapPost("/register", async (RegisterRequest req, AppDbContext db, TokenService tokens, IConfiguration config) =>
{
    var email = (req.Email ?? "").Trim().ToLowerInvariant();
    if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Results.BadRequest("Enter a valid email address.");
    if ((req.Password ?? "").Length < 8) return Results.BadRequest("Password must be at least 8 characters.");
    if (config["Auth:InviteCode"] is { Length: > 0 } invite && !string.Equals(req.InviteCode?.Trim(), invite, StringComparison.Ordinal))
        return Results.BadRequest("That invite code isn't right. Ask the person who shared this app with you.");
    if (await db.Users.AnyAsync(u => u.Email == email)) return Results.Conflict("An account with this email already exists. Try logging in.");

    var first = !await db.Users.AnyAsync();
    var user = new AppUser { Email = email, PasswordHash = PasswordHasher.Hash(req.Password!), IsAdmin = first };
    db.Users.Add(user);

    // The very first account takes over anything created before logins existed (profiles, hand-added jobs).
    var orphans = first ? await db.CandidateProfiles.Where(p => p.UserId == null).OrderBy(p => p.UpdatedAt).ToListAsync() : [];
    foreach (var o in orphans) o.UserId = user.Id;
    if (orphans.Count == 0)
        db.CandidateProfiles.Add(new CandidateProfile { UserId = user.Id, FirstName = req.FirstName?.Trim() ?? "", LastName = req.LastName?.Trim() ?? "", Email = email });
    await db.SaveChangesAsync();
    if (orphans.Count > 0)
    {
        var owner = orphans[0].Id;
        await db.JobPostings.Where(j => j.CandidateProfileId == null && j.AddedByProfileId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.AddedByProfileId, owner));
    }
    return Results.Ok(AuthResult(tokens, user));
});

const string DummyHash = "v1.210000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
auth.MapPost("/login", async (LoginRequest req, AppDbContext db, TokenService tokens) =>
{
    var email = (req.Email ?? "").Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    // Verify against a dummy hash for unknown emails so response time doesn't reveal which emails have accounts.
    var ok = PasswordHasher.Verify(req.Password ?? "", user?.PasswordHash ?? DummyHash);
    return user is not null && ok ? Results.Ok(AuthResult(tokens, user)) : Results.BadRequest("Wrong email or password.");
});

api.MapGet("/auth/me", async (ClaimsPrincipal me, AppDbContext db, IConfiguration config) =>
{
    var uid = me.UserId();
    if (await db.Users.FindAsync(uid) is not { } user) return Results.Unauthorized();
    return Results.Ok(new { user.Id, user.Email, user.IsAdmin });
});

// Candidate profiles (each user only ever sees their own)
api.MapGet("/profiles", async (ClaimsPrincipal me, AppDbContext db) =>
{
    var uid = me.UserId();
    return await db.CandidateProfiles.Where(x => x.UserId == uid).OrderBy(x => x.FirstName).ToListAsync();
});

api.MapGet("/profiles/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
    await db.OwnedProfile(me, id) is { } p ? Results.Ok(p) : Results.NotFound());

api.MapPost("/profiles", async (CandidateProfile profile, ClaimsPrincipal me, AppDbContext db) =>
{
    profile.Id = Guid.NewGuid();
    profile.UserId = me.UserId();
    profile.ResumePath = "";
    profile.UpdatedAt = DateTimeOffset.UtcNow;
    db.CandidateProfiles.Add(profile);
    await db.SaveChangesAsync();
    return Results.Created($"/api/profiles/{profile.Id}", profile);
});

api.MapPut("/profiles/{id:guid}", async (Guid id, CandidateProfile input, ClaimsPrincipal me, AppDbContext db, IJobDiscoveryService discovery, CancellationToken ct) =>
{
    if (await db.OwnedProfile(me, id, ct) is not { } p) return Results.NotFound();
    p.FirstName = input.FirstName; p.LastName = input.LastName; p.Email = input.Email; p.Phone = input.Phone;
    p.City = input.City; p.State = input.State; p.Country = input.Country; p.LinkedInUrl = input.LinkedInUrl;
    p.Summary = input.Summary; p.SkillsCsv = input.SkillsCsv; p.YearsOfExperience = input.YearsOfExperience;
    p.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    await discovery.RescoreAsync(id, ct);
    return Results.Ok(p);
});

api.MapPost("/profiles/{id:guid}/resume", async (Guid id, IFormFile file, ClaimsPrincipal me, AppDbContext db, IJobDiscoveryService discovery, CancellationToken ct) =>
{
    if (await db.OwnedProfile(me, id, ct) is not { } p) return Results.NotFound();
    if (file.Length > 10_000_000) return Results.BadRequest("That file is over 10 MB. Upload a smaller resume.");
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".pdf" or ".doc" or ".docx")) return Results.BadRequest("Resume must be a .pdf, .doc or .docx file.");

    var dir = Path.Combine(uploadsRoot, id.ToString());
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, Path.GetFileName(file.FileName));
    await using (var stream = File.Create(path)) await file.CopyToAsync(stream);

    p.ResumePath = path;
    p.ResumeText = ResumeParser.ExtractText(path);
    var parsed = ResumeParser.Parse(p.ResumeText);

    // Fill only empty fields: whatever the user already typed wins over the parser's guesses.
    var filled = new List<string>();
    void Fill(string label, Func<string> get, Action<string> set, string value)
    {
        if (string.IsNullOrWhiteSpace(get()) && !string.IsNullOrWhiteSpace(value)) { set(value); filled.Add(label); }
    }
    Fill("first name", () => p.FirstName, v => p.FirstName = v, parsed.FirstName);
    Fill("last name", () => p.LastName, v => p.LastName = v, parsed.LastName);
    Fill("email", () => p.Email, v => p.Email = v, parsed.Email);
    Fill("phone", () => p.Phone, v => p.Phone = v, parsed.Phone);
    Fill("city", () => p.City, v => p.City = v, parsed.City);
    Fill("state", () => p.State, v => p.State = v, parsed.State);
    Fill("LinkedIn", () => p.LinkedInUrl, v => p.LinkedInUrl = v, parsed.LinkedInUrl);
    Fill("summary", () => p.Summary, v => p.Summary = v, parsed.Summary);
    if (p.YearsOfExperience is null && parsed.YearsOfExperience is { } years) { p.YearsOfExperience = years; filled.Add("years of experience"); }

    // Add skills found in the resume to the profile so they are visible and editable.
    var existing = SkillCatalog.Normalize(p.SkillsCsv.Split(','));
    var added = parsed.Skills.Where(d => !existing.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
    p.SkillsCsv = string.Join(", ", existing.Concat(added));
    p.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    await discovery.RescoreAsync(id, ct);
    return Results.Ok(new { profile = p, detectedSkills = parsed.Skills, addedSkills = added, filledFields = filled, textExtracted = p.ResumeText.Length > 0 });
}).DisableAntiforgery();

// The resume file for the browser extension: the version tailored for the job being applied to (matched by URL), else the uploaded one.
api.MapGet("/profiles/{id:guid}/resume/file", async (Guid id, string? jobUrl, HttpContext http, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
{
    if (await db.OwnedProfile(me, id, ct) is not { } profile) return Results.NotFound();

    static string Key(string? url) => (url ?? "").Split('?', '#')[0].TrimEnd('/').ToLowerInvariant();
    var wanted = Key(jobUrl);
    string? path = null;
    if (wanted.Length > 0)
    {
        var tailored = await (from t in db.TailoredResumes
                              join j in db.JobPostings on t.JobPostingId equals j.Id
                              where t.CandidateProfileId == id
                              orderby t.CreatedAt descending
                              select new { t.FilePath, j.ApplyUrl, j.SourceUrl }).ToListAsync(ct);
        path = tailored.FirstOrDefault(t => new[] { Key(t.ApplyUrl), Key(t.SourceUrl) }
            .Any(k => k.Length > 0 && (wanted == k || wanted.StartsWith(k + "/") || k.StartsWith(wanted + "/"))) && File.Exists(t.FilePath))?.FilePath;
    }
    http.Response.Headers["X-Tailored"] = path is null ? "false" : "true";
    path ??= profile.ResumePath;
    if (!File.Exists(path)) return Results.NotFound();

    var type = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };
    return Results.File(path, type, Path.GetFileName(path));
});

// Parses a resume without saving it, so the create-profile form can be prefilled before a profile exists.
api.MapPost("/resume/parse", async (IFormFile file, CancellationToken ct) =>
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".pdf" or ".docx")) return Results.BadRequest("To autofill, upload a .pdf or .docx resume.");
    var temp = Path.Combine(Path.GetTempPath(), $"jobagent-{Guid.NewGuid()}{ext}");
    try
    {
        await using (var stream = File.Create(temp)) await file.CopyToAsync(stream, ct);
        var text = ResumeParser.ExtractText(temp);
        if (text.Length == 0) return Results.BadRequest("Couldn't read any text from that file. If it's a scanned image, fill the form manually.");
        var r = ResumeParser.Parse(text);
        return Results.Ok(new
        {
            r.FirstName, r.LastName, r.Email, r.Phone, r.City, r.State, r.Country, r.LinkedInUrl,
            r.YearsOfExperience, r.Summary, r.Skills
        });
    }
    finally { File.Delete(temp); }
}).DisableAntiforgery();

// Saved answers
api.MapGet("/profiles/{id:guid}/answers", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
    await db.OwnsProfile(me, id)
        ? Results.Ok(await db.SavedAnswers.Where(x => x.CandidateProfileId == id).OrderBy(x => x.Key).ToListAsync())
        : Results.NotFound());

api.MapPost("/profiles/{id:guid}/answers", async (Guid id, SavedAnswer answer, ClaimsPrincipal me, AppDbContext db) =>
{
    if (!await db.OwnsProfile(me, id)) return Results.NotFound();
    if (await db.SavedAnswers.AnyAsync(x => x.CandidateProfileId == id && x.Key == answer.Key))
        return Results.Conflict($"An answer with key '{answer.Key}' already exists.");
    answer.Id = Guid.NewGuid();
    answer.CandidateProfileId = id;
    db.SavedAnswers.Add(answer);
    await db.SaveChangesAsync();
    return Results.Ok(answer);
});

api.MapDelete("/answers/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
{
    var mine = db.OwnedProfileIds(me);
    return await db.SavedAnswers.Where(x => x.Id == id && mine.Contains(x.CandidateProfileId)).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound();
});

// Jobs
api.MapGet("/jobs", async (ClaimsPrincipal me, AppDbContext db) =>
{
    var mine = db.OwnedProfileIds(me);
    return await db.JobPostings.Where(x => x.CandidateProfileId == null && x.AddedByProfileId != null && mine.Contains(x.AddedByProfileId.Value))
        .OrderByDescending(x => x.CreatedAt).ToListAsync();
});

api.MapPost("/jobs/analyze", async (AnalyzeJobRequest req, ClaimsPrincipal me, AppDbContext db, IJobAnalysisService analysis) =>
{
    if (await db.OwnedProfile(me, req.CandidateProfileId) is not { } profile) return Results.NotFound("Profile not found.");
    if (!string.IsNullOrWhiteSpace(req.ApplyUrl) && !IsHttpUrl(req.ApplyUrl)) return Results.BadRequest("Apply URL must be an http(s) URL.");

    var match = analysis.Analyze(profile, JobDiscoveryService.PrimarySkills(await db.SearchPreferences.FirstOrDefaultAsync(x => x.CandidateProfileId == profile.Id)), req.Title, req.Description);
    var job = new JobPosting
    {
        Title = req.Title, Company = req.Company, Location = req.Location,
        SourceUrl = req.ApplyUrl, ApplyUrl = req.ApplyUrl, Description = req.Description, AddedByProfileId = profile.Id,
        MatchScore = match.Score, MatchReason = match.Reason
    };
    db.JobPostings.Add(job);
    await db.SaveChangesAsync();
    return Results.Ok(new { job, match });
});

// "Not interested": hides this job and every other posting of the same role at the same company (other cities,
// other sources). Future scans skip it too (see JobDiscoveryService, dismissedKeys).
api.MapPost("/jobs/{id:guid}/dismiss", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
{
    if (await db.OwnedJob(me, id, ct) is not { } job) return Results.NotFound();
    var company = job.Company.ToLower();
    var title = job.Title.ToLower();
    await db.JobPostings
        .Where(x => x.Id == id || (x.CandidateProfileId == job.CandidateProfileId && x.Company.ToLower() == company && x.Title.ToLower() == title))
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDismissed, true), ct);
    return Results.NoContent();
});

// Daily discovery: search preferences, company boards, runs
api.MapGet("/profiles/{id:guid}/preferences", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
    await db.OwnsProfile(me, id)
        ? Results.Ok(await db.SearchPreferences.FirstOrDefaultAsync(x => x.CandidateProfileId == id) ?? new SearchPreferences { CandidateProfileId = id })
        : Results.NotFound());

api.MapPut("/profiles/{id:guid}/preferences", async (Guid id, SearchPreferences input, ClaimsPrincipal me, AppDbContext db, IJobDiscoveryService discovery) =>
{
    if (!await db.OwnsProfile(me, id)) return Results.NotFound();
    if (input.ExperienceLevel is not ("Any" or "Entry" or "Mid" or "Senior")) return Results.BadRequest("Experience level must be Any, Entry, Mid or Senior.");

    var p = await db.SearchPreferences.FirstOrDefaultAsync(x => x.CandidateProfileId == id);
    if (p is null) { p = new SearchPreferences { CandidateProfileId = id }; db.SearchPreferences.Add(p); }
    p.Enabled = input.Enabled; p.RoleKeywordsCsv = input.RoleKeywordsCsv; p.ExcludeKeywordsCsv = input.ExcludeKeywordsCsv;
    p.LocationsCsv = input.LocationsCsv; p.RemoteOnly = input.RemoteOnly; p.ExperienceLevel = input.ExperienceLevel;
    p.NeedsSponsorship = input.NeedsSponsorship; p.ExcludeFederalJobs = input.ExcludeFederalJobs; p.BlockedCompaniesCsv = input.BlockedCompaniesCsv ?? ""; p.PrimarySkillsCsv = input.PrimarySkillsCsv ?? ""; p.MaxAgeHours = Math.Clamp(input.MaxAgeHours, 1, 24 * 14);
    p.ScanEveryHours = Math.Clamp(input.ScanEveryHours, 1, 24);
    var stackChanged = db.Entry(p).State == EntityState.Added || db.Entry(p).Property(x => x.PrimarySkillsCsv).IsModified;
    await db.SaveChangesAsync();
    // The main stack is part of every match score, so re-score saved jobs when it changes.
    if (stackChanged) await discovery.RescoreAsync(id, CancellationToken.None);
    return Results.Ok(p);
});

api.MapGet("/companies", async (AppDbContext db) => await db.CompanySources.OrderBy(x => x.Name).ToListAsync());

// Adds one company from any careers link: a job board, a careers page that uses one, or a careers website read directly.
api.MapPost("/companies", async (AddCompanyRequest req, AppDbContext db, CompanyImporter importer, CancellationToken ct) =>
{
    var url = req.CareersUrl.Trim();
    if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
    if (!IsHttpUrl(url)) return Results.BadRequest("That doesn't look like a web link. Paste the company's careers page URL.");
    if (AtsDetector.IsBlockedSource(url)) return Results.BadRequest("LinkedIn isn't supported. Paste the company's own careers page instead.");

    var (found, failure) = await importer.ResolveAsync(url, string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim(), ct);
    if (found is null) return Results.BadRequest(failure);
    if (await db.CompanySources.FirstOrDefaultAsync(x => x.AtsProvider == found.Provider && x.BoardToken.ToLower() == found.Token.ToLower(), ct) is { } existing)
        return Results.Conflict($"{existing.Name} is already in your list ({existing.AtsProvider}).");

    var source = new CompanySource { Name = found.RealName, AtsProvider = found.Provider, BoardToken = found.Token };
    db.CompanySources.Add(source);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { company = source, message = found.Message });
});

// Bulk import from a spreadsheet (.xlsx/.csv) of careers links and/or company names.
api.MapPost("/companies/import", async (IFormFile file, ClaimsPrincipal me, CompanyImporter importer, CancellationToken ct) =>
{
    if (!me.IsAdmin()) return Results.Forbid();
    if (file.Length > 5_000_000) return Results.BadRequest("That file is over 5 MB. Split it into smaller sheets.");
    List<ImportRow> rows;
    try
    {
        await using var stream = file.OpenReadStream();
        rows = CompanyImporter.ReadRows(stream, file.FileName);
    }
    catch (NotSupportedException ex) { return Results.BadRequest(ex.Message); }
    catch (Exception ex) when (ex is InvalidDataException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or IOException)
    {
        return Results.BadRequest("Couldn't read that spreadsheet. Save it as .xlsx or .csv and try again.");
    }
    if (rows.Count == 0) return Results.BadRequest("No company names or links found in that file.");

    var results = await importer.ImportAsync(rows, ct);
    return Results.Ok(new
    {
        added = results.Count(r => r.Status == "Added"),
        existing = results.Count(r => r.Status == "Exists"),
        notFound = results.Count(r => r.Status == "NotFound"),
        truncated = rows.Count >= CompanyImporter.MaxRows,
        results
    });
}).DisableAntiforgery();

// The company list is shared by everyone, so only the admin can switch companies off or remove them.
api.MapPut("/companies/{id:guid}/enabled", async (Guid id, bool enabled, ClaimsPrincipal me, AppDbContext db) =>
    !me.IsAdmin() ? Results.Forbid()
    : await db.CompanySources.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, enabled)) > 0
        ? Results.NoContent() : Results.NotFound());

api.MapDelete("/companies/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
    !me.IsAdmin() ? Results.Forbid()
    : await db.CompanySources.Where(x => x.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());

// Starts a scan in the background (it takes a few minutes); poll /discovery/status or the feed's runningSince.
api.MapPost("/profiles/{id:guid}/discovery/run", async (Guid id, ClaimsPrincipal me, AppDbContext db, IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<Program> logger) =>
{
    if (!await db.OwnsProfile(me, id)) return Results.NotFound();
    if (JobDiscoveryService.RunningSince is not null) return Results.Conflict("A job scan is already running.");
    _ = Task.Run(async () =>
    {
        using var scope = scopes.CreateScope();
        try { await scope.ServiceProvider.GetRequiredService<IJobDiscoveryService>().RunAsync(id, lifetime.ApplicationStopping); }
        catch (Exception ex) when (!lifetime.ApplicationStopping.IsCancellationRequested) { logger.LogWarning(ex, "Manual discovery failed for {Profile}", id); }
    });
    return Results.Accepted();
});

api.MapGet("/profiles/{id:guid}/discovery/runs", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
    await db.OwnsProfile(me, id)
        ? Results.Ok(await db.DiscoveryRuns.Where(x => x.CandidateProfileId == id).OrderByDescending(x => x.StartedAt).Take(10).ToListAsync())
        : Results.NotFound());

api.MapGet("/discovery/status", () => Results.Ok(new { runningSince = JobDiscoveryService.RunningSince, progress = ScanProgressView() }));

// Job feed: discovered jobs without descriptions (fetched per job via /jobs/{id}) to keep the list light.
api.MapGet("/profiles/{id:guid}/feed", async (Guid id, int? hours, int? minScore, bool? remote, string? q, string? sort, string? view, bool? mainStack, string? source, ClaimsPrincipal me, AppDbContext db) =>
{
    if (!await db.OwnsProfile(me, id)) return Results.NotFound();
    var since = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 30));
    var appliedJobIds = db.JobApplications.Where(a => a.CandidateProfileId == id).Select(a => a.JobPostingId);
    var query = db.JobPostings.Where(x => x.CandidateProfileId == id && !x.IsDismissed);

    query = view == "saved"
        ? query.Where(x => x.IsSaved)
        : query.Where(x => (x.PostedAt ?? x.CreatedAt) >= since && x.MatchScore >= (minScore ?? 0) && !appliedJobIds.Contains(x.Id));
    if (remote == true) query = query.Where(x => x.IsRemote);
    // "Main stack": the job is built at least partly on the candidate's primary stack (see JobAnalysisService.StackFit).
    if (mainStack == true) query = query.Where(x => x.StackScore >= 55);
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = $"%{q.Trim()}%";
        query = query.Where(x => EF.Functions.ILike(x.Title, term) || EF.Functions.ILike(x.Company, term) || EF.Functions.ILike(x.MatchedSkillsCsv, term));
    }
    query = sort == "recent"
        ? query.OrderByDescending(x => x.PostedAt).ThenByDescending(x => x.MatchScore)
        : query.OrderByDescending(x => x.MatchScore).ThenByDescending(x => x.PostedAt);

    // The source split ("adzuna" / "boards") is applied after grouping, so the tab counts below reflect every other filter.
    var jobs = await query.Take(2000).Select(x => new
    {
        x.Id, x.Title, x.Company, x.Location, x.IsRemote, x.ApplyUrl, x.CareersUrl, x.AtsProvider, x.PostedAt, x.IsSaved,
        x.MatchScore, x.SkillScore, x.ExperienceScore, x.TitleScore, x.StackScore, x.RequiredYears, x.MatchedSkillsCsv, x.MissingSkillsCsv, x.MatchReason
    }).ToListAsync();

    // Re-apply title/level, blocked-company and federal preferences, so changing them updates the feed
    // immediately instead of only affecting future scans. Saved jobs stay, since the user chose them.
    if (view != "saved" && await db.SearchPreferences.FirstOrDefaultAsync(x => x.CandidateProfileId == id) is { } prefs)
    {
        var exclusions = JobDiscoveryService.TitleExclusions(prefs);
        var blocked = JobDiscoveryService.BlockedCompanies(prefs);
        var candidateSkills = await db.CandidateProfiles.FindAsync(id) is { } cp ? JobAnalysisService.CandidateSkills(cp) : [];
        jobs = jobs.Where(j => j.IsSaved || JobDiscoveryService.StillWanted(j.Title, j.Company, prefs, exclusions, blocked, candidateSkills)).ToList();
    }

    // The same role posted in several cities shows once (the best-ranked copy), noting the other locations.
    var grouped = jobs
        .GroupBy(j => (j.Company.ToLowerInvariant(), System.Text.RegularExpressions.Regex.Replace(j.Title.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim()))
        .Select(g => new
        {
            Job = g.First(),
            OtherLocations = g.Skip(1).Select(x => x.Location.Split(';')[0].Trim()).Where(l => l != "").Distinct().ToList(),
        })
        .Select(x => new
        {
            x.Job.Id, x.Job.Title, x.Job.Company, x.Job.Location, x.Job.IsRemote, x.Job.ApplyUrl, x.Job.CareersUrl, x.Job.AtsProvider,
            x.Job.PostedAt, x.Job.IsSaved, x.Job.MatchScore, x.Job.SkillScore, x.Job.ExperienceScore, x.Job.TitleScore, x.Job.StackScore, x.Job.RequiredYears,
            x.Job.MatchedSkillsCsv, x.Job.MissingSkillsCsv, x.Job.MatchReason, x.OtherLocations,
        })
        .ToList();
    var isAdzuna = (string provider) => JobBoardClient.IsAggregator(provider);
    var counts = new { all = grouped.Count, adzuna = grouped.Count(j => isAdzuna(j.AtsProvider)), boards = grouped.Count(j => !isAdzuna(j.AtsProvider)) };
    var shown = source switch
    {
        "adzuna" => grouped.Where(j => isAdzuna(j.AtsProvider)),
        "boards" => grouped.Where(j => !isAdzuna(j.AtsProvider)),
        _ => grouped.AsEnumerable()
    };
    var lastRun = await db.DiscoveryRuns.Where(x => x.CandidateProfileId == id && x.FinishedAt != null).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync();
    return Results.Ok(new { jobs = shown.Take(500).ToList(), counts, lastRun, runningSince = JobDiscoveryService.RunningSince, progress = ScanProgressView() });
});

api.MapGet("/jobs/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db) =>
    await db.OwnedJob(me, id) is { } job ? Results.Ok(job) : Results.NotFound());

api.MapPost("/jobs/{id:guid}/save", async (Guid id, bool saved, ClaimsPrincipal me, AppDbContext db) =>
{
    if (await db.OwnedJob(me, id) is not { } job) return Results.NotFound();
    await db.JobPostings.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsSaved, saved));
    return Results.NoContent();
});

// "I already applied": records the job as submitted so it leaves the feed (the feed hides jobs that have an application).
// Passing applied=false removes the application records again, so the job returns to the feed.
api.MapPost("/jobs/{id:guid}/applied", async (Guid id, bool applied, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
{
    if (await db.OwnedJob(me, id, ct) is not { CandidateProfileId: { } profileId } job) return Results.NotFound();
    var existing = await db.JobApplications.Where(a => a.JobPostingId == id && a.CandidateProfileId == profileId).ToListAsync(ct);
    if (!applied)
    {
        db.JobApplications.RemoveRange(existing);
    }
    else if (existing.Count == 0)
    {
        db.JobApplications.Add(new JobApplication
        {
            JobPostingId = id, CandidateProfileId = profileId, AtsProvider = job.AtsProvider,
            Status = ApplicationStatus.Submitted, SubmittedAt = DateTimeOffset.UtcNow, Notes = "Marked as applied by you."
        });
    }
    else
    {
        foreach (var a in existing.Where(a => a.Status != ApplicationStatus.Submitted))
        {
            a.Status = ApplicationStatus.Submitted; a.SubmittedAt = DateTimeOffset.UtcNow; a.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// Job-search API sources (one shared set of keys for the whole app, managed by the admin)
api.MapGet("/sources/adzuna", (ClaimsPrincipal me, AdzunaSettings adzuna) =>
    Results.Ok(new { configured = adzuna.IsConfigured, appId = me.IsAdmin() ? adzuna.AppId : null }));

api.MapPost("/sources/adzuna", async (AdzunaKeysRequest req, ClaimsPrincipal me, AdzunaSettings adzuna, JobBoardClient boards, CancellationToken ct) =>
{
    if (!me.IsAdmin()) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(req.AppId) || string.IsNullOrWhiteSpace(req.AppKey))
    {
        adzuna.Save(null, null);
        return Results.Ok(new { configured = false, appId = (string?)null });
    }
    // Check the keys with one tiny search before saving them.
    try { await boards.SearchAdzunaAsync(req.AppId.Trim(), req.AppKey.Trim(), "software engineer", 1, 1, 1, ct); }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
    catch (HttpRequestException) { return Results.BadRequest("Couldn't reach Adzuna. Check your internet connection and try again."); }
    adzuna.Save(req.AppId, req.AppKey);
    return Results.Ok(new { configured = true, appId = adzuna.AppId });
});

// AI resume tailoring. One Anthropic key (the admin's) serves everyone; each user gets a monthly allowance.
api.MapGet("/ai/status", async (ClaimsPrincipal me, AppDbContext db, AiSettings ai, IConfiguration config) =>
{
    var (used, limit) = await TailoringUsage(db, me, config);
    return Results.Ok(new
    {
        configured = ai.IsConfigured,
        source = me.IsAdmin() ? ai.Source : null,
        maskedKey = me.IsAdmin() ? ai.MaskedKey : null,
        canManageKey = me.IsAdmin(),
        usedThisMonth = used,
        monthlyLimit = me.IsAdmin() ? (int?)null : limit,
    });
});

api.MapPost("/ai/key", (SaveApiKeyRequest req, ClaimsPrincipal me, AiSettings ai) =>
{
    if (!me.IsAdmin()) return Results.Forbid();
    var key = req.ApiKey?.Trim();
    if (!string.IsNullOrEmpty(key) && !key.StartsWith("sk-ant-", StringComparison.Ordinal))
        return Results.BadRequest("That doesn't look like an Anthropic API key (they start with sk-ant-).");
    ai.Save(key);
    return Results.Ok(new { configured = ai.IsConfigured, source = ai.Source, maskedKey = ai.MaskedKey, canManageKey = true, usedThisMonth = 0, monthlyLimit = (int?)null });
});

// Current resume vs. the job: score, and which job keywords the resume lacks (for the user to confirm honestly).
api.MapGet("/jobs/{id:guid}/ats", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
{
    if (await db.OwnedJob(me, id, ct) is not { } job) return Results.NotFound();
    if (await db.OwnedProfile(me, job.CandidateProfileId ?? job.AddedByProfileId ?? Guid.Empty, ct) is not { } profile) return Results.NotFound("Profile not found.");
    if (string.IsNullOrWhiteSpace(profile.ResumeText)) return Results.BadRequest("Upload your resume on the Profile tab first.");
    var latest = await db.TailoredResumes.Where(t => t.JobPostingId == id && t.CandidateProfileId == profile.Id)
        .OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct);
    return Results.Ok(new
    {
        report = AtsScorer.Score(profile.ResumeText, job.Title, job.Description),
        tailored = latest is null ? null : TailoredView(latest),
    });
});

api.MapPost("/jobs/{id:guid}/tailor", async (Guid id, TailorRequest req, ClaimsPrincipal me, AppDbContext db, ResumeTailor tailor, IConfiguration config, CancellationToken ct) =>
{
    if (await db.OwnedJob(me, id, ct) is not { } job) return Results.NotFound();
    if (await db.OwnedProfile(me, job.CandidateProfileId ?? job.AddedByProfileId ?? Guid.Empty, ct) is not { } profile) return Results.NotFound("Profile not found.");

    var (used, limit) = await TailoringUsage(db, me, config);
    if (used >= limit)
        return Results.BadRequest($"You've used your {limit} resume tailorings for this month. The allowance resets on the 1st.");

    var dir = Path.Combine(uploadsRoot, profile.Id.ToString(), "tailored");
    Directory.CreateDirectory(dir);
    var safe = string.Concat($"{profile.FirstName}_{profile.LastName}_{job.Company}_{AtsScorer.CoreTitle(job.Title)}".Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    var path = Path.Combine(dir, $"{Regex.Replace(safe, "_+", "_").Trim('_')}_{job.Id.ToString()[..8]}.docx");

    TailorResult result;
    try { result = await tailor.TailorAsync(profile, job, req.ConfirmedSkills ?? [], path, ct); }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
    catch (Anthropic.Exceptions.AnthropicUnauthorizedException) { return Results.BadRequest("The AI key on this server was rejected. Tell the person who runs the app."); }
    catch (Anthropic.Exceptions.AnthropicRateLimitException) { return Results.Problem("The AI service is busy (rate limited). Try again in a minute.", statusCode: 429); }
    catch (Anthropic.Exceptions.AnthropicApiException ex) { return Results.Problem($"The AI service returned an error: {ex.Message}", statusCode: 502); }

    var saved = new TailoredResume
    {
        CandidateProfileId = profile.Id, JobPostingId = job.Id, FilePath = path,
        ContentJson = System.Text.Json.JsonSerializer.Serialize(result.Content),
        ConfirmedSkillsCsv = string.Join(", ", req.ConfirmedSkills ?? []),
        AtsScoreBefore = result.Before.Score, AtsScoreAfter = result.After.Score,
    };
    db.TailoredResumes.Add(saved);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { before = result.Before, after = result.After, passes = result.Passes, tailored = TailoredView(saved), text = result.PlainText });
});

api.MapGet("/tailored/{id:guid}/download", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
{
    var mine = db.OwnedProfileIds(me);
    return await db.TailoredResumes.FirstOrDefaultAsync(t => t.Id == id && mine.Contains(t.CandidateProfileId), ct) is { } t && File.Exists(t.FilePath)
        ? Results.File(t.FilePath, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Path.GetFileName(t.FilePath))
        : Results.NotFound();
});

// Applications
api.MapGet("/applications", async (ClaimsPrincipal me, AppDbContext db) =>
{
    var mine = db.OwnedProfileIds(me);
    return await (from a in db.JobApplications
                  join j in db.JobPostings on a.JobPostingId equals j.Id
                  where mine.Contains(a.CandidateProfileId)
                  orderby a.UpdatedAt descending
                  select new { a.Id, a.JobPostingId, a.Status, a.AtsProvider, a.Notes, a.SubmittedAt, a.UpdatedAt, JobTitle = j.Title, j.Company, j.ApplyUrl })
                 .ToListAsync();
});

// Browser automation: opens a real browser window on the machine running the server, so it only makes sense when the
// app runs on your own computer. Hosted copies set Automation:Enabled=false and use the browser extension instead.
api.MapPost("/automation/start", async (StartAutomationRequest req, ClaimsPrincipal me, AppDbContext db, IAutomationService automation, IConfiguration config, CancellationToken ct) =>
{
    if (!config.GetValue("Automation:Enabled", true))
        return Results.BadRequest("Auto-apply isn't available on this hosted version. Open the job's apply link and use the JobAgent browser extension to fill the form.");
    if (!IsHttpUrl(req.ApplyUrl)) return Results.BadRequest("Apply URL must be an http(s) URL.");
    if (AtsDetector.IsBlockedSource(req.ApplyUrl))
        return Results.BadRequest("LinkedIn is not automated. Open the employer's own career page and use that apply URL.");
    if (await db.OwnedProfile(me, req.CandidateProfileId, ct) is not { } profile) return Results.NotFound("Profile not found.");
    if (req.JobPostingId is { } checkJobId && await db.OwnedJob(me, checkJobId, ct) is null) return Results.NotFound("Job not found.");

    // Upload the resume tailored for this job, if there is one; otherwise the profile's resume.
    var tailoredPath = req.JobPostingId is { } tailoredJobId
        ? await db.TailoredResumes.Where(t => t.JobPostingId == tailoredJobId && t.CandidateProfileId == profile.Id)
            .OrderByDescending(t => t.CreatedAt).Select(t => t.FilePath).FirstOrDefaultAsync(ct)
        : null;
    var resumePath = tailoredPath is not null && File.Exists(tailoredPath) ? tailoredPath : profile.ResumePath;
    var view = await automation.StartAsync(req.ApplyUrl, profile, resumePath, ct);
    sessionOwners[view.SessionId] = me.UserId();

    if (req.JobPostingId is { } jobId)
    {
        var application = new JobApplication
        {
            JobPostingId = jobId, CandidateProfileId = profile.Id, AtsProvider = view.AtsProvider,
            ResumePathUsed = resumePath, Status = ToApplicationStatus(view.Status), Notes = view.Message ?? ""
        };
        db.JobApplications.Add(application);
        await db.SaveChangesAsync(ct);
        sessionApplications[view.SessionId] = application.Id;
    }

    return Results.Ok(view);
});

api.MapGet("/automation/{sessionId:guid}", (Guid sessionId, ClaimsPrincipal me, IAutomationService automation) =>
    sessionOwners.GetValueOrDefault(sessionId) == me.UserId() && automation.Get(sessionId) is { } view ? Results.Ok(view) : Results.NotFound());

api.MapPost("/automation/{sessionId:guid}/submit", async (Guid sessionId, SubmitAutomationRequest req, ClaimsPrincipal me, AppDbContext db, IAutomationService automation, CancellationToken ct) =>
{
    if (!req.Approved) return Results.BadRequest("Submission requires explicit approval.");
    if (sessionOwners.GetValueOrDefault(sessionId) != me.UserId() || automation.Get(sessionId) is null) return Results.NotFound();

    var view = await automation.SubmitAsync(sessionId, ct);

    if (sessionApplications.TryGetValue(sessionId, out var appId) && await db.JobApplications.FindAsync([appId], ct) is { } application)
    {
        application.Status = ToApplicationStatus(view.Status);
        application.Notes = view.Message ?? "";
        application.UpdatedAt = DateTimeOffset.UtcNow;
        if (application.Status == ApplicationStatus.Submitted) application.SubmittedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    return Results.Ok(view);
});

api.MapDelete("/automation/{sessionId:guid}", async (Guid sessionId, ClaimsPrincipal me, IAutomationService automation) =>
{
    if (sessionOwners.GetValueOrDefault(sessionId) != me.UserId()) return Results.NotFound();
    await automation.CloseAsync(sessionId);
    sessionApplications.TryRemove(sessionId, out _);
    sessionOwners.TryRemove(sessionId, out _);
    return Results.NoContent();
});

// Anything that isn't an API call serves the frontend (when it has been built into wwwroot), so one URL does everything.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html")))
{
    app.MapFallback(async ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath!, "index.html"));
    });
}

app.Run();

// Progress counters are plain fields (updated with Interlocked), so project them for JSON.
static object? ScanProgressView() => JobDiscoveryService.Progress is { } p
    ? new { p.CompaniesTotal, p.CompaniesDone, p.JobsSeen, p.JobsSaved }
    : null;

static object TailoredView(TailoredResume t) => new
{
    t.Id, t.JobPostingId, t.AtsScoreBefore, t.AtsScoreAfter, t.CreatedAt, t.ConfirmedSkillsCsv,
    FileName = Path.GetFileName(t.FilePath),
    Content = System.Text.Json.JsonSerializer.Deserialize<TailoredResumeContent>(t.ContentJson),
};

// Resume tailorings this user has used this calendar month, and their allowance (admins are unlimited).
static async Task<(int Used, int Limit)> TailoringUsage(AppDbContext db, ClaimsPrincipal me, IConfiguration config)
{
    var now = DateTime.UtcNow;
    var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
    var mine = db.OwnedProfileIds(me);
    var used = await db.TailoredResumes.CountAsync(t => mine.Contains(t.CandidateProfileId) && t.CreatedAt >= monthStart);
    return (used, me.IsAdmin() ? int.MaxValue : config.GetValue("Ai:MonthlyTailorings", 30));
}
