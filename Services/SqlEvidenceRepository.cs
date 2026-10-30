using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Aepw.Evidence.Worker.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Aepw.Evidence.Worker.Services;

/// <summary>
/// Classifies a SqlException into the Worker's retry contract. Schema/object errors (wrong SP,
/// stale column, FK violation) are permanent — redelivering the same message hits the same error,
/// so it must dead-letter instead of looping. Deadlocks/timeouts/throttling are transient.
/// </summary>
internal static class SqlErrorClassifier
{
    private static readonly HashSet<int> PermanentErrorNumbers = new() { 208, 207, 2812, 547 };
    private static readonly HashSet<int> TransientErrorNumbers = new() { 1205, -2, 40197, 40501, 49918, 49919, 49920 };

    public static Exception Classify(SqlException ex)
    {
        if (PermanentErrorNumbers.Contains(ex.Number))
            return new PermanentApiException($"SQL error {ex.Number} (permanent/schema): {ex.Message}");

        if (TransientErrorNumbers.Contains(ex.Number))
            return new TransientApiException($"SQL error {ex.Number} (transient): {ex.Message}");

        // Unclassified SQL error: default to transient rather than silently dropping the message.
        // Bounded by the queue's MaxDeliveryCount, so an unexpectedly permanent error still
        // dead-letters quickly instead of looping forever.
        return new TransientApiException($"SQL error {ex.Number} (unclassified, treated transient): {ex.Message}");
    }
}

/// <summary>
/// Saves evidence by calling dbo.uspReviewSaveReviewMetricEvidences directly (connection-string
/// auth). Worker path omits MetricId and passes @metricCode; the SP resolves MetricId.
/// The SP returns rows of (Data, Message); Data>0 = new EvidenceId, Data=0 = error.
/// Delete path calls dbo.uspReviewDeleteReviewMetricEvidence (soft delete by ItemId).
/// </summary>
public sealed class SqlEvidenceRepository
{
    private const string SpName = "dbo.uspReviewSaveReviewMetricEvidences";
    private const string DeleteSpName = "dbo.uspReviewDeleteReviewMetricEvidence";
    private readonly string _connectionString;
    private readonly long _serviceUserId;
    private readonly ILogger<SqlEvidenceRepository> _log;

    public SqlEvidenceRepository(ILogger<SqlEvidenceRepository> log)
    {
        _log = log;

        _log.LogWarning("===== SQL DEBUG: SqlEvidenceRepository constructor started =====");

        _connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new InvalidOperationException("Missing app setting 'SqlConnectionString'.");

        var userIdRaw = Environment.GetEnvironmentVariable("EvidenceServiceUserId")
            ?? throw new InvalidOperationException("Missing app setting 'EvidenceServiceUserId'.");

        if (!long.TryParse(userIdRaw, out _serviceUserId))
            throw new InvalidOperationException(
                $"'EvidenceServiceUserId' is not a valid integer: '{userIdRaw}'.");

        _log.LogWarning(
            "===== SQL DEBUG: Repository initialized. EvidenceServiceUserId={UserId} =====",
            _serviceUserId);
    }

    public async Task<SaveEvidenceResult> SaveAsync(
        ProcessingRequest req,
        EvidencePath path,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        _log.LogWarning(
            "===== SQL DEBUG STEP 1: SaveAsync STARTED ===== ItemId={ItemId}, FileName={FileName}",
            req.ItemId,
            req.FileName);

        var evidenceItem = new[]
        {
            new
            {
                ItemId          = req.ItemId,
                FileName        = path.FileName,
                FileSizeBytes   = req.SizeBytes,
                FileUriView     = req.WebUrl ?? string.Empty,
                FileUriDownload = req.WebUrl ?? string.Empty,
                SiteTag         = path.SiteName,
                Period          = path.QuarterCode,
                Language        = "en",
                // Metric-agnostic EvidenceType.Code from the folder path (e.g. "CANNIBAL_SUMMARY_REPORT").
                // The save SP resolves this Code -> EvidenceType.EvidenceTypeId (mirrors MetricCode -> MetricId).
                EvidenceTypeCode = path.EvidenceType
            }
        };

        var json = JsonSerializer.Serialize(evidenceItem);

        _log.LogWarning(
            "===== SQL DEBUG STEP 2: PARAMETERS ===== UserId={UserId}, ProjectPin={Pin}, QuarterCode={Quarter}, MetricCode={Metric}, ItemId={ItemId}, FileName={FileName}",
            _serviceUserId,
            path.ProjectPin,
            path.QuarterCode,
            path.L1Metric,
            req.ItemId,
            path.FileName);

        _log.LogDebug(
            "[SQL] reviewMetricEvidencesJson={Json}",
            json);

        try
        {
            _log.LogWarning(
                "===== SQL DEBUG STEP 3: OPENING SQL CONNECTION =====");

            await using var conn = new SqlConnection(_connectionString);

            await conn.OpenAsync(ct);

            _log.LogWarning(
                "===== SQL DEBUG STEP 4: SQL CONNECTION OPENED ===== Database={Database}, Server={Server}",
                conn.Database,
                conn.DataSource);

            _log.LogWarning(
                "===== SQL DEBUG STEP 5: CREATING SQL COMMAND ===== SP={Sp}",
                SpName);

            await using var cmd = new SqlCommand(SpName, conn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.Add("@UserId", SqlDbType.BigInt).Value =
                _serviceUserId;

            cmd.Parameters.Add("@reviewMetricEvidencesJson", SqlDbType.NVarChar, -1).Value =
                json;

            cmd.Parameters.Add("@ReviewId", SqlDbType.BigInt).Value =
                DBNull.Value;

            cmd.Parameters.Add("@projectPin", SqlDbType.VarChar, 50).Value =
                path.ProjectPin;

            cmd.Parameters.Add("@quarterCode", SqlDbType.VarChar, 50).Value =
                path.QuarterCode;

            cmd.Parameters.Add("@metricCode", SqlDbType.VarChar, 20).Value =
                path.L1Metric;   // send L1 metric (e.g. "MW1.11") so ReviewMetric resolves to the sub-metric

            _log.LogWarning(
                "===== SQL DEBUG STEP 6: ABOUT TO EXECUTE SP ===== UserId={UserId}, ProjectPin={Pin}, QuarterCode={Quarter}, MetricCode={Metric}",
                _serviceUserId,
                path.ProjectPin,
                path.QuarterCode,
                path.L1Metric);

            long evidenceId = 0;
            string message = "No result row returned by the stored procedure.";
            int rowCount = 0;

            _log.LogWarning(
            "===== SQL DEBUG: reviewMetricEvidencesJson ===== {Json}",
            json);

            _log.LogWarning(
                "===== SQL DEBUG STEP 7: EXECUTING SP NOW =====");

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            _log.LogWarning(
                "===== SQL DEBUG STEP 8: SP EXECUTION COMPLETED =====");

            if (await reader.ReadAsync(ct))
            {
                rowCount++;

                _log.LogWarning(
                    "===== SQL DEBUG STEP 9: SP RETURNED FIRST ROW =====");

                var dataOrdinal = reader.GetOrdinal("Data");
                var msgOrdinal = reader.GetOrdinal("Message");

                evidenceId = reader.IsDBNull(dataOrdinal)
                    ? 0
                    : Convert.ToInt64(reader.GetValue(dataOrdinal));

                message = reader.IsDBNull(msgOrdinal)
                    ? string.Empty
                    : reader.GetString(msgOrdinal);

                _log.LogWarning(
                    "===== SQL DEBUG STEP 10: FIRST ROW VALUES ===== Data={Data}, Message={Message}",
                    evidenceId,
                    message);
            }
            else
            {
                _log.LogWarning(
                    "===== SQL DEBUG STEP 9 FAILED: SP RETURNED ZERO ROWS =====");
            }

            // Drain any additional rows.
            while (await reader.ReadAsync(ct))
            {
                rowCount++;

                _log.LogWarning(
                    "===== SQL DEBUG: ADDITIONAL SP ROW FOUND. RowCount={RowCount} =====",
                    rowCount);
            }

            _log.LogWarning(
                "===== SQL DEBUG STEP 11: FINAL SP RESULT ===== Rows={Rows}, EvidenceId={EvidenceId}, Message={Message}, DurationMs={Ms}",
                rowCount,
                evidenceId,
                message,
                sw.ElapsedMilliseconds);

            if (rowCount > 1)
            {
                _log.LogWarning(
                    "[SQL] SP returned {Rows} rows; using the first. (Concurrency note.)",
                    rowCount);
            }

            return new SaveEvidenceResult(evidenceId, message);
        }
        catch (SqlException ex)
        {
            var classified = SqlErrorClassifier.Classify(ex);

            _log.LogError(
                ex,
                "===== SQL DEBUG FAILED: SqlException ===== Number={Number}, State={State}, DurationMs={Ms}, Classified={Classified}",
                ex.Number,
                ex.State,
                sw.ElapsedMilliseconds,
                classified.GetType().Name);

            throw classified;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "===== SQL DEBUG FAILED: Unexpected exception ===== DurationMs={Ms}",
                sw.ElapsedMilliseconds);

            throw;
        }
    }

    /// <summary>
    /// Soft-deletes evidence by ItemId via dbo.uspReviewDeleteReviewMetricEvidence.
    /// The SP returns (Data, Message): Data>0 = rows updated; Data=0 = no matching row OR error.
    /// Returns the result; the caller decides how to treat a no-match (Data=0).
    /// Throws on connection/SQL exceptions (transient -> retry -> DLQ upstream).
    /// </summary>
    public async Task<SaveEvidenceResult> DeleteAsync(
        ProcessingRequest req,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        _log.LogWarning(
            "===== SQL DEBUG DELETE STEP 1: DeleteAsync STARTED ===== ItemId={ItemId}, FileName={FileName}",
            req.ItemId,
            req.FileName);

        _log.LogWarning(
            "===== SQL DEBUG DELETE STEP 2: PARAMETERS ===== UserId={UserId}, ItemId={ItemId}",
            _serviceUserId,
            req.ItemId);

        try
        {
            _log.LogWarning(
                "===== SQL DEBUG DELETE STEP 3: OPENING SQL CONNECTION =====");

            await using var conn = new SqlConnection(_connectionString);

            await conn.OpenAsync(ct);

            _log.LogWarning(
                "===== SQL DEBUG DELETE STEP 4: SQL CONNECTION OPENED ===== Database={Database}, Server={Server}",
                conn.Database,
                conn.DataSource);

            _log.LogWarning(
                "===== SQL DEBUG DELETE STEP 5: CREATING SQL COMMAND ===== SP={Sp}",
                DeleteSpName);

            await using var cmd = new SqlCommand(DeleteSpName, conn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.Add("@UserId", SqlDbType.BigInt).Value =
                _serviceUserId;

            cmd.Parameters.Add("@ItemId", SqlDbType.NVarChar, -1).Value =
                req.ItemId;

            _log.LogWarning(
                "===== SQL DEBUG DELETE STEP 6: ABOUT TO EXECUTE DELETE SP ===== UserId={UserId}, ItemId={ItemId}",
                _serviceUserId,
                req.ItemId);

            long data = 0;
            string message = "No result row returned by the delete stored procedure.";
            int rowCount = 0;

            _log.LogWarning(
                "===== SQL DEBUG DELETE STEP 7: EXECUTING DELETE SP NOW =====");

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            _log.LogWarning(
                "===== SQL DEBUG DELETE STEP 8: DELETE SP EXECUTION COMPLETED =====");

            if (await reader.ReadAsync(ct))
            {
                rowCount++;

                _log.LogWarning(
                    "===== SQL DEBUG DELETE STEP 9: DELETE SP RETURNED FIRST ROW =====");

                var dataOrdinal = reader.GetOrdinal("Data");
                var msgOrdinal = reader.GetOrdinal("Message");

                data = reader.IsDBNull(dataOrdinal)
                    ? 0
                    : Convert.ToInt64(reader.GetValue(dataOrdinal));

                message = reader.IsDBNull(msgOrdinal)
                    ? string.Empty
                    : reader.GetString(msgOrdinal);

                _log.LogWarning(
                    "===== SQL DEBUG DELETE STEP 10: FIRST ROW VALUES ===== Data={Data}, Message={Message}",
                    data,
                    message);
            }
            else
            {
                _log.LogWarning(
                    "===== SQL DEBUG DELETE STEP 9 FAILED: DELETE SP RETURNED ZERO ROWS =====");
            }

            while (await reader.ReadAsync(ct))
            {
                rowCount++;

                _log.LogWarning(
                    "===== SQL DEBUG DELETE: ADDITIONAL SP ROW FOUND. RowCount={RowCount} =====",
                    rowCount);
            }

            _log.LogWarning(
                "===== SQL DEBUG DELETE STEP 11: FINAL DELETE SP RESULT ===== Rows={Rows}, Data={Data}, Message={Message}, DurationMs={Ms}",
                rowCount,
                data,
                message,
                sw.ElapsedMilliseconds);

            return new SaveEvidenceResult(data, message);
        }
        catch (SqlException ex)
        {
            var classified = SqlErrorClassifier.Classify(ex);

            _log.LogError(
                ex,
                "===== SQL DEBUG DELETE FAILED: SqlException ===== Number={Number}, State={State}, DurationMs={Ms}, Classified={Classified}",
                ex.Number,
                ex.State,
                sw.ElapsedMilliseconds,
                classified.GetType().Name);

            throw classified;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "===== SQL DEBUG DELETE FAILED: Unexpected exception ===== DurationMs={Ms}",
                sw.ElapsedMilliseconds);

            throw;
        }
    }
}