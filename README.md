# AutoVolume

Windows 托盘小工具：**网页/播放器开始出声时，自动暂停 QQ 音乐并把系统音量切到「视频」方案；声源关掉后，恢复音乐和「音乐」音量。**

C# / .NET 8 + WPF，单文件自包含发布，不依赖系统 .NET。

## 行为

| 场景 | 动作 |
|---|---|
| 监测源开始出声（Edge / VLC / PotPlayer / PowerToys 速览） | 暂停受控播放器 → 会话静音掐掉尾音 → 主音量渐变到「视频」方案 |
| 明显有声（峰值 ≥ 0.02） | 600 ms 就确认；弱信号（速览 0.005–0.01）仍等 1.5 s，避免误触发 |
| 源关闭 / 静音超时 | 等待「恢复延迟」→ 主音量回「音乐」→ 恢复播放 |
| 插拔耳机、蓝牙断开 | 「设备切换抑制」时间内不判断，避免误恢复 |
| 用户手动调音量 / 手动恢复播放 | 学习音量或让权，本周期不再抢回 |
| 崩溃 / 强杀 | 下次启动按 `journal.json` 恢复播放与音量 |

## 下载

不装 .NET 也能直接用，去 Release 下载单文件：

- 最新版：<https://github.com/zeroyun23/AutoVolume/releases/latest>
- 附件 `AutoVolume.exe`（约 179 MB，自包含）
- 同页 `AutoVolume-0.3.7.sha256.txt` 可用于校验

双击 exe 即用；首次运行会弹设置页，勾选「开机自动启动」后以后只进托盘。

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows）。

```bat
git clone https://github.com/zeroyun23/AutoVolume.git
cd AutoVolume

:: 编译
scripts\dotnet.cmd build AutoVolume.sln -c Release

:: 发布单文件（输出 artifacts\portable\AutoVolume.exe）
scripts\publish.cmd
```

> 发布参数固定为：`win-x64` / `self-contained` / `PublishSingleFile` / **不压缩** / **不裁剪**。
> 不要开 `PublishTrimmed`（SMTC 走 WinRT，会读不到会话）；不要开单文件压缩（私有工作集从 14 MB 涨到 90 MB）。

## 运行

```bat
:: 正常启动（设置窗口 + 托盘）
AutoVolume.exe

:: 开机自启用：只进托盘，不弹设置
AutoVolume.exe --autostart

:: 开发模式：独立配置 %LOCALAPPDATA%\AutoVolume-dev
AutoVolume.exe --dev
```

配置：`%LOCALAPPDATA%\AutoVolume\config.json`（`--dev` 为 `AutoVolume-dev`）。
日志：`%LOCALAPPDATA%\AutoVolume\logs\app.log`（2 MB × 3 份滚动）。

## 目录

```
src/
  AutoVolume.Core/     纯逻辑状态机（不碰 COM，可用假快照测试）
  AutoVolume.Audio/    WASAPI 主音量、SMTC、会话静音、设备监听
  AutoVolume.Runtime/  轮询循环、执行动作、journal 自愈、日志
  AutoVolume.App/      WPF 设置页 + 托盘
  AutoVolume.Cli/      诊断用命令行（--diag/--once）
scripts/               构建、发布、图标生成
spike/                 实测工具（UIA、SMTC、音量、截图）
docs/                  设计、实测数据、交接文档
```

## 测试工具

```bat
:: 打印默认端点、会话峰值、SMTC 状态
spike\AutoVolume.Spike\bin\Debug\net8.0-windows10.0.19041.0\AutoVolume.Spike.exe probe

:: 持续采样（CSV）
AutoVolume.Spike.exe monitor --out logs\mon --interval 50 --seconds 30

:: 播放控制 / 会话静音状态
AutoVolume.Spike.exe smtc play QQMusic.exe
AutoVolume.Spike.exe sessmute QQMusic.exe
```

## 已知限制

- 热键控制（`method: "hotkey"`）未实现，目前只有 SMTC 自动控制。
- 恢复延迟、抑制窗口等参数按经验值设定，不同设备可能需要在设置页微调。
- macOS / Linux 不支持（大量 Win32/WASAPI/WinRT 调用）。

## 版本

当前 `0.3.8`。行为细节与实测数据见 `docs/`。
