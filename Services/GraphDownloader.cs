using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;

namespace Aepw.Evidence.Worker.Services;

/// <summary>
/// Downloads a file's bytes from SharePoint via Microsoft Graph (Managed Identity auth).
/// </summary>
public sealed class GraphDownloader
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger<GraphDownloader> _log;

    public GraphDownloader(GraphServiceClient graph, ILogger<GraphDownloader> log)
    {
        _graph = graph;
        _log = log;
    }

    /// <summary>
    /// Office files (xlsx/docx/pptx) arrive with a Doc.aspx "open" link instead of a folder path.
    /// Rebuilds the canonical folder-path URL (driveWebUrl + parent folder + name) from Graph so
    /// WebUrlParser can read the evidence hierarchy.
    /// </summary>
    public async Task<string> ResolveFolderPathUrlAsync(
        string driveId,
        string itemId,
        CancellationToken ct = default)
    {
        var item = await _graph.Drives[driveId].Items[itemId].GetAsync(cancellationToken: ct);
        var drive = await _graph.Drives[driveId].GetAsync(cancellationToken: ct);

        var parentPath = item?.ParentReference?.Path;
        var split = parentPath?.Split("root:", 2);
        if (item?.Name is null || drive?.WebUrl is null || split is null || split.Length < 2)
            throw new Aepw.Evidence.Worker.Models.PermanentApiException(
                $"Could not resolve folder path for item {itemId} (parentPath='{parentPath}').");

        var url = $"{drive.WebUrl.TrimEnd('/')}{split[1]}/{item.Name}";
        _log.LogInformation("[GRAPH] Resolved folder-path URL: {Url}", url);
        return url;
    }

    public async Task<byte[]> DownloadAsync(
        string driveId,
        string itemId,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        _log.LogInformation(
            "[GRAPH] Requesting content stream. DriveId={Drive}, ItemId={Item}.",
            driveId,
            itemId);

        try
        {
            await using var stream = await _graph
                .Drives[driveId]
                .Items[itemId]
                .Content
                .GetAsync(cancellationToken: ct)
                ?? throw new InvalidOperationException(
                    $"No content stream returned for item {itemId}.");

            _log.LogInformation(
                "[GRAPH] Stream acquired in {Ms} ms; copying to memory...",
                sw.ElapsedMilliseconds);

            using var ms = new MemoryStream();

            await stream.CopyToAsync(ms, ct);

            var bytes = ms.ToArray();

            _log.LogInformation(
                "[GRAPH] Download complete. {Bytes} bytes in {Ms} ms.",
                bytes.Length,
                sw.ElapsedMilliseconds);

            return bytes;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
        {
            _log.LogError(
                "[GRAPH] ODataError. Code={Code}, Message={Message}, StatusCode={StatusCode}, DriveId={Drive}, ItemId={Item}",
                ex.Error?.Code,
                ex.Error?.Message,
                ex.ResponseStatusCode,
                driveId,
                itemId);

            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "[GRAPH] Download FAILED after {Ms} ms for DriveId={Drive}, ItemId={Item}.",
                sw.ElapsedMilliseconds,
                driveId,
                itemId);

            throw;
        }
    }
}