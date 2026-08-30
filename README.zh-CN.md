# EitHost

<p align="right">
  <a href="README.md">English</a> | <strong>简体中文</strong>
</p>

<p align="center">
  <strong>面向多套电阻抗成像设备的 Windows 采集、数据管理与实时重构工作站。</strong>
</p>

<p align="center">
  <a href="https://github.com/CBZ199671/EitHost"><img alt="平台" src="https://img.shields.io/badge/platform-Windows%20x64-0078D4?logo=windows"></a>
  <a href="https://dotnet.microsoft.com/"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet"></a>
  <a href="https://github.com/CBZ199671/EitHost/blob/main/LICENSE"><img alt="许可证" src="https://img.shields.io/badge/license-MIT-green"></a>
  <a href="https://github.com/CBZ199671/PyEIDORS"><img alt="后端" src="https://img.shields.io/badge/backend-PyEIDORS-0B6FB8"></a>
</p>

EitHost 是面向多套电阻抗成像（EIT）设备的 Windows 桌面上位机。它负责 USB2070 数据采集、DDS 激励控制、设备配对、HDF5/SQLite 数据管理、实时解调与质量诊断；在具备兼容后端时，可通过 WSL2 对接 PyEIDORS 完成重构和可视化。

> **项目定位：** EitHost 是科研与工程实验软件，并非医疗器械。硬件控制、同步精度和重构结果应在目标设备与实验条件下独立验证。

> **后端公开状态：** EitHost 当前对接实验室使用的、尚未公开的 PyEIDORS v2 后端。我们承诺在相关论文撰写完成后开源 PyEIDORS v2。当前公开的 [PyEIDORS 仓库](https://github.com/CBZ199671/PyEIDORS)对应较早的公开版本，不包含本版 EitHost 所需的 worker。

## 主要能力

- **多套设备编排：** 每套设备由一块 USB2070 采集卡和一块 DDS 串口控制板组成，支持手动配对、单套控制与多套同步启动。
- **实时采集与解调：** 采集、解调、诊断、重构和界面渲染采用解耦流水线，降低慢任务对采集节拍的影响。
- **数据可追溯：** 原始数据和派生结果使用 HDF5，实验目录与处理状态使用 SQLite catalog 管理，并支持 CSV 导出与数据库回放。
- **PyEIDORS 集成层：** EitHost 包含可配置的 WSL2 持久 worker 桥接与 manifest/profile 路由；实验室当前使用的兼容 PyEIDORS v2 后端尚未公开。
- **可视化与分析：** 支持实时边界电压、重构图像、固定 ROI 时序分析，以及由两层独立二维重构插值得到的显示型 2.5D 视图。
- **现场运维：** 提供设备扫描、驱动预检、运行日志、证据导出和中英文界面。
- **上位机开箱运行：** 仓库包含 Windows x64 自包含发布版，无需另行安装 .NET Runtime；实时重构仍需兼容的 PyEIDORS 后端。

## 系统架构

```mermaid
flowchart LR
    H[USB2070 + DDS hardware] --> A[EitHost WPF]
    A --> C[EitHost.Core]
    C --> H5[(HDF5 data)]
    C --> DB[(SQLite catalog)]
    C --> B[WSL2 backend bridge]
    B --> P[兼容的 PyEIDORS 后端]
    P --> V[2D / display-only 2.5D visualization]
```

EitHost 负责 Windows 侧硬件、实验流程与数据生命周期；兼容的 PyEIDORS 后端负责有限元正问题和逆问题求解。两者通过明确的后端配置与数据协议连接，便于独立演进。

## PyEIDORS v2 公开状态与开源承诺

PyEIDORS v2 是下一代重大版本，包含大量新增能力、架构改进、扩展的求解流程，以及面向实时 EIT 系统的性能、稳定性和集成能力提升。

由于相关研究论文仍在撰写，PyEIDORS v2 的源代码、worker 实现、后端 manifest 和求解器 profile 目前尚未公开。当前公开的 [PyEIDORS 仓库](https://github.com/CBZ199671/PyEIDORS)对应较早的公开版本，不应视为已经实现当前 EitHost 重构流水线所需的 worker 接口。

**我们郑重承诺：在相关论文撰写完成后，将开源 PyEIDORS v2。** 计划公开的内容包括完整源代码、兼容 EitHost 的 worker 实现、后端 manifest 与 profile、安装文档、可复现实例以及相关验证材料。当前限制只是科研成果发布时序上的阶段性安排，并不意味着 PyEIDORS v2 将长期闭源。

| 组件 | 当前公开状态 |
|---|---|
| EitHost 的采集、设备管理、数据存储、解调、诊断、回放与可视化 | 已包含在本仓库中 |
| EitHost 的 Windows/WSL2 重构集成层 | 已包含在本仓库中 |
| 实验室当前使用的 PyEIDORS v2 重构后端 | 尚未公开；计划在论文撰写完成后发布 |
| 公开环境下完整的端到端实时重构流程 | 在兼容后端发布前暂时无法复现 |

## 快速开始

### 直接运行 Windows x64 发布版

1. 从硬件厂商提供的安装包安装 USB2070 Windows 驱动。
2. 克隆或下载本仓库。
3. 完整保留 `release/EitHost-Windows-x64` 目录，然后运行：

```powershell
.\release\EitHost-Windows-x64\EitHost.App.exe
```

不要只复制单个 `.exe`。程序需要同目录中的 `HDF.PInvoke.dll`、`HDF.PInvoke.dll.config`、`hdf5.dll`、`hdf5_hl.dll` 和 `USB2070.dll`。启动时会在打开硬件前真实执行一次 HDF5 创建、写入、关闭和删除探针。仓库不包含 USB2070 内核驱动安装包。

发布文件的使用方法与 SHA-256 校验说明见 [`release/EitHost-Windows-x64/README.md`](release/EitHost-Windows-x64/README.md)。

### 从源码构建

源码构建需要 [.NET 10 官方支持的 Windows x64 版本](https://learn.microsoft.com/en-us/dotnet/core/install/windows)，即仍受支持的 Windows 11，或仍受支持的 Windows 10 Enterprise/LTSC，并需要 [.NET SDK 10.0.301](https://dotnet.microsoft.com/download/dotnet/10.0)或更高的 10.0 feature band。仓库中的 `global.json` 不接受更早的 .NET 10 SDK。

```powershell
git clone https://github.com/CBZ199671/EitHost.git
cd EitHost
dotnet restore .\EitHost.slnx
dotnet build .\EitHost.slnx --configuration Release --no-restore
dotnet run --project .\src\EitHost.App\EitHost.App.csproj --configuration Release --no-build
```

## 配置 PyEIDORS 后端

实时重构是可选功能，需要 WSL2 和兼容的 PyEIDORS v2 后端。目前本节仅适用于已经获得该后端授权访问权限的合作人员；公开的 PyEIDORS 仓库不包含所需的 worker 实现。

EitHost 从 `%LOCALAPPDATA%\EitHost\eithost.reconstruction.json` 读取用户配置。可通过以下命令从示例创建配置：

```powershell
$configDirectory = Join-Path $env:LOCALAPPDATA 'EitHost'
$configPath = Join-Path $configDirectory 'eithost.reconstruction.json'
New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
Copy-Item `
  .\src\EitHost.App\eithost.reconstruction.example.json `
  $configPath
notepad $configPath
```

然后根据已获授权的本地后端环境设置 `DistroName`、`BackendRepositoryPath` 和可选的 `BackendProfile`。也可以在启动 EitHost 前，将 `EITHOST_RECONSTRUCTION_CONFIG` 设置为其他配置文件的绝对路径。

## 仓库结构

| 路径 | 内容 |
|---|---|
| `src/EitHost.App` | .NET 10 / WPF 桌面应用、工作区 ViewModel 与实时可视化 |
| `src/EitHost.Core` | 采集、硬件协议、解调、诊断、存储、同步与重构桥接 |
| `scripts` | USB2070 驱动安装与管理员启动辅助脚本 |
| `release/EitHost-Windows-x64` | 可直接运行的 Windows x64 自包含发布版与校验值 |

## 实验室、单位与基金支持

| 项目 | 信息 |
|---|---|
| 实验室 | 455 Lab |
| 地点 | 中国，北京 |
| 单位 | 中国农业大学信息与电气工程学院 |
| 实验室负责人 | 黄岚教授、王忠义教授、范力锋博士 |

455 Lab 聚焦植物电生理表型、作物根系表型与作物水分状态监测。EitHost 和 PyEIDORS 服务于该研究背景下实时、原位、无损的 EIT 实验。

本项目得到国家自然科学基金（项目编号：62271488）支持。

## 许可证

EitHost 原创源码采用 [MIT License](LICENSE)。仓库中的厂商运行库、自包含 .NET 组件和 NuGet 依赖保留各自的许可条款，不因本仓库采用 MIT 而被重新授权；详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

尚未公开的 PyEIDORS v2 后端是独立项目：它不包含在本仓库中，也不受 EitHost 的 MIT 许可证授权。PyEIDORS v2 正式发布时将另行说明其开源许可证。
