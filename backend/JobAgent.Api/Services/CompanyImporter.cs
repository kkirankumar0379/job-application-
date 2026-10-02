using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JobAgent.Api.Data;
using JobAgent.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace JobAgent.Api.Services;

public sealed record ImportRow(int Row, string? Url, string? Name);

public sealed record ImportResultItem(int Row, string Input, string Status, string Message, string? Company, string? Provider, Guid? CompanyId);

/// <summary>Imports company job boards from a spreadsheet of careers links and/or company names.</summary>
public sealed partial class CompanyImporter(AppDbContext db, JobBoardClient boards, ILogger<CompanyImporter> logger)
{
    public const int MaxRows = 1000;

    public static List<ImportRow> ReadRows(Stream file, string fileName)
    {
        var rows = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".xlsx" => ReadXlsx(file),
            ".csv" or ".txt" => ReadCsv(file),
            _ => throw new NotSupportedException("Upload an .xlsx or .csv file. (For an old .xls file, use Save As → Excel Workbook first.)")
        };
        return rows.Select(r => ToImportRow(r.Row, r.Cells)).Where(r => r is not null).Select(r => r!).Take(MaxRows).ToList();
    }

    public async Task<List<ImportResultItem>> ImportAsync(List<ImportRow> rows, CancellationToken ct)
    {
        // Network lookups run in parallel; database writes happen afterwards on this thread.
        var resolved = new (Resolution? Found, string? Failure)[rows.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, rows.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) => resolved[i] = await ResolveAsync(rows[i].Url, rows[i].Name, token));

        var existing = (await db.CompanySources.Select(x => new { x.Id, x.AtsProvider, x.BoardToken }).ToListAsync(ct))
            .ToDictionary(x => (x.AtsProvider, x.BoardToken.ToLowerInvariant()), x => x.Id);
        var results = new List<ImportResultItem>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var input = row.Url ?? row.Name ?? "";
            if (resolved[i].Found is not { } r)
            {
                results.Add(new ImportResultItem(row.Row, input, "NotFound", resolved[i].Failure ?? "Not found.", row.Name, null, null));
                continue;
            }
            var name = !string.IsNullOrWhiteSpace(row.Name) && row.Name.Length <= 60 ? row.Name.Trim() : r.RealName;
            var key = (r.Provider, r.Token.ToLowerInvariant());
            if (existing.TryGetValue(key, out var existingId))
            {
                results.Add(new ImportResultItem(row.Row, input, "Exists", "Already in your company list.", name, r.Provider, existingId));
                continue;
            }
            var source = new CompanySource { Name = name, AtsProvider = r.Provider, BoardToken = r.Token };
            db.CompanySources.Add(source);
            existing[key] = source.Id;
            results.Add(new ImportResultItem(row.Row, input, "Added", r.Message, name, r.Provider, source.Id));
        }
        await db.SaveChangesAsync(ct);
        return results.OrderBy(r => r.Row).ToList();
    }

    public sealed record Resolution(string Provider, string Token, string RealName, string Message);

    /// <summary>
    /// Works out how to scan a company: a job-board link as given, a board linked from its careers page,
    /// the careers website itself (read directly), or finally a board guessed from the company name.
    /// </summary>
    public async Task<(Resolution? Found, string? Failure)> ResolveAsync(string? url, string? name, CancellationToken ct)
    {
        var (found, failure) = await ResolveUrlOrNameAsync(url, name, url is null, ct);
        if (found is not null || url is null) return (found, failure);

        // The given link was wrong or showed no jobs: follow the site's own "Careers" / "Search jobs" links.
        try
        {
            foreach (var candidate in await boards.FindCareerLinksAsync(url, ct))
            {
                var (viaLink, _) = await ResolveUrlOrNameAsync(candidate, null, false, ct);
                if (viaLink is not null)
                    return (viaLink with
                    {
                        RealName = name ?? viaLink.RealName,
                        Message = $"The link you gave didn't list jobs, so we followed the site's careers link ({candidate}). {viaLink.Message}"
                    }, null);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogInformation("Careers link search failed for {Url}: {Error}", url, ex.Message); }

        // Last resort: guess a job board from the company name.
        var (byName, nameFailure) = await ResolveUrlOrNameAsync(null, name, true, ct);
        return byName is not null ? (byName, null) : (null, failure ?? nameFailure);
    }

    private async Task<(Resolution? Found, string? Failure)> ResolveUrlOrNameAsync(string? url, string? name, bool tryName, CancellationToken ct)
    {
        string? failure = null;
        if (url is not null)
        {
            try
            {
                if (await boards.DiscoverBoardAsync(url, ct) is { } board)
                    return (await Describe(board.Provider, board.Token, name,
                        JobBoardClient.ParseBoardUrl(url) is not null ? $"Found on {board.Provider}." : $"Its careers page uses {board.Provider}; added that job board."), null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { failure = Friendly(ex); }

            try
            {
                var links = await boards.FetchAsync("Website", url, name ?? "", ct);
                if (links.Count > 0)
                    return (new Resolution("Website", url, name ?? JobBoardClient.WebsiteName(url) ?? NameFromHost(url),
                        $"No standard job board, so we'll read this website directly ({links.Count} job link{(links.Count == 1 ? "" : "s")} found on the page)."), null);
                failure ??= "That page doesn't list job links we can read (it probably loads jobs with JavaScript). Try the page that lists the individual jobs, or the company's job-board link.";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { failure ??= Friendly(ex); }
        }

        if (tryName && !string.IsNullOrWhiteSpace(name))
        {
            try
            {
                if (await boards.ProbeByNameAsync(name, ct) is { } byName)
                    return (await Describe(byName.Provider, byName.Token, name,
                        $"Matched by name on {byName.Provider} ({byName.Token}). Remove it if that's a different company."), null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { failure ??= Friendly(ex); }
            failure ??= "Couldn't find a Greenhouse, Lever or Ashby board under that name. Add its careers page link instead.";
        }
        return (null, failure ?? "Nothing to look up in this row.");
    }

    private async Task<Resolution> Describe(string provider, string token, string? name, string message)
    {
        var realName = token;
        try
        {
            if ((await boards.FetchAsync(provider, token, name ?? token, CancellationToken.None)).FirstOrDefault()?.Company is { Length: > 0 } n && n != token)
                realName = n;
        }
        catch (Exception ex) { logger.LogInformation("Couldn't read {Provider}/{Token} for its name: {Error}", provider, token, ex.Message); }
        return new Resolution(provider, token, name ?? realName, message);
    }

    private static string NameFromHost(string url)
    {
        var host = new Uri(url).Host.ToLowerInvariant();
        var labels = host.Split('.');
        var main = labels.Length >= 2 ? labels[^2] : labels[0];
        return char.ToUpperInvariant(main[0]) + main[1..];
    }

    private static string Friendly(Exception ex) => ex switch
    {
        TaskCanceledException => "The site took too long to respond.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "The site blocked our request (403). Try the company's job-board link instead.",
        HttpRequestException h when h.StatusCode is not null => $"The site returned an error ({(int)h.StatusCode}).",
        HttpRequestException => "Couldn't connect to that site.",
        _ => ex.Message
    };

    /// <summary>Picks the URL-like cell and a name-like cell from a row; header rows and blank rows are skipped.</summary>
    private static ImportRow? ToImportRow(int rowNumber, List<string> cells)
    {
        var values = cells.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        if (values.Count == 0) return null;
        if (values.All(v => HeaderWord().IsMatch(v))) return null;

        var url = values.Select(NormalizeUrl).FirstOrDefault(u => u is not null);
        var name = values.FirstOrDefault(v => NormalizeUrl(v) is null && v.Length <= 80 && !HeaderWord().IsMatch(v) && v.Any(char.IsLetter));
        return url is null && name is null ? null : new ImportRow(rowNumber, url, name);
    }

    private static string? NormalizeUrl(string value)
    {
        var m = UrlLike().Match(value);
        if (!m.Success) return null;
        var url = m.Value.TrimEnd('.', ',', ';', ')');
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Contains('.') ? uri.ToString() : null;
    }

    private static List<(int Row, List<string> Cells)> ReadXlsx(Stream file)
    {
        using var doc = SpreadsheetDocument.Open(file, false);
        var wb = doc.WorkbookPart ?? throw new InvalidDataException("That workbook has no sheets.");
        var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
        var result = new List<(int, List<string>)>();

        foreach (var sheetPart in wb.WorksheetParts.Where(p => p.Worksheet is not null))
        {
            // Cells showing text like "Careers" often hide the real link in a hyperlink; read those too.
            var links = sheetPart.Worksheet!.Descendants<Hyperlink>()
                .Where(h => h.Id?.Value is not null && h.Reference?.Value is not null)
                .Select(h => (Ref: h.Reference!.Value!, Uri: sheetPart.HyperlinkRelationships.FirstOrDefault(r => r.Id == h.Id!.Value)?.Uri))
                .Where(x => x.Uri is not null)
                .ToDictionary(x => x.Ref.Split(':')[0], x => x.Uri!.ToString());

            foreach (var row in sheetPart.Worksheet!.Descendants<Row>())
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var raw = cell.CellValue?.Text ?? cell.InnerText;
                    var text = cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, out var idx) && idx < shared.Count ? shared[idx] : raw;
                    cells.Add(text);
                    if (cell.CellReference?.Value is { } reference && links.TryGetValue(reference, out var link)) cells.Add(link);
                }
                result.Add(((int)(row.RowIndex?.Value ?? (uint)result.Count + 1), cells));
            }
        }
        return result;
    }

    private static List<(int Row, List<string> Cells)> ReadCsv(Stream file)
    {
        using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var result = new List<(int, List<string>)>();
        var n = 0;
        while (reader.ReadLine() is { } line)
        {
            n++;
            var cells = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = !quoted;
                else if ((ch == ',' || ch == ';' || ch == '\t') && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            result.Add((n, cells));
        }
        return result;
    }

    [GeneratedRegex(@"(?:https?://)?(?:[a-z0-9\-]+\.)+[a-z]{2,}(?:/[^\s""'<>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();
    [GeneratedRegex(@"^(company|company name|name|employer|organization|url|link|website|careers?|careers? (page|url|link|site)|job board|ats|#|no\.?|s\.?no\.?)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderWord();
}
