using System.Text.Json;

namespace JobAgent.Api.Services;

/// <summary>
/// Secrets saved from the app (API keys) in %APPDATA%\JobAgent\secrets.json: in the user's profile,
/// never in the repository. Each key is stored independently so saving one doesn't erase another.
/// </summary>
public static class LocalSecrets
{
    private static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JobAgent", "secrets.json");
    private static readonly Lock Gate = new();

    public static string? Get(string name)
    {
        lock (Gate) return Read().GetValueOrDefault(name) is { Length: > 0 } v ? v : null;
    }

    public static void Set(string name, string? value)
    {
        lock (Gate)
        {
            var all = Read();
            if (string.IsNullOrWhiteSpace(value)) all.Remove(name); else all[name] = value.Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(all));
        }
    }

    private static Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(Path_)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path_)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }
}

/// <summary>
/// Anthropic API key for resume tailoring. Order: ANTHROPIC_API_KEY env var, "Anthropic:ApiKey" config
/// (e.g. dotnet user-secrets), then a key saved from the app.
/// </summary>
public sealed class AiSettings(IConfiguration config)
{
    private const string SecretName = "anthropicApiKey";

    public string? ApiKey =>
        NonEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        ?? NonEmpty(config["Anthropic:ApiKey"])
        ?? LocalSecrets.Get(SecretName);

    public bool IsConfigured => ApiKey is not null;

    public string Source =>
        NonEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) is not null ? "environment variable"
        : NonEmpty(config["Anthropic:ApiKey"]) is not null ? "app configuration"
        : LocalSecrets.Get(SecretName) is not null ? "saved in this app" : "not set";

    /// <summary>Last four characters only, for display.</summary>
    public string? MaskedKey => ApiKey is { Length: > 8 } k ? "…" + k[^4..] : null;

    public void Save(string? apiKey) => LocalSecrets.Set(SecretName, apiKey);

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Adzuna job-search API credentials (free at developer.adzuna.com). Env vars ADZUNA_APP_ID / ADZUNA_APP_KEY win.</summary>
public sealed class AdzunaSettings
{
    public string? AppId => NonEmpty(Environment.GetEnvironmentVariable("ADZUNA_APP_ID")) ?? LocalSecrets.Get("adzunaAppId");
    public string? AppKey => NonEmpty(Environment.GetEnvironmentVariable("ADZUNA_APP_KEY")) ?? LocalSecrets.Get("adzunaAppKey");
    public bool IsConfigured => AppId is not null && AppKey is not null;

    public void Save(string? appId, string? appKey)
    {
        LocalSecrets.Set("adzunaAppId", appId);
        LocalSecrets.Set("adzunaAppKey", appKey);
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
