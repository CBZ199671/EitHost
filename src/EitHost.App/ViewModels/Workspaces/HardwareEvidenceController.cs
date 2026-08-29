using System.IO;
using System.IO.Ports;
using System.Text.Encodings.Web;
using System.Text.Json;
using EitHost.Core.Application.Hardware;
using EitHost.Core.Diagnostics;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record HardwareEvidenceSnapshot(
    Guid SessionId,
    DateTimeOffset SessionStartedAt,
    string SessionName,
    string DataRootPath,
    string CatalogPath,
    string SessionDirectory,
    int AcquisitionReadSampleRows,
    int AcquisitionSampleRateHz,
    string AcquisitionRange,
    string AcquisitionTriggerMode,
    string AcquisitionTriggerSource,
    int AcquisitionTriggerDelay,
    int AcquisitionTriggerLength,
    int AcquisitionTriggerLevel,
    int DdsFrequencyHz,
    int DdsDacChannel,
    double DdsGain,
    int DdsPhaseDegrees,
    int DdsPgaGain,
    string ExcitationMode,
    double ExcitationChannelCycles,
    int ExcitationScanTimes,
    int ExcitationOverheadUs,
    IReadOnlyList<PairingSummaryItem> BoundPairings,
    IReadOnlyList<CatalogRunSummaryItem> RecentRuns);

internal sealed class HardwareEvidenceController
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly HardwareWorkspaceViewModel workspace;
    private readonly Func<HardwareEvidenceSnapshot> captureSnapshot;
    private readonly Func<CancellationToken, Task<HardwareSmokeReport>> smokeCapture;
    private readonly Action<string> publishStatus;

    internal HardwareEvidenceController(
        HardwareWorkspaceViewModel workspace,
        Func<HardwareEvidenceSnapshot> captureSnapshot,
        Func<CancellationToken, Task<HardwareSmokeReport>> smokeCapture,
        Action<string> publishStatus)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.captureSnapshot = captureSnapshot ?? throw new ArgumentNullException(nameof(captureSnapshot));
        this.smokeCapture = smokeCapture ?? throw new ArgumentNullException(nameof(smokeCapture));
        this.publishStatus = publishStatus ?? throw new ArgumentNullException(nameof(publishStatus));
    }

    internal static Func<CancellationToken, Task<HardwareSmokeReport>> CreateRealSmokeCapture(
        IUsb2070NativeApi usb2070NativeApi)
    {
        ArgumentNullException.ThrowIfNull(usb2070NativeApi);
        return cancellationToken => new HardwareSmokeReporter(
            new WindowsPnpDeviceScanner(),
            usb2070NativeApi,
            SerialPort.GetPortNames,
            () => WindowsUsb2070DriverPreflightProvider.Capture()).CaptureAsync(cancellationToken);
    }

    internal bool CanGenerateT25SmokePlan() => workspace.BoundPairings.Count > 0;

    internal bool CanExportPairingManifest() => workspace.BoundPairings.Count > 0;

    internal async Task GenerateHardwareSmokeReportAsync()
    {
        try
        {
            var snapshot = captureSnapshot();
            var smokeDirectory = Path.Combine(snapshot.DataRootPath, "SmokeReports");
            Directory.CreateDirectory(smokeDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var markdownPath = Path.Combine(smokeDirectory, $"hardware-smoke-{timestamp}.md");
            var jsonPath = Path.ChangeExtension(markdownPath, ".json");
            var currentMarkdownPath = Path.Combine(smokeDirectory, "hardware-smoke-current.md");
            var currentJsonPath = Path.ChangeExtension(currentMarkdownPath, ".json");

            var report = await smokeCapture(CancellationToken.None).ConfigureAwait(true);
            var markdown = HardwareSmokeReportFormatter.ToMarkdown(report);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            await File.WriteAllTextAsync(markdownPath, markdown).ConfigureAwait(true);
            await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(true);
            await File.WriteAllTextAsync(currentMarkdownPath, markdown).ConfigureAwait(true);
            await File.WriteAllTextAsync(currentJsonPath, json).ConfigureAwait(true);

            workspace.HardwareSmokeReportPath = markdownPath;
            workspace.HardwareSmokeSummary = $"完整套数 {report.EstimatedCompleteSetCount}，T24就绪 {(report.Readiness.ReadyForSingleSetSmoke ? "是" : "否")}，T25就绪 {(report.MultiSetReadiness.ReadyForMultiSetSmoke ? "是" : "否")}，PnP USB2070 {report.PnpUsb2070Devices.Count}，DDS COM {report.PnpDdsSerialDevices.Count}，SDK USB2070 {report.Usb2070SdkDevices.Count}，T25阻断 {report.MultiSetReadiness.Blockers.Count}，警告 {report.Warnings.Count}";
            workspace.HardwareSmokeLogs.Insert(0, $"{DateTime.Now:HH:mm:ss} {workspace.HardwareSmokeSummary} | current={currentMarkdownPath}");
            publishStatus($"硬件报告已生成：{markdownPath}");
        }
        catch (Exception ex)
        {
            publishStatus($"生成硬件报告失败：{ex.Message}");
        }
    }

    internal async Task GenerateT25SmokePlanAsync()
    {
        var snapshot = captureSnapshot();
        if (snapshot.BoundPairings.Count == 0)
        {
            publishStatus("请先至少绑定一套 EIT 设备，再生成 T25 验收计划。");
            return;
        }

        try
        {
            var smokeDirectory = Path.Combine(snapshot.DataRootPath, "SmokeReports");
            Directory.CreateDirectory(smokeDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var markdownPath = Path.Combine(smokeDirectory, $"t25-smoke-plan-{timestamp}.md");
            var scriptPath = Path.ChangeExtension(markdownPath, ".ps1");
            var currentMarkdownPath = Path.Combine(smokeDirectory, "t25-smoke-plan-current.md");
            var currentScriptPath = Path.ChangeExtension(currentMarkdownPath, ".ps1");
            var repositoryRoot = HardwareEvidenceArtifacts.FindRepositoryRoot();
            const string outputDirectory = @".\artifacts\t25-multi-set-smoke-current";
            var rowCount = Math.Max(snapshot.AcquisitionReadSampleRows, 1024);
            var pairs = snapshot.BoundPairings.Select(HardwareEvidenceArtifacts.CreateT25PairArgument).ToArray();
            var command = HardwareEvidenceArtifacts.CreateT25SmokeCommand(
                outputDirectory,
                pairs,
                rowCount,
                snapshot.AcquisitionSampleRateHz,
                snapshot.DdsFrequencyHz);
            var script = HardwareEvidenceArtifacts.CreateT25SmokeScript(repositoryRoot, command);
            var markdown = HardwareEvidenceArtifacts.CreateT25SmokePlanMarkdown(new T25SmokePlanModel(
                DateTimeOffset.Now,
                markdownPath,
                scriptPath,
                currentMarkdownPath,
                currentScriptPath,
                repositoryRoot,
                outputDirectory,
                command,
                rowCount,
                snapshot.AcquisitionSampleRateHz,
                snapshot.DdsFrequencyHz,
                snapshot.DdsDacChannel,
                snapshot.DdsGain,
                snapshot.DdsPhaseDegrees,
                snapshot.DdsPgaGain,
                snapshot.ExcitationMode,
                snapshot.AcquisitionRange,
                snapshot.BoundPairings));

            await File.WriteAllTextAsync(scriptPath, script).ConfigureAwait(true);
            await File.WriteAllTextAsync(markdownPath, markdown).ConfigureAwait(true);
            await File.WriteAllTextAsync(currentScriptPath, script).ConfigureAwait(true);
            await File.WriteAllTextAsync(currentMarkdownPath, markdown).ConfigureAwait(true);

            workspace.T25SmokePlanPath = markdownPath;
            workspace.T25SmokePlanSummary = snapshot.BoundPairings.Count >= 2
                ? $"T25 验收计划已生成：{snapshot.BoundPairings.Count} 套绑定，可运行脚本。"
                : $"T25 验收计划已生成：当前 {snapshot.BoundPairings.Count}/2 套，第二套绑定后请重新生成。";
            workspace.T25SmokePlanLogs.Insert(0, $"{DateTime.Now:HH:mm:ss} {workspace.T25SmokePlanSummary} | current={currentMarkdownPath}");
            publishStatus($"T25 验收计划已生成：{markdownPath}");
        }
        catch (Exception ex)
        {
            publishStatus($"生成 T25 验收计划失败：{ex.Message}");
        }
    }

    internal async Task ExportPairingManifestAsync()
    {
        var snapshot = captureSnapshot();
        if (snapshot.BoundPairings.Count == 0)
        {
            publishStatus("请先至少绑定一套 EIT 设备，再导出配对清单。");
            return;
        }

        try
        {
            var manifestDirectory = Path.Combine(snapshot.DataRootPath, "PairingManifests");
            Directory.CreateDirectory(manifestDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var markdownPath = Path.Combine(manifestDirectory, $"pairing-manifest-{timestamp}.md");
            var jsonPath = Path.ChangeExtension(markdownPath, ".json");
            var currentMarkdownPath = Path.Combine(manifestDirectory, "pairing-manifest-current.md");
            var currentJsonPath = Path.ChangeExtension(currentMarkdownPath, ".json");
            var manifest = CreatePairingManifest(snapshot, DateTimeOffset.Now, markdownPath, currentMarkdownPath);
            var markdown = HardwareEvidenceArtifacts.CreatePairingManifestMarkdown(manifest, jsonPath, currentJsonPath);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);

            await File.WriteAllTextAsync(markdownPath, markdown).ConfigureAwait(true);
            await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(true);
            await File.WriteAllTextAsync(currentMarkdownPath, markdown).ConfigureAwait(true);
            await File.WriteAllTextAsync(currentJsonPath, json).ConfigureAwait(true);

            workspace.PairingManifestPath = markdownPath;
            workspace.PairingManifestSummary = $"配对清单已导出：{snapshot.BoundPairings.Count} 套，记录当前激励/采集参数。";
            workspace.PairingManifestLogs.Insert(0, $"{DateTime.Now:HH:mm:ss} {workspace.PairingManifestSummary} | current={currentMarkdownPath}");
            publishStatus($"配对清单已导出：{markdownPath}");
        }
        catch (Exception ex)
        {
            publishStatus($"导出配对清单失败：{ex.Message}");
        }
    }

    internal async Task ExportEvidenceIndexAsync()
    {
        try
        {
            var snapshot = captureSnapshot();
            var indexDirectory = Path.Combine(snapshot.DataRootPath, "EvidenceIndexes");
            Directory.CreateDirectory(indexDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var markdownPath = Path.Combine(indexDirectory, $"evidence-index-{timestamp}.md");
            var jsonPath = Path.ChangeExtension(markdownPath, ".json");
            var currentMarkdownPath = Path.Combine(indexDirectory, "evidence-index-current.md");
            var currentJsonPath = Path.ChangeExtension(currentMarkdownPath, ".json");
            var index = CreateEvidenceIndex(snapshot, DateTimeOffset.Now, markdownPath, currentMarkdownPath);
            var markdown = HardwareEvidenceArtifacts.CreateEvidenceIndexMarkdown(index, jsonPath, currentJsonPath);
            var json = JsonSerializer.Serialize(index, JsonOptions);

            await File.WriteAllTextAsync(markdownPath, markdown).ConfigureAwait(true);
            await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(true);
            await File.WriteAllTextAsync(currentMarkdownPath, markdown).ConfigureAwait(true);
            await File.WriteAllTextAsync(currentJsonPath, json).ConfigureAwait(true);

            workspace.EvidenceIndexPath = markdownPath;
            workspace.EvidenceIndexSummary = $"证据索引已导出：绑定 {snapshot.BoundPairings.Count} 套，最近数据 {snapshot.RecentRuns.Count} 条。";
            workspace.EvidenceIndexLogs.Insert(0, $"{DateTime.Now:HH:mm:ss} {workspace.EvidenceIndexSummary} | current={currentMarkdownPath}");
            publishStatus($"证据索引已导出：{markdownPath}");
        }
        catch (Exception ex)
        {
            publishStatus($"导出证据索引失败：{ex.Message}");
        }
    }

    internal async Task ExportFieldSnapshotAsync()
    {
        try
        {
            var steps = new List<string>();
            await GenerateHardwareSmokeReportAsync().ConfigureAwait(true);
            steps.Add("硬件报告");

            if (workspace.BoundPairings.Count > 0)
            {
                await ExportPairingManifestAsync().ConfigureAwait(true);
                steps.Add("配对清单");
                await GenerateT25SmokePlanAsync().ConfigureAwait(true);
                steps.Add("T25 计划");
            }
            else
            {
                steps.Add("跳过配对清单/T25计划：尚未绑定设备");
            }

            await ExportEvidenceIndexAsync().ConfigureAwait(true);
            steps.Add("证据索引");

            workspace.FieldSnapshotPath = workspace.EvidenceIndexPath;
            workspace.FieldSnapshotSummary = $"现场快照已导出：{string.Join("，", steps)}。";
            workspace.FieldSnapshotLogs.Insert(0, $"{DateTime.Now:HH:mm:ss} {workspace.FieldSnapshotSummary}");
            publishStatus($"现场快照已导出：{workspace.EvidenceIndexPath}");
        }
        catch (Exception ex)
        {
            publishStatus($"导出现场快照失败：{ex.Message}");
        }
    }

    private static PairingManifest CreatePairingManifest(
        HardwareEvidenceSnapshot snapshot,
        DateTimeOffset generatedAt,
        string markdownPath,
        string currentMarkdownPath)
    {
        return new PairingManifest(
            snapshot.SessionId,
            snapshot.SessionStartedAt,
            snapshot.SessionName,
            generatedAt,
            markdownPath,
            currentMarkdownPath,
            snapshot.BoundPairings.Count,
            new PairingManifestExcitation(
                snapshot.DdsDacChannel,
                snapshot.DdsFrequencyHz,
                snapshot.DdsGain,
                snapshot.DdsPhaseDegrees,
                snapshot.DdsPgaGain,
                snapshot.ExcitationMode,
                snapshot.ExcitationChannelCycles,
                snapshot.ExcitationScanTimes,
                snapshot.ExcitationOverheadUs),
            new PairingManifestAcquisition(
                snapshot.AcquisitionSampleRateHz,
                snapshot.AcquisitionRange,
                snapshot.AcquisitionTriggerMode,
                snapshot.AcquisitionTriggerSource,
                snapshot.AcquisitionTriggerDelay,
                snapshot.AcquisitionTriggerLength,
                snapshot.AcquisitionTriggerLevel,
                snapshot.AcquisitionReadSampleRows,
                EitSet.MeasurementChannelCount),
            snapshot.BoundPairings.Select(pairing => new PairingManifestSet(
                pairing.Pairing.Label,
                pairing.Pairing.CreatedAt,
                pairing.Pairing.Usb2070DeviceNumber,
                pairing.Pairing.Usb2070Candidate.DeviceId,
                pairing.Pairing.Usb2070Candidate.DisplayName,
                pairing.Pairing.Usb2070Candidate.Vid,
                pairing.Pairing.Usb2070Candidate.Pid,
                pairing.Pairing.Usb2070Candidate.LocationPath,
                pairing.Pairing.DdsSerialCandidate.PortName ?? string.Empty,
                pairing.Pairing.DdsSerialCandidate.DeviceId,
                pairing.Pairing.DdsSerialCandidate.DisplayName,
                pairing.Pairing.DdsSerialCandidate.Vid,
                pairing.Pairing.DdsSerialCandidate.Pid,
                pairing.Pairing.DdsSerialCandidate.LocationPath)).ToArray());
    }

    private EvidenceIndex CreateEvidenceIndex(
        HardwareEvidenceSnapshot snapshot,
        DateTimeOffset generatedAt,
        string markdownPath,
        string currentMarkdownPath)
    {
        return new EvidenceIndex(
            snapshot.SessionId,
            snapshot.SessionStartedAt,
            snapshot.SessionName,
            generatedAt,
            snapshot.DataRootPath,
            snapshot.CatalogPath,
            snapshot.SessionDirectory,
            markdownPath,
            currentMarkdownPath,
            new EvidenceIndexArtifacts(
                HardwareEvidenceArtifacts.CaptureFileEvidence(workspace.HardwareSmokeReportPath),
                HardwareEvidenceArtifacts.CaptureFileEvidence(workspace.T25SmokePlanPath),
                HardwareEvidenceArtifacts.CaptureFileEvidence(workspace.PairingManifestPath)),
            snapshot.BoundPairings.Count,
            snapshot.RecentRuns.Count,
            snapshot.BoundPairings.Select(pairing => new EvidenceIndexBoundSet(
                pairing.Pairing.Label,
                pairing.Pairing.Usb2070DeviceNumber,
                pairing.Pairing.Usb2070Candidate.DeviceId,
                pairing.Pairing.DdsSerialCandidate.PortName ?? string.Empty,
                pairing.Pairing.DdsSerialCandidate.DeviceId)).ToArray(),
            snapshot.RecentRuns.Select(run => new EvidenceIndexRecentRun(
                run.Summary.RunId,
                run.Summary.SetLabel,
                run.Summary.CapturedAt,
                run.Summary.Hdf5Path,
                run.Summary.SampleRows,
                run.Summary.ChannelCount,
                run.Summary.FileCount,
                run.Summary.ExportCount,
                HardwareEvidenceArtifacts.CaptureFileEvidence(run.Summary.Hdf5Path),
                HardwareEvidenceArtifacts.CaptureFileEvidence(run.Summary.LatestDemodHdf5Path ?? string.Empty),
                HardwareEvidenceArtifacts.CaptureFileEvidence(run.Summary.LatestCsvPath ?? string.Empty))).ToArray());
    }
}
