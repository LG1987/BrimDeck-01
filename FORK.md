# BrimDeck 峰谷图标版（fork）

基于 [MoFeng2223/BrimDeck](https://github.com/MoFeng2223/BrimDeck) **v1.3.1**（Apache-2.0）的修改版。
在 AI 用量面板的标题行里，`名称 → 套餐徽章 → **峰谷图标**`，把原本藏在两行小字和 tooltip 里的「峰谷计时」做成一枚可见、可点、每秒更新的 42 × 19 徽章。

## 直接安装

`dist/` 里是编译好的成品，双击即可：

| 文件 | 用途 |
| --- | --- |
| `BrimDeck-1.3.2-Setup.exe` | Windows 安装包（当前用户级，无需管理员） |
| `BrimDeck-1.3.2-Portable-x64.zip` | 免安装便携版，解压后运行 `BrimDeck/BrimDeck.exe` |
| `SHA256SUMS.txt` | 全部成品的校验值 |
| `update.json` | 官方自更新格式的版本清单 |

从本仓库下载（不必 clone 整个仓库）：打开 `dist/` 目录，点开文件后选 **Download raw file**，或直接用浏览器访问：

- `https://github.com/LG1987/BrimDeck-01/raw/main/dist/BrimDeck-1.3.2-Setup.exe`
- `https://github.com/LG1987/BrimDeck-01/raw/main/dist/BrimDeck-1.3.2-Portable-x64.zip`

下载后先核对校验值再运行：

```powershell
Get-FileHash .\BrimDeck-1.3.2-Setup.exe -Algorithm SHA256
# 将结果与 dist/SHA256SUMS.txt 中的值比对
```

> 两个成品超过 GitHub 建议的 50 MB，推送时会有 Large files 警告，但都低于 100 MB 硬上限，文件已完整入库。若要长期分发，建议改用 GitHub Release 附件（本仓库已打 `v1.3.2` tag）。

- 安装包的 `AppId` 与官方 v1.3.1 相同，安装会**替换**已装的官方 1.3.1，并保留 `%LOCALAPPDATA%\BrimDeck` 里的设置与数据；卸载也从「应用 → BrimDeck」走。
- 安装包**没有数字签名**（与官方一致），首次手动运行会看到 SmartScreen 提示，点「更多信息 → 仍要运行」。
- **建议关闭应用内的更新检查**：本 fork 仍会向官方仓库 `MoFeng2223/BrimDeck` 检查新版本，官方发布新版时可能把这个改动覆盖掉。设置里关掉更新检查即可。

## 改动了什么

| 文件 | 改动 |
| --- | --- |
| `src/BrimDeck/PeakValleyBadge.cs` | **新增**。`PeakValleySchedule`（时段表 / 倍率 / `At()` / `NextChange()`，三种数据源）与 `PeakValleyBadge`（42 × 19 WPF 控件：色带 + 指针 + 倍率文字、悬停 ToolTip、点击 Popup 完整卡片、每秒 `DispatcherTimer`、`Unloaded` 时停表）。 |
| `src/BrimDeck/UsagePanel.cs` | 标题行 Grid 从 3 列改为 4 列（在 `PlanChip` 后插入一个 Auto 列）；插入徽章、把右侧 `UsageLastSeen` 标记顺延到第 3 列、`occupied` 计入徽章宽度。 |
| `releases/1.3.2.md` | 本版本发行说明。 |
| `README.md` / `README.zh-CN.md` | 顶部加本 fork 的说明链接。 |
| `FORK.md` | 本文件。 |

原版的实现仍完整保留，没有删除任何逻辑：

- `src/BrimDeck.Core/CodeAgentUsage.cs` — `CreditPeriod` / `CreditHours`（`coefficient`、`weekday_coeff`、`weekend_coeff`、`At()` / `NextChange()` / `Next()`），本 fork 优先读它。
- `src/BrimDeck.Core/Settings.cs` — `AppEntry.OffPeak` / `OffPeakPeriod` / `IsOffPeak()` / `NextOffPeakChange()`，本 fork 的第二数据源。
- `src/BrimDeck/UsagePanel.cs:646` — 原版只在 `Balance` 且标签为「账户余额」且配置了低峰时段时写「低峰时段 / 高峰时段」与「xx切换」，本 fork **去掉了这个门槛**，让每一列都显示图标。
- `src/BrimDeck/OffPeakBar.cs` / `SettingsProviders.cs` / `PanelTheme.cs` / `MainWindow.xaml.cs: OffPeakChanged()` — 原样保留。

数据源优先级与诚实性：

1. 该列额度带 `UsageMetric.Hours`（服务商自己的忙/平/闲定义与系数）→ 显示真实倍率。
2. 用户在设置里配置的「低峰时间」→ 显示高峰/低谷，但**不显示倍率**，因为官方该功能只改配色、不代表价格。
3. 都没有 → 内置示例时段，Tooltip 与卡片里明确写着「内置示例时段（非官方计费规则）」，只用于演示，绝不参与计费。

时段按本机时间判定；「下次切换」取的是**状态真正翻转**的边界，不是简单的段边界，跨日不会算错。

## 自己构建

需要 .NET 10 SDK（项目目标框架 `net10.0-windows10.0.19041.0`）和 Windows x64。Inno Setup 编译器由 `installer/InnoSetup.csproj` 从 NuGet 自动拉 7.1.0，不用单独安装。

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
dotnet restore installer/InnoSetup.csproj
scripts/Publish-Release.ps1 -Version 1.3.2
```

官方脚本会依次：`dotnet publish` 传感器服务（写入其 SHA-256）→ `dotnet publish` 主程序（self-contained / ReadyToRun）→ 编译 `installer/BrimDeck.iss` → 生成便携 zip → 写 `update.json` 与 `SHA256SUMS.txt`，全部落在 `artifacts/releases/`。

也可以只编译不打包：

```powershell
dotnet build src/BrimDeck/BrimDeck.csproj -c Release -p:Platform=x64
```

## 许可

上游 v1.3.1 使用 **Apache License 2.0**，原 `LICENSE` 与 `ThirdPartyNotices.txt` 均完整保留。
本 fork 的修改同样按 Apache-2.0 提供；按许可证第 4 条，此处明确标注：

- 修改文件：`src/BrimDeck/UsagePanel.cs`
- 新增文件：`src/BrimDeck/PeakValleyBadge.cs`、`releases/1.3.2.md`、`FORK.md`
- 上游版权与许可声明未改动。

本仓库与上游作者无关联；成品仅供自用，未做代码签名。
