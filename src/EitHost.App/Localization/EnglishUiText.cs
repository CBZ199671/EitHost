using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace EitHost.App.Localization;

internal static partial class EnglishUiText
{
    private const string UnknownEnglishDetail = "[untranslated]";

    private static readonly FrozenDictionary<string, string> Exact =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EIT 工作站"] = "EIT Workstation",
            ["文件(F)"] = "File (F)",
            ["刷新 catalog"] = "Refresh catalog",
            ["导出现场快照"] = "Export field snapshot",
            ["退出"] = "Exit",
            ["视图(V)"] = "View (V)",
            ["实测工作台"] = "Live Workbench",
            ["仪表盘"] = "Dashboard",
            ["设备配对"] = "Device Pairing",
            ["控制"] = "Control",
            ["数据库"] = "Database",
            ["系统日志"] = "System Log",
            ["工具(T)"] = "Tools (T)",
            ["扫描新增设备"] = "Scan for new devices",
            ["生成硬件报告"] = "Generate hardware report",
            ["安装/修复驱动"] = "Install / repair driver",
            ["语言(L)"] = "Language (L)",
            ["简体中文"] = "Simplified Chinese",
            ["水桶实验"] = "Water-tank Experiment",
            ["向日葵茎秆"] = "Sunflower Stem",
            ["快速 · 1 帧 / 至少 1 帧"] = "Fast · 1 frame / at least 1 frame",
            ["平衡 · 2 帧 / 至少 2 帧"] = "Balanced · 2 frames / at least 2 frames",
            ["稳定（推荐）· 3 帧 / 至少 3 帧"] = "Stable (Recommended) · 3 frames / at least 3 frames",
            ["容错 · 3 帧 / 至少 2 帧"] = "Fault-tolerant · 3 frames / at least 2 frames",
            ["自动（安全 NOSER 图像域）"] = "Auto (Safe NOSER Image Domain)",
            ["测量域动态（实验）"] = "Measurement-domain Dynamic (Experimental)",
            ["快速图像域"] = "Fast Image Domain",
            ["完整记录：raw + 解调 + 重构"] = "Full Record: raw + demodulation + reconstruction",
            ["仅预览：不入库、不自动保存"] = "Preview Only: no database write or automatic save",
            ["目标 - 参考"] = "Target - Reference",
            ["参考 - 目标"] = "Reference - Target",
            ["保留物理尺度（推荐）"] = "Preserve Physical Scale (Recommended)",
            ["公共尺度归一化（会移除全局 α）"] = "Common-scale Normalization (Removes Global α)",
            ["解调"] = "Demodulated",
            ["参考帧"] = "Reference Frame",
            ["目标帧"] = "Target Frame",
            ["实部 + 虚部（默认）"] = "Real + Imaginary (Default)",
            ["幅值 + 相位（专家）"] = "Magnitude + Phase (Expert)",
            ["方形"] = "Square",
            ["圆形"] = "Circle",
            ["自定义 ROI"] = "Custom ROI",
            ["固定 D/10"] = "Fixed D/10",
            ["动态变化"] = "Dynamic Change",
            ["到达时间"] = "Arrival Time",
            ["中"] = "Medium",
            ["小"] = "Small",
            ["大"] = "Large",
            ["自定义"] = "Custom",
            ["请选择后端路线"] = "Select a Backend Route",
            ["选择日期"] = "Select a date",
            ["状态"] = "Status",
            ["完成"] = "Completed",
            ["完整"] = "Complete",
            ["不完整"] = "Incomplete",
            ["中断"] = "Interrupted",
            ["记录中"] = "Recording",
            ["就绪"] = "Ready",
            ["待处理"] = "Pending",
            ["参考前"] = "Pre-reference",
            ["参考前不适用"] = "Not applicable before reference",
            ["未请求"] = "Not requested",
            ["已归档"] = "Archived",
            ["旧版只读记录"] = "Legacy read-only record",
            ["统一实验"] = "Unified experiment",
            ["回放帧"] = "Replay frames",
            ["无可靠实验级回放关联"] = "No reliable experiment-level replay association",
            ["旧关联"] = "legacy links",
            ["规范 HDF5 回放就绪"] = "Canonical HDF5 Replay Ready",
            ["尚无可回放解调块；原始数据仍可检查或离线补算"] = "No replayable demodulation blocks yet; raw data can still be inspected or processed offline",
            ["旧库只读回放"] = "Legacy database read-only replay",
            ["无回放帧；原始数据仍可检查"] = "No replay frames; raw data can still be inspected",
            ["位置"] = "Location",
            ["位置："] = "Location: ",
            ["旧版帧库（位置未知）"] = "Legacy frame store (location unknown)",
            ["raw + 回放（仅按相同旧 ID 合并）"] = "raw + replay (joined only by the same legacy ID)",
            ["中心"] = "Center",
            ["中心圆盘"] = "Center Disk",
            ["固定 ROI"] = "Fixed ROI",
            ["帮助(H)"] = "Help (H)",
            ["点击展开/收起"] = "Click to expand / collapse",
            ["目标套"] = "Target set",
            ["选择单套目标设备，「本套」按钮对其生效"] = "Select one target set; the This Set buttons apply to it",
            ["启动本套"] = "Start This Set",
            ["停止本套"] = "Stop This Set",
            ["全部启动"] = "Start All",
            ["全部停止"] = "Stop All",
            ["启动上方所选目标套的实时采集 + 成像"] = "Start realtime acquisition and imaging for the selected set",
            ["停止上方所选目标套的实时成像"] = "Stop realtime imaging for the selected set",
            ["对全部已绑定套同时启动采集 + 成像"] = "Start acquisition and imaging for all bound sets",
            ["急停所有设备的激励与采集"] = "Emergency-stop excitation and acquisition on every device",
            ["实测"] = "Live",
            ["模式  实测"] = "Mode  Live",
            ["当前会话"] = "Current Session",
            ["实验会话"] = "Experiment Session",
            ["已绑定"] = "Bound",
            ["待配对 USB2070"] = "USB2070 Awaiting Pairing",
            ["每套 = 1× USB2070 + 1× DDS 串口"] = "Each set = 1× USB2070 + 1× DDS serial port",
            ["设备状态"] = "Device Status",
            ["采集"] = "Acquisition",
            ["操作流程"] = "Workflow",
            ["配对工作流"] = "Pairing Workflow",
            ["数据目录"] = "Data Directory",
            ["Catalog 路径"] = "Catalog Path",
            ["手动配对向导"] = "Manual Pairing Wizard",
            ["设备配对流程"] = "Device Pairing Workflow",
            ["启动后先记录基线，逐套插入硬件，扫描新增候选后绑定标签与 USB2070 编号。"] = "After startup, record a baseline first. Insert one hardware set at a time, scan for new candidates, then bind its label and USB2070 number.",
            ["记录基线"] = "Record Baseline",
            ["新增 USB2070"] = "New USB2070",
            ["新增 DDS 串口"] = "New DDS Serial Port",
            ["标签"] = "Label",
            ["USB2070 编号"] = "USB2070 Number",
            ["绑定"] = "Bind",
            ["本次会话已绑定"] = "Bound in This Session",
            ["当前设备状态"] = "Current Device Status",
            ["未显示旧配对；每次重新打开软件都需要重新手动绑定。"] = "Previous pairings are not shown. Devices must be paired manually each time the application is reopened.",
            ["当前操作目标"] = "Current Target",
            ["单套控制选定一套已绑定设备后生效"] = "Single-set controls apply after selecting one bound set",
            ["DDS 串口激励板"] = "DDS Serial Excitation Board",
            ["DDS 单独控制"] = "DDS Controls",
            ["频率 Hz"] = "Frequency (Hz)",
            ["DAC 通道"] = "DAC Channel",
            ["电流"] = "Current",
            ["相位 deg"] = "Phase (deg)",
            ["PGA 增益"] = "PGA Gain",
            ["激励模式"] = "Excitation Mode",
            ["周期数"] = "Cycles",
            ["前丢弃"] = "Leading Discard",
            ["后丢弃"] = "Trailing Discard",
            ["扫描圈数（0=连续）"] = "Scan Cycles (0 = continuous)",
            ["0 表示连续扫描；大于 0 需要固件 v1.4+ 扫描状态能力。"] = "0 means continuous scanning. Values above 0 require firmware v1.4+ scan-status support.",
            ["实时分块"] = "Realtime Blocks",
            ["接触阈值介质配置"] = "Contact-threshold Medium Profile",
            ["固件构建号 / Git 哈希"] = "Firmware Build / Git Hash",
            ["必须对应当前实际烧录固件；留空时禁用自适应阈值配置匹配。"] = "Must identify the firmware currently flashed on the device. Leave blank to disable adaptive-threshold profile matching.",
            ["我已确认 16 个电极全部连接，允许本次生成健康阈值配置"] = "I confirm that all 16 electrodes are connected and allow a healthy-threshold profile to be generated",
            ["E15 等任一电极已断开时禁止勾选，否则会把故障学习成正常。"] = "Do not select this if any electrode, including E15, is disconnected; otherwise the fault will be learned as normal.",
            ["设置 DAC"] = "Set DAC",
            ["停止 DAC"] = "Stop DAC",
            ["扫描运行时请先停止激励，再停止 DAC。"] = "While scanning, stop excitation before stopping the DAC.",
            ["设置 PGA"] = "Set PGA",
            ["启动激励"] = "Start Excitation",
            ["停止激励"] = "Stop Excitation",
            ["USB2070 采集卡"] = "USB2070 Acquisition Card",
            ["采集单独控制"] = "Acquisition Controls",
            ["USB2070 设备号"] = "USB2070 Device Number",
            ["采样率 Hz"] = "Sample Rate (Hz)",
            ["量程"] = "Range",
            ["触发模式"] = "Trigger Mode",
            ["触发源"] = "Trigger Source",
            ["读取行数"] = "Rows to Read",
            ["延迟"] = "Delay",
            ["长度"] = "Length",
            ["电平"] = "Level",
            ["启动采集"] = "Start Acquisition",
            ["内存快照"] = "Memory Snapshot",
            ["快照全部"] = "Snapshot All",
            ["保存 HDF5"] = "Save HDF5",
            ["保存全部"] = "Save All",
            ["停止采集"] = "Stop Acquisition",
            ["多套同步协调器"] = "Multi-set Synchronization Coordinator",
            ["多套同步启动"] = "Synchronized Multi-set Start",
            ["沿用上方各套的激励/采集参数。同步启动让所有已绑定套同时启动激励与采集；全部停止可随时急停所有套。需 ≥2 套设备；下位机未必返回 ACK，软件会记录发送的数据包 HEX 和状态。"] = "Uses the excitation and acquisition settings above for each set. Synchronized Start starts excitation and acquisition on all bound sets; Stop All can emergency-stop every set. At least two sets are required. The controller may not return an ACK; sent packet HEX and status are logged.",
            ["同步启动"] = "Synchronized Start",
            ["一"] = "1",
            ["二"] = "2",
            ["三"] = "3",
            ["连接"] = "Connection",
            ["选择传输方式并验证设备"] = "Select a transport and verify the device",
            ["传输方式"] = "Transport",
            ["串口 Serial"] = "Serial Port",
            ["端口"] = "Port",
            ["未绑定"] = "Not bound",
            ["手动绑定设备"] = "Bind Devices Manually",
            ["打开软件后先记录基线；每插入一套硬件，扫描新增候选后绑定标签与 USB2070 编号。"] = "Record a baseline after opening the application. Insert one hardware set at a time, scan for new candidates, then bind its label and USB2070 number.",
            ["扫描新增"] = "Scan for New Devices",
            ["扫描 SDK"] = "Scan SDK",
            ["新增 DDS"] = "New DDS",
            ["USB号"] = "USB Number",
            ["设置"] = "Settings",
            ["激励 · 采集 · 保存 / 实时成像"] = "Excitation · Acquisition · Storage / Realtime Imaging",
            ["设备套"] = "Device Set",
            ["切换步骤二参数编辑的设备套"] = "Select the device set whose Step 2 settings will be edited",
            ["激励 · DDS"] = "Excitation · DDS",
            ["模式"] = "Mode",
            ["频率"] = "Frequency",
            ["相位"] = "Phase",
            ["周期"] = "Cycles",
            ["频分锁相"] = "Frequency-division Lock-in",
            ["实验模式：勾选后解调时同时投影其他设备的激励频率。"] = "Experimental mode: also project the excitation frequencies of other devices during demodulation.",
            ["异常值处理"] = "Outlier Processing",
            ["异常值检测"] = "Outlier Detection",
            ["异常值补偿"] = "Outlier Compensation",
            ["时序去毛刺"] = "Temporal Despiking",
            ["动态 Kalman"] = "Dynamic Kalman",
            ["动态模式"] = "Dynamic Mode",
            ["勾选后启用实时异常值/电极接触诊断，并在异常恢复时提示重锁参考。"] = "Enable realtime outlier and electrode-contact diagnostics and prompt for reference relock after recovery.",
            ["勾选后启用异常值权重补偿，并生成模板显示补偿曲线和入库标记。"] = "Enable outlier weight compensation and generate compensated display curves and persistence markers.",
            ["使用5个连续高质量块判断孤立尖峰；显示固定落后2块，持续阶跃和多帧响应保留。"] = "Use five consecutive high-quality blocks to identify isolated spikes. Display latency is fixed at two blocks; sustained steps and multi-frame responses are preserved.",
            ["在持久 PyEidors worker 内执行 lag=0 动态重构；总显示延迟仍为2块。"] = "Run lag=0 dynamic reconstruction in the persistent PyEidors worker. Total display latency remains two blocks.",
            ["自动模式优先使用缓存 Jacobian 的测量域动态重构，模型不可用或超预算时退回快速图像域。"] = "Auto mode prefers measurement-domain dynamic reconstruction with a cached Jacobian and falls back to the fast image-domain route when the model is unavailable or over budget.",
            ["采集 · USB2070"] = "Acquisition · USB2070",
            ["采样率"] = "Sample Rate",
            ["逆问题 · PyEIDORS"] = "Inverse Problem · PyEIDORS",
            ["算法"] = "Algorithm",
            ["差分方向"] = "Difference Direction",
            ["自定义 lambda"] = "Custom lambda",
            ["勾选后可手动输入正则化 lambda，否则按算法自动取值。"] = "Select to enter the regularization lambda manually; otherwise it is selected automatically for the algorithm.",
            ["后端路线"] = "Backend Route",
            ["PyEIDORS 后端路径"] = "PyEIDORS Backend Path",
            ["选择…"] = "Browse…",
            ["选择 WSL2 中的 PyEIDORS 仓库目录"] = "Select the PyEIDORS repository directory in WSL2",
            ["参考尺度策略"] = "Reference Scale Policy",
            ["保留物理尺度会保留对象真实的全局慢变；公共尺度归一化仅适用于已确认公共幅值漂移属于仪器因素的实验。"] = "Preserving physical scale retains genuine global slow changes. Common-scale normalization should be used only when common amplitude drift is known to be instrumental.",
            ["采集 + 实时成像"] = "Acquisition + Realtime Imaging",
            ["单套控制"] = "Single-set Controls",
            ["目标设备"] = "Target Device",
            ["对上方选定的单套设备启动实时采集 + 成像"] = "Start realtime acquisition and imaging for the selected device set",
            ["停止上方选定单套的实时成像"] = "Stop realtime imaging for the selected device set",
            ["全部控制"] = "All-set Controls",
            ["忽略上方选择，对全部已绑定套同时操作。"] = "Ignore the selection above and operate all bound sets together.",
            ["保存"] = "Storage",
            ["存储模式"] = "Storage Mode",
            ["完整记录会保存连续原始采集、解调状态与重构结果；不需要任何持久化时请选择“仅预览”。"] = "Full recording stores continuous raw acquisition, demodulation state, and reconstruction results. Select Preview Only when no persistence is required.",
            ["仪表盘 · 会话摘要"] = "Dashboard · Session Summary",
            ["录制"] = "Recording",
            ["传输"] = "Transport",
            ["布局"] = "Layout",
            ["激励"] = "Excitation",
            ["显示设备"] = "Display Device",
            ["原始边界电压 CH1"] = "Raw Boundary Voltage CH1",
            ["默认显示 EIDORS 对齐的实部/虚部；专家模式显示幅值/相位"] = "Default: EIDORS-aligned real/imaginary components. Expert mode: magnitude/phase",
            ["边界电压拟合"] = "Boundary-voltage Fit",
            ["重构图像"] = "Reconstructed Image",
            ["启用双平面 2.5D 插值显示"] = "Enable Dual-plane 2.5D Interpolation",
            ["双平面 2.5D"] = "Dual-plane 2.5D",
            ["两套设备分别完成二维反演后沿 z 线性插值；不填造跨层观测，不是真实 3D CEM 反演。"] = "Linearly interpolate along z after independent 2D inversions for two device sets. No cross-plane observations are fabricated; this is not a true 3D CEM inverse.",
            ["下层设备"] = "Lower-plane Device",
            ["上层设备"] = "Upper-plane Device",
            ["显示层数"] = "Display Layers",
            ["允许 2–9 层；默认 5 层。"] = "Allows 2–9 layers; default: 5.",
            ["归一化高度"] = "Normalized Height",
            ["相对模型半径的显示高度，不代表毫米。"] = "Display height relative to the model radius; this is not a millimetre value.",
            ["配对容差 ms"] = "Pairing Tolerance (ms)",
            ["两层采集时间差超过此值时拒绝合成。"] = "Reject composition when the acquisition-time difference exceeds this value.",
            ["2.5D：未启用。"] = "2.5D: Disabled.",
            ["显示层由两套独立二维重建沿 z 线性插值；不是真实 3D CEM 反演。"] = "Display layers are linearly interpolated along z from two independent 2D reconstructions; this is not a true 3D CEM inverse.",
            ["显示层由两套独立二维重建沿 z 线性插值；不填造跨层观测，不是真实 3D CEM 反演。"] = "Display layers are linearly interpolated along z from two independent 2D reconstructions. No cross-plane observations are fabricated; this is not a true 3D CEM inverse.",
            ["显示参数超出范围"] = "display parameter out of range",
            ["上下层选择无效"] = "invalid lower/upper selection",
            ["网格规模溢出"] = "mesh size overflow",
            ["两层输入不匹配"] = "the two plane inputs do not match",
            ["请选择下层和上层设备"] = "Select lower- and upper-plane devices",
            ["下层和上层必须选择不同设备"] = "Lower- and upper-plane devices must be different",
            ["等待有效二维重建帧"] = "Waiting for a valid 2D reconstruction frame",
            ["等待同步帧"] = "Waiting for synchronized frames",
            ["相对单位"] = "relative units",
            ["显示插值，非真实 3D CEM 反演"] = "display interpolation, not a true 3D CEM inverse",
            ["未生成插值体；两套二维重建结果保持不变。"] = "No interpolated volume was generated; both 2D reconstruction results remain unchanged.",
            ["设备"] = "Device",
            ["参考帧失效"] = "Reference Invalid",
            ["请重锁参考"] = "Please Relock the Reference",
            ["低置信度重构"] = "Low-confidence Reconstruction",
            ["图像仅供参考"] = "Image for Reference Only",
            ["已录制的帧"] = "Recorded Frames",
            ["自动稳定锁定；100 个高质量帧后可由用户建立正常参考。"] = "Automatic stability lock; a normal reference can be created after 100 high-quality frames.",
            ["慢变样本无需等待稳定阈值；用户参考不会被标为低置信，也不会禁止 ROI。"] = "Slowly changing samples do not need to meet the stability threshold. A user reference is not marked low-confidence and does not disable ROI.",
            ["ROI 分析"] = "ROI Analysis",
            ["形状"] = "Shape",
            ["尺寸"] = "Size",
            ["极图"] = "Polar Map",
            ["周向环"] = "Circumferential Ring",
            ["局部曲线"] = "Local Curves",
            ["径向热图"] = "Radial Heatmap",
            ["周向热图"] = "Circumferential Heatmap",
            ["蓝=负变化 · 白=基线 · 橙=正变化 · 固定 z∈[-5,+5]"] = "Blue = negative · White = baseline · Orange = positive · Fixed z∈[-5,+5]",
            ["清空曲线"] = "Clear Curves",
            ["导出 CSV"] = "Export CSV",
            ["手动开始成像与 ROI"] = "Start Imaging and ROI Manually",
            ["主按钮会自动冻结点击时刻，综合点击前当前同一工况连续段的全部质量合格帧；100 帧只是最低门槛。无需等待对象静止或通过稳定阈值，稳健统计会剔除离群帧，随后按正常置信度成像并统计 ROI。"] = "The primary action freezes the click time and combines all qualified frames in the current continuous operating segment before that time. 100 frames is only the minimum. There is no need to wait for the object to become stationary or meet a stability threshold; robust statistics reject outliers before normal-confidence imaging and ROI analysis.",
            ["按钮始终可见；点击后自动汇总操作前最近同工况连续段的全部高质量帧。100 帧只是最低门槛，无需等待稳定阈值。"] = "The button remains visible. Clicking it automatically combines all recent high-quality frames in the same continuous operating segment. 100 frames is only the minimum; no stability wait is required.",
            ["高级：选择历史参考区间"] = "Advanced: Select a Historical Reference Interval",
            ["仅在需要复现实验时使用。这里最多显示 8 个不重叠代表区间；主按钮始终使用点击前自动汇总结果。"] = "Use only when reproducing an experiment. Up to eight non-overlapping representative intervals are shown; the primary action always uses the automatically aggregated pre-click result.",
            ["高级历史复现：选择固定 100 帧代表区间，不影响主按钮的自动汇总逻辑。"] = "Advanced historical reproduction: select a fixed 100-frame representative interval without changing the primary action's automatic aggregation.",
            ["使用所选历史区间（高级）"] = "Use Selected Historical Interval (Advanced)",
            ["确认切换"] = "Confirm Switch",
            ["在下一有效目标边界原子切换到已准备的新参考"] = "Atomically switch to the prepared reference at the next valid target boundary",
            ["取消重锁"] = "Cancel Relock",
            ["放弃替换参考；原参考和 ROI 保持不变"] = "Abandon the replacement; keep the current reference and ROI unchanged",
            ["多集合共同参考时刻"] = "Shared Reference Time for Multiple Sets",
            ["同步准备"] = "Prepare Together",
            ["以一次共同操作时间，为所有运行集合选择此前最近的完整高质量窗口"] = "Use one shared action time to select the nearest preceding complete high-quality window for every running set",
            ["统一确认"] = "Confirm All",
            ["所有集合均准备成功后，统一确认其在各自下一有效块切换"] = "After every set is prepared, confirm switching each one at its next valid block",
            ["全部取消"] = "Cancel All",
            ["在任何集合切换前，取消整个共同 action"] = "Cancel the shared action before any set switches",
            ["准备重锁"] = "Relock",
            ["后台准备替换参考；当前参考、成像和 ROI 不会中断"] = "Prepare a replacement reference in the background without interrupting the current reference, imaging, or ROI",
            ["导出设备标定"] = "Device Cal.",
            ["导出会话标定"] = "Session Cal.",
            ["导出当前设备级接触标定文件"] = "Export the device-level contact calibration file",
            ["导出当前会话的接触标定文件"] = "Export the contact calibration file for the current session",
            ["实验数据与回放"] = "Experiment Data and Replay",
            ["刷新"] = "Refresh",
            ["一条实验统一显示 raw、解调、重构及回放覆盖；选择后自动关联离线工具和下方回放。旧数据只读显示且不会伪造关联。"] = "One experiment row combines raw, demodulation, reconstruction, and replay coverage. Selecting it links the offline tools and replay below. Legacy data is read-only and never receives fabricated links.",
            ["统一数据根目录"] = "Unified Data Root",
            ["刷新容量"] = "Refresh Capacity",
            ["打开数据目录"] = "Open Data Directory",
            ["打开所选目录"] = "Open Selected Directory",
            ["归档所选"] = "Archive Selected",
            ["移动到 DataRoot/archives 并事务更新 catalog；仍可回放，不释放磁盘空间"] = "Move to DataRoot/archives and update the catalog transactionally. Replay remains available and disk space is not released.",
            ["归档超期"] = "Archive Expired",
            ["确认后批量归档超过 90 天的终态实验；仍可回放，不会自动删除"] = "After confirmation, archive terminal experiments older than 90 days in a batch. Replay remains available and nothing is deleted automatically.",
            ["永久删除"] = "Delete Permanently",
            ["仅终态统一实验可删除；确认后删除 raw、派生、导出和 catalog 记录"] = "Only terminal unified experiments can be deleted. Confirmation deletes raw, derived, export, and catalog records.",
            ["取消归档"] = "Cancel Archive",
            ["按日期"] = "By Date",
            ["清除"] = "Clear",
            ["补齐所选"] = "Catch Up Selected",
            ["按处理账本补齐缺失解调与参考锁定后的待重构块；重复执行不会重复写入"] = "Use the processing ledger to fill missing demodulation and post-reference reconstruction blocks. Repeated runs do not duplicate data.",
            ["停止追赶"] = "Stop Catch-up",
            ["在当前块结束后停止；已完成的块保持不变，未处理部分仍标记为待处理"] = "Stop after the current block. Completed blocks remain unchanged and unprocessed blocks remain pending.",
            ["离线处理"] = "Offline Processing",
            ["离线解调"] = "Offline Demodulation",
            ["输入 HDF5"] = "Input HDF5",
            ["浏览…"] = "Browse…",
            ["统一派生输出"] = "Unified Derived Output",
            ["输出由实验 ID 自动归入 derived/ 并登记处理账本；旧版文件须先导入。"] = "Output is placed in derived/ by experiment ID and registered in the processing ledger. Legacy files must be imported first.",
            ["执行解调"] = "Run Demodulation",
            ["批量解调"] = "Batch Demodulation",
            ["完整性检查"] = "Integrity Check",
            ["HDF5 文件检查"] = "HDF5 File Inspection",
            ["检查 HDF5"] = "Inspect HDF5",
            ["导出"] = "Export",
            ["HDF5 导出 CSV"] = "HDF5 to CSV Export",
            ["源 HDF5"] = "Source HDF5",
            ["统一导出位置"] = "Unified Export Location",
            ["CSV 自动归入同一实验的 exports/ 并记录来源 HDF5、Dataset 与筛选条件。"] = "CSV output is placed automatically in the experiment's exports/ directory and records the source HDF5, dataset, and filters.",
            ["批量 raw CSV"] = "Batch Raw CSV",
            ["所选实验 · 派生成像"] = "Selected Experiment · Derived Imaging",
            ["实验回放"] = "Experiment Replay",
            ["从上方实验列表选择同一条实验，拖动滑条或点击播放逐帧回看：左图为重构模型相对值图像（物理标定前不可视为 S/m），右图为该帧 208 点边界电压幅值。"] = "Select an experiment above, then drag the slider or press Play to review each frame. The left panel shows relative reconstructed-model values (not S/m before physical calibration); the right panel shows the frame's 208 boundary-voltage magnitudes.",
            ["计算 ROI"] = "Calculate ROI",
            ["命令 / 数据包 / 采集 / 导出"] = "Commands / Packets / Acquisition / Export",
            ["实时活动日志"] = "Realtime Activity Log",
            ["集中显示需要操作员关注的状态、警告与失败；各面板的高频明细不再重复插入。"] = "Shows statuses, warnings, and failures requiring operator attention. High-frequency panel details are not duplicated here.",
            ["现场证据导出"] = "Field Evidence Export",
            ["一键现场快照"] = "One-click Field Snapshot",
            ["一次性打包硬件报告、配对清单、T25 计划与证据索引，写入 outputs 目录。"] = "Package the hardware report, pairing manifest, T25 plan, and evidence index into the outputs directory in one operation.",
            ["尚未读取采集块。"] = "No acquisition block has been read yet.",
            ["尚未导出现场快照。"] = "No field snapshot has been exported yet.",
            ["尚未检查 HDF5。"] = "No HDF5 file has been inspected yet.",
            ["未开始实验"] = "No experiment started",
            ["ROI：选择成像记录后可离线计算。"] = "ROI: computed offline once an imaging record is selected.",
            ["回放状态：等待选择实验"] = "Replay status: waiting for an experiment selection",
            ["尚未执行离线解调。"] = "Offline demodulation has not run yet.",
            ["批量归档：未运行。"] = "Batch archiving: not running.",
            ["接触诊断：选择成像帧后显示。"] = "Contact diagnostics: shown once an imaging frame is selected.",
            ["播放"] = "Play",
            ["暂停"] = "Pause",
            ["显示日历"] = "Show calendar",
            ["选择成像记录后可逐帧回放。"] = "Select an imaging record to replay it frame by frame.",
            ["选择统一实验后显示占用与保留状态。"] = "Select a unified experiment to show its footprint and retention state.",
            ["EIDORS {ad} 有符号边界电压 · 蓝：实部 Re(V)；红：虚部 Im(V)。first=-I、next=+I，测量为 V(first)-V(next)；独立电流探头仅用于绝对阻抗相角溯源。"] = "EIDORS {ad} signed boundary voltage · blue: real part Re(V); red: imaginary part Im(V). first=-I, next=+I, measured as V(first)-V(next); the independent current probe only traces the absolute impedance phase.",
            ["专家极坐标 · 上半区蓝：幅值 |V|（V）；下半区红：相位 φ（°）。低幅值点不绘制相位；坐标与 EIDORS {ad} 有符号边界电压一致。"] = "Expert polar view · upper half blue: magnitude |V| (V); lower half red: phase φ (°). Phase is not drawn for low-magnitude points; the coordinates match the EIDORS {ad} signed boundary voltage.",
            ["ROI 就绪：否 · 等待参考与重构"] = "ROI ready: No · waiting for reference and reconstruction",
            ["参考模式：尚未锁定"] = "Reference mode: not locked yet",
            ["多频证据：单频模式，证据 E 未启用。"] = "Multi-frequency evidence: single-frequency mode, evidence E is disabled.",
            ["开始采集后可手动建立参考"] = "Start acquisition to create a reference manually",
            ["自动参考：累计 100 个质量合格帧后，主按钮将综合点击前最近同工况连续段的全部有效帧；无需等待对象静止。"] = "Automatic reference: once 100 quality-passed frames have accumulated, the primary action combines every valid frame of the latest continuous segment under the same operating condition before the click; there is no need to wait for the object to become stationary.",
            ["解调复数"] = "Demodulated Complex",
            ["解调极坐标"] = "Demodulated Polar",
            ["重构状态：等待开始"] = "Reconstruction status: waiting to start",
            ["重构质量：尚未开始"] = "Reconstruction quality: not started yet",
            ["重锁：未启动；当前参考持续用于成像与 ROI。"] = "Relock: not started; the current reference stays in use for imaging and ROI.",
            ["1. 打开软件并记录当前基线"] = "1. Open the software and record the current baseline",
            ["2. 插入一套 USB2070 + DDS 串口硬件"] = "2. Plug in one USB2070 + DDS serial hardware set",
            ["3. 扫描新增候选并手动绑定标签"] = "3. Scan for new candidates and bind the label manually",
            ["4. 重复插入与绑定下一套设备"] = "4. Repeat the plug-in and binding for the next set",
            ["5. 本次关闭后下次重新配对"] = "5. Pair again after the next restart",
            ["植物稳定优先起点：3125 Hz / 30 µA，解调前/后裁剪建议 3/2 周期。频率和电流会影响电极极化与稳定时间；请为不同对象保留独立参数档。"] = "Plant-stability starting point: 3125 Hz / 30 µA, with a suggested 3/2 cycle trim before and after demodulation. Frequency and current affect electrode polarisation and settling time, so keep a separate parameter profile per subject.",
            ["关 · 仅预览"] = "Off · Preview Only",
            ["开 · 完整记录"] = "On · Full Record",
            ["分块延迟：参数待修正"] = "Block latency: parameters need correction",
            ["手动有效裁剪：参数待修正"] = "Manual usable trim: parameters need correction",
            ["快速预览参考"] = "Fast preview reference",
            ["稳定（推荐）"] = "Stable (Recommended)",
            ["快速"] = "Fast",
            ["平衡"] = "Balanced",
            ["容错"] = "Fault-tolerant"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly (string Chinese, string English)[] Phrases =
        Exact
            .Where(pair => pair.Key.Length >= 2)
            .Select(pair => (Chinese: pair.Key, English: pair.Value))
            .Concat(
            [
                ("尚未启动实时成像", "Realtime imaging has not started"),
                ("持久化+内存", "Persisted + memory"),
                ("需 NVIDIA", "Requires NVIDIA"),
                ("需 AMGX", "Requires AMGX"),
                ("复值", "Complex-valued"),
                ("实值", "Real-valued"),
                ("尚未启动", "Not started"),
                ("尚未就绪", "Not ready"),
                ("等待采集数据", "Waiting for acquisition data"),
                ("等待解调数据", "Waiting for demodulated data"),
                ("等待边界电压", "Waiting for boundary voltage"),
                ("等待重构图像", "Waiting for reconstructed image"),
                ("等待参数", "Waiting for parameters"),
                ("等待参考", "Waiting for reference"),
                ("等待采集", "Waiting for acquisition"),
                ("等待成像", "Waiting for imaging"),
                ("等待", "Waiting"),
                ("尚无", "None yet"),
                ("尚未", "Not yet"),
                ("未启动", "Not started"),
                ("未就绪", "Not ready"),
                ("未选择", "Not selected"),
                ("未知", "Unknown"),
                ("空闲", "Idle"),
                ("断开", "Disconnected"),
                ("已连接", "Connected"),
                ("已停止", "Stopped"),
                ("已启动", "Started"),
                ("已完成", "Completed"),
                ("运行中", "Running"),
                ("扫描中", "Scanning"),
                ("准备中", "Preparing"),
                ("处理中", "Processing"),
                ("失败", "Failed"),
                ("成功", "Succeeded"),
                ("警告", "Warning"),
                ("错误", "Error"),
                ("重构状态", "Reconstruction status"),
                ("重构图像", "Reconstructed image"),
                ("重构", "Reconstruction"),
                ("解调状态", "Demodulation status"),
                ("解调数据", "Demodulated data"),
                ("解调", "Demodulation"),
                ("原始数据", "Raw data"),
                ("边界电压", "Boundary voltage"),
                ("参考模式", "Reference mode"),
                ("参考状态", "Reference status"),
                ("参考", "Reference"),
                ("数据质量", "Data quality"),
                ("接触诊断", "Contact diagnostics"),
                ("接触状态", "Contact status"),
                ("来源", "Source"),
                ("漂移", "Drift"),
                ("间隙", "Gaps"),
                ("饱和", "Saturation"),
                ("基线诊断", "Baseline diagnostics"),
                ("多频证据", "Multi-frequency evidence"),
                ("单频模式", "Single-frequency mode"),
                ("质量合格", "Quality passed"),
                ("质量", "Quality"),
                ("统一数据根目录已准备", "Unified data root ready"),
                ("目录占用待扫描", "Directory usage not scanned yet"),
                ("磁盘可用空间未知", "Disk free space unknown"),
                ("警告（可用空间低于 10 GiB）", "Warning (free space below 10 GiB)"),
                ("严重不足（低于 2 GiB 保底）", "Critical (below the 2 GiB floor)"),
                ("目录占用", "Directory usage"),
                ("磁盘可用", "Disk free"),
                ("保留策略", "Retention policy"),
                ("仅建议标记，不自动归档或删除", "advisory marking only, no automatic archiving or deletion"),
                ("归档后仍可回放", "archived runs remain replayable"),
                ("预计采集聚合", "estimated acquisition aggregation"),
                ("每驻留有效积分", "usable integration per dwell"),
                ("不含重构与界面刷新", "excludes reconstruction and UI refresh"),
                ("手动有效裁剪", "Manual usable trim"),
                ("有效积分", "usable integration"),
                ("无隐藏裁剪", "no hidden trim"),
                ("参数待修正", "parameters need correction"),
                ("分块延迟", "Block latency"),
                ("接受", "accepts"),
                ("周期", "cycles"),
                ("天", "days"),
                // The realtime footer prints both calibration states on one fixed-height
                // line, so the labelled form stays compact enough to read in English.
                ("设备标定：", "Device cal.: "),
                ("会话标定：", "Session cal.: "),
                ("设备标定", "Device calibration"),
                ("会话标定", "Session calibration"),
                ("准备重锁", "Prepare relock"),
                ("重锁", "Relock"),
                ("基线", "Baseline"),
                ("数据", "Data"),
                ("路径", "Path"),
                ("目录", "Directory"),
                ("文件", "File"),
                ("当前", "Current"),
                ("最近", "Latest"),
                ("自动", "Automatic"),
                ("手动", "Manual"),
                ("可用", "Available"),
                ("不可用", "Unavailable"),
                ("启用", "Enabled"),
                ("禁用", "Disabled"),
                ("正常", "Normal"),
                ("异常", "Abnormal"),
                ("无", "None"),
                ("套", "sets"),
                ("帧", "frames"),
                ("块", "blocks"),
                (" 层", " layers"),
                ("行", "rows"),
                ("条", "entries"),
                ("个", string.Empty)
            ])
            .OrderByDescending(pair => pair.Item1.Length)
            .ToArray();

    internal static bool ContainsChinese(string value)
    {
        return ChineseRunRegex().IsMatch(value);
    }

    internal static string Translate(string source)
    {
        if (string.IsNullOrEmpty(source) || !ContainsChinese(source))
        {
            return source;
        }

        if (Exact.TryGetValue(source, out var exact))
        {
            return exact;
        }

        var protectedPaths = new List<string>();
        var translatableSource = EmbeddedPathRegex().Replace(source, match =>
        {
            protectedPaths.Add(match.Value);
            return $"\uE000{protectedPaths.Count - 1}\uE001";
        });

        var translated = TotalExperimentCountRegex().Replace(translatableSource, "${count} experiments total");
        translated = FilteredExperimentCountRegex().Replace(translated, "${shown} / ${total} experiments");
        translated = SegmentCountRegex().Replace(
            translated,
            match => $"{match.Groups["count"].Value} "
                + (match.Groups["count"].Value == "1" ? "segment" : "segments"));
        translated = ExportCountRegex().Replace(translated, "Exports ${count}");
        translated = RingPositionRegex().Replace(translated, "Ring ${ring}/${total}");
        translated = SectorPositionRegex().Replace(translated, "Sector ${sector}/${total}");
        translated = ManualTrimRegex().Replace(
            translated,
            "leading ${lead} cycles / ${leadPoints} points, trailing ${trail} cycles / ${trailPoints} points");
        translated = RecoveredShardRegex().Replace(
            translated,
            "restored ${count} raw tail shards to their actual HDF5 extent");
        translated = RecoveredRunRegex().Replace(
            translated,
            "marked ${count} interrupted experiments as recovered");
        translated = DemodulationLabelRegex().Replace(translated, "Demodulation");
        translated = BoundSetCountRegex().Replace(translated, "${count} sets bound");
        translated = ExperimentCountRegex().Replace(translated, "Experiments  ${count}");
        translated = FailureCountRegex().Replace(translated, "Failures ${count}");
        translated = LatestCountRegex().Replace(translated, "Latest ${count}");
        translated = UnitCountRegex().Replace(translated, "${count} ${unit}");

        foreach (var (chinese, english) in Phrases)
        {
            translated = translated.Replace(chinese, english, StringComparison.Ordinal);
        }

        translated = translated
            .Replace("，", ", ", StringComparison.Ordinal)
            .Replace('。', '.')
            .Replace("；", "; ", StringComparison.Ordinal)
            .Replace("：", ": ", StringComparison.Ordinal)
            .Replace("（", " (", StringComparison.Ordinal)
            .Replace('）', ')')
            .Replace('「', '“')
            .Replace('」', '”');

        translated = ChineseRunRegex().Replace(translated, UnknownEnglishDetail);
        for (var index = 0; index < protectedPaths.Count; index++)
        {
            translated = translated.Replace(
                $"\uE000{index}\uE001",
                protectedPaths[index],
                StringComparison.Ordinal);
        }

        return translated;
    }

    [GeneratedRegex("[\\u3400-\\u4DBF\\u4E00-\\u9FFF\\uF900-\\uFAFF]+", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseRunRegex();

    [GeneratedRegex("已绑定\\s*(?<count>\\d+)\\s*套", RegexOptions.CultureInvariant)]
    private static partial Regex BoundSetCountRegex();

    [GeneratedRegex("实验\\s*(?<count>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ExperimentCountRegex();

    [GeneratedRegex("失败\\s*(?<count>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FailureCountRegex();

    [GeneratedRegex("最近\\s*(?<count>\\d+)\\s*条", RegexOptions.CultureInvariant)]
    private static partial Regex LatestCountRegex();

    [GeneratedRegex("(?<count>\\d+)\\s*(?<unit>套|帧|块|行|条|个|天)", RegexOptions.CultureInvariant)]
    private static partial Regex UnitCountRegex();

    [GeneratedRegex(
        "前\\s*(?<lead>[\\d.]+)\\s*周期\\s*/\\s*(?<leadPoints>\\d+)\\s*点，"
        + "后\\s*(?<trail>[\\d.]+)\\s*周期\\s*/\\s*(?<trailPoints>\\d+)\\s*点",
        RegexOptions.CultureInvariant)]
    private static partial Regex ManualTrimRegex();

    [GeneratedRegex("已按 HDF5 实际范围恢复\\s*(?<count>\\d+)\\s*个 raw 尾分片", RegexOptions.CultureInvariant)]
    private static partial Regex RecoveredShardRegex();

    [GeneratedRegex("已恢复标记\\s*(?<count>\\d+)\\s*条中断实验", RegexOptions.CultureInvariant)]
    private static partial Regex RecoveredRunRegex();

    [GeneratedRegex("共\\s*(?<count>\\d+)\\s*个实验", RegexOptions.CultureInvariant)]
    private static partial Regex TotalExperimentCountRegex();

    [GeneratedRegex("(?<shown>\\d+)\\s*/\\s*(?<total>\\d+)\\s*个实验", RegexOptions.CultureInvariant)]
    private static partial Regex FilteredExperimentCountRegex();

    [GeneratedRegex("(?<count>\\d+)\\s*段", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentCountRegex();

    [GeneratedRegex("导出\\s*(?<count>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ExportCountRegex();

    [GeneratedRegex("第\\s*(?<ring>\\d+)\\s*/\\s*(?<total>\\d+)\\s*环", RegexOptions.CultureInvariant)]
    private static partial Regex RingPositionRegex();

    [GeneratedRegex("扇区\\s*(?<sector>\\d+)\\s*/\\s*(?<total>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SectorPositionRegex();

    [GeneratedRegex("(?:[A-Za-z]:[\\\\/]|\\\\\\\\|(?<![A-Za-z0-9])/(?!\\s))[^\\r\\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedPathRegex();

    [GeneratedRegex("解调(?=\\s)", RegexOptions.CultureInvariant)]
    private static partial Regex DemodulationLabelRegex();
}
