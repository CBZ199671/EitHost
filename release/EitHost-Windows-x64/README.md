# EitHost Windows x64

此目录是 Windows x64 自包含发布版，已经包含所需的 .NET 运行时。

## 使用方法

1. 使用硬件厂商提供的安装包安装 USB2070 驱动。
2. 将整个 `EitHost-Windows-x64` 目录解压到具有写入权限的位置。
3. 双击 `EitHost.App.exe`。

请勿直接在压缩包内运行，也不要只复制 `.exe`。程序需要同目录中的 `USB2070.dll` 和其余配置文件。运行数据默认写入此目录下的 `Data` 文件夹。

`scripts` 目录中的脚本只用于从本机已有的厂商驱动文件执行安装或修复；本发布包不包含驱动安装包。

## 文件校验

`SHA256SUMS.txt` 记录主程序和厂商运行库的 SHA-256。可在 PowerShell 中执行：

```powershell
Get-FileHash .\EitHost.App.exe -Algorithm SHA256
Get-FileHash .\USB2070.dll -Algorithm SHA256
```
