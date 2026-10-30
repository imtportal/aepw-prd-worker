using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Aepw.Evidence.Worker.Models;
using Microsoft.Extensions.Logging;

namespace Aepw.Evidence.Worker.Services;

/// <summary>
/// POSTs (evidenceId + file) to the Backend API /process endpoint as multipart/form-data and maps
/// the HTTP result to the retry contract (2xx ok · 4xx permanent · 5xx/408/429 transient).
/// </summary>
public sealed class BackendApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<BackendApiClient> _log;
    private readonly string _processPath;

    public BackendApiClient(HttpClient http, ILogger<BackendApiClient> log)
    {
        _http = http;
        _log = log;
        _processPath = Environment.GetEnvironmentVariable("EvidenceProcessEndpointPath") ?? "/api/evidence/process";
    }

    public async Task ProcessAsync(
        long evidenceId, ProcessingRequest req, EvidencePath path, byte[] fileBytes, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Derive reporting year + quarter from the parsed QuarterCode (e.g. "2026-Q3" -> 2026, "Q3").
        // Safe parse: split on '-'; fall back to sending the whole code as the quarter if the shape is unexpected.
        var (reportingYear, reportingQuarter) = SplitQuarterCode(path.QuarterCode);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(evidenceId.ToString()), "evidenceId" },
            // The evidenceType folder segment IS the exact EvidenceType.Code — sent verbatim.
            { new StringContent(path.EvidenceType),     "evidenceType" },
            // TLM metric (e.g. "MW1.1") — the API resolves the MetricEvidenceType binding from this + evidenceType.
            { new StringContent(path.TopMetric),        "tlmMetricCode" },
            // L1 metric (e.g. "MW1.11") — diagnostic only; binding resolves off the TLM.
            { new StringContent(path.L1Metric),         "l1MetricCode" },
            // Reporting period, split from QuarterCode — passed through to the Foundry agent for context.
            { new StringContent(reportingYear.ToString()), "reportingYear" },
            { new StringContent(reportingQuarter),         "reportingQuarter" },
            // Extra fields below are ignored by the API's [FromForm] binding; kept for its own logging.
            { new StringContent(req.CorrelationId),     "correlationId" },
            { new StringContent(req.ItemId),            "itemId" },
            { new StringContent(req.FileName),          "fileName" }
        };
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(req.FileName));
        form.Add(fileContent, "file", req.FileName);

        _log.LogInformation("[API] POST {BaseUrl}{Path} (EvidenceId={Id}, EvidenceType={Type}, TLM={Tlm}, L1={L1}, Year={Year}, Quarter={Quarter}, {Bytes} bytes, file={File})...",
            _http.BaseAddress, _processPath, evidenceId, path.EvidenceType, path.TopMetric, path.L1Metric, reportingYear, reportingQuarter, fileBytes.Length, req.FileName);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(_processPath, form, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[API] HTTP call FAILED (no response) after {Ms} ms. Treating as transient -> retry -> DLQ.",
                sw.ElapsedMilliseconds);
            throw new TransientApiException($"API call failed for {req.FileName}: {ex.Message}");
        }

        var detail = await resp.Content.ReadAsStringAsync(ct);
        var code = (int)resp.StatusCode;
        _log.LogInformation("[API] Response {Status} in {Ms} ms. Body={Body}", code, sw.ElapsedMilliseconds, Trim(detail));

        if (resp.IsSuccessStatusCode)
        {
            _log.LogInformation("[API] SUCCESS ({Status}) for {File}.", code, req.FileName);
            resp.Dispose();
            return;
        }

        resp.Dispose();

        if (resp.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            _log.LogWarning("[API] {Status} (retryable) for {File}.", code, req.FileName);
            throw new TransientApiException($"API {code} (retryable) for {req.FileName}: {Trim(detail)}");
        }

        if (code >= 400 && code < 500)
        {
            _log.LogError("[API] {Status} (permanent) for {File}. No retry.", code, req.FileName);
            throw new PermanentApiException($"API {code} (permanent) for {req.FileName}: {Trim(detail)}");
        }

        _log.LogWarning("[API] {Status} (transient) for {File}. Will retry -> DLQ.", code, req.FileName);
        throw new TransientApiException($"API {code} (transient) for {req.FileName}: {Trim(detail)}");
    }

    private static string Trim(string s) => string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= 500 ? s : s[..500]);

    /// <summary>
    /// Splits a QuarterCode like "2026-Q3" into (year, quarter) = (2026, "Q3").
    /// Falls back gracefully: if the code doesn't parse, returns year 0 and the whole code as the quarter,
    /// so the API still receives something rather than throwing on an unexpected format.
    /// </summary>
    private static (int Year, string Quarter) SplitQuarterCode(string quarterCode)
    {
        if (string.IsNullOrWhiteSpace(quarterCode))
            return (0, string.Empty);

        var parts = quarterCode.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var year))
            return (year, parts[1]);

        // Unexpected shape — send the whole thing as the quarter, year unknown.
        return (0, quarterCode);
    }

    /// <summary>
    /// Maps a file name's extension to a specific MIME type. The Foundry agent rejects
    /// generic "application/octet-stream"; it needs a concrete type (application/pdf, image/png, ...).
    /// Falls back to application/pdf for supported-document uploads.
    /// </summary>
    private static string GetMimeType(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
        ".pdf"  => "application/pdf",
        ".png"  => "image/png",
        ".jpg"  => "image/jpeg",
        ".jpeg" => "image/jpeg",
        ".tif"  => "image/tiff",
        ".tiff" => "image/tiff",
        ".bmp"  => "image/bmp",
        ".gif"  => "image/gif",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xlsm" => "application/vnd.ms-excel.sheet.macroEnabled.12",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _       => "application/pdf"
        };
    }
}