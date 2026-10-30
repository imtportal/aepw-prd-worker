namespace Aepw.Evidence.Worker.Models;

/// <summary>
/// Result of the save stored procedure. The SP returns rows of (Data, Message):
///   Data > 0  -> the new EvidenceId (success)
///   Data = 0  -> the SP hit an error; Message carries the detail (from its CATCH block or a THROW)
/// </summary>
public sealed record SaveEvidenceResult(long EvidenceId, string Message)
{
    public bool IsSuccess => EvidenceId > 0;
}
