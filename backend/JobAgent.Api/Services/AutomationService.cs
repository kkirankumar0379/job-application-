using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JobAgent.Api.Contracts;
using JobAgent.Api.Domain;
using Microsoft.Playwright;

namespace JobAgent.Api.Services;

public interface IAutomationService
{
    Task<AutomationSessionView> StartAsync(string applyUrl, CandidateProfile profile, string resumePath, CancellationToken ct);
    AutomationSessionView? Get(Guid sessionId);
    Task<AutomationSessionView> SubmitAsync(Guid sessionId, CancellationToken ct);
    Task CloseAsync(Guid sessionId);
}

public sealed class AutomationService(ILogger<AutomationService> logger) : IAutomationService, IAsyncDisposable
{
    private sealed class Session
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string ApplyUrl { get; init; }
        public required string AtsProvider { get; init; }
        public required IBrowserContext Context { get; init; }
        public required IPage Page { get; init; }
        public string Status { get; set; } = "Starting";
        public List<string> FilledFields { get; } = [];
        public List<string> UnknownFields { get; set; } = [];
        public string? Message { get; set; }
        public AutomationSessionView ToView() => new(Id, ApplyUrl, AtsProvider, Status, FilledFields.ToList(), UnknownFields.ToList(), Message);
    }

    private sealed record FieldInfo(
        [property: JsonPropertyName("idx")] int Idx,
        [property: JsonPropertyName("tag")] string Tag,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("autocomplete")] string Autocomplete,
        [property: JsonPropertyName("required")] bool Required,
        [property: JsonPropertyName("value")] string Value)
    {
        public string Descriptor => $"{Label} {Name} {Id} {Autocomplete}".ToLowerInvariant();
        public string DisplayName => !string.IsNullOrWhiteSpace(Label) ? Label.Trim() : !string.IsNullOrWhiteSpace(Name) ? Name : Id;
    }

    // Tags every visible form control with data-jobagent-idx and describes it, including its label text.
    private const string ScanScript = """
        () => {
          const els = [...document.querySelectorAll('input, select, textarea')];
          const out = [];
          els.forEach((el, i) => {
            const type = (el.getAttribute('type') || '').toLowerCase();
            if (['hidden', 'submit', 'button', 'reset', 'image'].includes(type)) return;
            const rect = el.getBoundingClientRect();
            const visible = type === 'file' || (rect.width > 0 && rect.height > 0 && getComputedStyle(el).visibility !== 'hidden');
            if (!visible) return;
            el.setAttribute('data-jobagent-idx', String(i));
            let label = '';
            if (el.id) { const l = document.querySelector(`label[for="${CSS.escape(el.id)}"]`); if (l) label = l.innerText; }
            if (!label) { const l = el.closest('label'); if (l) label = l.innerText; }
            if (!label) label = el.getAttribute('aria-label') || el.getAttribute('placeholder') || '';
            const required = el.required || el.getAttribute('aria-required') === 'true' || /\*\s*$/.test(label.trim());
            let value = el.value || '';
            if (type === 'checkbox' || type === 'radio') {
              const group = el.name ? [...document.querySelectorAll(`input[name="${CSS.escape(el.name)}"]`)] : [el];
              value = group.some(g => g.checked) ? 'checked' : '';
            } else if (type === 'file') {
              value = el.files && el.files.length ? el.files[0].name : '';
            }
            out.push({ idx: i, tag: el.tagName.toLowerCase(), type, label: label.replace(/\s+/g, ' ').trim(), name: el.name || '',
                       id: el.id || '', autocomplete: el.getAttribute('autocomplete') || '', required, value });
          });
          return out;
        }
        """;

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<AutomationSessionView> StartAsync(string applyUrl, CandidateProfile profile, string resumePath, CancellationToken ct)
    {
        var browser = await GetBrowserAsync();
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var session = new Session { ApplyUrl = applyUrl, AtsProvider = AtsDetector.Detect(applyUrl), Context = context, Page = page };
        _sessions[session.Id] = session;

        try
        {
            await page.GotoAsync(applyUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 }).ContinueWith(_ => { });

            var fields = await ScanAsync(page);
            foreach (var field in fields)
            {
                if (!string.IsNullOrEmpty(field.Value)) continue;
                var locator = page.Locator($"[data-jobagent-idx='{field.Idx}']");

                if (field.Type == "file")
                {
                    if (IsResumeField(field, fields) && File.Exists(resumePath))
                    {
                        await locator.SetInputFilesAsync(resumePath);
                        session.FilledFields.Add($"{field.DisplayName} (resume)");
                    }
                    continue;
                }

                if (field.Tag != "input" || field.Type is "checkbox" or "radio") continue;
                var value = ValueFor(field, profile);
                if (string.IsNullOrWhiteSpace(value)) continue;
                await locator.FillAsync(value);
                session.FilledFields.Add(field.DisplayName);
            }

            session.UnknownFields = await FindUnfilledRequiredAsync(page);
            session.Status = session.UnknownFields.Count > 0 ? "NeedsUserInput" : "ReadyForApproval";
            session.Message = session.UnknownFields.Count > 0
                ? "Some required fields need your input. Complete them in the browser window, then approve submission."
                : "Form prepared. Review it in the browser window, then approve submission.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automation failed for {Url}", applyUrl);
            session.Status = "Failed";
            session.Message = ex.Message;
        }

        return session.ToView();
    }

    public AutomationSessionView? Get(Guid sessionId) => _sessions.TryGetValue(sessionId, out var s) ? s.ToView() : null;

    public async Task<AutomationSessionView> SubmitAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) throw new KeyNotFoundException();
        if (session.Status is "Submitted" or "Failed") return session.ToView();

        // The user may have filled things in manually since the start; re-check before submitting.
        session.UnknownFields = await FindUnfilledRequiredAsync(session.Page);
        if (session.UnknownFields.Count > 0)
        {
            session.Status = "NeedsUserInput";
            session.Message = "Required fields are still empty. Complete them in the browser window before submitting.";
            return session.ToView();
        }

        var submit = session.Page.Locator("button[type=submit], input[type=submit]")
            .Or(session.Page.GetByRole(AriaRole.Button, new() { NameRegex = new("submit|apply", RegexOptions.IgnoreCase) }))
            .First;

        if (await submit.CountAsync() == 0)
        {
            session.Message = "Could not find a submit button. Submit manually in the browser window.";
            return session.ToView();
        }

        await submit.ClickAsync();
        session.Status = "Submitted";
        session.Message = "Submit clicked. Check the browser window for the confirmation page (CAPTCHA may still need you).";
        return session.ToView();
    }

    public async Task CloseAsync(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session)) await session.Context.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        await _browserLock.WaitAsync();
        try
        {
            if (_browser is { IsConnected: true }) return _browser;
            // Idempotent: downloads Chromium on first use, no-op afterwards.
            var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exit != 0) throw new InvalidOperationException($"Playwright Chromium install failed with exit code {exit}.");
            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = false });
            return _browser;
        }
        finally { _browserLock.Release(); }
    }

    private static async Task<List<FieldInfo>> ScanAsync(IPage page) => (await page.EvaluateAsync<List<FieldInfo>>(ScanScript)) ?? [];

    private static async Task<List<string>> FindUnfilledRequiredAsync(IPage page) =>
        (await ScanAsync(page)).Where(f => f.Required && string.IsNullOrWhiteSpace(f.Value)).Select(f => f.DisplayName).Distinct().ToList();

    private static bool IsResumeField(FieldInfo field, List<FieldInfo> all)
    {
        var d = field.Descriptor;
        if (d.Contains("cover")) return false;
        return d.Contains("resume") || d.Contains("cv") || all.Count(f => f.Type == "file") == 1;
    }

    private static string? ValueFor(FieldInfo f, CandidateProfile p)
    {
        var d = f.Descriptor;
        // Whole-word matching so e.g. "ethnicity" never matches "city" and "tell us" never matches "tel".
        bool Has(params string[] terms) => terms.Any(t => Regex.IsMatch(d, $@"(^|[^a-z]){Regex.Escape(t)}([^a-z]|$)"));

        if (f.Type == "email" || Has("email", "e-mail")) return p.Email;
        if (f.Type == "tel" || Has("phone", "mobile", "tel")) return p.Phone;
        if (Has("linkedin")) return p.LinkedInUrl;
        if (Has("first name", "firstname", "first_name", "given-name", "given name", "fname")) return p.FirstName;
        if (Has("last name", "lastname", "last_name", "family-name", "family name", "surname", "lname")) return p.LastName;
        if (Has("full name", "fullname", "full_name") || f.Autocomplete == "name" ||
            new[] { f.Label.TrimEnd('*', ' '), f.Name, f.Id }.Any(x => x.Equals("name", StringComparison.OrdinalIgnoreCase)))
            return $"{p.FirstName} {p.LastName}".Trim();
        if (Has("city", "address-level2")) return p.City;
        // Unknown questions are deliberately left for the user.
        return null;
    }
}
