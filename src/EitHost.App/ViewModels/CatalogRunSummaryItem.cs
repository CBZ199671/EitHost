using System.IO;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App.ViewModels;

public sealed class CatalogRunSummaryItem
{
    public CatalogRunSummaryItem(EitCatalogRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Summary = summary;
    }

    public EitCatalogRunSummary Summary { get; }

    public bool RawHdf5Exists => File.Exists(Summary.Hdf5Path);

    public bool DemodHdf5Exists => !string.IsNullOrWhiteSpace(Summary.LatestDemodHdf5Path)
        && File.Exists(Summary.LatestDemodHdf5Path);

    public bool CsvExists => !string.IsNullOrWhiteSpace(Summary.LatestCsvPath)
        && File.Exists(Summary.LatestCsvPath);

    public string Title => $"{Summary.SetLabel}  {Summary.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";

    public string ShapeLine => $"raw {Summary.SampleRows}x{Summary.ChannelCount} | files {Summary.FileCount} | exports {Summary.ExportCount}";

    public string Hdf5Line => $"Raw [{(RawHdf5Exists ? "就绪" : "缺失")}]: {Summary.Hdf5Path}";

    public string DemodLine => string.IsNullOrWhiteSpace(Summary.LatestDemodHdf5Path)
        ? "Demod: none"
        : $"Demod [{(DemodHdf5Exists ? "就绪" : "缺失")}]: {Summary.LatestDemodHdf5Path}";

    public string CsvLine => string.IsNullOrWhiteSpace(Summary.LatestCsvPath)
        ? "CSV: none"
        : $"CSV [{(CsvExists ? "就绪" : "缺失")}]: {Summary.LatestCsvPath}";
}
