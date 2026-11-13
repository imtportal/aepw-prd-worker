namespace Aepw.Evidence.Worker.Models;

/// <summary>
/// Result returned by dbo.uspEvidenceCheckAlreadyProcessed.
/// AlreadyProcessed=true means the evidence has a good processed status AND the stored
/// FileHash matches the one supplied — the Worker should skip the agent call.
/// </summary>
public sealed class AlreadyProcessedResult
{
    public bool AlreadyProcessed { get; init; }
    public string? CurrentStatusCode { get; init; }
    public string? StoredFileHash { get; init; }
}
