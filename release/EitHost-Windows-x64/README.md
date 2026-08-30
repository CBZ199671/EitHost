# EitHost Windows x64

此目录是 Windows x64 自包含发布版，已经包含所需的 .NET 运行时。

> **后端公开状态：** 实验室当前使用的完整实时重构流程依赖尚未公开的 PyEIDORS v2 后端。本发布包不包含该后端；我们承诺在相关论文撰写完成后开源 PyEIDORS v2。

## 使用方法

1. 使用硬件厂商提供的安装包安装 USB2070 驱动。
2. 将整个 `EitHost-Windows-x64` 目录解压到具有写入权限的位置。
3. 双击 `EitHost.App.exe`。

请勿直接在压缩包内运行，也不要只复制 `.exe`。最小运行文件必须保持在同一目录：

- `EitHost.App.exe`
- `HDF.PInvoke.dll`
- `HDF.PInvoke.dll.config`
- `hdf5.dll`
- `hdf5_hl.dll`
- `USB2070.dll`

程序每次启动都会先创建、写入、关闭并删除一个临时 HDF5 文件；运行库不完整时会在打开硬件前明确退出，不会采集一段时间后才停止。运行数据默认写入此目录下的 `Data` 文件夹。

`scripts` 目录中的脚本只用于从本机已有的厂商驱动文件执行安装或修复；本发布包不包含驱动安装包。

## 当前功能边界

本发布包可以运行 EitHost 的采集、设备管理、数据存储、解调、质量诊断、回放与可视化功能。EitHost 的 Windows/WSL2 集成层也包含在程序中，但公开的 PyEIDORS 仓库对应较早版本，不包含本版 EitHost 所需的 worker。

因此，在兼容的 PyEIDORS v2 后端正式公开前，仅使用当前公开仓库和本发布包无法复现实验室的完整端到端实时重构流程。当前限制是论文发布时序上的阶段性安排，并不意味着 PyEIDORS v2 将长期闭源。

## 文件校验

`SHA256SUMS.txt` 记录发布目录全部文件的 SHA-256。可在 PowerShell 中执行：

```powershell
Get-FileHash .\EitHost.App.exe -Algorithm SHA256
Get-FileHash .\HDF.PInvoke.dll -Algorithm SHA256
Get-FileHash .\hdf5.dll -Algorithm SHA256
Get-FileHash .\USB2070.dll -Algorithm SHA256
```
