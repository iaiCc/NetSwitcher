# NetSwitcher · 网络切换

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6.svg)
![.NET](https://img.shields.io/badge/.NET-Framework%204.0%2B-512BD4.svg)
![Version](https://img.shields.io/badge/version-3.0.0-green.svg)

一个 Windows 托盘小工具：手机热点一键切换、内网/有线识别、默认出口与内网专线可视化管理。

> 纯 .NET Framework 单文件工具，**无需安装、无需第三方依赖**，双击 `NetSwitcher.exe` 即用。
> 如果你觉得好用，欢迎点个 ⭐ Star。

---

## 功能

- **手机热点一键切换**：自动扫描附近 Wi-Fi，按「连接中 › 已固定 › 在附近 › 离线」排序，左键点一下就连；未保存的热点会提示输入一次密码，之后系统记住。
- **内网 / 有线识别**：列出所有带网关的网卡，绿灯表示网关可达（ping / ARP / TCP 多种方式确认，界面会写明依据）。
- **两条路径可视化**：顶栏直接写明「上网走哪块网卡、内网资料走哪块网卡」，一眼看清双线并存状态。
- **内网固定路由**：给指定网段加一条永久路由指向对应网卡，上网与内网互不干扰（需管理员权限，会弹 UAC）。
- **出口优先级**：可把某块网卡设为默认出口，或恢复系统自动选择。
- **网络备注**：给热点 / 网卡起自己的名字，直接显示在卡片上。
- **托盘常驻 + 全局热键**：`Ctrl+Alt+N` 呼出/收起，关闭窗口只是收进托盘。

---

## 界面速览

<div align="center">
<table width="100%" style="max-width:560px; border:collapse; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;">
<tr><td style="background:linear-gradient(135deg,#467DE9,#3566D8); color:#fff; border-radius:14px 14px 0 0; padding:16px 18px;">

<div style="font-size:15px; font-weight:700; margin-bottom:12px;">网络切换 <span style="float:right; font-weight:400; opacity:.8;">⚙ &nbsp;↻ &nbsp;— &nbsp;✕</span></div>
<div style="font-size:11px; opacity:.85; letter-spacing:.5px;">外网 · 上网</div>
<div style="font-size:14px; font-weight:600; margin:1px 0 8px;">WLAN &nbsp;<span style="opacity:.75; font-size:11px; font-weight:400;">192.168.x.x</span></div>
<div style="font-size:11px; opacity:.85; letter-spacing:.5px;">内网 · 资料</div>
<div style="font-size:14px; font-weight:600; margin:1px 0 8px;">以太网 &nbsp;<span style="opacity:.75; font-size:11px; font-weight:400;">10.x.x.x</span></div>
<div style="font-size:11px; opacity:.8; border-top:1px solid rgba(255,255,255,.25); padding-top:8px;">两条线并存：上网走 Wi-Fi，内网资料走有线</div>

</td></tr>
<tr><td style="background:#F5F7FB; border:1px solid #EDF0F5; border-top:0; border-radius:0 0 14px 14px; padding:14px 18px;">

<div style="font-size:12px; font-weight:700; color:#1F2430; margin-bottom:8px;">📶 手机热点 <span style="font-weight:400; color:#8A94A6; font-size:10px;">3 个 · 左键连接 / 右键选项</span></div>
<div style="margin-bottom:14px;">
<span style="display:inline-block; background:#fff; border:1px solid #EDF0F5; border-radius:8px; padding:8px 14px; margin:0 6px 6px 0; color:#1F2430; font-size:12px;">📶 使用中 &nbsp;<span style="background:#E9F6F0; color:#2EA87F; border-radius:10px; padding:1px 8px; font-size:10px;">使用中</span></span>
<span style="display:inline-block; background:#fff; border:1px solid #EDF0F5; border-radius:8px; padding:8px 14px; margin:0 6px 6px 0; color:#1F2430; font-size:12px;">📶 在附近 &nbsp;<span style="background:#EAEFFC; color:#3566D8; border-radius:10px; padding:1px 8px; font-size:10px;">在附近</span></span>
</div>

<div style="font-size:12px; font-weight:700; color:#1F2430; margin-bottom:8px;">🔌 内网 / 有线 <span style="font-weight:400; color:#8A94A6; font-size:10px;">2 个 · 右键打开选项</span></div>
<div>
<span style="display:inline-block; background:#fff; border:1px solid #EDF0F5; border-radius:8px; padding:8px 14px; margin:0 6px 6px 0; color:#1F2430; font-size:12px;">🟢 外网 &nbsp;<span style="background:#EAEFFC; color:#3566D8; border-radius:10px; padding:1px 8px; font-size:10px;">外网</span></span>
<span style="display:inline-block; background:#EAF6F0; border:1px solid #9CD9BE; border-radius:8px; padding:8px 14px; margin:0 6px 6px 0; color:#1F2430; font-size:12px;">🟢 内网 &nbsp;<span style="background:#E9F6F0; color:#2EA87F; border-radius:10px; padding:1px 8px; font-size:10px;">专线</span></span>
</div>

</td></tr>
</table>
</div>

---

## 下载与运行

1. 到 [Releases](../../releases) 下载最新的 `NetSwitcher.exe`（或自己编译，见下）。
2. 双击运行，面板出现在屏幕右上角；托盘出现图标。
3. 需要改路由 / 出口时会弹 UAC，点「是」即可；拒绝也不影响其他功能。

**运行环境**：Windows 7 / 8 / 8.1 / 10 / 11（x86 / x64 均可），需 .NET Framework 4.0+（Win7 及以上基本都已内置或可一键安装）。

---

## 从源码编译

只需要系统自带的 .NET Framework C# 编译器，无需 Visual Studio：

```bat
:: 方式一：双击
build.bat

:: 方式二：命令行
csc /target:winexe /optimize+ /win32icon:app.ico /win32manifest:app.manifest ^
    /out:NetSwitcher.exe NetSwitcher.cs AssemblyInfo.cs
```

`build.bat` 会自动在 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\` 等位置定位 `csc.exe`，并把参数写进临时 response file 以避免路径转义问题。

---

## 操作

| 操作 | 效果 |
| --- | --- |
| 左键热点卡片 | 连接到该热点 |
| 右键热点卡片 | 复制 SSID / 连接 / 断开 / 备注 / 固定到面板 / 手动输入 SSID |
| 右键内网卡片 | 固定或取消内网网段 / 设为默认出口 / 恢复自动 / 备注 / 复制 IP / Ping 网关 / 详情 |
| 拖动标题栏空白处 | 移动面板 |
| `Ctrl+Alt+N` | 呼出 / 收起 |
| `F5` | 立即重新检测 |
| `Esc` | 收起面板 |
| 关闭窗口 | 收进托盘（真正退出请用托盘菜单 → 退出） |

### 两种「选网络」的区别

- **固定内网网段（推荐）**：只给这个网段加一条静态路由指向该网卡网关。上网继续走原来的默认出口，内网走这条线，互不干扰。
- **所有上网流量改走它**：把该网卡接口度量调到 1、其他调高，全部出网流量改从它走。只在整机要切换到另一条线时使用。

两者都改系统设置，需要管理员权限。

---

## 配置

所有配置存在 `%APPDATA%\NetSwitcher\`，不写注册表、不污染程序目录：

| 文件 | 内容 |
| --- | --- |
| `slots.txt` | 手动固定住的热点 SSID（每行一个） |
| `ui.txt` | 面板位置 |
| `pref.txt` | 指定的优先出口网卡名（空 = 自动） |
| `notes.txt` | 网络备注（键\<TAB\>备注；键为 `wifi:SSID` / `lan:网卡名`） |
| `probe.txt` | 可选，自定义外网探测点（见 [probe.example.txt](probe.example.txt)） |

### 外网探测点

程序通过「绑定源地址连接公共 DNS」来判断某块网卡能否上网。默认用 `1.1.1.1 / 8.8.8.8 / 9.9.9.9` 的 443/53/80 端口。

如果所在网络访问这些地址不通，可复制 `probe.example.txt` 为 `%APPDATA%\NetSwitcher\probe.txt`，改成自己的探测点，例如国内 DNS 或内网网关。格式见示例文件。

---

## 兼容性与已知限制

- 手机热点改名后，旧卡片显示「离线」，扫到新名字后需重新固定。
- 不少机器禁了 ICMP，网关可达性会退到 ARP / TCP 判断，界面会标注依据。
- 切换热点靠 `netsh wlan connect`，实测约 3–10 秒，期间手机热点需保持开启。
- Wi-Fi 扫描、路由读写依赖系统 `netsh` / `route` 命令输出，程序按「列位置」而非本地化关键字解析，兼容中英文系统。
- 需要无线网卡及其驱动正常（无 Wi-Fi 的台式机可用内网/有线部分）。
- 固定路由 / 改出口会修改系统网络配置，操作前会请求管理员权限；程序会在运行时回读校验是否真正生效。

---

## 许可证

[MIT](LICENSE) — 可自由使用、修改、分发。

---

## 贡献

欢迎提 Issue 和 PR。若要改动网络解析逻辑，请在中英文系统、以及有无 IPv6 的环境下各验证一次。
