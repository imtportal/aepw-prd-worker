using System.Diagnostics;
using System.Text.Json;
using Aepw.Evidence.Worker.Models;
using Aepw.Evidence.Worker.Services;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Aepw.Evidence.Worker.Functions;

/// <summary>
/// FUNCTION #2 — the Worker. Verbose, step-by-step logging so each stage is visible in the logs:
///   [1] message received  →  [2] parse body  →  [3] event type  →  [4] parse WebUrl
///   →  [5] SQL save        →  [6] download    →  [7] process API  →  [8] done
/// deleted events call the delete SP (soft delete by ItemId) at [STEP 3].
///
/// Message settlement is manual (host.json: autoCompleteMessages = false) so the retry budget is
/// spent only on genuine transient failures: success completes, a PermanentApiException (or a SQL
/// schema error reclassified as one) dead-letters immediately instead of being redelivered, and a
/// TransientApiException/unexpected error abandons the message for redelivery up to the queue's
/// MaxDeliveryCount, after which Service Bus itself dead-letters it.
/// </summary>
public sealed class EvidenceWorker
{
    private readonly SqlEvidenceRepository _sql;
    private readonly GraphDownloader _downloader;
    private readonly BackendApiClient _api;
    private readonly ILogger<EvidenceWorker> _log;

    public EvidenceWorker(
        SqlEvidenceRepository sql,
        GraphDownloader downloader,
        BackendApiClient api,
        ILogger<EvidenceWorker> log)
    {
        _sql = sql;
        _downloader = downloader;
        _api = api;
        _log = log;
    }

    [Function("EvidenceWorker")]
    public async Task Run(
        [ServiceBusTrigger("%ServiceBusQueueName%", Connection = "ServiceBus")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var messageBody = message.Body.ToString();

        // ---- [1] MESSAGE RECEIVED ---------------------------------------------------------
        _log.LogInformation("[STEP 1] === EvidenceWorker triggered: message received. Length={Length} chars, DeliveryCount={Count} ===",
            messageBody?.Length ?? 0, message.DeliveryCount);
        _log.LogDebug("[STEP 1] Raw message body: {Body}", messageBody);

        // ---- [2] PARSE MESSAGE BODY ------------------------------------------------------
        ProcessingRequest req;
        try
        {
            _log.LogInformation("[STEP 2] Deserializing queue message...");
            req = JsonSerializer.Deserialize<ProcessingRequest>(messageBody!)
                  ?? throw new PermanentApiException("Deserialization returned null.");
            _log.LogInformation("[STEP 2] Parsed OK. CorrelationId={Corr}, ItemId={ItemId}, FileName={File}, EventType={Event}, WebUrl={WebUrl}",
                req.CorrelationId, req.ItemId, req.FileName, req.EventType, req.WebUrl);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[STEP 2] FAILED to deserialize message body; dead-lettering (permanent, will never parse on retry).");
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "PermanentFailure",
                deadLetterErrorDescription: ex.Message, cancellationToken: ct);
            return;
        }

        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = req.CorrelationId,
            ["ItemId"]        = req.ItemId,
            ["FileName"]      = req.FileName
        });

        try
        {
            // ---- [3] EVENT TYPE ----------------------------------------------------------
            _log.LogInformation("[STEP 3] Evaluating EventType='{Event}'.", req.EventType);
            if (string.Equals(req.EventType, "deleted", StringComparison.OrdinalIgnoreCase))
            {
                // ---- DELETE FLOW: soft-delete by ItemId via the delete SP --------------
                _log.LogInformation("[STEP 3] EventType is 'deleted' — calling delete SP for ItemId={Item}...", req.ItemId);
                var del = await _sql.DeleteAsync(req, ct);
                _log.LogInformation("[STEP 3] Delete SP returned: Data(rowsUpdated)={Data}, Message='{Msg}'.",
                    del.EvidenceId, del.Message);

                // No-match (Data=0) is treated as SUCCESS: the desired end state (no active evidence)
                // already holds, so there is nothing to retry. A real SQL error throws inside
                // DeleteAsync, classified permanent/transient, and is handled by the catch blocks below.
                _log.LogInformation("[STEP 3] === DELETE COMPLETE for ItemId={Item} in {Ms} ms (rowsUpdated={Data}). ===",
                    req.ItemId, sw.ElapsedMilliseconds, del.EvidenceId);
                await messageActions.CompleteMessageAsync(message, ct);
                return;
            }
            _log.LogInformation("[STEP 3] EventType is '{Event}' — proceeding with create flow.", req.EventType);

            // ---- [4] PARSE WEBURL --------------------------------------------------------
            _log.LogInformation("[STEP 4] Parsing WebUrl into evidence hierarchy...");
            EvidencePath path;

            // Office files (xlsx/docx/pptx) come with a Doc.aspx link, not a folder path: resolve via Graph.
            // Overwriting req.WebUrl also makes the SQL save and process API use the real URL.
            if (req.WebUrl?.Contains("/_layouts/", StringComparison.OrdinalIgnoreCase) == true)
            {
                _log.LogInformation("[STEP 4] WebUrl is a Doc.aspx link; resolving folder path via Graph...");
                req.WebUrl = await _downloader.ResolveFolderPathUrlAsync(req.DriveId, req.ItemId, ct);
            }

            try
            {
                path = WebUrlParser.Parse(req.WebUrl ?? string.Empty);
            }
            catch (FormatException fx)
            {
                _log.LogError(fx, "[STEP 4] WebUrl parse FAILED (permanent). WebUrl={WebUrl}", req.WebUrl);
                throw new PermanentApiException($"WebUrl parse failed: {fx.Message}");
            }
            _log.LogInformation("[STEP 4] Parsed WebUrl: SiteName={Site}, DocLib={Lib}, ProjectPin={Pin}, QuarterCode={Quarter}, MetricCategory={Cat}, TopMetric={Top}, FileName={File}",
                path.SiteName, path.DocumentLibrary, path.ProjectPin, path.QuarterCode, path.MetricCategory, path.TopMetric, path.FileName);

            // ---- [5] SQL SAVE (stored procedure) -----------------------------------------
            _log.LogInformation("[STEP 5] Calling SQL save SP (projectPin={Pin}, quarterCode={Quarter}, metricCode={Metric})...",
                path.ProjectPin, path.QuarterCode, path.TopMetric);
            var save = await _sql.SaveAsync(req, path, ct);
            _log.LogInformation("[STEP 5] SP returned: Data(EvidenceId)={Id}, Message='{Msg}'.", save.EvidenceId, save.Message);

            if (!save.IsSuccess)
            {
                _log.LogError("[STEP 5] SP reported FAILURE (Data=0). Message='{Msg}'. Will retry -> DLQ.", save.Message);
                throw new TransientApiException($"Save SP error for {req.FileName}: {save.Message}");
            }
            _log.LogInformation("[STEP 5] SAVE SUCCESS. EvidenceId={Id}.", save.EvidenceId);

            // ---- [6] DOWNLOAD FILE (Graph) -----------------------------------------------
            _log.LogInformation("[STEP 6] Downloading file from Graph (DriveId={Drive}, ItemId={Item})...",
                req.DriveId, req.ItemId);
            var bytes = await _downloader.DownloadAsync(req.DriveId, req.ItemId, ct);
            _log.LogInformation("[STEP 6] DOWNLOAD SUCCESS. {Bytes} bytes.", bytes.Length);

            // ---- [7] PROCESS API (Document Intelligence) ---------------------------------
            _log.LogInformation("[STEP 7] Calling Backend API /process (EvidenceId={Id}, {Bytes} bytes)...",
                save.EvidenceId, bytes.Length);
            await _api.ProcessAsync(save.EvidenceId, req, path, bytes, ct);
            _log.LogInformation("[STEP 7] PROCESS API SUCCESS.");

            // ---- [8] DONE ----------------------------------------------------------------
            _log.LogInformation("[STEP 8] === COMPLETE for {File} (EvidenceId={Id}) in {Ms} ms. ===",
                req.FileName, save.EvidenceId, sw.ElapsedMilliseconds);
            await messageActions.CompleteMessageAsync(message, ct);
        }
        catch (PermanentApiException ex)
        {
            _log.LogError(ex, "[RESULT] PERMANENT failure for {File} after {Ms} ms; dead-lettering WITHOUT retry.",
                req.FileName, sw.ElapsedMilliseconds);
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "PermanentFailure",
                deadLetterErrorDescription: ex.Message, cancellationToken: ct);
        }
        catch (TransientApiException ex)
        {
            _log.LogWarning(ex, "[RESULT] TRANSIENT failure for {File} after {Ms} ms; abandoning for redelivery.",
                req.FileName, sw.ElapsedMilliseconds);
            await messageActions.AbandonMessageAsync(message, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RESULT] UNEXPECTED failure for {File} after {Ms} ms; abandoning for redelivery (bounded by MaxDeliveryCount).",
                req.FileName, sw.ElapsedMilliseconds);
            await messageActions.AbandonMessageAsync(message, cancellationToken: ct);
        }
    }
}