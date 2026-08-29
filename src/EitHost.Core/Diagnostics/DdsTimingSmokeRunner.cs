using System.Security.Cryptography;
using System.Text.Json;
using EitHost.Core.Demodulation;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Diagnostics;

public sealed class DdsTimingSmokeRunner
{
    public const int MinimumTimingEvidenceFrames = 30;

    private readonly ISingleSetSmokeHardware hardware;
    private readonly Hdf5RunWriter hdf5RunWriter;
    private readonly Hdf5OfflineDemodService demodService;

    public DdsTimingSmokeRunner(
        ISingleSetSmokeHardware hardware,
        Hdf5RunWriter? hdf5RunWriter = null,
        Hdf5OfflineDemodService? demodService = null)
    {
        this.hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        this.hdf5RunWriter = hdf5RunWriter ?? new Hdf5RunWriter();
        this.demodService = demodService ?? new Hdf5OfflineDemodService();
    }

    public async Task<DdsTimingSmokeReport> RunAsync(
        DdsTimingSmokeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        var startedAt = DateTimeOffset.Now;
        if (!options.Execute)
        {
            return new DdsTimingSmokeReport(
                startedAt,
                DateTimeOffset.Now,
                ExecuteRequested: false,
                Ready: false,
                Passed: false,
                "验证矩阵已生成；未传入 --execute，未发送硬件命令。",
                options,
                [],
                ["需要显式 --execute 才会配置 DDS 和启动 USB2070。"],
                []);
        }

        var requestedEvidenceFrames = (long)options.FramesPerBlock * options.TargetBlocks;
        if (requestedEvidenceFrames < MinimumTimingEvidenceFrames)
        {
            return new DdsTimingSmokeReport(
                startedAt,
                DateTimeOffset.Now,
                ExecuteRequested: true,
                Ready: false,
                Passed: false,
                $"时序证据不足，未发送硬件命令；至少需要 {MinimumTimingEvidenceFrames} 帧。",
                options,
                [],
                [$"请求 {requestedEvidenceFrames} 帧；时序判决至少需要 {MinimumTimingEvidenceFrames} 帧。"],
                []);
        }

        var hardwareReport = await hardware.CaptureHardwareReportAsync(cancellationToken).ConfigureAwait(false);
        var blockers = CreateBlockers(options, hardwareReport);
        if (blockers.Count > 0)
        {
            return new DdsTimingSmokeReport(
                startedAt,
                DateTimeOffset.Now,
                ExecuteRequested: true,
                Ready: false,
                Passed: false,
                "硬件或参数未就绪，未发送 DDS/USB2070 启动命令。",
                options,
                [],
                blockers,
                hardwareReport.Warnings);
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var usbSdk = hardwareReport.Usb2070SdkDevices.Single(
            device => device.DeviceNumber == options.Usb2070DeviceNumber);
        var usbPnp = hardwareReport.PnpUsb2070Devices.First();
        var ddsPnp = hardwareReport.PnpDdsSerialDevices.First(
            device => string.Equals(device.PortName, options.DdsPortName, StringComparison.OrdinalIgnoreCase));
        var usbDevice = new Usb2070Device(
            usbSdk.DeviceNumber,
            $"USB2070:{usbSdk.DeviceNumber}",
            usbPnp.DisplayName,
            usbPnp.Vid,
            usbPnp.Pid,
            usbPnp.LocationPath,
            usbSdk.AvailableChannelCount,
            usbSdk.AdBit,
            usbSdk.MaxSampleRateHz);
        var reports = new List<DdsTimingSmokeCaseReport>();
        var warnings = hardwareReport.Warnings.ToList();
        foreach (var cycles in options.ChannelCycles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                reports.Add(await RunCaseAsync(
                    options,
                    cycles,
                    usbDevice,
                    usbPnp,
                    ddsPnp,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"{cycles:0.###} 周期验证失败：{exception.Message}");
                reports.Add(CreateFailedCase(cycles, exception.Message));
            }
        }

        var passed = reports.Count == options.ChannelCycles.Count && reports.All(report => report.Passed);
        return new DdsTimingSmokeReport(
            startedAt,
            DateTimeOffset.Now,
            ExecuteRequested: true,
            Ready: true,
            passed,
            passed
                ? $"DDS 时序矩阵通过：{reports.Count} 档，连续目标 {options.TargetBlocks} blocks/档。"
                : "DDS 时序矩阵未通过；请查看各档 Failure、ACK 和原始 HDF5。",
            options,
            reports,
            [],
            warnings);
    }

    private async Task<DdsTimingSmokeCaseReport> RunCaseAsync(
        DdsTimingSmokeOptions options,
        double cycles,
        Usb2070Device usbDevice,
        HardwareSmokeDeviceCandidate usbPnp,
        HardwareSmokeDeviceCandidate ddsPnp,
        CancellationToken cancellationToken)
    {
        var excitation = new DdsExcitationSettings(
            DdsExcitationMode.Adjacent,
            options.FrequencyHz,
            cycles);
        var dac = new DdsDacSettings(
            1,
            options.FrequencyHz,
            options.CurrentUa / 100.0,
            0);
        var actualFrequencyHz = dac.ActualFrequencyHz;
        await hardware.SendDdsStopExcitationAsync(options.DdsPortName, cancellationToken).ConfigureAwait(false);
        var startup = await hardware.SendDdsStartupSequenceAsync(
            options.DdsPortName,
            dac,
            options.PgaGain,
            excitation,
            cancellationToken).ConfigureAwait(false);
        var start = startup.StartExcitation;
        var execution = start.ExecutionReceipt ?? throw new DdsProtocolException(
            "StartExcitation firmware v2 ACK did not contain an execution receipt.");
        var caseDirectory = Path.Combine(options.OutputDirectory, $"cycles-{cycles:0.###}".Replace('.', '_'));
        Directory.CreateDirectory(caseDirectory);
        var ackPath = Path.Combine(caseDirectory, "ack.json");
        await File.WriteAllTextAsync(
            ackPath,
            JsonSerializer.Serialize(
                new
                {
                    Request = start.PacketHex,
                    Response = start.Response?.Hex,
                    RequestedFrequencyHz = dac.FrequencyHz,
                    FrequencyTuningWord = dac.FrequencyTuningWord,
                    ActualFrequencyHz = actualFrequencyHz,
                    FrequencyErrorHz = dac.FrequencyErrorHz,
                    RequestedDwellUs = execution.RequestedTimeUs,
                    EffectiveDwellUs = execution.EffectiveTimeUs,
                    Capabilities = start.FirmwareCapabilities,
                    Execution = execution
                },
                new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        var requiredFrames = checked(options.FramesPerBlock * options.TargetBlocks);
        var expectedWindowSamples = execution.EffectiveTimeNs * options.SampleRateHz / 1_000_000_000.0;
        var rawPath = Path.Combine(caseDirectory, "raw.h5");
        var demodPath = Path.Combine(caseDirectory, "demod.h5");
        var realtimeSettings = new RealtimeDemodulationSettings(
            options.SampleRateHz,
            actualFrequencyHz,
            channelCycles: execution.CalculateEffectiveChannelCycles(actualFrequencyHz),
            framesPerBlock: options.FramesPerBlock,
            minimumAcceptedFrames: options.FramesPerBlock,
            discardLeadingCycles: options.DiscardLeadingCycles,
            discardTrailingCycles: options.DiscardTrailingCycles,
            maxDegreeOfParallelism: 0);
        var captureMarginFrames = Math.Max(
            realtimeSettings.SearchExtraFrames + 1,
            2 * options.FramesPerBlock);
        var requiredSampleRows = checked((int)Math.Ceiling(
            expectedWindowSamples * DemodulatedFrame.StimulationCount * (requiredFrames + captureMarginFrames)));
        var sampleRows = AlignCaptureRows(requiredSampleRows);
        var captureOptions = new SingleSetSmokeOptions
        {
            SetLabel = options.SetLabel,
            Usb2070DeviceNumber = options.Usb2070DeviceNumber,
            DdsPortName = options.DdsPortName,
            SampleRows = sampleRows,
            SampleRateHz = options.SampleRateHz,
            ExcitationFrequencyHz = options.FrequencyHz,
            DacChannel = 1,
            DacGain = options.CurrentUa / 100.0,
            PgaGain = options.PgaGain,
            TriggerLength = Math.Min(sampleRows, 1024)
        };
        try
        {
            SingleSetAdCapture capture;
            try
            {
                capture = hardware.CaptureAdBlock(usbDevice, captureOptions, cancellationToken);
            }
            finally
            {
                await hardware.SendDdsStopExcitationAsync(options.DdsPortName, cancellationToken).ConfigureAwait(false);
            }

            var runData = new Hdf5RunData(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Now,
            new DeviceRunMetadata(
                options.SetLabel,
                EitSet.MeasurementChannelCount,
                options.Usb2070DeviceNumber,
                usbPnp.DeviceId,
                usbPnp.DisplayName,
                usbPnp.Vid,
                usbPnp.Pid,
                usbPnp.LocationPath,
                options.DdsPortName,
                ddsPnp.DeviceId,
                ddsPnp.DisplayName,
                ddsPnp.Vid,
                ddsPnp.Pid,
                ddsPnp.LocationPath),
            new Hdf5ExcitationMetadata(dac, excitation, options.PgaGain, execution),
            capture.Metadata,
            capture.AdcCounts);
            hdf5RunWriter.Write(rawPath, runData);
            var demodSettings = new OfflineDemodulationSettings(
            options.SampleRateHz,
            actualFrequencyHz,
            maxFrames: requiredFrames + 1,
            channelCycles: execution.CalculateEffectiveChannelCycles(actualFrequencyHz),
            forceUniformCadence: true,
            discardLeadingCycles: options.DiscardLeadingCycles,
            discardTrailingCycles: options.DiscardTrailingCycles,
            maxDegreeOfParallelism: 0,
            adRange: capture.Metadata.Range);
            var realtimeQuality = AnalyzeRealtimeStrictQuality(
            capture.AdcCounts,
            realtimeSettings,
            options.TargetBlocks);
            var demodulation = realtimeQuality.Demodulation;
            demodService.WriteDemodulationResult(rawPath, demodPath, demodulation, demodSettings);
            var analysis = DdsTimingSmokeAnalyzer.Analyze(
            capture.AdcCounts,
            options.SampleRateHz,
            actualFrequencyHz,
            execution,
            demodulation);
            var strictQualityPassed = realtimeQuality.Passed;
            var passed = analysis.Timing.IsMatch &&
                analysis.CarrierErrorPercent <= 1.0 &&
                analysis.StepOrderMatched &&
                strictQualityPassed;
            return new DdsTimingSmokeCaseReport(
            cycles,
            execution.RequestedTimeUs,
            execution.FirmwareVersion.ToString(3),
            execution.FirmwareProtocolVersion,
            execution.TimerClockHz,
            execution.TimerTicks,
            execution.EffectiveTimeUs,
            execution.CalculateEffectiveChannelCycles(actualFrequencyHz),
            execution.SwitchGuardMinimumUs,
            start.PacketHex,
            start.Response?.Hex ?? string.Empty,
            analysis.Timing.ExpectedWindowSamples,
            analysis.Timing.ObservedWindowSamples,
            analysis.Timing.ToleranceSamples,
            analysis.Timing.IsMatch,
            analysis.MeasuredCarrierHz,
            analysis.CarrierErrorPercent,
            analysis.ObservedStepOrder,
            analysis.StepOrderMatched,
            realtimeQuality.StrictAcceptedFrames,
            realtimeQuality.RejectedFrames,
            requiredFrames,
            realtimeQuality.ValidTop3Windows,
            realtimeQuality.TotalWindows,
            strictQualityPassed,
            rawPath,
            CalculateSha256(rawPath),
            demodPath,
            CalculateSha256(demodPath),
            ackPath,
            CalculateSha256(ackPath),
            passed,
                passed ? null : CreateFailure(analysis, strictQualityPassed));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFailedCaseWithDdsEvidence(
                cycles,
                start,
                execution,
                actualFrequencyHz,
                expectedWindowSamples,
                requiredFrames,
                rawPath,
                demodPath,
                ackPath,
                exception.Message);
        }
    }

    public static DdsTimingRealtimeQualityResult AnalyzeRealtimeStrictQuality(
        ushort[,] rawAdcCounts,
        RealtimeDemodulationSettings settings,
        int targetBlockCount,
        int chunkRows = Usb2070Session.DefaultMaxRowsPerRead)
    {
        ArgumentNullException.ThrowIfNull(rawAdcCounts);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetBlockCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkRows);
        var demodulator = new RealtimeBlockDemodulator(settings);
        var blocks = new List<RealtimeDemodulatedBlock>();
        for (var rowStart = 0; rowStart < rawAdcCounts.GetLength(0) && blocks.Count < targetBlockCount; rowStart += chunkRows)
        {
            var rowCount = Math.Min(chunkRows, rawAdcCounts.GetLength(0) - rowStart);
            var chunk = new ushort[rowCount, rawAdcCounts.GetLength(1)];
            for (var row = 0; row < rowCount; row++)
            {
                for (var channel = 0; channel < rawAdcCounts.GetLength(1); channel++)
                {
                    chunk[row, channel] = rawAdcCounts[rowStart + row, channel];
                }
            }

            demodulator.AppendSamples(chunk);
            blocks.AddRange(demodulator.ProcessAvailableBlocks());
        }

        var evaluated = blocks.Take(targetBlockCount).ToArray();
        var frames = evaluated.SelectMany(block => block.Frames).ToArray();
        var acceptedFrames = evaluated.Sum(block => block.AcceptedFrameCount);
        var rejectedFrames = evaluated.Sum(block => block.RejectedFrameCount);
        var validTop3Windows = frames
            .SelectMany(frame => frame.WindowQualities)
            .Count(quality => quality.State == DemodulatedWindowQualityState.Valid);
        var totalWindows = frames.Sum(frame => frame.WindowQualities.Count);
        var highQualityBlocks = evaluated.Count(block => block.IsHighQuality);
        var demodulation = new OfflineDemodulator().CombineRealtimeBlocks(evaluated);
        var requiredFrames = checked(targetBlockCount * settings.FramesPerBlock);
        var passed = evaluated.Length == targetBlockCount &&
            highQualityBlocks == targetBlockCount &&
            acceptedFrames >= requiredFrames &&
            rejectedFrames == 0 &&
            validTop3Windows >= requiredFrames * DemodulatedFrame.StimulationCount;
        return new DdsTimingRealtimeQualityResult(
            evaluated.Length,
            highQualityBlocks,
            acceptedFrames,
            rejectedFrames,
            validTop3Windows,
            totalWindows,
            passed,
            demodulation);
    }

    public static int AlignCaptureRows(
        int requiredRows,
        int rowsPerChunk = Usb2070Session.DefaultMaxRowsPerRead)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowsPerChunk);
        var fullChunks = Math.DivRem(requiredRows, rowsPerChunk, out var remainder);
        return checked((fullChunks + (remainder == 0 ? 0 : 1)) * rowsPerChunk);
    }

    private static void ValidateOptions(DdsTimingSmokeOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FrequencyHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.CurrentUa);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FramesPerBlock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TargetBlocks);
        if (options.ChannelCycles.Count == 0)
        {
            throw new ArgumentException("At least one channel-cycle value is required.", nameof(options));
        }

        var gain = options.CurrentUa / 100.0;
        if (!DdsDacSettings.IsSupportedGain(gain))
        {
            throw new ArgumentOutOfRangeException(nameof(options.CurrentUa), "Current must map to 10/20/30/50/100 uA.");
        }

        if (options.PgaGain is not (1 or 2 or 5 or 10))
        {
            throw new ArgumentOutOfRangeException(nameof(options.PgaGain), "PGA must be 1, 2, 5 or 10.");
        }

        foreach (var cycles in options.ChannelCycles)
        {
            _ = new DdsExcitationSettings(DdsExcitationMode.Adjacent, options.FrequencyHz, cycles);
            if (options.DiscardLeadingCycles + options.DiscardTrailingCycles >= cycles)
            {
                throw new ArgumentOutOfRangeException(nameof(options.ChannelCycles), "Discard cycles must leave usable signal.");
            }
        }
    }

    private static List<string> CreateBlockers(
        DdsTimingSmokeOptions options,
        HardwareSmokeReport report)
    {
        var blockers = new List<string>();
        if (!report.Usb2070SdkDevices.Any(device => device.DeviceNumber == options.Usb2070DeviceNumber))
        {
            blockers.Add($"USB2070 SDK device #{options.Usb2070DeviceNumber} is unavailable.");
        }

        if (!report.PnpUsb2070Devices.Any())
        {
            blockers.Add("No USB2070 PnP device was detected.");
        }

        if (string.IsNullOrWhiteSpace(options.DdsPortName) ||
            !report.PnpDdsSerialDevices.Any(device =>
                string.Equals(device.PortName, options.DdsPortName, StringComparison.OrdinalIgnoreCase)))
        {
            blockers.Add($"DDS port {options.DdsPortName} is unavailable or not identified as DDS hardware.");
        }

        return blockers;
    }

    private static string CreateFailure(DdsTimingSmokeAnalysis analysis, bool strictQualityPassed)
    {
        var failures = new List<string>();
        if (!analysis.Timing.IsMatch)
        {
            failures.Add(DdsTimingValidationResult.ExcitationTimingMismatch);
        }

        if (analysis.CarrierErrorPercent > 1.0)
        {
            failures.Add($"carrier error {analysis.CarrierErrorPercent:0.###}%");
        }

        if (!analysis.StepOrderMatched)
        {
            failures.Add("16-step order mismatch");
        }

        if (!strictQualityPassed)
        {
            failures.Add("strict Top3 quality target not met");
        }

        return string.Join("; ", failures);
    }

    private static DdsTimingSmokeCaseReport CreateFailedCase(double cycles, string failure) => new(
        cycles,
        0,
        string.Empty,
        0,
        0,
        0,
        0,
        0,
        0,
        string.Empty,
        string.Empty,
        0,
        0,
        0,
        false,
        0,
        100,
        [],
        false,
        0,
        0,
        0,
        0,
        0,
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        failure);

    private static DdsTimingSmokeCaseReport CreateFailedCaseWithDdsEvidence(
        double cycles,
        DdsCommandResult start,
        DdsExecutionReceipt execution,
        double actualFrequencyHz,
        double expectedWindowSamples,
        int requiredFrames,
        string rawPath,
        string demodPath,
        string ackPath,
        string failure) => new(
        cycles,
        execution.RequestedTimeUs,
        execution.FirmwareVersion.ToString(3),
        execution.FirmwareProtocolVersion,
        execution.TimerClockHz,
        execution.TimerTicks,
        execution.EffectiveTimeUs,
        execution.CalculateEffectiveChannelCycles(actualFrequencyHz),
        execution.SwitchGuardMinimumUs,
        start.PacketHex,
        start.Response?.Hex ?? string.Empty,
        expectedWindowSamples,
        0,
        0,
        false,
        0,
        100,
        [],
        false,
        0,
        0,
        requiredFrames,
        0,
        0,
        false,
        File.Exists(rawPath) ? rawPath : string.Empty,
        File.Exists(rawPath) ? CalculateSha256(rawPath) : string.Empty,
        File.Exists(demodPath) ? demodPath : string.Empty,
        File.Exists(demodPath) ? CalculateSha256(demodPath) : string.Empty,
        ackPath,
        CalculateSha256(ackPath),
        false,
        failure);

    private static string CalculateSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed record DdsTimingRealtimeQualityResult(
    int EvaluatedBlockCount,
    int HighQualityBlockCount,
    int StrictAcceptedFrames,
    int RejectedFrames,
    int ValidTop3Windows,
    int TotalWindows,
    bool Passed,
    OfflineDemodulationResult Demodulation);
