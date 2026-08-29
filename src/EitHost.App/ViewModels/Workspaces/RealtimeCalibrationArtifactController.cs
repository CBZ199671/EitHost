using System.Globalization;
using System.IO;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimeCalibrationArtifactController
{
    private const int MinimumCalibrationFrames = 100;
    private readonly RealtimeSessionController sessions;
    private readonly string dataRootPath;
    private readonly RealtimeCalibrationArtifactCallbacks callbacks;
    private readonly Dictionary<string, EcdCwrHealthCalibration> completedContactCalibrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EcdCwrDeviceCalibration> completedDeviceCalibrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EcdCwrSessionCalibration> completedSessionCalibrations =
        new(StringComparer.OrdinalIgnoreCase);

    internal RealtimeCalibrationArtifactController(
        RealtimeSessionController sessions,
        string dataRootPath,
        RealtimeCalibrationArtifactCallbacks callbacks)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.dataRootPath = string.IsNullOrWhiteSpace(dataRootPath)
            ? throw new ArgumentException("DataRoot 不能为空。", nameof(dataRootPath))
            : dataRootPath;
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal async Task ObserveRunAsync(RealtimeRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            await (state.Task ?? Task.CompletedTask).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            callbacks.AddDiagnostic($"{state.SetLabel} observe realtime task canceled");
            callbacks.SetStatus($"{state.SetLabel} 实时成像已停止。");
        }
        catch (Exception ex)
        {
            callbacks.AddDiagnostic($"{state.SetLabel} observe realtime task failed: {ex}");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {state.SetLabel} realtime imaging failed {ex.Message}");
            callbacks.SetStatus($"{state.SetLabel} 实时成像失败：{ex.Message}");
        }
        finally
        {
            CompleteRun(state);
        }
    }

    internal void CompleteRun(RealtimeRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!sessions.TryGetState(state.SetLabel, out var existing)
            || !ReferenceEquals(existing, state))
        {
            return;
        }

        callbacks.AddDiagnostic($"{state.SetLabel} cleanup realtime task");
        if (!state.ReferenceInvalidated &&
            RealtimeContactCalibrationController.IsExportableCalibration(state.ExportableContactCalibration))
        {
            completedContactCalibrations[state.SetLabel] = state.ExportableContactCalibration!;
        }
        else
        {
            completedContactCalibrations.Remove(state.SetLabel);
        }

        if (!state.ReferenceInvalidated && state.ExportableSessionCalibration is not null)
        {
            completedSessionCalibrations[state.SetLabel] = state.ExportableSessionCalibration;
        }
        else
        {
            completedSessionCalibrations.Remove(state.SetLabel);
        }

        if (state.ExportableDeviceCalibration is not null)
        {
            completedDeviceCalibrations[
                CreateDeviceCalibrationKey(state.SetLabel, state.ExportableDeviceCalibration.FrequencyHz)] =
                state.ExportableDeviceCalibration;
        }

        sessions.Complete(state);
        callbacks.NotifyRunStateChanged();
    }

    internal void SaveSelectedSessionCalibration()
    {
        if (!TryGetSelectedSessionCalibration(out var label, out var calibration))
        {
            callbacks.SetStatus($"当前没有可导出的对象/会话标定；请等待累计 {MinimumCalibrationFrames} 个稳定全绿帧。");
            return;
        }

        var defaultPath = CreateDefaultSessionCalibrationPath(label, calibration);
        Directory.CreateDirectory(Path.GetDirectoryName(defaultPath) ?? dataRootPath);
        var selectedPath = callbacks.PromptSaveFile(
            defaultPath,
            "ECD-CWR 会话标定 (*.ecd-cwr-session-calibration.json)|*.ecd-cwr-session-calibration.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            ".json");
        if (selectedPath is null)
        {
            return;
        }

        try
        {
            new EcdCwrSessionCalibrationStore().Save(selectedPath, calibration);
            var referenceSummary = $"{label} 对象/会话标定已导出：{selectedPath}";
            callbacks.PublishReferenceSummary(label, referenceSummary);
            callbacks.SetReferenceSummary(referenceSummary);
            callbacks.SetStatus($"{label} ECD-CWR 对象/会话标定已保存。");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {label} session calibration saved {selectedPath}");
        }
        catch (Exception ex)
        {
            callbacks.SetStatus($"{label} ECD-CWR 对象/会话标定保存失败：{ex.Message}");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {label} session calibration save failed {ex.Message}");
        }
    }

    internal void SaveSelectedDeviceCalibration()
    {
        if (!TryGetSelectedDeviceCalibration(out var label, out var calibration))
        {
            callbacks.SetStatus("当前没有可导出的设备标定；完成一次合格的对象/会话标定后即可生成。");
            return;
        }

        var defaultPath = CreateDefaultDeviceCalibrationPath(label, calibration);
        Directory.CreateDirectory(Path.GetDirectoryName(defaultPath) ?? dataRootPath);
        var selectedPath = callbacks.PromptSaveFile(
            defaultPath,
            "ECD-CWR 设备标定 (*.ecd-cwr-device-calibration.json)|*.ecd-cwr-device-calibration.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            ".json");
        if (selectedPath is null)
        {
            return;
        }

        try
        {
            new EcdCwrDeviceCalibrationStore().Save(selectedPath, calibration);
            callbacks.SetStatus($"{label} ECD-CWR 设备标定已保存。");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {label} device calibration saved {selectedPath}");
        }
        catch (Exception ex)
        {
            callbacks.SetStatus($"{label} ECD-CWR 设备标定保存失败：{ex.Message}");
            callbacks.AddLog($"{DateTime.Now:HH:mm:ss} {label} device calibration save failed {ex.Message}");
        }
    }

    internal bool CanSaveSelectedSessionCalibration() =>
        TryGetSelectedSessionCalibration(out _, out _);

    internal bool CanSaveSelectedDeviceCalibration() =>
        TryGetSelectedDeviceCalibration(out _, out _);

    internal string CreateExportStateText()
    {
        var hasDevice = TryGetSelectedDeviceCalibration(out _, out _);
        if (TryGetSelectedSessionCalibration(out _, out var calibration))
        {
            return $"设备标定：{(hasDevice ? "就绪" : "待生成")} · 会话标定：就绪，frames={calibration.ReferenceFrameCount}，P99={calibration.HealthCalibration.Quality.Contact48WhitenedResidualP99:G3}";
        }

        var label = callbacks.GetSelectedLabel();
        if (label is not null && sessions.TryGetState(label, out var state) && state.IsActive)
        {
            if (state.StartupDegradedReference is { } degraded)
            {
                var faults = string.Join(',', degraded.FaultElectrodes.Select(electrode => $"E{electrode}"));
                return $"设备标定：{(hasDevice ? "已有" : "待生成")} · 降级参考：已锁定 {degraded.RobustReference.FrameCount} 帧等效（{faults}） · 正式会话：需全绿";
            }

            if (state.StartupDegradedReferenceWarmupCount > 0)
            {
                var faults = string.Join(',', state.StartupDegradedReferenceFaultElectrodes.Select(electrode => $"E{electrode}"));
                return $"设备标定：{(hasDevice ? "已有" : "待生成")} · 降级参考：{Math.Min(state.StartupDegradedReferenceWarmupCount, MinimumCalibrationFrames)}/{MinimumCalibrationFrames}（{faults}；聚合 {state.StartupDegradedReferenceAggregateCount}） · 正式会话：需全绿";
            }

            var green = state.ReferenceCandidateFrames.Count(EcdCwrRobustReferenceBuilder.IsStrictGreenFrame);
            if (state.ReferenceIsProvisional)
            {
                var background = state.LatestReferenceStationarity is null
                    ? "等待严格全绿物理帧"
                    : RealtimeReferenceLifecycleController.FormatReferenceStationarity(state.LatestReferenceStationarity);
                return $"设备标定：{(hasDevice ? "已有" : "待生成")} · 快速预览参考：已锁定（低置信） · " +
                    $"正式参考后台：{background} · 候选全绿 {green} 帧";
            }

            if (state.ReferenceVoltage208 is not null &&
                string.Equals(state.ActiveReferenceLockKind, "user_selected", StringComparison.Ordinal))
            {
                var stationarity = state.LatestReferenceStationarity is null
                    ? "稳定性仅作提示"
                    : RealtimeReferenceLifecycleController.FormatReferenceStationarity(state.LatestReferenceStationarity);
                return $"设备标定：{(hasDevice ? "已有" : "待生成")} · 用户参考：已锁定（正常成像/ROI） · {stationarity}";
            }

            if (state.LatestReferenceStationarity is { CanLock: false } blocked)
            {
                return $"设备标定：{(hasDevice ? "已有" : "待生成")} · 会话参考：" +
                    $"{RealtimeReferenceLifecycleController.FormatReferenceStationarity(blocked)} · " +
                    $"全绿 {Math.Min(green, MinimumCalibrationFrames)}/{MinimumCalibrationFrames}";
            }

            return $"设备标定：{(hasDevice ? "已有" : "待生成")} · 会话参考：{Math.Min(green, MinimumCalibrationFrames)}/{MinimumCalibrationFrames}";
        }

        return $"设备标定：{(hasDevice ? "已有" : "尚无")} · 会话标定：尚未就绪";
    }

    internal void ClearCompleted(string setLabel)
    {
        completedContactCalibrations.Remove(setLabel);
        completedSessionCalibrations.Remove(setLabel);
    }

    internal void Invalidate(string setLabel)
    {
        callbacks.PostToUi(() =>
        {
            ClearCompleted(setLabel);
            callbacks.NotifyCalibrationStateChanged();
        });
    }

    internal void RaiseStateChanged() =>
        callbacks.PostToUi(callbacks.NotifyCalibrationStateChanged);

    private bool TryGetSelectedSessionCalibration(
        out string label,
        out EcdCwrSessionCalibration calibration)
    {
        label = callbacks.GetSelectedLabel() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(label) &&
            sessions.TryGetState(label, out var state) &&
            !state.ReferenceInvalidated &&
            state.ExportableSessionCalibration is not null)
        {
            calibration = state.ExportableSessionCalibration;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(label) &&
            completedSessionCalibrations.TryGetValue(label, out var completedCalibration))
        {
            calibration = completedCalibration;
            return true;
        }

        calibration = null!;
        return false;
    }

    private bool TryGetSelectedDeviceCalibration(
        out string label,
        out EcdCwrDeviceCalibration calibration)
    {
        label = callbacks.GetSelectedLabel() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(label) &&
            sessions.TryGetState(label, out var state) &&
            state.ExportableDeviceCalibration is not null)
        {
            calibration = state.ExportableDeviceCalibration;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(label) &&
            completedDeviceCalibrations.TryGetValue(
                CreateDeviceCalibrationKey(label, callbacks.GetSelectedFrequencyHz()),
                out var completedCalibration))
        {
            calibration = completedCalibration;
            return true;
        }

        calibration = null!;
        return false;
    }

    private string CreateDefaultSessionCalibrationPath(
        string label,
        EcdCwrSessionCalibration calibration)
    {
        var directory = Path.Combine(dataRootPath, "EcdCwrCalibrations");
        var safeLabel = SanitizeFileNameComponent(label);
        var frequency = calibration.FrequencyHz.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', 'p');
        return Path.Combine(
            directory,
            $"{safeLabel}_{frequency}Hz_{calibration.ImagingRunId:N}_ref{calibration.ReferenceGeneration}.ecd-cwr-session-calibration.json");
    }

    private string CreateDefaultDeviceCalibrationPath(
        string label,
        EcdCwrDeviceCalibration calibration)
    {
        var directory = Path.Combine(dataRootPath, "EcdCwrCalibrations");
        var safeLabel = SanitizeFileNameComponent(label);
        var frequency = calibration.FrequencyHz.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', 'p');
        return Path.Combine(directory, $"{safeLabel}_{frequency}Hz.ecd-cwr-device-calibration.json");
    }

    private static string CreateDeviceCalibrationKey(string label, double frequencyHz) =>
        FormattableString.Invariant($"{label}|{frequencyHz:R}");

    private static string SanitizeFileNameComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }
}

internal sealed record RealtimeCalibrationArtifactCallbacks(
    Func<string?> GetSelectedLabel,
    Func<double> GetSelectedFrequencyHz,
    Func<string, string, string, string?> PromptSaveFile,
    Action<string, string> PublishReferenceSummary,
    Action<string> SetReferenceSummary,
    Action<Action> PostToUi,
    Action<string> AddDiagnostic,
    Action<string> AddLog,
    Action<string> SetStatus,
    Action NotifyCalibrationStateChanged,
    Action NotifyRunStateChanged);
