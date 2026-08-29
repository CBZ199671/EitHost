using System.Collections.ObjectModel;
using EitHost.Core.Deployment;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Pairing;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record HardwareDiscoveryCallbacks(
    Action<string> PublishStatus,
    Action<string> AddAcquisitionLog,
    Action<PairingSummaryItem> InitializeRunProfile,
    Action RaisePairingCommandStates,
    Action<string> PublishSessionName,
    Action RaiseDashboardState);

internal sealed class HardwareDiscoveryController
{
    private readonly HardwareWorkspaceViewModel workspace;
    private readonly PnpInsertionMonitor insertionMonitor;
    private readonly IUsb2070NativeApi usb2070NativeApi;
    private readonly ExperimentCatalog experimentCatalog;
    private readonly Guid sessionId;
    private readonly Func<bool> isCatalogReady;
    private readonly HardwareDiscoveryCallbacks callbacks;
    private readonly ManualPairingSession pairingSession = new();

    internal HardwareDiscoveryController(
        HardwareWorkspaceViewModel workspace,
        PnpInsertionMonitor insertionMonitor,
        IUsb2070NativeApi usb2070NativeApi,
        ExperimentCatalog experimentCatalog,
        Guid sessionId,
        Func<bool> isCatalogReady,
        HardwareDiscoveryCallbacks callbacks)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.insertionMonitor = insertionMonitor ?? throw new ArgumentNullException(nameof(insertionMonitor));
        this.usb2070NativeApi = usb2070NativeApi ?? throw new ArgumentNullException(nameof(usb2070NativeApi));
        this.experimentCatalog = experimentCatalog ?? throw new ArgumentNullException(nameof(experimentCatalog));
        this.sessionId = sessionId;
        this.isCatalogReady = isCatalogReady ?? throw new ArgumentNullException(nameof(isCatalogReady));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal void ApplyFirstRunCheck()
    {
        var result = new FirstRunCheckService().Check(AppContext.BaseDirectory);
        if (!result.IsReady)
        {
            callbacks.PublishStatus(string.Join("；", result.Issues));
        }
    }

    internal async Task InitializeBaselineAsync()
    {
        try
        {
            workspace.PendingUsb2070Candidates.Clear();
            workspace.PendingDdsCandidates.Clear();
            workspace.SelectedUsb2070Candidate = null;
            workspace.SelectedDdsCandidate = null;

            var snapshot = await insertionMonitor.InitializeAsync().ConfigureAwait(true);
            callbacks.PublishStatus(
                $"基线已记录：USB2070 {snapshot.Usb2070Devices.Count} 个，串口 {snapshot.SerialDevices.Count} 个。");
        }
        catch (Exception ex)
        {
            callbacks.PublishStatus($"记录基线失败：{ex.Message}");
        }
    }

    internal async Task DetectNewDevicesAsync()
    {
        try
        {
            var change = await insertionMonitor.DetectChangesAsync().ConfigureAwait(true);
            RemoveCandidates(change.Removed);
            AddCandidates(change.AddedUsb2070Devices, workspace.PendingUsb2070Candidates);
            AddCandidates(change.AddedSerialDevices, workspace.PendingDdsCandidates);

            callbacks.PublishStatus(change.HasChanges
                ? $"新增 USB2070 {change.AddedUsb2070Devices.Count} 个，新增 DDS 串口 {change.AddedSerialDevices.Count} 个。"
                : "没有发现新增设备。");
        }
        catch (Exception ex)
        {
            callbacks.PublishStatus($"扫描新增设备失败：{ex.Message}");
        }
    }

    internal async Task ScanUsb2070NumbersAsync()
    {
        try
        {
            var devices = await Task.Run(() => new Usb2070Service(usb2070NativeApi).Scan()).ConfigureAwait(true);
            if (devices.Count == 0)
            {
                callbacks.AddAcquisitionLog($"{DateTime.Now:HH:mm:ss} USB2070 SDK scan: no devices");
                callbacks.PublishStatus("USB2070 SDK 未扫描到采集卡。");
                return;
            }

            foreach (var device in devices)
            {
                callbacks.AddAcquisitionLog(
                    $"{DateTime.Now:HH:mm:ss} USB2070 SDK #{device.DeviceNumber} ch={device.AvailableChannelCount} bit={device.AdBit} max={device.MaxSampleRateHz}Hz");
            }

            callbacks.PublishStatus($"USB2070 SDK 扫描到 {devices.Count} 块采集卡。");
        }
        catch (Exception ex)
        {
            callbacks.PublishStatus($"USB2070 SDK 扫描失败：{ex.Message}");
        }
    }

    internal void InstallUsb2070Driver()
    {
        try
        {
            new Usb2070DriverInstallLauncher().Launch(AppContext.BaseDirectory);
            workspace.HardwareSmokeLogs.Insert(0, $"{DateTime.Now:HH:mm:ss} 已请求管理员安装/修复 USB2070 驱动");
            callbacks.PublishStatus("已打开管理员驱动安装窗口；完成后请重新生成硬件报告。");
        }
        catch (Exception ex)
        {
            callbacks.PublishStatus($"打开 USB2070 驱动安装窗口失败：{ex.Message}");
        }
    }

    internal async Task BindSelectedDevicesAsync()
    {
        if (workspace.SelectedUsb2070Candidate is null || workspace.SelectedDdsCandidate is null)
        {
            return;
        }

        try
        {
            var portName = workspace.SelectedDdsCandidate.Candidate.PortName;
            if (string.IsNullOrWhiteSpace(portName))
            {
                throw new InvalidOperationException("所选 DDS 设备没有可用串口名。");
            }

            using var transport = new DdsSerialPortTransport(portName);
            var client = new DdsProtocolClient(transport);
            var capabilities = await client.GetCapabilitiesAsync().ConfigureAwait(true);
            var pairing = pairingSession.Bind(
                workspace.PairingLabel,
                workspace.PairingUsb2070DeviceNumber,
                workspace.SelectedUsb2070Candidate.Candidate,
                workspace.SelectedDdsCandidate.Candidate);

            if (isCatalogReady())
            {
                experimentCatalog.UpsertPairing(sessionId, pairing);
            }

            var summaryItem = new PairingSummaryItem(pairing, capabilities);
            workspace.BoundPairings.Add(summaryItem);
            callbacks.InitializeRunProfile(summaryItem);
            workspace.SelectedBoundPairing = summaryItem;
            workspace.SelectedRealtimeDisplayPairing ??= summaryItem;
            callbacks.RaisePairingCommandStates();
            workspace.PendingUsb2070Candidates.Remove(workspace.SelectedUsb2070Candidate);
            workspace.PendingDdsCandidates.Remove(workspace.SelectedDdsCandidate);
            workspace.SelectedUsb2070Candidate = null;
            workspace.SelectedDdsCandidate = null;
            workspace.PairingLabel = pairingSession.SuggestNextLabel();
            workspace.PairingUsb2070DeviceNumber = workspace.BoundPairings.Count;
            callbacks.PublishSessionName($"已绑定 {workspace.BoundPairings.Count} 套设备");
            callbacks.RaiseDashboardState();
            callbacks.PublishStatus(
                $"{pairing.Label} 已完成手动配对；DDS firmware {capabilities.FirmwareVersion} / protocol v{DdsProtocolConstants.ProtocolVersion} 已验证。");
        }
        catch (Exception ex)
        {
            callbacks.PublishStatus($"绑定失败：{ex.Message}");
        }
    }

    internal bool CanBindSelectedDevices()
    {
        return !string.IsNullOrWhiteSpace(workspace.PairingLabel)
            && workspace.PairingUsb2070DeviceNumber >= 0
            && workspace.SelectedUsb2070Candidate is not null
            && workspace.SelectedDdsCandidate is not null;
    }

    internal void ReplacePairings(IEnumerable<EitSetPairing> pairings)
    {
        pairingSession.ReplaceAll(pairings);
    }

    private static void AddCandidates(
        IEnumerable<PnpDeviceCandidate> candidates,
        ObservableCollection<DeviceCandidateOption> target)
    {
        foreach (var candidate in candidates)
        {
            if (target.All(option => !string.Equals(
                    option.IdentityKey,
                    candidate.IdentityKey,
                    StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(new DeviceCandidateOption(candidate));
            }
        }
    }

    private void RemoveCandidates(IEnumerable<PnpDeviceCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            RemoveCandidate(workspace.PendingUsb2070Candidates, candidate);
            RemoveCandidate(workspace.PendingDdsCandidates, candidate);
        }
    }

    private static void RemoveCandidate(
        ObservableCollection<DeviceCandidateOption> target,
        PnpDeviceCandidate candidate)
    {
        var existing = target.FirstOrDefault(option =>
            string.Equals(option.IdentityKey, candidate.IdentityKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            target.Remove(existing);
        }
    }
}
