namespace Aepw.Evidence.Worker.Models;

/// <summary>
/// The reference message the Listener placed on the queue. The Worker deserializes it, downloads
/// the file by (driveId,itemId), and forwards it to the Backend API.
/// </summary>
public sealed class ProcessingRequest
{
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = "created";   // created | updated | deleted
    public string SiteId { get; set; } = string.Empty;
    public string DriveId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? WebUrl { get; set; }
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
}
