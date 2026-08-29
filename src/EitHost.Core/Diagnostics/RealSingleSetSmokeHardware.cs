using EitHost.Core.Acquisition;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Diagnostics;

public sealed class RealSingleSetSmokeHardware : ISingleSetSmokeHardware
{
    private static readonly TimeSpan DdsStartupCommandDelay = TimeSpan.FromMilliseconds(80);

    private readonly IPnpDeviceScanner pnpScanner;
    private readonly IUsb2070NativeApi usb2070NativeApi;
    private readonly Func<IReadOnlyList<string>> serialPortProvider;
    private readonly Func<Usb2070DriverPreflight>? driverPreflightProvider;

    public RealSingleSetSmokeHardware(
        IPnpDeviceScanner pnpScanner,
        IUsb2070NativeApi usb2070NativeApi,
        Func<IReadOnlyList<string>> serialPortProvider,
        Func<Usb2070DriverPreflight>? driverPreflightProvider = null)
    {
        this.pnpScanner = pnpScanner ?? throw new ArgumentNullException(nameof(pnpScanner));
        this.usb2070NativeApi = usb2070NativeApi ?? throw new ArgumentNullException(nameof(usb2070NativeApi));
        this.serialPortProvider = serialPortProvider ?? throw new ArgumentNullException(nameof(serialPortProvider));
        this.driverPreflightProvider = driverPreflightProvider;
    }

    public Task<HardwareSmokeReport> CaptureHardwareReportAsync(CancellationToken cancellationToken = default)
    {
        var reporter = new HardwareSmokeReporter(
            pnpScanner,
            usb2070NativeApi,
            serialPortProvider,
            driverPreflightProvider);
        return reporter.CaptureAsync(cancellationToken);
    }

    public async Task<SingleSetDdsStartupResult> SendDdsStartupSequenceAsync(
        string portName,
        DdsDacSettings dacSettings,
        byte pgaGain,
        DdsExcitationSettings excitationSettings,
        CancellationToken cancellationToken = default)
    {
        using var transport = new DdsSerialPortTransport(portName);
        var client = new DdsProtocolClient(transport);
        var setDac = await client.SetDacAsync(dacSettings, cancellationToken).ConfigureAwait(false);
        await Task.Delay(DdsStartupCommandDelay, cancellationToken).ConfigureAwait(false);
        var setPga = await client.SetPgaAsync(pgaGain, cancellationToken).ConfigureAwait(false);
        await Task.Delay(DdsStartupCommandDelay, cancellationToken).ConfigureAwait(false);
        var startExcitation = await client.StartExcitationAsync(excitationSettings, cancellationToken).ConfigureAwait(false);
        return new SingleSetDdsStartupResult(setDac, setPga, startExcitation);
    }

    public async Task<DdsCommandResult> SendDdsStartExcitationAsync(
        string portName,
        DdsExcitationSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var transport = new DdsSerialPortTransport(portName);
        var client = new DdsProtocolClient(transport);
        return await client.StartExcitationAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DdsCommandResult> SendDdsSetDacAsync(
        string portName,
        DdsDacSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var transport = new DdsSerialPortTransport(portName);
        var client = new DdsProtocolClient(transport);
        return await client.SetDacAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DdsCommandResult> SendDdsSetPgaAsync(
        string portName,
        byte gain,
        CancellationToken cancellationToken = default)
    {
        using var transport = new DdsSerialPortTransport(portName);
        var client = new DdsProtocolClient(transport);
        return await client.SetPgaAsync(gain, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DdsCommandResult> SendDdsStopExcitationAsync(
        string portName,
        CancellationToken cancellationToken = default)
    {
        using var transport = new DdsSerialPortTransport(portName);
        var client = new DdsProtocolClient(transport);
        return await client.StopExcitationAsync(cancellationToken).ConfigureAwait(false);
    }

    public SingleSetAdCapture CaptureAdBlock(
        Usb2070Device device,
        SingleSetSmokeOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);

        var service = new Usb2070Service(usb2070NativeApi);
        using var session = service.Open(device);
        var settings = new Usb2070AcquisitionSettings(
            options.SampleRateHz,
            options.Range,
            options.TriggerMode,
            options.TriggerSource,
            options.TriggerDelay,
            options.TriggerLength,
            options.TriggerLevel);

        try
        {
            session.StartAcquisition(settings);
            var matrix = session.ReadRowsChunked(options.SampleRows);
            var readCount = checked(matrix.Length);
            var metadata = session.LastAcquisitionMetadata
                ?? new Usb2070AcquisitionMetadata(
                    settings.SampleRateHz,
                    settings.Range,
                    device.AdBit,
                    settings.EnabledOneBasedChannels,
                    settings.TriggerMode,
                    settings.TriggerSource);

            return new SingleSetAdCapture(device, metadata, matrix, readCount);
        }
        finally
        {
            session.StopAcquisition();
        }
    }
}
