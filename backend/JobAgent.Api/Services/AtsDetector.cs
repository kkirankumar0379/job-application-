namespace JobAgent.Api.Services;

public static class AtsDetector
{
    public static string Detect(string url)
    {
        var host = new Uri(url).Host.ToLowerInvariant();
        if (host.Contains("greenhouse")) return "Greenhouse";
        if (host.Contains("lever.co")) return "Lever";
        if (host.Contains("myworkdayjobs") || host.Contains("workday")) return "Workday";
        if (host.Contains("icims")) return "iCIMS";
        if (host.Contains("smartrecruiters")) return "SmartRecruiters";
        return "Generic";
    }

    public static bool IsBlockedSource(string url)
    {
        var host = new Uri(url).Host.ToLowerInvariant();
        return host == "linkedin.com" || host.EndsWith(".linkedin.com");
    }
}
