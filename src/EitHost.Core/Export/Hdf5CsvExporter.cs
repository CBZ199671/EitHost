using System.Globalization;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Export;

public sealed class Hdf5CsvExporter
{
    public CsvExportResult Export(CsvExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceHdf5Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CsvPath);

        if (string.Equals(request.DatasetPath, "/raw/adc_counts", StringComparison.Ordinal))
        {
            return ExportRaw(request);
        }

        using var file = Hdf5FileAccess.OpenReadWithRetry(request.SourceHdf5Path);
        var dataset = file.Dataset(request.DatasetPath);
        var matrix = ReadSupportedMatrix(dataset);
        WriteCsv(request.CsvPath, matrix);

        return new CsvExportResult(
            Path.GetFullPath(request.SourceHdf5Path),
            request.DatasetPath,
            Path.GetFullPath(request.CsvPath),
            matrix.GetLength(0),
            matrix.GetLength(1),
            request.Filter);
    }

    private static CsvExportResult ExportRaw(CsvExportRequest request)
    {
        int rowCount;
        int columnCount;
        using (var file = Hdf5FileAccess.OpenReadWithRetry(request.SourceHdf5Path))
        {
            var dimensions = file.Dataset(request.DatasetPath).Space.Dimensions.ToArray();
            if (dimensions.Length != 2 || dimensions[0] > int.MaxValue || dimensions[1] > int.MaxValue)
            {
                throw new NotSupportedException("Raw CSV export requires a two-dimensional Int32-sized matrix.");
            }

            rowCount = checked((int)dimensions[0]);
            columnCount = checked((int)dimensions[1]);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.CsvPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var writer = new StreamWriter(request.CsvPath, append: false))
        {
            foreach (var chunk in new Hdf5RawDatasetReader().ReadRange(
                         request.SourceHdf5Path,
                         rowOffset: 0,
                         rowCount))
            {
                WriteCsvRows(writer, chunk.Values);
            }
        }

        return new CsvExportResult(
            Path.GetFullPath(request.SourceHdf5Path),
            request.DatasetPath,
            Path.GetFullPath(request.CsvPath),
            rowCount,
            columnCount,
            request.Filter);
    }

    public IReadOnlyList<CsvExportResult> ExportMany(IEnumerable<CsvExportRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return requests.Select(Export).ToArray();
    }

    private static double[,] ReadSupportedMatrix(IH5Dataset dataset)
    {
        if (TryRead(() => dataset.Read<double[,]>(), out var doubleMatrix))
        {
            return doubleMatrix;
        }

        if (TryRead(() => dataset.Read<ushort[,]>(), out var ushortMatrix))
        {
            return ToDouble(ushortMatrix);
        }

        if (TryRead(() => dataset.Read<int[,]>(), out var intMatrix))
        {
            return ToDouble(intMatrix);
        }

        throw new NotSupportedException("Only 2D numeric HDF5 datasets are supported for CSV export.");
    }

    private static bool TryRead<T>(Func<T> read, out T value)
    {
        try
        {
            value = read();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

    private static double[,] ToDouble(ushort[,] values)
    {
        var result = new double[values.GetLength(0), values.GetLength(1)];
        for (var row = 0; row < values.GetLength(0); row++)
        {
            for (var column = 0; column < values.GetLength(1); column++)
            {
                result[row, column] = values[row, column];
            }
        }

        return result;
    }

    private static double[,] ToDouble(int[,] values)
    {
        var result = new double[values.GetLength(0), values.GetLength(1)];
        for (var row = 0; row < values.GetLength(0); row++)
        {
            for (var column = 0; column < values.GetLength(1); column++)
            {
                result[row, column] = values[row, column];
            }
        }

        return result;
    }

    private static void WriteCsv(string csvPath, double[,] matrix)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(csvPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(csvPath, append: false);
        for (var row = 0; row < matrix.GetLength(0); row++)
        {
            for (var column = 0; column < matrix.GetLength(1); column++)
            {
                if (column > 0)
                {
                    writer.Write(',');
                }

                var value = matrix[row, column];
                writer.Write(double.IsNaN(value) ? "NaN" : value.ToString("G17", CultureInfo.InvariantCulture));
            }

            writer.WriteLine();
        }
    }

    private static void WriteCsvRows(StreamWriter writer, ushort[,] matrix)
    {
        for (var row = 0; row < matrix.GetLength(0); row++)
        {
            for (var column = 0; column < matrix.GetLength(1); column++)
            {
                if (column > 0)
                {
                    writer.Write(',');
                }

                writer.Write(matrix[row, column].ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteLine();
        }
    }
}
