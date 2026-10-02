[English](./README.en.md)

# <img src="./resources/screenshots/logo.png"  width="28" style="vertical-align: middle; margin-top: -4px;" /> LiteMonitor-Fork
一款轻量、可定制的开源桌面硬件监控软件LiteMonitor的同人修复版


## 修复内容
-修复了"核显不能显示温度"的BUG

-修复了"修改BIOS导致CPU温度不显示"的BUG

## 更新内容
-添加了"MSR →LHM → WMI →估算"的测温方法

-添加了"只有MSR →LHM → WMI全部失效了才用方程组估算"的测温方法

-添加了"3种不同的CPU温度估算方程组"

-添加了"3种不同的GPU温度估算方程组"

-添加了"软件可以根据用户的电脑配置选择合适的估算方案,如果可以用两套及以上,选择估算最准确的那套"


## 编译说明

### 环境要求
- Windows 10 / 11  
- .NET 8 SDK  
- Visual Studio 2022 或 Rider

### 编译命令
```bash
git clone https://github.com/YuChen-OmO/LiteMonitor-Fork.git
cd LiteMonitor-Fork
dotnet build -c Release
```

输出文件：
```
/bin/Release/net8.0-windows/LiteMonitor-Fork.exe
```


## 开源协议
本项目基于 **MIT License** 开源，可自由使用、修改与分发。


## 联系方式
**作者**:雨辰&YuChen_OmO

**Github**:https://github.com/YuChen-OmO

**抖音**:https://v.douyin.com/AFWO2MLWaRk/ 2@2.com

**快手**:https://v.kuaishou.com/K1GuZw25

**哔哩哔哩**:https://b23.tv/qUWhzgO
