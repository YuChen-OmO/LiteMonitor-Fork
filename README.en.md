[中文](./README.md)

# <img src="./resources/screenshots/logo.png"  width="28" style="vertical-align: middle; margin-top: -4px;" /> LiteMonitor-Fork
A lightweight, customizable open-source desktop hardware monitoring software, and a community-fixed fork of LiteMonitor.

## Fixes
- Fixed the bug where the integrated GPU (iGPU) could not display temperature.

- Fixed the bug where modifying the BIOS caused the CPU temperature to not display.


## Updates
- Added a temperature measurement method: "MSR → LHM → WMI → Estimation".

- Added a temperature measurement method: "Only use equation estimation when MSR → LHM → WMI all fail".

- Added "3 different CPU temperature estimation equation sets".

- Added "3 different GPU temperature estimation equation sets".

- Added: "The software can select the appropriate estimation scheme based on the user's computer configuration. If two or more sets are applicable, it selects the most accurate one".


## Build Instructions

### Requirements
- Windows 10 / 11
- .NET 8 SDK
- Visual Studio 2022 or Rider

### Build Commands
```bash
git clone https://github.com/YuChen-OmO/LiteMonitor-Fork.git
cd LiteMonitor-Fork
dotnet build -c Release
```

Output file:
```
/bin/Release/net8.0-windows/LiteMonitor-Fork.exe
```


## License
This project is open-sourced under the MIT License, allowing free use, modification, and distribution.


## Contact
**Author**: 雨辰&YuChen_OmO

**GitHub**: https://github.com/YuChen-OmO

**Douyin**: https://v.douyin.com/AFWO2MLWaRk/ 2@2.com

**Kuaishou**: https://v.kuaishou.com/K1GuZw25

**Bilibili**: https://b23.tv/qUWhzgO