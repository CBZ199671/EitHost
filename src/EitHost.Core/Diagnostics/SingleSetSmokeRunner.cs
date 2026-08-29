using EitHost.Core.Domain;
using EitHost.Core.Demodulation;
using EitHost.Core.Export;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Pairing;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Diagnostics;

public sealed class SingleSetSmokeRunner
{
    private readonly ISingleSetSmokeHardware hardware;
    private readonly Hdf5RunWriter hdf5RunWriter;
    private readonly Hdf5OfflineDemodService demodService;
    private readonly Hdf5CsvExporter csvExporter;

    public SingleSetSmokeRunner(
        ISingleSetSmokeHardware hardware,
        Hdf5RunWriter? hdf5RunWriter = null,
        Hdf5OfflineDemodService? demodService = null,
        Hdf5CsvExporter? csvExporter = null)
    {
        this.hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        this.hdf5RunWriter = hdf5RunWriter ?? new Hdf5RunWriter();
        this.demodService = demodService ?? new Hdf5OfflineDemodService();
        this.csvExporter = csvExporter ?? new Hdf5CsvExporter();
    }

    public async Task<SingleSetSmokeReport> RunAsync(
        SingleSetSmokeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SampleRows);

        var startedAt = DateTimeOffset.Now;
        var hardwareReport = await hardware.CaptureHardwareReportAsync(cancellationToken).ConfigureAwait(false);
        var hardwareSummary = CreateHardwareSummary(hardwareReport);
        if (!hardwareReport.Readiness.ReadyForSingleSetSmoke)
        {
            return new SingleSetSmokeReport(
                startedAt,
                DateTimeOffset.Now,
                Ready: false,
                Passed: false,
                "硬件未就绪，未执行 DDS/AD 操作。",
                hardwareSummary,
                Pairing: null,
                SetDacCommand: null,
                SetPgaCommand: null,
                StartExcitationCommand: null,
                StopExcitationCommand: null,
                Acquisition: null,
                Artifacts: null,
                hardwareReport.Warnings);
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var usbCandidate = SelectUsbCandidate(hardwareReport);
        var ddsCandidate = SelectDdsCandidate(hardwareReport, options);
        var sdkDevice = SelectSdkDevice(hardwareReport, options);
        var usbDevice = CreateUsbDevice(usbCandidate, sdkDevice);
        var pairing = CreatePairing(options, usbCandidate, ddsCandidate, sdkDevice);
        var dacSettings = CreateDacSettings(options);
        var excitationSettings = CreateExcitationSettings(options);
        var startupResult = await hardware
            .SendDdsStartupSequenceAsync(pairing.DdsPortName, dacSettings, options.PgaGain, excitationSettings, cancellationToken)
            .ConfigureAwait(false);
        SingleSetAdCapture capture;
        DdsCommandResult? stopDdsResult = null;
        try
        {
            capture = hardware.CaptureAdBlock(usbDevice, options, cancellationToken);
        }
        finally
        {
            stopDdsResult = await hardware.SendDdsStopExcitationAsync(pairing.DdsPortName, cancellationToken).ConfigureAwait(false);
        }

        var runData = CreateRunData(
            options,
            pairing,
            capture,
            startedAt,
            startupResult.StartExcitation.ExecutionReceipt);
        var rawHdf5Path = CreateRawHdf5Path(options.OutputDirectory, runData);
        hdf5RunWriter.Write(rawHdf5Path, runData);

        var demodHdf5Path = Path.Combine(
            options.OutputDirectory,
            $"{Path.GetFileNameWithoutExtension(rawHdf5Path)}.demod.h5");
        var demodResult = demodService.DemodulateFileDetailed(rawHdf5Path, demodHdf5Path);
        var rawCsvPath = Path.Combine(
            options.OutputDirectory,
            $"{Path.GetFileNameWithoutExtension(rawHdf5Path)}.raw.csv");
        var csvResult = csvExporter.Export(new CsvExportRequest(rawHdf5Path, "/raw/adc_counts", rawCsvPath, "all"));
        var catalogPath = Path.Combine(options.OutputDirectory, "eit_smoke_catalog.sqlite");
        var catalogSummary = WriteCatalog(catalogPath, runData, rawHdf5Path, demodResult.OutputHdf5Path, csvResult);

        var artifacts = new SingleSetSmokeArtifacts(
            rawHdf5Path,
            demodResult.OutputHdf5Path,
            csvResult.CsvPath,
            catalogPath,
            demodResult.Demodulation.Frames.Count,
            demodResult.Demodulation.PeakLocations.Count,
            csvResult.RowCount,
            csvResult.ColumnCount)
        {
            CatalogSummary = catalogSummary
        };

        return new SingleSetSmokeReport(
            startedAt,
            DateTimeOffset.Now,
            Ready: true,
            Passed: true,
            "T24 单套真机冒烟完成。",
            hardwareSummary,
            pairing,
            ToCommand(startupResult.SetDac),
            ToCommand(startupResult.SetPga),
            ToCommand(startupResult.StartExcitation),
            ToCommand(stopDdsResult ?? throw new InvalidOperationException("DDS stop command was not recorded.")),
            new SingleSetSmokeAcquisition(
                capture.AdcCounts.GetLength(0),
                capture.AdcCounts.GetLength(1),
                capture.RawValueCount,
                capture.Metadata.SampleRateHz,
                capture.Metadata.Range.ToString(),
                capture.Metadata.AdBit),
            artifacts,
            hardwareReport.Warnings);
    }

    private static SingleSetSmokeDdsCommand ToCommand(DdsCommandResult result)
    {
        return new SingleSetSmokeDdsCommand(
            result.Command.ToString(),
            result.PacketHex,
            result.SentAt);
    }

    private static SingleSetSmokeHardwareSummary CreateHardwareSummary(HardwareSmokeReport report)
    {
        return new SingleSetSmokeHardwareSummary(
            report.PnpUsb2070Devices.Count,
            report.PnpDdsSerialDevices.Count,
            report.OsSerialPorts.Count,
            report.Usb2070SdkDevices.Count,
            report.Readiness.ReadyForSingleSetSmoke,
            report.Readiness.Blockers);
    }

    private static HardwareSmokeDeviceCandidate SelectUsbCandidate(HardwareSmokeReport report)
    {
        return report.PnpUsb2070Devices.First();
    }

    private static HardwareSmokeDeviceCandidate SelectDdsCandidate(
        HardwareSmokeReport report,
        SingleSetSmokeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DdsPortName))
        {
            return report.PnpDdsSerialDevices.First(device =>
                string.Equals(device.PortName, options.DdsPortName, StringComparison.OrdinalIgnoreCase));
        }

        return report.PnpDdsSerialDevices.First();
    }

    private static HardwareSmokeUsb2070Device SelectSdkDevice(
        HardwareSmokeReport report,
        SingleSetSmokeOptions options)
    {
        if (options.Usb2070DeviceNumber is { } deviceNumber)
        {
            return report.Usb2070SdkDevices.First(device => device.DeviceNumber == deviceNumber);
        }

        return report.Usb2070SdkDevices.First();
    }

    private static Usb2070Device CreateUsbDevice(
        HardwareSmokeDeviceCandidate candidate,
        HardwareSmokeUsb2070Device sdkDevice)
    {
        return new Usb2070Device(
            sdkDevice.DeviceNumber,
            $"USB2070:{sdkDevice.DeviceNumber}",
            candidate.DisplayName,
            candidate.Vid,
            candidate.Pid,
            candidate.LocationPath,
            sdkDevice.AvailableChannelCount,
            sdkDevice.AdBit,
            sdkDevice.MaxSampleRateHz);
    }

    private static SingleSetSmokePairing CreatePairing(
        SingleSetSmokeOptions options,
        HardwareSmokeDeviceCandidate usbCandidate,
        HardwareSmokeDeviceCandidate ddsCandidate,
        HardwareSmokeUsb2070Device sdkDevice)
    {
        return new SingleSetSmokePairing(
            options.SetLabel,
            sdkDevice.DeviceNumber,
            usbCandidate.DeviceId,
            usbCandidate.DisplayName,
            usbCandidate.Vid,
            usbCandidate.Pid,
            usbCandidate.LocationPath,
            ddsCandidate.PortName ?? throw new InvalidOperationException("DDS candidate has no COM port."),
            ddsCandidate.DeviceId,
            ddsCandidate.DisplayName,
            ddsCandidate.Vid,
            ddsCandidate.Pid,
            ddsCandidate.LocationPath);
    }

    private static Hdf5RunData CreateRunData(
        SingleSetSmokeOptions options,
        SingleSetSmokePairing pairing,
        SingleSetAdCapture capture,
        DateTimeOffset capturedAt,
        DdsExecutionReceipt? execution)
    {
        return new Hdf5RunData(
            Guid.NewGuid(),
            Guid.NewGuid(),
            capturedAt,
            new DeviceRunMetadata(
                pairing.SetLabel,
                EitSet.MeasurementChannelCount,
                pairing.Usb2070DeviceNumber,
                pairing.Usb2070DeviceId,
                pairing.Usb2070DisplayName,
                pairing.Usb2070Vid,
                pairing.Usb2070Pid,
                pairing.Usb2070LocationPath,
                pairing.DdsPortName,
                pairing.DdsDeviceId,
                pairing.DdsDisplayName,
                pairing.DdsVid,
                pairing.DdsPid,
                pairing.DdsLocationPath),
            new Hdf5ExcitationMetadata(
                CreateDacSettings(options),
                CreateExcitationSettings(options),
                options.PgaGain,
                execution),
            capture.Metadata,
            capture.AdcCounts);
    }

    private static DdsExcitationSettings CreateExcitationSettings(SingleSetSmokeOptions options)
    {
        return new DdsExcitationSettings(
            DdsExcitationMode.Adjacent,
            options.ExcitationFrequencyHz);
    }

    private static DdsDacSettings CreateDacSettings(SingleSetSmokeOptions options)
    {
        return new DdsDacSettings(
            checked((byte)options.DacChannel),
            options.ExcitationFrequencyHz,
            options.DacGain,
            options.DacPhaseDegrees);
    }

    private static string CreateRawHdf5Path(string outputDirectory, Hdf5RunData runData)
    {
        var safeLabel = string.Concat(runData.Device.SetLabel.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(
            outputDirectory,
            $"{runData.CapturedAt:yyyyMMdd_HHmmss_fff}_{safeLabel}_{runData.RunId:N}.h5");
    }

    private static EitCatalogSummary WriteCatalog(
        string catalogPath,
        Hdf5RunData runData,
        string rawHdf5Path,
        string demodHdf5Path,
        CsvExportResult csvResult)
    {
        var catalog = new EitCatalog(catalogPath);
        catalog.Initialize();
        catalog.AddSession(runData.SessionId, $"T24 smoke {runData.CapturedAt:yyyy-MM-dd HH:mm:ss}", runData.CapturedAt);
        catalog.AddPairing(
            runData.SessionId,
            new EitSetPairing(
                runData.Device.SetLabel,
                runData.Device.UsbDeviceNumber,
                new PnpDeviceCandidate(
                    PnpDeviceKind.Usb2070,
                    runData.Device.UsbDeviceId,
                    runData.Device.UsbDisplayName,
                    runData.Device.UsbVid,
                    runData.Device.UsbPid,
                    runData.Device.UsbLocationPath),
                new PnpDeviceCandidate(
                    PnpDeviceKind.SerialPort,
                    runData.Device.DdsDeviceId,
                    runData.Device.DdsDisplayName,
                    runData.Device.DdsVid,
                    runData.Device.DdsPid,
                    runData.Device.DdsLocationPath,
                    runData.Device.DdsPortName),
                runData.CapturedAt));
        catalog.AddRun(runData, rawHdf5Path);
        catalog.AddFile(runData.RunId, "demod_hdf5", demodHdf5Path, "/demod", DateTimeOffset.Now);
        catalog.AddExport(
            runData.RunId,
            csvResult.SourceHdf5Path,
            csvResult.DatasetPath,
            csvResult.CsvPath,
            csvResult.Filter,
            DateTimeOffset.Now);
        return catalog.GetSummary();
    }
}
