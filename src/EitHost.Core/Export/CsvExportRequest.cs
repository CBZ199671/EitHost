namespace EitHost.Core.Export;

public sealed record CsvExportRequest(
    string SourceHdf5Path,
    string DatasetPath,
    string CsvPath,
    string Filter = "all");
