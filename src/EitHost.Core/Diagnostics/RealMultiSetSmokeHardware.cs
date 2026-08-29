using System.IO.Ports;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Diagnostics;

public sealed class RealMultiSetSmokeHardware : IMultiSetSmokeHardware
{
    private readonly HardwareSmokeReporter reporter;
    private readonly Usb2070Service usb2070Service;

    public RealMultiSetSmokeHardware(
        IPnpDeviceScanner pnpDeviceScanner,
        IUsb2070NativeApi usb2070NativeApi,
        Func<IReadOnlyList<string>> serialPortProvider,
        Func<Usb2070DriverPreflight> driverPreflightProvider)
    {
        ArgumentNullException.ThrowIfNull(pnpDeviceScanner);
        ArgumentNullException.ThrowIfNull(usb2070NativeApi);
        ArgumentNullException.ThrowIfNull(serialPortProvider);
        ArgumentNullException.ThrowIfNull(driverPreflightProvider);

        reporter = new HardwareSmokeReporter(
            pnpDeviceScanner,
            usb2070NativeApi,
            serialPortProvider,
            driverPreflightProvider);
        usb2070Service = new Usb2070Service(usb2070NativeApi);
    }

    public Task<HardwareSmokeReport> CaptureHardwareReportAsync(CancellationToken cancellationToken = default)
    {
        return reporter.CaptureAsync(cancellationToken);
    }

    public IMultiSetSmokeSetController CreateController(
        MultiSetSmokeSetPlan plan,
        MultiSetSmokeOptions options)
    {
        return new RealMultiSetSmokeSetController(usb2070Service, plan, options);
    }

    private sealed class RealMultiSetSmokeSetController : IMultiSetSmokeSetController
    {
        private readonly Usb2070Service usb2070Service;
        private readonly MultiSetSmokeOptions options;
        private Usb2070Session? session;

        public RealMultiSetSmokeSetController(
            Usb2070Service usb2070Service,
            MultiSetSmokeSetPlan plan,
            MultiSetSmokeOptions options)
        {
            this.usb2070Service = usb2070Service;
            Plan = plan;
            this.options = options;
        }

        public MultiSetSmokeSetPlan Plan { get; }

        public string Label => Plan.Pairing.SetLabel;

        public SingleSetSmokeDdsCommand? StartExcitationCommand { get; private set; }

        public DdsExecutionReceipt? ExecutionReceipt { get; private set; }

        public SingleSetSmokeDdsCommand? StopExcitationCommand { get; private set; }

        public Task StartAcquisitionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = usb2070Service.Open(Plan.UsbDevice);
            session.StartAcquisition(new Usb2070AcquisitionSettings(
                options.SampleRateHz,
                options.Range,
                options.TriggerMode,
                options.TriggerSource,
                triggerDelay: 0,
                triggerLength: options.SampleRows,
                triggerLevel: 2048,
                Enumerable.Range(1, Usb2070Constants.RequiredMeasurementChannelCount).ToArray()));
            return Task.CompletedTask;
        }

        public async Task StartExcitationAsync(CancellationToken cancellationToken = default)
        {
            using var transport = CreateTransport();
            var client = new DdsProtocolClient(transport);
            var result = await client.StartExcitationAsync(
                options.CreateExcitationSettings(),
                cancellationToken).ConfigureAwait(false);
            StartExcitationCommand = ToCommand(result);
            ExecutionReceipt = result.ExecutionReceipt;
        }

        public Task StopAcquisitionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session?.StopAcquisition();
            return Task.CompletedTask;
        }

        public async Task StopExcitationAsync(CancellationToken cancellationToken = default)
        {
            using var transport = CreateTransport();
            var client = new DdsProtocolClient(transport);
            var result = await client.StopExcitationAsync(cancellationToken).ConfigureAwait(false);
            StopExcitationCommand = ToCommand(result);
        }

        public SingleSetAdCapture ReadCapture(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session?.LastAcquisitionMetadata is not { } metadata)
            {
                throw new InvalidOperationException($"EIT set {Label} acquisition has not started.");
            }

            var valueCount = checked(options.SampleRows * Usb2070Constants.RequiredMeasurementChannelCount);
            var values = new ushort[valueCount];
            var readCount = session.Read(values, checked((uint)valueCount));
            var matrix = new ushort[options.SampleRows, Usb2070Constants.RequiredMeasurementChannelCount];
            for (var row = 0; row < options.SampleRows; row++)
            {
                for (var channel = 0; channel < Usb2070Constants.RequiredMeasurementChannelCount; channel++)
                {
                    matrix[row, channel] = values[(row * Usb2070Constants.RequiredMeasurementChannelCount) + channel];
                }
            }

            return new SingleSetAdCapture(Plan.UsbDevice, metadata, matrix, readCount);
        }

        public void Dispose()
        {
            session?.Dispose();
            session = null;
        }

        private DdsSerialPortTransport CreateTransport()
        {
            return new DdsSerialPortTransport(Plan.Pairing.DdsPortName);
        }

        private static SingleSetSmokeDdsCommand ToCommand(DdsCommandResult result)
        {
            return new SingleSetSmokeDdsCommand(
                result.Command.ToString(),
                result.PacketHex,
                result.SentAt);
        }
    }
}
