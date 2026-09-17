using System.IO.Compression;

namespace EitHost.Core.Storage.Hdf5;

internal static class RawCompressionPolicy
{
    internal const string Id = "sampled_shuffle_deflate1_or_uncompressed_v1";

    // Probe at most 32 KiB, once when a shard is created. Four separated windows
    // reduce dependence on a single stimulus phase. Both routes are lossless and
    // HDF5 records the selected filters on the dataset for existing readers.
    internal static bool ShouldCompress(ReadOnlySpan<ushort> values)
    {
        if (values.Length < 2048) return true;
        var count = Math.Min(16384, values.Length);
        count -= count % 4;
        var shuffled = new byte[count * 2];
        var window = count / 4;
        for (var section = 0; section < 4; section++)
        {
            var start = (values.Length - window) * section / 3;
            for (var i = 0; i < window; i++)
            {
                var value = values[start + i];
                var destination = section * window + i;
                shuffled[destination] = (byte)value;
                shuffled[count + destination] = (byte)(value >> 8);
            }
        }
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            compressor.Write(shuffled);
        return output.Length < shuffled.Length * 0.90;
    }
}
