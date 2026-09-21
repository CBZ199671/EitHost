namespace EitHost.App;

public enum StatusSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>
/// Classifies operator status text that reaches the shell without an explicit severity.
///
/// Status text is produced at ~100 call sites across the workspace controllers, all of which
/// funnel through a single sink. Threading a severity argument through every callback would be a
/// wide signature change for little gain, so callers that know their severity pass it explicitly
/// and everything else is classified here from the failure vocabulary the messages already use.
/// A missed classification degrades to today's behaviour (rendered as information); it never
/// suppresses a message.
/// </summary>
internal static class StatusSeverityClassifier
{
    private static readonly string[] ErrorMarkers =
    [
        "失败",
        "异常",
        "错误",
        "拒绝",
        "无法",
        "不能",
        "未准备",
        "已中断",
        "损坏"
    ];

    private static readonly string[] WarningMarkers =
    [
        "跳过",
        "部分",
        "超时",
        "未登记",
        "待处理",
        "已取消",
        "请先",
        "建议"
    ];

    internal static StatusSeverity Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return StatusSeverity.Info;
        }

        // Feature names and zero failure counters are not incidents.
        message = message.Replace("异常值", "接触", StringComparison.Ordinal);
        message = System.Text.RegularExpressions.Regex.Replace(message, @"失败\s*[:：]?\s*0(?![\d.a-zA-Z])", string.Empty);
        foreach (var marker in ErrorMarkers)
        {
            if (message.Contains(marker, StringComparison.Ordinal))
            {
                return StatusSeverity.Error;
            }
        }

        foreach (var marker in WarningMarkers)
        {
            if (message.Contains(marker, StringComparison.Ordinal))
            {
                return StatusSeverity.Warning;
            }
        }

        return StatusSeverity.Info;
    }
}
