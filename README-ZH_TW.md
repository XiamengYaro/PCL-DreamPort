[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

# PCL-夢港（PCL-DreamPort）

> **獨立二次創作聲明**：`PCL-夢港`（倉庫名 `PCL-DreamPort`）是第三方基於
> [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL) 與
> [PCL Community Edition (PCL CE)](https://github.com/PCL-Community/PCL-CE)
> 獨立進行二次創作的衍生修改版，與龍騰貓躍及 PCL 官方無關聯。請勿向 PCL / PCL CE 上游回報本分支的問題。

## 目前階段（PoC）已實現能力

- **跨平台底座**：基於 .NET 10 / C# 15 / Avalonia 12，支援 macOS（ARM64 / x64）、Windows 與 Linux
- **合規底座與隔離**：
  - 嚴格保留雙授權架構與原作者首位署名、贊助連結及啟動勸導（`McLaunchPrecheck`）
  - Windows 主色相鎖定 PCL 藍色系，停用上游 Sentry 與連線遙測上報
  - 應用資料目錄獨立隔離為 `PCL-DreamPort`，不影響原有 PCL CE 資料
- **僅正版遊戲帳號**：屏蔽離線登入與外置登入入口，保留微軟正版登入並內建裝置碼掃描二維碼
- **連線能力**：完全移除 EasyTier 與 Natayark ID 相關模組及設定，僅保留夢港連線（TCP/TLS 中繼）與區域網路廣播發現入口
- **下載與更新源分工**：遊戲資料下載沿用 PCL 原生官方源 / BMCLAPI 並隔離下載層 User-Agent（`PCL-DreamPort/{version}`）；夢港加速源（`https://dl.xmcraft.cn`，`DP_UPDATE_MIRROR_URL`）僅用於啟動器更新等配套服務
- **夢港帳號登入原型**：「工具 -> 夢港帳號」支援設定站點位址並透過瀏覽器確認登入（複用 `/api/qr-login/*` 授權流程，支援電腦瀏覽器確認與同碼手機掃碼確認，啟動器不採集明文密碼）與安全儲存工作階段
- **PCL 原生自訂主頁原型**：首次啟動預設承載夢港 PCL XAML 原型，並支援對接伺服器端 `GET /api/client/pcl-homepage`（不可達時優雅回退本機原型）

## 執行環境要求

本倉保持輕量非自包含（`--no-self-contained`）建置策略：

| 平台 | 架構 | 執行時期要求 |
|---|---|---|
| macOS 12+ | Apple Silicon (ARM64) / Intel (x64) | [.NET 10 Runtime](https://get.dot.net/10) |
| Windows 10 1809+ / Windows 11 | x64 / ARM64 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Linux（Ubuntu 22.04+ / Arch 等） | x64 / ARM64 | [.NET 10 Runtime](https://get.dot.net/10) |

## 建置與環境變數

建置本倉庫需要安裝 **.NET 11 SDK**（方案 `TargetFramework` 為 `net10.0`，`LangVersion 15.0`）。

```bash
# 建置 Debug / Release
dotnet build "PCL Community Edition.slnx" -c Debug
dotnet build "PCL Community Edition.slnx" -c Release

# 執行測試
dotnet test DreamPort.Test/DreamPort.Test.csproj -c Debug
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug

# 打包 macOS 輕量 .app（需傳入實際版本號，本機可加 PCL_SKIP_CODESIGN=1）
PCL_SKIP_CODESIGN=1 bash scripts/package-macos.sh osx-arm64 Release 0.1.0-poc dist
```

| 環境變數 / 建置參數 | 預設值 | 說明 |
|---|---|---|
| `DP_BASE_URL` | `https://xmcraft.cn` | DreamPort 站點根位址 |
| `DP_REPO_URL` | `https://github.com/Xiameng/PCL-DreamPort` | 開源倉庫位址 |
| `DP_UPDATE_MIRROR_URL` | `https://dl.xmcraft.cn` | 更新等配套服務下載加速源（不用於遊戲資料下載） |
| `PCL_MS_CLIENT_ID` | （必填） | 自有 Azure 正版登入 Client ID（建置期注入；Debug 支援 `DP_MS_CLIENT_ID`） |

## 開源授權

- `PCL.Launcher/` 與 `PCL.Launcher.Core/` 目錄嚴格適用並延續 [PCL 自訂分發有限授權及合理使用指南](PCL.Launcher.Core/LICENCE)
- `DreamPort.Core/`、`DreamPort.UI/`、`DreamPort.Test/` 及其餘全部目錄適用 [Apache License 2.0](LICENSE)（變更說明見 [NOTICE](NOTICE)）
