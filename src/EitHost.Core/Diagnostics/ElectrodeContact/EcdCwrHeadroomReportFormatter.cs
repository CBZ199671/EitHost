using System.Text;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrHeadroomReportFormatter
{
    public static string ToMarkdown(EcdCwrHeadroomReport report, int maxRowsPerSection = 24)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (maxRowsPerSection <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRowsPerSection), "Row limit must be positive.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("# ECD-CWR P0.1 Headroom Report");
        builder.AppendLine();
        builder.AppendLine($"- Source: `{report.SourceLabel}`");
        builder.AppendLine($"- Frequency: {report.FrequencyHz:G} Hz");
        builder.AppendLine($"- Channel cycles: {report.ChannelCycles:G}");
        builder.AppendLine($"- Frames: {report.FrameCount}");
        builder.AppendLine($"- Guard magnitude: {report.GuardMagnitudeCounts} counts");
        builder.AppendLine($"- Saturation threshold magnitude: {report.SaturationThresholdMagnitudeCounts} counts");
        builder.AppendLine($"- Conclusion: `{report.Conclusion}`");
        builder.AppendLine($"- Summary: {report.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Aggregate");
        builder.AppendLine();
        builder.AppendLine("| Scope | Window saturation | Minimum headroom |");
        builder.AppendLine("|---|---:|---:|");
        builder.AppendLine($"| 48 excitation-related points | {report.SaturationRate48:P4} | {report.MinHeadroom48:P2} |");
        builder.AppendLine($"| 208 reconstruction points | {report.SaturationRate208:P4} | {report.MinHeadroom208:P2} |");
        builder.AppendLine();
        AppendCells(builder, "48 Excitation-Related Points", report.Cells48, maxRowsPerSection);
        builder.AppendLine();
        AppendCells(builder, "208 Reconstruction Points", report.Cells208, maxRowsPerSection);
        return builder.ToString();
    }

    private static void AppendCells(
        StringBuilder builder,
        string title,
        IReadOnlyList<EcdCwrHeadroomCell> cells,
        int maxRows)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| s | k | windows | window sat | sample sat | P99 magnitude | headroom |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var cell in cells
            .OrderBy(cell => cell.HeadroomFraction)
            .ThenByDescending(cell => cell.WindowSaturationRate)
            .Take(maxRows))
        {
            builder.AppendLine(
                $"| {cell.StimulationIndex} | {cell.RelativeChannelIndex} | {cell.WindowCount} | " +
                $"{cell.WindowSaturationRate:P4} | {cell.SampleSaturationRate:P4} | " +
                $"{cell.P99MagnitudeCounts:F1} | {cell.HeadroomFraction:P2} |");
        }

        if (cells.Count > maxRows)
        {
            builder.AppendLine();
            builder.AppendLine($"Showing worst {maxRows} of {cells.Count} cells by headroom.");
        }
    }
}
