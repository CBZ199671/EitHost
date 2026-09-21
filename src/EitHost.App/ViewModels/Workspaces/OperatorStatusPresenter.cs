namespace EitHost.App.ViewModels.Workspaces;

/// <summary>
/// Owns the operator status line: the newest message, its severity, and the failures that have
/// not been acknowledged yet.
///
/// The status line always shows the newest message, so a failure reported by one device set is
/// replaced within seconds by routine progress from another. This keeps every failure both
/// visually distinct while it is shown and discoverable afterwards, without holding a stale
/// message on screen.
/// </summary>
internal sealed class OperatorStatusPresenter(Action<string> appendActivityLog) : ObservableObject
{
    private readonly Action<string> appendActivityLog =
        appendActivityLog ?? throw new ArgumentNullException(nameof(appendActivityLog));

    private string statusMessage = "启动后先记录当前设备基线，再逐套插入硬件。";
    private StatusSeverity statusMessageSeverity = StatusSeverity.Info;
    private string lastErrorMessage = string.Empty;
    private int unreviewedErrorCount;
    private string lastReportedMessage = string.Empty;
    private StatusSeverity lastReportedSeverity;

    internal event Action? AcknowledgeAvailabilityChanged;

    internal string StatusMessage
    {
        get => statusMessage;
        set => Report(value, StatusSeverityClassifier.Classify(value));
    }

    internal StatusSeverity StatusMessageSeverity
    {
        get => statusMessageSeverity;
        private set => SetProperty(ref statusMessageSeverity, value);
    }

    internal int UnreviewedErrorCount
    {
        get => unreviewedErrorCount;
        private set
        {
            if (!SetProperty(ref unreviewedErrorCount, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasUnreviewedErrors));
            OnPropertyChanged(nameof(UnreviewedErrorSummary));
            AcknowledgeAvailabilityChanged?.Invoke();
        }
    }

    internal bool HasUnreviewedErrors => UnreviewedErrorCount > 0;

    internal string UnreviewedErrorSummary => UnreviewedErrorCount switch
    {
        0 => string.Empty,
        1 => $"1 条失败未查看：{lastErrorMessage}",
        _ => $"{UnreviewedErrorCount} 条失败未查看，最近一条：{lastErrorMessage}"
    };

    internal void Report(string message, StatusSeverity severity)
    {
        var operatorMessage = OperatorMessageText.Format(message);
        SetProperty(ref statusMessage, operatorMessage, nameof(StatusMessage));
        StatusMessageSeverity = severity;
        if (string.Equals(message, lastReportedMessage, StringComparison.Ordinal) && severity == lastReportedSeverity) return;
        lastReportedMessage = message;
        lastReportedSeverity = severity;
        if (!string.IsNullOrWhiteSpace(message))
            appendActivityLog($"{DateTime.Now:HH:mm:ss} [{(severity == StatusSeverity.Error ? "失败" : severity == StatusSeverity.Warning ? "提醒" : "状态")}] {message}");
        if (severity != StatusSeverity.Error || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lastErrorMessage = operatorMessage;
        UnreviewedErrorCount = checked(UnreviewedErrorCount + 1);
        OnPropertyChanged(nameof(UnreviewedErrorSummary));
    }

    internal void Acknowledge()
    {
        lastReportedMessage = string.Empty;
        lastErrorMessage = string.Empty;
        UnreviewedErrorCount = 0;
    }
}
