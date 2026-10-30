namespace Aepw.Evidence.Worker.Models;

/// <summary>
/// The evidence metadata parsed out of a SharePoint file's WebUrl. The path after the host is a
/// fixed 10-segment structure:
///   .../sites/{siteName}/{documentLibrary}/{projectPin}/{quarterCode}/{metricCategory}/{topMetric}/{l1Metric}/{evidenceType}/{fileName}
///
/// EvidenceType is the exact EvidenceType.Code (e.g. "MW1_1_PURCHASE_NOTE") — the folder is named
/// with the code, so it is read verbatim (no construction/normalization).
/// L1Metric is a sub-metric level (e.g. "MW 1.11"), sent to the API as a diagnostic corroborating value.
/// </summary>
public sealed record EvidencePath(
    string SiteUri,
    string SiteName,
    string DocumentLibrary,
    string ProjectPin,
    string QuarterCode,
    string MetricCategory,
    string TopMetric,
    string L1Metric,
    string EvidenceType,
    string FileName);