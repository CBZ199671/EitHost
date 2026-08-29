namespace EitHost.Core.Export;

public sealed record CsvExportResult(
    string SourceHdf5Path,
    string DatasetPath,
    string CsvPath,
    int RowCount,
    int ColumnCount,
    string Filter);
