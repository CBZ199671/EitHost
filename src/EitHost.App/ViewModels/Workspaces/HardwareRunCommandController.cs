using EitHost.Core.Acquisition;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;
using EitHost.Core.Sync;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class HardwareRunCommandController
{
    private const string CatalogNotReadyMessage =
        "数据目录尚未准备完成，不能执行会持久化数据的操作；请等待初始化完成或检查数据目录初始化失败提示。";
    private const int BufferedReadRowsPerBlock = 8192;
    private const long BytesPerAdcValue = sizeof(ushort);
    private const double ReadLoopYieldFraction = 0.125;
    private const double CompressionStartThresholdRatio = 0.5;
    private static readonly TimeSpan RealtimeShutdownWait = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan MinReadLoopIdleDelay = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan MaxReadLoopIdleDelay = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan CompressionYieldDelay = TimeSpan.FromMilliseconds(2);

    private readonly AcquisitionSessionController acquisition;
    private readonly DdsRunController ddsRuns;
    private readonly RealtimeSessionController realtimeSessions;
    private readonly RealtimeRunCommandController realtimeRuns;
    private readonly long autoFlushByteThreshold;
    private readonly HardwareRunCommandCallbacks callbacks;

    internal HardwareRunCommandController(
        AcquisitionSessionController acquisition,
        DdsRunController ddsRuns,
        RealtimeSessionController realtimeSessions,
        RealtimeRunCommandController realtimeRuns,
        long autoFlushByteThreshold,
        HardwareRunCommandCallbacks callbacks)
    {
        this.acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        this.ddsRuns = ddsRuns ?? throw new ArgumentNullException(nameof(ddsRuns));
        this.realtimeSessions = realtimeSessions ?? throw new ArgumentNullException(nameof(realtimeSessions));
        this.realtimeRuns = realtimeRuns ?? throw new ArgumentNullException(nameof(realtimeRuns));
        this.autoFlushByteThreshold = autoFlushByteThreshold > 0
            ? autoFlushByteThreshold
            : throw new ArgumentOutOfRangeException(nameof(autoFlushByteThreshold));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal Task SetDacAsync()
    {
        var parameters = GetSelectedParameters();
        return parameters is null
            ? Task.CompletedTask
            : SendSelectedDdsCommandAsync(
                "设置 DAC",
                client => client.SetDacAsync(new DdsDacSettings(
                    checked((byte)parameters.DdsDacChannel),
                    parameters.DdsFrequencyHz,
                    parameters.DdsGain,
                    parameters.DdsPhaseDegrees)));
    }

    internal Task StopDacAsync()
    {
        if (callbacks.GetSelectedPairing() is { } selected && ddsRuns.IsActive(selected.Title))
        {
            callbacks.SetStatus("请先停止激励，再停止 DAC。");
            return Task.CompletedTask;
        }

        var parameters = GetSelectedParameters();
        return parameters is null
            ? Task.CompletedTask
            : SendSelectedDdsCommandAsync(
                "停止 DAC",
                client => client.StopDacAsync(checked((byte)parameters.DdsDacChannel)));
    }

    internal Task SetPgaAsync()
    {
        var parameters = GetSelectedParameters();
        return parameters is null
            ? Task.CompletedTask
            : SendSelectedDdsCommandAsync(
                "设置 PGA",
                client => client.SetPgaAsync(checked((byte)parameters.DdsPgaGain)));
    }

    internal Task StartExcitationAsync()
    {
        if (GetSelectedParameters() is not { } parameters)
        {
            return Task.CompletedTask;
        }

        if (!parameters.TryValidateDemodDiscardCycles(out var message))
        {
            callbacks.SetStatus($"启动激励失败：{message}");
            return Task.CompletedTask;
        }

        return RunSelectedAsync("启动激励", StartExcitationForPairingAsync);
    }

    internal Task StopExcitationAsync() =>
        RunSelectedAsync("停止激励", StopExcitationForPairingAsync);

    internal bool CanConfigureExcitation() =>
        callbacks.GetSelectedPairing() is { } pairing && !ddsRuns.IsActive(pairing.Title);

    internal bool CanStartExcitation() => CanConfigureExcitation();

    internal bool CanStopExcitation() =>
        callbacks.GetSelectedPairing() is { } pairing && ddsRuns.IsActive(pairing.Title);

    internal Task StartAcquisitionAsync() =>
        RunSelectedAsync("启动采集", StartAcquisitionForPairingAsync);

    internal Task ReadAcquisitionBlockAsync()
    {
        if (!callbacks.IsCatalogReady())
        {
            callbacks.SetStatus($"读取采集块失败：{CatalogNotReadyMessage}");
            return Task.CompletedTask;
        }

        return RunSelectedAsync(
            "读取采集块",
            async pairing =>
            {
                var capture = await CaptureAsync(pairing).ConfigureAwait(true);
                callbacks.SetLastCaptureSummary(
                    $"{pairing.Title} 最近读取 {capture.AdcCounts.GetLength(0)} 行 x {capture.AdcCounts.GetLength(1)} 通道");
                callbacks.NotifySaveStateChanged();
            });
    }

    internal async Task ReadAllActiveAcquisitionBlocksAsync()
    {
        if (!callbacks.IsCatalogReady())
        {
            callbacks.SetStatus($"批量读取采集块失败：{CatalogNotReadyMessage}");
            return;
        }

        var pairings = callbacks.GetBoundPairings();
        if (pairings.Count == 0)
        {
            callbacks.SetStatus("没有已绑定设备套。");
            return;
        }

        var failures = new List<string>();
        var readCount = 0;
        foreach (var pairing in pairings.ToArray())
        {
            try
            {
                await CaptureAsync(pairing).ConfigureAwait(true);
                readCount++;
            }
            catch (Exception ex)
            {
                failures.Add($"{pairing.Title}: {ex.Message}");
                callbacks.AddAcquisitionLog($"{DateTime.Now:HH:mm:ss} {pairing.Title} AD read failed {ex.Message}");
            }
        }

        callbacks.NotifySaveStateChanged();
        var readRows = callbacks.GetRunParameters(pairings[0]).AcquisitionReadSampleRows;
        callbacks.SetLastCaptureSummary(failures.Count == 0
            ? $"全部读取 {readCount} 套，每套 {readRows} 行 x {Usb2070Constants.RequiredMeasurementChannelCount} 通道"
            : $"批量读取 {readCount} 套，失败 {failures.Count} 套。");
        callbacks.SetStatus(failures.Count == 0
            ? $"批量读取完成：{readCount} 套。"
            : $"批量读取部分失败：{string.Join("；", failures)}");
    }

    internal bool CanReadAllActiveAcquisitionBlocks() =>
        callbacks.IsCatalogReady() && callbacks.GetBoundPairings().Count > 0;

    internal bool CanStartSelectedAcquisition() =>
        callbacks.IsCatalogReady()
        && callbacks.GetSelectedPairing() is { } pairing
        && !acquisition.IsActive(pairing.Title)
        && !realtimeSessions.IsSetActive(pairing.Title);

    internal bool CanReadSelectedAcquisitionBlock() =>
        callbacks.IsCatalogReady()
        && callbacks.GetSelectedPairing() is { } pairing
        && !realtimeSessions.IsSetActive(pairing.Title);

    internal bool CanStopSelectedAcquisition() =>
        callbacks.GetSelectedPairing() is { } pairing && acquisition.CanStop(pairing.Title);

    internal Task StopAcquisitionAsync() =>
        RunSelectedAsync("停止采集", StopAcquisitionForPairingAsync);

    internal async Task<DdsCommandResult> SendDdsCommandForPairingAsync(
        PairingSummaryItem pairing,
        string actionName,
        Func<DdsProtocolClient, Task<DdsCommandResult>> sendCommand)
    {
        if (pairing.Pairing.DdsSerialCandidate.PortName is not { } portName)
        {
            throw new InvalidOperationException($"{pairing.Title} 没有可用 DDS 串口。");
        }

        using var transport = new DdsSerialPortTransport(portName);
        var client = new DdsProtocolClient(transport);
        var result = await sendCommand(client).ConfigureAwait(true);
        var acknowledgement = result.Response is null ? "ACK=-" : $"ACK={result.Response.Hex}";
        var requestedFrequencyHz = callbacks.GetRunParameters(pairing).DdsFrequencyHz;
        var tuningWord = DdsFrequencyPlan.CalculateTuningWord(requestedFrequencyHz);
        var actualFrequencyHz = DdsFrequencyPlan.CalculateActualFrequencyHz(tuningWord);
        var execution = result.ExecutionReceipt is null
            ? string.Empty
            : FormattableString.Invariant(
                $" firmware={result.ExecutionReceipt.FirmwareVersion} requested={result.ExecutionReceipt.RequestedTimeUs}us effective={result.ExecutionReceipt.EffectiveTimeUs:0.###}us ticks={result.ExecutionReceipt.TimerTicks} requestedHz={requestedFrequencyHz} FTW={tuningWord} actualHz={actualFrequencyHz:0.######} cycles={result.ExecutionReceipt.CalculateEffectiveChannelCycles(actualFrequencyHz):0.######}");
        callbacks.AddDdsLog(
            $"{DateTime.Now:HH:mm:ss} {pairing.Title} {actionName} {result.PacketHex} {acknowledgement}{execution}");
        return result;
    }

    internal async Task StartExcitationForPairingAsync(PairingSummaryItem pairing)
    {
        var parameters = callbacks.GetRunParameters(pairing);
        var result = await SendDdsCommandForPairingAsync(
            pairing,
            FormattableString.Invariant($"启动激励 {parameters.DdsFrequencyHz}Hz"),
            client => client.StartExcitationAsync(new DdsExcitationSettings(
                parameters.ExcitationMode,
                parameters.DdsFrequencyHz,
                parameters.ExcitationChannelCycles,
                parameters.ExcitationScanTimes))).ConfigureAwait(true);
        var execution = result.ExecutionReceipt ?? throw new DdsProtocolException(
            $"{pairing.Title} DDS firmware v2 ACK did not include an execution receipt.");
        ddsRuns.MarkStarted(pairing.Title, execution);
        var initialScanStatus = parameters.ExcitationScanTimes > 0
            ? new DdsScanStatus(
                DdsScanState.Running,
                true,
                0,
                checked((uint)parameters.ExcitationScanTimes),
                0)
            : null;
        pairing.UpdateScanStatus(initialScanStatus);
        UpdateActiveAcquisitionExcitation(pairing, initialScanStatus);
        if (parameters.ExcitationScanTimes > 0)
        {
            ddsRuns.StartFiniteScanMonitor(pairing, execution);
        }

        callbacks.NotifyRunStateChanged();
    }

    internal async Task StopExcitationForPairingAsync(PairingSummaryItem pairing)
    {
        await ddsRuns.CancelMonitorAsync(pairing.Title).ConfigureAwait(true);
        await SendDdsCommandForPairingAsync(
            pairing,
            "停止激励",
            client => client.StopExcitationAsync()).ConfigureAwait(true);
        ddsRuns.MarkStopped(pairing.Title);
        pairing.UpdateScanStatus(null);
        callbacks.NotifyRunStateChanged();
    }

    internal async Task CompleteFiniteScanAsync(PairingSummaryItem pairing, DdsScanStatus status)
    {
        try
        {
            ApplyFiniteScanStatus(pairing, status);
            if (acquisition.IsActive(pairing.Title))
            {
                await StopActiveAcquisitionSessionAsync(
                    pairing,
                    $"{pairing.Title} 有限扫描完成，自动停止 AD").ConfigureAwait(true);
            }

            callbacks.AddDdsLog(
                $"{DateTime.Now:HH:mm:ss} {pairing.Title} 有限扫描完成 {status.CompletedCycles}/{status.TargetCycles} 圈");
            callbacks.SetStatus($"{pairing.Title} 有限扫描已完成：{status.CompletedCycles}/{status.TargetCycles} 圈。");
        }
        catch (Exception ex)
        {
            callbacks.AddDdsLog($"{DateTime.Now:HH:mm:ss} {pairing.Title} 有限扫描收尾失败 {ex.Message}");
            callbacks.SetStatus($"{pairing.Title} 有限扫描已结束，但采集收尾失败：{ex.Message}");
        }
        finally
        {
            ddsRuns.MarkStopped(pairing.Title);
            callbacks.NotifyRunStateChanged();
        }
    }

    internal void ApplyFiniteScanStatus(PairingSummaryItem pairing, DdsScanStatus status)
    {
        pairing.UpdateScanStatus(status);
        UpdateActiveAcquisitionExcitation(pairing, status);
    }

    internal Task StartAcquisitionForPairingAsync(PairingSummaryItem pairing)
    {
        EnsureCatalogReadyForBufferedAcquisition();
        var parameters = callbacks.GetRunParameters(pairing);
        var settings = parameters.CreateAcquisitionSettings();
        acquisition.Start(
            pairing,
            CreateUsbDevice(pairing, parameters.AcquisitionSampleRateHz),
            settings,
            CreateAcquisitionMetadata(settings),
            CreateExcitationMetadata(pairing, parameters),
            CreateBufferPolicy(parameters));
        callbacks.StartBufferedPreview();
        callbacks.NotifyAcquisitionStateChanged();
        callbacks.NotifyRunStateChanged();
        return Task.CompletedTask;
    }

    internal Task StopAcquisitionForPairingAsync(PairingSummaryItem pairing) =>
        StopActiveAcquisitionSessionAsync(pairing, $"{pairing.Title} AD buffer stop");

    internal Usb2070Device CreateUsbDevice(PairingSummaryItem pairing) =>
        CreateUsbDevice(pairing, callbacks.GetRunParameters(pairing).AcquisitionSampleRateHz);

    internal async Task StopTrackedExcitationsAsync()
    {
        var activeLabels = ddsRuns.ActiveSetLabels.ToArray();
        foreach (var pairing in callbacks.GetBoundPairings()
                     .Where(item => activeLabels.Contains(item.Title, StringComparer.Ordinal))
                     .ToArray())
        {
            try
            {
                await StopExcitationForPairingAsync(pairing).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                callbacks.AddDdsLog(
                    $"{DateTime.Now:HH:mm:ss} {pairing.Title} shutdown DDS stop failed {ex.Message}");
            }
        }
    }

    private DeviceRunParameterProfile? GetSelectedParameters()
    {
        if (callbacks.GetSelectedPairing() is { } pairing)
        {
            return callbacks.GetRunParameters(pairing);
        }

        callbacks.SetStatus("请先选择已绑定设备套。");
        return null;
    }

    private async Task SendSelectedDdsCommandAsync(
        string actionName,
        Func<DdsProtocolClient, Task<DdsCommandResult>> sendCommand)
    {
        if (callbacks.GetSelectedPairing() is not { } pairing)
        {
            callbacks.SetStatus("请先选择已绑定设备套。");
            return;
        }

        try
        {
            await SendDdsCommandForPairingAsync(pairing, actionName, sendCommand).ConfigureAwait(true);
            callbacks.SetStatus($"{pairing.Title} {actionName}已发送。");
        }
        catch (Exception ex)
        {
            callbacks.SetStatus($"{actionName}失败：{ex.Message}");
        }
    }

    private async Task RunSelectedAsync(string actionName, Func<PairingSummaryItem, Task> action)
    {
        if (callbacks.GetSelectedPairing() is not { } pairing)
        {
            callbacks.SetStatus("请先选择已绑定设备套。");
            return;
        }

        try
        {
            await action(pairing).ConfigureAwait(true);
            callbacks.SetStatus($"{pairing.Title} {actionName}完成。");
        }
        catch (Exception ex)
        {
            var message = $"{DateTime.Now:HH:mm:ss} {pairing.Title} {actionName}失败 {ex.Message}";
            if (IsDdsAction(actionName))
            {
                callbacks.AddDdsLog(message);
            }
            else
            {
                callbacks.AddAcquisitionLog(message);
            }

            callbacks.SetStatus($"{actionName}失败：{ex.Message}");
        }
    }

    private async Task<CapturedRawBlock> CaptureAsync(PairingSummaryItem pairing)
    {
        var parameters = callbacks.GetRunParameters(pairing);
        var settings = parameters.CreateAcquisitionSettings();
        try
        {
            return await acquisition.CaptureAsync(
                pairing,
                CreateUsbDevice(pairing, parameters.AcquisitionSampleRateHz),
                settings,
                CreateAcquisitionMetadata(settings),
                CreateExcitationMetadata(pairing, parameters),
                parameters.AcquisitionReadSampleRows).ConfigureAwait(true);
        }
        finally
        {
            callbacks.NotifyAcquisitionStateChanged();
        }
    }

    private async Task StopActiveAcquisitionSessionAsync(PairingSummaryItem pairing, string? logMessage)
    {
        var outcome = await acquisition.StopAsync(pairing, logMessage).ConfigureAwait(true);
        if (outcome.CaptureSummary is { } summary)
        {
            callbacks.SetLastCaptureSummary(summary);
        }

        callbacks.StopBufferedPreviewIfIdle();
        callbacks.NotifyAcquisitionStateChanged();
        callbacks.NotifySaveStateChanged();
        callbacks.NotifyRunStateChanged();
    }

    private HardwareSyncController CreateSyncController(PairingSummaryItem pairing)
    {
        EnsureCatalogReadyForBufferedAcquisition();
        var portName = pairing.Pairing.DdsSerialCandidate.PortName;
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException($"{pairing.Title} 没有可用 DDS 串口。");
        }

        var parameters = callbacks.GetRunParameters(pairing);
        var settings = parameters.CreateAcquisitionSettings();
        return acquisition.CreateSyncController(
            pairing,
            CreateUsbDevice(pairing, parameters.AcquisitionSampleRateHz),
            portName,
            new DdsExcitationSettings(
                parameters.ExcitationMode,
                parameters.DdsFrequencyHz,
                parameters.ExcitationChannelCycles,
                parameters.ExcitationScanTimes),
            settings,
            CreateAcquisitionMetadata(settings),
            CreateExcitationMetadata(pairing, parameters),
            CreateBufferPolicy(parameters));
    }

    private AcquisitionBufferPolicy CreateBufferPolicy(DeviceRunParameterProfile parameters)
    {
        var readValueCount = checked(
            Math.Max(BufferedReadRowsPerBlock, parameters.AcquisitionReadSampleRows)
            * Usb2070Constants.RequiredMeasurementChannelCount);
        var autoFlushBytes = Math.Max(readValueCount * BytesPerAdcValue, autoFlushByteThreshold);
        var rowsPerBlock = Math.Max(1, readValueCount / Usb2070Constants.RequiredMeasurementChannelCount);
        var targetMs = (rowsPerBlock * 1000.0 / Math.Max(1, parameters.AcquisitionSampleRateHz))
            * ReadLoopYieldFraction;
        var readLoopIdleDelay = TimeSpan.FromMilliseconds(Math.Clamp(
            targetMs,
            MinReadLoopIdleDelay.TotalMilliseconds,
            MaxReadLoopIdleDelay.TotalMilliseconds));
        var compressionStartBytes = Math.Max(
            readValueCount * BytesPerAdcValue,
            (long)(autoFlushBytes * CompressionStartThresholdRatio));
        return new AcquisitionBufferPolicy(
            readValueCount,
            autoFlushBytes,
            checked(autoFlushBytes * 2),
            readLoopIdleDelay,
            compressionStartBytes,
            CompressionYieldDelay);
    }

    private void UpdateActiveAcquisitionExcitation(PairingSummaryItem pairing, DdsScanStatus? status)
    {
        if (ddsRuns.TryGetExecution(pairing.Title, out var execution))
        {
            acquisition.UpdateExcitationMetadata(pairing.Title, execution, status);
        }
    }

    private Hdf5ExcitationMetadata CreateExcitationMetadata(
        PairingSummaryItem pairing,
        DeviceRunParameterProfile parameters)
    {
        var metadata = parameters.CreateExcitationMetadata();
        return metadata with
        {
            Execution = ddsRuns.TryGetExecution(pairing.Title, out var execution) ? execution : null,
            ScanStatus = pairing.ScanStatus,
        };
    }

    private static Usb2070AcquisitionMetadata CreateAcquisitionMetadata(Usb2070AcquisitionSettings settings) =>
        new(
            settings.SampleRateHz,
            settings.Range,
            16,
            settings.EnabledOneBasedChannels,
            settings.TriggerMode,
            settings.TriggerSource);

    private static Usb2070Device CreateUsbDevice(PairingSummaryItem pairing, int sampleRateHz)
    {
        var candidate = pairing.Pairing.Usb2070Candidate;
        return new Usb2070Device(
            pairing.Pairing.Usb2070DeviceNumber,
            $"USB2070:{pairing.Pairing.Usb2070DeviceNumber}",
            candidate.DisplayName,
            candidate.Vid,
            candidate.Pid,
            candidate.LocationPath,
            Usb2070Constants.RequiredMeasurementChannelCount,
            16,
            sampleRateHz);
    }

    private static bool IsDdsAction(string actionName) =>
        actionName.Contains("激励", StringComparison.Ordinal)
        || actionName.Contains("DAC", StringComparison.Ordinal)
        || actionName.Contains("PGA", StringComparison.Ordinal);

    internal async Task SyncStartAsync()
    {
        if (!callbacks.IsCatalogReady())
        {
            callbacks.SetStatus($"同步启动失败：{CatalogNotReadyMessage}");
            return;
        }

        var pairings = callbacks.GetBoundPairings();
        if (pairings.Count < 2)
        {
            callbacks.SetStatus("同步启动需要至少两套已绑定设备；单套设备请使用单独采集/激励控制。");
            return;
        }

        var invalid = pairings
            .Select(pairing => (pairing, parameters: callbacks.GetRunParameters(pairing)))
            .Select(item => (item.pairing, valid: item.parameters.TryValidateDemodDiscardCycles(out var message), message, item.parameters))
            .FirstOrDefault(item => !item.valid);
        if (invalid.pairing is not null)
        {
            callbacks.SetStatus($"同步启动失败：{invalid.pairing.Title}: {invalid.message}");
            return;
        }

        if (pairings.Any(pairing => callbacks.GetRunParameters(pairing).ExcitationScanTimes > 0))
        {
            callbacks.SetStatus("同步启动暂不支持有限扫描圈数；请设为 0（连续），或逐套单独启动并由固件状态回执收尾。");
            return;
        }

        var busy = pairings
            .Where(pairing => acquisition.IsActive(pairing.Title) || realtimeSessions.IsSetActive(pairing.Title))
            .Select(pairing => pairing.Title)
            .ToArray();
        if (busy.Length > 0)
        {
            callbacks.SetStatus($"同步启动失败：{string.Join(", ", busy)} 已在采集或实时成像。请先停止后再同步启动。");
            return;
        }

        var cleanupDiagnostics = new List<string>();
        try
        {
            var coordinator = new SyncStartCoordinator(reportCleanupFailure: cleanupDiagnostics.Add);
            var controllers = pairings.Select(CreateSyncController).ToArray();
            var result = await coordinator.StartAsync(controllers).ConfigureAwait(true);
            foreach (var controller in controllers)
            {
                var activeSession = controller.TakeStartedSession();
                acquisition.AdoptStartedSession(controller.Label, activeSession);
                ddsRuns.MarkActive(controller.Label);
                if (controller.StartExcitationResult is { } dds)
                {
                    callbacks.AddDdsLog($"{DateTime.Now:HH:mm:ss} {controller.Label} 同步启动激励 {dds.PacketHex}");
                }
            }

            foreach (var record in result.Records)
            {
                callbacks.AddAcquisitionLog(
                    $"{DateTime.Now:HH:mm:ss} {record.Label} sync AD={record.AcquisitionStartRequestedAt:HH:mm:ss.fff} DDS={record.ExcitationStartRequestedAt:HH:mm:ss.fff}");
            }

            callbacks.StartBufferedPreview();
            callbacks.NotifyAcquisitionStateChanged();
            callbacks.NotifyRunStateChanged();
            callbacks.SetStatus($"同步启动完成：{result.SetCount} 套设备，后台正在采集；原始 CH1 预览将显示当前选中设备。");
        }
        catch (SyncStartException ex)
        {
            foreach (var diagnostic in cleanupDiagnostics)
            {
                callbacks.AddDdsLog($"{DateTime.Now:HH:mm:ss} {diagnostic}");
            }

            callbacks.SetStatus($"同步启动失败，已尝试停止所有设备：{ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            callbacks.SetStatus($"同步启动失败：{ex.Message}");
        }
    }

    internal bool CanSyncStart() =>
        callbacks.IsCatalogReady() && callbacks.GetBoundPairings().Count >= 2;

    internal async Task StopAllAsync()
    {
        var pairings = callbacks.GetBoundPairings();
        if (pairings.Count == 0)
        {
            callbacks.SetStatus("没有已绑定设备套可停止。");
            return;
        }

        var stoppedAdCount = 0;
        var stopDdsCount = 0;
        var realtimeStates = realtimeRuns.GetStatesToStop(null);
        var realtimeStopRequestedCount = realtimeStates.Length;
        var realtimeStopCompletedCount = 0;
        var realtimeControlledLabels = new HashSet<string>(
            realtimeStates.Select(static state => state.SetLabel),
            StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        if (realtimeStates.Length > 0 || realtimeSessions.HasUnfinishedTask)
        {
            realtimeRuns.RequestStop(showIdleMessage: false);
            realtimeStopCompletedCount = await WaitForRealtimeStopsAsync(
                    realtimeStates,
                    RealtimeShutdownWait + TimeSpan.FromSeconds(1))
                .ConfigureAwait(true);
        }

        foreach (var pairing in pairings.ToArray())
        {
            var realtimeControlled = realtimeControlledLabels.Contains(pairing.Title);
            if (!realtimeControlled && ddsRuns.IsActive(pairing.Title))
            {
                try
                {
                    await StopExcitationForPairingAsync(pairing).ConfigureAwait(true);
                    stopDdsCount++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{pairing.Title} DDS: {ex.Message}");
                    callbacks.AddDdsLog($"{DateTime.Now:HH:mm:ss} {pairing.Title} 停止激励失败 {ex.Message}");
                }
            }

            if (!realtimeControlled && acquisition.IsActive(pairing.Title))
            {
                try
                {
                    await StopAcquisitionForPairingAsync(pairing).ConfigureAwait(true);
                    stoppedAdCount++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{pairing.Title} AD: {ex.Message}");
                    callbacks.AddAcquisitionLog($"{DateTime.Now:HH:mm:ss} {pairing.Title} AD stop failed {ex.Message}");
                }
            }
        }

        callbacks.NotifyAcquisitionStateChanged();
        var realtimeSummary = realtimeStopRequestedCount > 0
            ? realtimeStopCompletedCount >= realtimeStopRequestedCount
                ? $"实时 {realtimeStopRequestedCount} 套"
                : $"实时 {realtimeStopCompletedCount}/{realtimeStopRequestedCount} 套已清理"
            : "实时 0 套";
        callbacks.SetStatus(failures.Count == 0
            ? realtimeStopRequestedCount > 0 && realtimeStopCompletedCount < realtimeStopRequestedCount
                ? $"全部停止已发送取消：{realtimeSummary}，DDS {stopDdsCount} 套，AD {stoppedAdCount} 套。"
                : $"全部停止完成：{realtimeSummary}，DDS {stopDdsCount} 套，AD {stoppedAdCount} 套。"
            : $"全部停止已尽力完成：{realtimeSummary}，DDS {stopDdsCount} 套，AD {stoppedAdCount} 套，失败 {failures.Count} 项。");
    }

    internal bool CanStopAll() =>
        callbacks.GetBoundPairings().Count > 0 || acquisition.ActiveCount > 0 || realtimeSessions.IsAnyActive;

    private void EnsureCatalogReadyForBufferedAcquisition()
    {
        if (!callbacks.IsCatalogReady())
        {
            throw new InvalidOperationException(CatalogNotReadyMessage);
        }
    }

    private async Task<int> WaitForRealtimeStopsAsync(RealtimeRunState[] states, TimeSpan wait)
    {
        var tasks = states
            .Select(static state => state.Task)
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length == 0)
        {
            return states.Count(static state => !state.IsActive);
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(wait).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Normal realtime stop path: cancellation tears down AD/DDS in the run loop finally block.
        }
        catch (TimeoutException)
        {
            callbacks.AddRealtimeLog(
                $"{DateTime.Now:HH:mm:ss} realtime stop wait timeout after {wait.TotalMilliseconds:F0}ms; hardware cleanup continues in background");
        }
        catch (Exception ex)
        {
            callbacks.AddRealtimeLog($"{DateTime.Now:HH:mm:ss} realtime stop wait warning {ex.Message}");
        }

        return states.Count(static state => !state.IsActive);
    }
}

internal sealed record HardwareRunCommandCallbacks(
    Func<PairingSummaryItem?> GetSelectedPairing,
    Func<IReadOnlyList<PairingSummaryItem>> GetBoundPairings,
    Func<PairingSummaryItem, DeviceRunParameterProfile> GetRunParameters,
    Func<bool> IsCatalogReady,
    Action StartBufferedPreview,
    Action StopBufferedPreviewIfIdle,
    Action NotifyAcquisitionStateChanged,
    Action NotifySaveStateChanged,
    Action NotifyRunStateChanged,
    Action<string> SetLastCaptureSummary,
    Action<string> AddDdsLog,
    Action<string> AddAcquisitionLog,
    Action<string> AddRealtimeLog,
    Action<string> SetStatus);
