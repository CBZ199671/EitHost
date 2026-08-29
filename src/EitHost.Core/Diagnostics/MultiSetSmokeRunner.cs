using System.Diagnostics;
using EitHost.Core.Domain;
using EitHost.Core.Demodulation;
using EitHost.Core.Export;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Pairing;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Hdf5;
using EitHost.Core.Sync;

namespace EitHost.Core.Diagnostics;

public sealed class MultiSetSmokeRunner
{
    private readonly IMultiSetSmokeHardware hardware;
    private readonly Hdf5RunWriter hdf5RunWriter;
    private readonly Hdf5OfflineDemodService demodService;
    private readonly Hdf5CsvExporter csvExporter;
    private readonly Func<SyncStartCoordinator> coordinatorFactory;
    private readonly Action<string> cleanupFailureReporter;

    public MultiSetSmokeRunner(
        IMultiSetSmokeHardware hardware,
        Hdf5RunWriter? hdf5RunWriter = null,
        Hdf5OfflineDemodService? demodService = null,
        Hdf5CsvExporter? csvExporter = null,
        Func<SyncStartCoordinator>? coordinatorFactory = null,
        Action<string>? reportCleanupFailure = null)
    {
        this.hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        this.hdf5RunWriter = hdf5RunWriter ?? new Hdf5RunWriter();
        this.demodService = demodService ?? new Hdf5OfflineDemodService();
        this.csvExporter = csvExporter ?? new Hdf5CsvExporter();
        this.coordinatorFactory = coordinatorFactory ?? (() => new SyncStartCoordinator());
        cleanupFailureReporter = reportCleanupFailure ?? (message => Trace.TraceWarning(message));
    }

    public async Task<MultiSetSmokeReport> RunAsync(
        MultiSetSmokeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.EffectiveSetCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SampleRows);

        var startedAt = DateTimeOffset.Now;
        var hardwareReport = await hardware.CaptureHardwareReportAsync(cancellationToken).ConfigureAwait(false);
        var hardwareSummary = CreateHardwareSummary(hardwareReport);
        var blockers = CreateBlockers(hardwareReport, options);
        if (blockers.Count > 0)
        {
            return new MultiSetSmokeReport(
                startedAt,
                DateTimeOffset.Now,
                Ready: false,
                options.Execute,
                Passed: false,
                "硬件数量不足或基础链路未就绪，未执行 DDS/AD 操作。",
                hardwareSummary,
                blockers,
                [],
                [],
                hardwareReport.Warnings);
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var plans = CreatePlans(hardwareReport, options);
        var plannedSets = plans
            .Select(plan => new MultiSetSmokeSetReport(plan.Pairing, null, null, null, null))
            .ToArray();
        if (!options.Execute)
        {
            return new MultiSetSmokeReport(
                startedAt,
                DateTimeOffset.Now,
                Ready: true,
                ExecuteRequested: false,
                Passed: false,
                "硬件已满足多套 smoke 条件；未传入 --execute，按无副作用模式停止。",
                hardwareSummary,
                [],
                plannedSets,
                [],
                hardwareReport.Warnings);
        }

        var controllers = plans
            .Select(plan => hardware.CreateController(plan, options))
            .ToArray();
        var captures = new List<(IMultiSetSmokeSetController Controller, SingleSetAdCapture Capture)>();
        SyncStartResult? syncResult = null;
        try
        {
            syncResult = await coordinatorFactory().StartAsync(controllers, cancellationToken).ConfigureAwait(false);
            foreach (var controller in controllers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                captures.Add((controller, controller.ReadCapture(cancellationToken)));
            }
        }
        catch (SyncStartException ex)
        {
            return CreateFailedReport(
                startedAt,
                hardwareSummary,
                hardwareReport,
                options,
                plans,
                ex.PartialResult.Records,
                ex.Message);
        }
        catch (Exception ex)
        {
            return CreateFailedReport(
                startedAt,
                hardwareSummary,
                hardwareReport,
                options,
                plans,
                syncResult?.Records ?? [],
                ex.Message);
        }
        finally
        {
            await StopAndDisposeAsync(controllers).ConfigureAwait(false);
        }

        var sessionId = Guid.NewGuid();
        var catalogPath = Path.Combine(options.OutputDirectory, "eit_multi_set_smoke_catalog.sqlite");
        var catalog = new EitCatalog(catalogPath);
        catalog.Initialize();
        catalog.AddSession(sessionId, $"T25 smoke {startedAt:yyyy-MM-dd HH:mm:ss}", startedAt);
        foreach (var plan in plans)
        {
            catalog.AddPairing(sessionId, ToPairing(plan.Pairing, startedAt));
        }

        var setReports = new List<MultiSetSmokeSetReport>();
        foreach (var (controller, capture) in captures)
        {
            var runData = CreateRunData(
                options,
                controller.Plan.Pairing,
                capture,
                sessionId,
                startedAt,
                controller.ExecutionReceipt);
            var artifacts = WriteArtifacts(options, catalog, catalogPath, runData);
            setReports.Add(new MultiSetSmokeSetReport(
                controller.Plan.Pairing,
                controller.StartExcitationCommand,
                controller.StopExcitationCommand,
                new SingleSetSmokeAcquisition(
                    capture.AdcCounts.GetLength(0),
                    capture.AdcCounts.GetLength(1),
                    capture.RawValueCount,
                    capture.Metadata.SampleRateHz,
                    capture.Metadata.Range.ToString(),
                    capture.Metadata.AdBit),
                artifacts));
        }

        var catalogSummary = catalog.GetSummary();
        var finalizedSetReports = setReports
            .Select(set => set.Artifacts is null
                ? set
                : set with { Artifacts = set.Artifacts with { CatalogSummary = catalogSummary } })
            .ToArray();

        return new MultiSetSmokeReport(
            startedAt,
            DateTimeOffset.Now,
            Ready: true,
            ExecuteRequested: true,
            Passed: true,
            "T25 多套真机冒烟执行完成。",
            hardwareSummary,
            [],
            finalizedSetReports,
            syncResult?.Records ?? [],
            hardwareReport.Warnings);
    }

    private static MultiSetSmokeReport CreateFailedReport(
        DateTimeOffset startedAt,
        SingleSetSmokeHardwareSummary hardwareSummary,
        HardwareSmokeReport hardwareReport,
        MultiSetSmokeOptions options,
        IReadOnlyList<MultiSetSmokeSetPlan> plans,
        IReadOnlyList<SyncSetStartRecord> syncRecords,
        string failure)
    {
        var warnings = hardwareReport.Warnings.Concat([$"执行失败：{failure}"]).ToArray();
        return new MultiSetSmokeReport(
            startedAt,
            DateTimeOffset.Now,
            Ready: true,
            options.Execute,
            Passed: false,
            "T25 多套真机冒烟执行失败；已尝试停止所有套件。",
            hardwareSummary,
            [],
            plans.Select(plan => new MultiSetSmokeSetReport(plan.Pairing, null, null, null, null)).ToArray(),
            syncRecords,
            warnings);
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

    private static IReadOnlyList<string> CreateBlockers(
        HardwareSmokeReport report,
        MultiSetSmokeOptions options)
    {
        var blockers = new List<string>();
        if (!report.Readiness.ReadyForSingleSetSmoke)
        {
            blockers.AddRange(report.Readiness.Blockers);
        }

        var requiredSetCount = options.EffectiveSetCount;
        if (report.PnpUsb2070Devices.Count < requiredSetCount)
        {
            blockers.Add($"PnP USB2070 数量不足：{report.PnpUsb2070Devices.Count}/{requiredSetCount}。");
        }

        if (report.PnpDdsSerialDevices.Count < requiredSetCount)
        {
            blockers.Add($"DDS 串口数量不足：{report.PnpDdsSerialDevices.Count}/{requiredSetCount}。");
        }

        if (report.Usb2070SdkDevices.Count < requiredSetCount)
        {
            blockers.Add($"USB2070 SDK 可打开设备数量不足：{report.Usb2070SdkDevices.Count}/{requiredSetCount}。");
        }

        if (options.RequestedPairs.Count > 0)
        {
            AddDuplicateBlockers(blockers, options.RequestedPairs);
            foreach (var pair in options.RequestedPairs)
            {
                if (report.Usb2070SdkDevices.All(device => device.DeviceNumber != pair.Usb2070DeviceNumber))
                {
                    blockers.Add($"显式配对 `{pair.Label}` 指定的 USB2070 SDK #{pair.Usb2070DeviceNumber} 不存在。");
                }

                if (report.PnpDdsSerialDevices.All(device =>
                        !string.Equals(device.PortName, pair.DdsPortName, StringComparison.OrdinalIgnoreCase)))
                {
                    blockers.Add($"显式配对 `{pair.Label}` 指定的 DDS 串口 {pair.DdsPortName} 不存在。");
                }

                if (!string.IsNullOrWhiteSpace(pair.Usb2070PnpIdentityFragment))
                {
                    var matchCount = CountUsbPnpMatches(report.PnpUsb2070Devices, pair.Usb2070PnpIdentityFragment);
                    if (matchCount == 0)
                    {
                        blockers.Add($"显式配对 `{pair.Label}` 指定的 USB2070 PnP 片段 `{pair.Usb2070PnpIdentityFragment}` 未匹配设备。");
                    }
                    else if (matchCount > 1)
                    {
                        blockers.Add($"显式配对 `{pair.Label}` 指定的 USB2070 PnP 片段 `{pair.Usb2070PnpIdentityFragment}` 匹配 {matchCount} 个设备。");
                    }
                }
            }
        }

        return blockers.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void AddDuplicateBlockers(
        ICollection<string> blockers,
        IReadOnlyList<MultiSetSmokeRequestedPair> requestedPairs)
    {
        AddDuplicateBlockers(blockers, requestedPairs.Select(pair => pair.Label), "设备标签");
        AddDuplicateBlockers(blockers, requestedPairs.Select(pair => pair.Usb2070DeviceNumber.ToString()), "USB2070 SDK 编号");
        AddDuplicateBlockers(blockers, requestedPairs.Select(pair => pair.DdsPortName), "DDS 串口");
        AddDuplicateBlockers(
            blockers,
            requestedPairs
                .Select(pair => pair.Usb2070PnpIdentityFragment)
                .Where(fragment => !string.IsNullOrWhiteSpace(fragment))
                .Select(fragment => fragment!),
            "USB2070 PnP 片段");
    }

    private static void AddDuplicateBlockers(
        ICollection<string> blockers,
        IEnumerable<string> values,
        string name)
    {
        foreach (var duplicate in values
                     .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            blockers.Add($"显式配对 {name} 重复：{duplicate}。");
        }
    }

    private static IReadOnlyList<MultiSetSmokeSetPlan> CreatePlans(
        HardwareSmokeReport report,
        MultiSetSmokeOptions options)
    {
        if (options.RequestedPairs.Count > 0)
        {
            return CreateRequestedPairPlans(report, options);
        }

        var plans = new List<MultiSetSmokeSetPlan>();
        var usbCandidates = report.PnpUsb2070Devices.Take(options.EffectiveSetCount).ToArray();
        var ddsCandidates = report.PnpDdsSerialDevices.Take(options.EffectiveSetCount).ToArray();
        var sdkDevices = report.Usb2070SdkDevices
            .OrderBy(device => device.DeviceNumber)
            .Take(options.EffectiveSetCount)
            .ToArray();

        for (var index = 0; index < options.EffectiveSetCount; index++)
        {
            var pairing = CreatePairing(options.CreateSetLabel(index), usbCandidates[index], ddsCandidates[index], sdkDevices[index]);
            plans.Add(new MultiSetSmokeSetPlan(pairing, CreateUsbDevice(usbCandidates[index], sdkDevices[index])));
        }

        return plans;
    }

    private static IReadOnlyList<MultiSetSmokeSetPlan> CreateRequestedPairPlans(
        HardwareSmokeReport report,
        MultiSetSmokeOptions options)
    {
        var plans = new List<MultiSetSmokeSetPlan>();
        var usbCandidates = report.PnpUsb2070Devices.Take(options.RequestedPairs.Count).ToArray();
        for (var index = 0; index < options.RequestedPairs.Count; index++)
        {
            var requestedPair = options.RequestedPairs[index];
            var sdkDevice = report.Usb2070SdkDevices.Single(device => device.DeviceNumber == requestedPair.Usb2070DeviceNumber);
            var ddsCandidate = report.PnpDdsSerialDevices.Single(device =>
                string.Equals(device.PortName, requestedPair.DdsPortName, StringComparison.OrdinalIgnoreCase));
            var usbCandidate = string.IsNullOrWhiteSpace(requestedPair.Usb2070PnpIdentityFragment)
                ? usbCandidates[index]
                : SelectUsbPnpMatch(report.PnpUsb2070Devices, requestedPair.Usb2070PnpIdentityFragment);
            var pairing = CreatePairing(requestedPair.Label, usbCandidate, ddsCandidate, sdkDevice);
            plans.Add(new MultiSetSmokeSetPlan(pairing, CreateUsbDevice(usbCandidate, sdkDevice)));
        }

        return plans;
    }

    private static int CountUsbPnpMatches(
        IReadOnlyList<HardwareSmokeDeviceCandidate> usbCandidates,
        string fragment)
    {
        return usbCandidates.Count(candidate => MatchesUsbPnpFragment(candidate, fragment));
    }

    private static HardwareSmokeDeviceCandidate SelectUsbPnpMatch(
        IReadOnlyList<HardwareSmokeDeviceCandidate> usbCandidates,
        string fragment)
    {
        return usbCandidates.Single(candidate => MatchesUsbPnpFragment(candidate, fragment));
    }

    private static bool MatchesUsbPnpFragment(
        HardwareSmokeDeviceCandidate candidate,
        string fragment)
    {
        return candidate.DeviceId.Contains(fragment, StringComparison.OrdinalIgnoreCase)
            || candidate.LocationPath.Contains(fragment, StringComparison.OrdinalIgnoreCase)
            || candidate.DisplayName.Contains(fragment, StringComparison.OrdinalIgnoreCase);
    }

    private static SingleSetSmokePairing CreatePairing(
        string label,
        HardwareSmokeDeviceCandidate usbCandidate,
        HardwareSmokeDeviceCandidate ddsCandidate,
        HardwareSmokeUsb2070Device sdkDevice)
    {
        return new SingleSetSmokePairing(
            label,
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

    private static Hdf5RunData CreateRunData(
        MultiSetSmokeOptions options,
        SingleSetSmokePairing pairing,
        SingleSetAdCapture capture,
        Guid sessionId,
        DateTimeOffset capturedAt,
        DdsExecutionReceipt? execution)
    {
        return new Hdf5RunData(
            sessionId,
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
                new DdsDacSettings(
                    checked((byte)options.DacChannel),
                    options.ExcitationFrequencyHz,
                    options.DacGain,
                    options.DacPhaseDegrees),
                options.CreateExcitationSettings(),
                options.PgaGain,
                execution),
            capture.Metadata,
            capture.AdcCounts);
    }

    private SingleSetSmokeArtifacts WriteArtifacts(
        MultiSetSmokeOptions options,
        EitCatalog catalog,
        string catalogPath,
        Hdf5RunData runData)
    {
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

        catalog.AddRun(runData, rawHdf5Path);
        catalog.AddFile(runData.RunId, "demod_hdf5", demodResult.OutputHdf5Path, "/demod", DateTimeOffset.Now);
        catalog.AddExport(
            runData.RunId,
            csvResult.SourceHdf5Path,
            csvResult.DatasetPath,
            csvResult.CsvPath,
            csvResult.Filter,
            DateTimeOffset.Now);

        return new SingleSetSmokeArtifacts(
            rawHdf5Path,
            demodResult.OutputHdf5Path,
            csvResult.CsvPath,
            catalogPath,
            demodResult.Demodulation.Frames.Count,
            demodResult.Demodulation.PeakLocations.Count,
            csvResult.RowCount,
            csvResult.ColumnCount);
    }

    private static EitSetPairing ToPairing(SingleSetSmokePairing pairing, DateTimeOffset createdAt)
    {
        return new EitSetPairing(
            pairing.SetLabel,
            pairing.Usb2070DeviceNumber,
            new PnpDeviceCandidate(
                PnpDeviceKind.Usb2070,
                pairing.Usb2070DeviceId,
                pairing.Usb2070DisplayName,
                pairing.Usb2070Vid,
                pairing.Usb2070Pid,
                pairing.Usb2070LocationPath),
            new PnpDeviceCandidate(
                PnpDeviceKind.SerialPort,
                pairing.DdsDeviceId,
                pairing.DdsDisplayName,
                pairing.DdsVid,
                pairing.DdsPid,
                pairing.DdsLocationPath,
                pairing.DdsPortName),
            createdAt);
    }

    private static string CreateRawHdf5Path(string outputDirectory, Hdf5RunData runData)
    {
        var safeLabel = string.Concat(runData.Device.SetLabel.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(
            outputDirectory,
            $"{runData.CapturedAt:yyyyMMdd_HHmmss_fff}_{safeLabel}_{runData.RunId:N}.h5");
    }

    private async Task StopAndDisposeAsync(IReadOnlyList<IMultiSetSmokeSetController> controllers)
    {
        foreach (var controller in controllers)
        {
            try
            {
                await controller.StopExcitationAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportCleanupFailure($"{controller.Label} smoke cleanup StopExcitation failed: {ex}");
            }

            try
            {
                await controller.StopAcquisitionAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportCleanupFailure($"{controller.Label} smoke cleanup StopAcquisition failed: {ex}");
            }

            try
            {
                controller.Dispose();
            }
            catch (Exception ex)
            {
                ReportCleanupFailure($"{controller.Label} smoke cleanup Dispose failed: {ex}");
            }
        }
    }

    private void ReportCleanupFailure(string message)
    {
        try
        {
            cleanupFailureReporter(message);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Smoke cleanup diagnostic reporter failed: {ex}; original={message}");
        }
    }
}
