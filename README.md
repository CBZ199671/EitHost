# EitHost

EitHost 是面向多套电阻抗成像设备的 Windows 桌面端软件，负责 USB2070 采集、DDS 控制、数据存储、解调、重构和实时可视化。

## 环境要求

- Windows 10 或更高版本
- USB2070 采集卡及厂商驱动
- DDS 串口控制板
- 可选：WSL2 与 PyEIDORS 重构后端

使用仓库内预编译版本不需要安装 .NET。仅从源码构建时需要 .NET 10 SDK。

## 直接运行

1. 安装 USB2070 厂商驱动。
2. 下载仓库并解压到具有写入权限的目录。
3. 运行 `release/EitHost-Windows-x64/EitHost.App.exe`。

发布目录必须完整保留，不能只复制 `.exe`。其中的 `USB2070.dll` 是 GUI 访问采集卡所需的 x64 厂商运行库。

## 构建

```powershell
dotnet build EitHost.slnx -c Release
```

## 运行

```powershell
dotnet run --project src/EitHost.App -c Release
```

仓库包含 GUI 运行所需的 `USB2070.dll`。Windows 设备驱动需要从硬件厂商提供的安装包中安装，不随本仓库分发。
