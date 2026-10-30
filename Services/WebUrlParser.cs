using Aepw.Evidence.Worker.Models;

namespace Aepw.Evidence.Worker.Services;

/// <summary>
/// Parses a SharePoint file WebUrl into its evidence hierarchy. Expects the fixed layout:
///   https://{host}/sites/{siteName}/{documentLibrary}/{projectPin}/{quarterCode}/{metricCategory}/{topMetric}/{l1Metric}/{evidenceType}/{fileName}
/// Throws FormatException if the URL does not match (surfaced as a permanent failure upstream).
/// </summary>
public static class WebUrlParser
{
    public static EvidencePath Parse(string webUrl)
    {
        if (string.IsNullOrWhiteSpace(webUrl))
            throw new FormatException("WebUrl is null or empty.");

        var uri = new Uri(webUrl);

        // Decode each segment (%20 -> space, etc.) and drop empties.
        var segs = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        // Layout: sites / siteName / docLib / projectPin / quarterCode / metricCategory / topMetric / l1Metric / evidenceType / fileName
        //  index:   0        1          2         3            4              5                6           7           8             9
        if (segs.Length < 10 || !string.Equals(segs[0], "sites", StringComparison.OrdinalIgnoreCase))
            throw new FormatException(
                $"Unexpected WebUrl shape ({segs.Length} segments, expected >=10 starting with 'sites'): {webUrl}");

        return new EvidencePath(
            SiteUri:         $"{uri.Scheme}://{uri.Host}/sites",
            SiteName:        segs[1],
            DocumentLibrary: segs[2],
            ProjectPin:      segs[3],
            QuarterCode:     segs[4],
            MetricCategory:  segs[5],
            TopMetric:       segs[6],
            L1Metric:        segs[7],
            EvidenceType:    segs[8],
            FileName:        segs[9]);
    }
}