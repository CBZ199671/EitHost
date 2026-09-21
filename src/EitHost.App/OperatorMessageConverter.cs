using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace EitHost.App;

/// <summary>Presentation only. Original diagnostic text stays in the model and logs.</summary>
public sealed class OperatorMessageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        OperatorMessageText.Format(value as string, parameter as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

internal static class OperatorMessageText
{
    internal static string Format(string? source, string? context = null)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var text = source.Trim();
        bool Has(params string[] words) => words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        var device = Regex.Match(text, @"^(?:EIT-\d+|\S+ 套)\s*").Value.Trim();
        string Say(string message) => device.Length == 0 ? message : $"{device}：{message}";
        var failure = StatusSeverityClassifier.Classify(text) == StatusSeverity.Error;

        if (context == "GroupSummary")
            return string.Join(" · ", text.Split('·', StringSplitOptions.TrimEntries).Skip(1).Take(2));
        if (context == "GroupLayer")
        {
            var label = string.Join(" · ", text.Split('·', StringSplitOptions.TrimEntries).Take(2));
            var state = Has("成员缺失") ? "缺少设备记录。"
                : Has("不可用", "失败", "不一致", "未完整保存") ? "本轮读取失败，请查看系统日志。"
                : Has("无二维重构", "解调未完成") ? "本轮没有图像，请选择其他轮次。" : "";
            return state.Length == 0 ? label : $"{label} · {state}";
        }
        if (context == "GroupStatus")
        {
            var round = Regex.Match(text, @"轮次 (\d+/\d+)").Groups[1].Value;
            var prefix = round.Length > 0 ? $"轮次 {round} · " : "";
            if (Has("不可用", "失败")) return prefix + "读取失败，请刷新组回放；仍失败时查看系统日志。";
            if (Has("未保存三维")) return prefix + "本轮没有三维图像，可查看左侧二维图像或选择其他轮次。";
            if (Has("已加载采集时保存")) return prefix + "已加载三维图像。";
        }

        if (context == "Contact")
        {
            if (Has("已关闭")) return "接触检查已关闭。";
            if (Has("系统级", "system-level")) return "信号整体异常，请检查激励连接和电极接触。";
            var red = Regex.Matches(text, @"(?:dark|red)=\[([\d, ]+)\]")
                .SelectMany(match => match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Distinct().ToArray();
            if (red.Length > 0) return $"请检查电极 {string.Join("、", red)} 的接触。";
            var yellow = Regex.Match(text, @"yellow=\[([\d, ]+)\]");
            if (yellow.Success) return $"电极 {yellow.Groups[1].Value} 接触不稳定，请检查。";
            if (Regex.IsMatch(text, @"uncertain=[1-9]\d*")) return "接触状态尚不确定，请观察信号并检查电极。";
            if (Has("red=[]")) return Has("无 qc_ref") ? "正在检查接触，请稍候。" : "电极接触正常。";
            return "接触状态等待更新。";
        }

        if (context == "ReferenceButton")
        {
            if (Has("开始采集后")) return "请先开始采集";
            if (Has("正在建立")) return "正在建立参考…";
            if (Has("正式参考已启用")) return "参考已启用";
            if (Has("准备新参考")) return "准备新参考";
            if (Has("建立正式参考")) return "建立参考并开始曲线";
            var progress = Regex.Match(text, @"\d+/\d+").Value;
            return progress.Length > 0 ? $"准备参考 {progress}" : "准备参考";
        }

        // Explicit operator actions take precedence over generic failure handling.
        if (Has("数据目录尚未准备完成")) return "数据目录尚未准备完成，请稍候再操作。";
        if (Has("后端路线") && Has("请先选择", "尚未选择", "未选择")) return "尚未选择成像服务。请在“设备设置”中完成成像配置，再开始采集。";
        if (Has("不能为负数")) return text.Split('；', '\n')[0] + "。请输入非负数。";
        if (Has("请手动停止激励")) return Say("设备未确认停止。请手动停止激励，再检查设备连接。");
        if (failure && Has("全部停止")) return "全部停止尚未完成。请手动停止激励，再检查设备连接。";
        if (Has("磁盘已满", "disk full")) return Say("保存失败：磁盘已满。请释放数据盘空间后重新开始采集。");
        if (Has("空间不足")) return Say("保存失败：磁盘空间不足。请释放数据盘空间后重试。");
        if (failure && Has("保存", "写入", "持久化")) return Say("数据保存失败。请停止采集，检查数据盘空间和写入权限。");
        if (Has("失效参考", "参考已失效", "已失效并暂停"))
            return Has("已准备", "请确认切换") ? Say("新参考已准备。请点击“确认切换”。")
                : Has("正在采集新参考") ? Say("图像已暂停，正在准备新参考，请稍候。")
                : Say("参考已失效，图像已暂停。请检查电极接触，再点击“准备重锁”。");
        if (failure && Has("离线")) return Say("离线重构未完成。请打开“系统日志”，导出现场快照以便排查。");
        if (failure && Has("回放", "实验记录")) return Say("回放加载失败。请重新选择实验；仍失败时导出现场快照。");
        if (failure && Has("采集", "USB2070", "DDS")) return Say("采集操作失败。请检查设备电源和连接，再重试。");
        if (failure && Has("后端", "PyEIDORS", "WSL", "重构")) return Say("成像服务未就绪。请稍后重试；仍失败时导出现场快照。");
        if (failure) return Say("操作未完成。请打开“系统日志”，导出现场快照以便排查。");

        if (Has("归档完成")) return "归档完成。";
        var csvCompleted = Regex.Match(text, @"批量 CSV 导出完成：\d+ 个文件");
        if (csvCompleted.Success) return csvCompleted.Value + "。";
        if (Has("已保存 HDF5")) return Say("采集数据已保存。");

        if (context == "Lane") return Has("离线", "offline") ? "离线回放" : Has("实时", "live") ? "实时回放" : "请选择回放方式";
        if (context == "Run")
        {
            var parts = text.Split('·', StringSplitOptions.TrimEntries);
            return string.Join(" · ", parts.Where((part, index) => index < 2 || part.StartsWith("回放帧", StringComparison.Ordinal)));
        }
        if (context is "Frame" or "Load")
        {
            var position = Regex.Match(text, @"帧\s*(\d+)/(\d+)");
            var prefix = position.Success ? $"第 {position.Groups[1].Value}/{position.Groups[2].Value} 帧 · " : string.Empty;
            if (Has("excluded-no-reference", "正式参考前", "无正式参考")) return prefix + "参考尚未建立，请选择后面的帧。";
            if (Has("temporal-edge", "时序边缘")) return prefix + "该位置前后数据不足，请选择相邻帧。";
            if (Has("中性帧", "outcome neutral")) return prefix + "此位置为基准记录。";
            if (Has("该帧无重构", "无重构结果", "excluded")) return prefix + "此位置没有图像，请选择其他帧。";
            if (Has("无已发布", "没有可回放", "无图像帧")) return "暂无可回放图像。请先生成离线回放。";
            if (Has("正在加载", "正在读取")) return "正在加载回放，请稍候。";
            if (Has("等待选择")) return "请选择一条实验记录。";
            if (position.Success) return prefix + (context == "Load" ? "可拖动滑块或点击回放。" : "已加载图像。");
        }
        if (context == "Reference" || Has("参考", "重锁"))
        {
            if (Has("统一确认", "同步参考已准备")) return "各设备的新参考已准备。请点击“统一确认”。";
            if (Has("新参考已准备", "新参考已就绪")) return "新参考已准备。请点击“确认切换”。";
            if (Has("自动参考就绪")) return "参考数据已备齐，可以点击下方按钮建立参考。";
            if (Has("重锁新参考就绪")) return "新参考数据已备齐，可以点击下方按钮准备切换。";
            if (Has("已确认")) return "正在切换参考，请稍候。";
            if (Has("后台准备中", "重锁新参考采集中")) return "正在准备新参考，请稍候。";
            if (Has("已启用", "已生效", "原子切换", "已确认切换" ) && !Has("预览", "临时")) return Say("参考已启用，可以观察图像和曲线。");
            if (Has("未启动", "当前参考持续")) return "需要更换参考时，请点击“准备重锁”。";
            if (Has("预览", "临时", "准备中", "预热", "正在准备")) return "正在准备参考，请稍候；下方按钮可用后也可手动建立。";
            if (context == "Reference" && Has("自动参考", "累计")) return "采集后，按钮可用时即可建立参考。";
        }
        if (Has("仍在记录") || (Has("回放") && Has("停止采集"))) return "请先停止采集，等待保存完成后再回放。";
        if (Has("已按批保存", "已保存并登记")) return Say("采集数据已保存。");
        if (Has("离线", "回放") && Has("尚未", "没有可用")) return "暂无离线结果。请先生成离线回放。";
        if (Has("首次编译", "预热", "正在启动后端")) return "正在准备成像，请稍候，无需重复启动。";
        if (Has("重构") && Has("连续运行")) return "正在成像。";
        if (Has("已自动恢复")) return Say("信号已恢复，可以继续采集。");
        if (Has("catalog", "HDF5", "raw", "block", "revision", "Kalman", "epoch", "all-one", "PyEIDORS", "skew", "Q=", "k=", "策略", "置信", "=", "\\"))
        {
            if (Has("完成", "已保存")) return Say("操作已完成。");
            if (Has("等待", "准备", "未就绪")) return Say("正在准备，请稍候。");
            return Say("详细记录请在“系统日志”中查看。");
        }
        return text;
    }
}
