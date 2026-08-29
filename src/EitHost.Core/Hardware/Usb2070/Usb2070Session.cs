using EitHost.Core.Domain;

namespace EitHost.Core.Hardware.Usb2070;

public sealed class Usb2070Session : IDisposable
{
    public const int DefaultMaxRowsPerRead = 2048;

    private static readonly TimeSpan SoftwareTriggerWarmup = TimeSpan.FromMilliseconds(100);

    private readonly IUsb2070NativeApi nativeApi;
    private readonly nint handle;
    private bool disposed;
    private bool acquisitionStarted;
    private bool softwareTriggerExecuted;
    private DateTimeOffset? acquisitionStartedAt;

    internal Usb2070Session(IUsb2070NativeApi nativeApi, nint handle, Usb2070Device device, Usb2070CardInfo cardInfo)
    {
        this.nativeApi = nativeApi;
        this.handle = handle;
        Device = device;
        CardInfo = cardInfo;
    }

    public Usb2070Device Device { get; }

    public Usb2070CardInfo CardInfo { get; }

    public Usb2070AcquisitionMetadata? LastAcquisitionMetadata { get; private set; }

    public bool LastReadBufferOverflow { get; private set; }

    public long BufferOverflowReadCount { get; private set; }

    public void StartAcquisition(Usb2070AcquisitionSettings settings)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettingsAgainstDevice(settings);

        var parameters = settings.ToNativeParameters();
        if (!nativeApi.InitAd(handle, ref parameters))
        {
            throw CreateException("InitAD", $"初始化 USB2070 #{Device.DeviceNumber} AD 失败。");
        }

        acquisitionStarted = true;
        softwareTriggerExecuted = false;
        LastReadBufferOverflow = false;
        BufferOverflowReadCount = 0;
        acquisitionStartedAt = DateTimeOffset.UtcNow;
        LastAcquisitionMetadata = new Usb2070AcquisitionMetadata(
            settings.SampleRateHz,
            settings.Range,
            CardInfo.AdBit,
            settings.EnabledOneBasedChannels,
            settings.TriggerMode,
            settings.EffectiveTriggerSource);
    }

    public int Read(ushort[] buffer, uint count)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);

        if (count == 0 || count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Read count must fit the provided buffer.");
        }

        ExecuteSoftwareTriggerIfNeeded();
        if (!nativeApi.ReadAd(handle, buffer, count))
        {
            throw CreateException("ReadAD", CreateReadFailureMessage(count));
        }

        LastReadBufferOverflow = nativeApi.GetBufferOverflow(handle, out var overflow) && overflow != 0;
        if (LastReadBufferOverflow)
        {
            BufferOverflowReadCount++;
        }

        return checked((int)count);
    }

    public ushort[,] ReadRowsChunked(
        int rowCount,
        int channelCount = Usb2070Constants.RequiredMeasurementChannelCount,
        int maxRowsPerRead = DefaultMaxRowsPerRead)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRowsPerRead);

        var matrix = new ushort[rowCount, channelCount];
        var rowOffset = 0;
        while (rowOffset < rowCount)
        {
            var chunkRows = Math.Min(maxRowsPerRead, rowCount - rowOffset);
            var valueCount = checked(chunkRows * channelCount);
            var buffer = new ushort[valueCount];
            var readCount = Read(buffer, checked((uint)valueCount));
            if (readCount != valueCount)
            {
                throw new InvalidDataException(
                    $"USB2070 chunk returned {readCount} values; expected {valueCount}.");
            }

            for (var row = 0; row < chunkRows; row++)
            {
                for (var channel = 0; channel < channelCount; channel++)
                {
                    matrix[rowOffset + row, channel] = buffer[(row * channelCount) + channel];
                }
            }

            rowOffset += chunkRows;
        }

        return matrix;
    }

    public void StopAcquisition()
    {
        ThrowIfDisposed();

        if (!acquisitionStarted)
        {
            return;
        }

        acquisitionStarted = false;
        softwareTriggerExecuted = false;
        LastReadBufferOverflow = false;
        acquisitionStartedAt = null;
        if (!nativeApi.StopAd(handle, checked((byte)Device.DeviceNumber)))
        {
            throw CreateException("StopAD", $"停止 USB2070 #{Device.DeviceNumber} AD 失败。");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            if (acquisitionStarted)
            {
                nativeApi.StopAd(handle, checked((byte)Device.DeviceNumber));
                acquisitionStarted = false;
            }
        }
        finally
        {
            nativeApi.UnLink(handle);
            disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void ExecuteSoftwareTriggerIfNeeded()
    {
        if (softwareTriggerExecuted)
        {
            return;
        }

        if (LastAcquisitionMetadata is not
            {
                TriggerMode: not Usb2070TriggerMode.Continue,
                TriggerSource: Usb2070TriggerSource.Software
            })
        {
            return;
        }

        WaitForSoftwareTriggerWarmup();
        if (!nativeApi.ExecuteSoftTrigger(handle))
        {
            throw CreateException("ExeSoftTrig", $"USB2070 #{Device.DeviceNumber} 软件触发失败。");
        }

        softwareTriggerExecuted = true;
    }

    private void ValidateSettingsAgainstDevice(Usb2070AcquisitionSettings settings)
    {
        var availableChannelCount = Math.Min(Device.AvailableChannelCount, CardInfo.AdChannelCount);
        if (availableChannelCount < Usb2070Constants.RequiredMeasurementChannelCount)
        {
            throw CreateException(
                "InitAD",
                $"USB2070 #{Device.DeviceNumber} 可用 AD 通道数 {availableChannelCount} 小于 EIT 需要的 {Usb2070Constants.RequiredMeasurementChannelCount} 通道。");
        }

        var highestEnabledChannel = settings.EnabledOneBasedChannels.Max();
        if (highestEnabledChannel > availableChannelCount)
        {
            throw CreateException(
                "InitAD",
                $"USB2070 #{Device.DeviceNumber} 报告 {availableChannelCount} 个 AD 通道，但启用了 CH{highestEnabledChannel}。");
        }

        if (settings.SampleRateHz > CardInfo.MaxSampleRateHz)
        {
            throw CreateException(
                "InitAD",
                $"USB2070 #{Device.DeviceNumber} 采样率 {settings.SampleRateHz} Hz 超过设备上限 {CardInfo.MaxSampleRateHz} Hz。");
        }
    }

    private void WaitForSoftwareTriggerWarmup()
    {
        if (acquisitionStartedAt is not { } startedAt)
        {
            return;
        }

        var remaining = SoftwareTriggerWarmup - (DateTimeOffset.UtcNow - startedAt);
        if (remaining > TimeSpan.Zero)
        {
            Thread.Sleep(remaining);
        }
    }

    private string CreateReadFailureMessage(uint count)
    {
        var metadata = LastAcquisitionMetadata;
        var overflowText = nativeApi.GetBufferOverflow(handle, out var overflow)
            ? overflow.ToString()
            : "unknown";
        var sampleRate = metadata?.SampleRateHz.ToString() ?? "unknown";
        var trigger = metadata is null ? "unknown" : $"{metadata.TriggerMode}/{metadata.TriggerSource}";

        return $"读取 USB2070 #{Device.DeviceNumber} 数据失败（请求 {count} values，采样率 {sampleRate} Hz，触发 {trigger}，缓冲溢出 {overflowText}）。";
    }

    private Usb2070NativeException CreateException(string operation, string message)
    {
        return new Usb2070NativeException(new Usb2070OperationError(operation, message, Device.DeviceNumber));
    }
}
