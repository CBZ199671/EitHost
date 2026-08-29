# EitHost

EitHost 是面向多套电阻抗成像设备的 Windows 桌面端软件，负责 USB2070 采集、DDS 控制、数据存储、解调、重构和实时可视化。

## 环境要求

- Windows 10 或更高版本
- .NET 10 SDK
- USB2070 采集卡及厂商驱动
- DDS 串口控制板
- 可选：WSL2 与 PyEIDORS 重构后端

## 构建

```powershell
dotnet build EitHost.slnx -c Release
```

## 运行

```powershell
dotnet run --project src/EitHost.App -c Release
```

仓库包含 GUI 运行所需的 `USB2070.dll`。Windows 设备驱动需要从硬件厂商提供的安装包中安装，不随本仓库分发。
