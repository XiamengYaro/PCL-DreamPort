# PCL-梦港（PCL-DreamPort）

> **独立二次创作声明**：`PCL-梦港`（仓库名 `PCL-DreamPort`）是第三方基于
> [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL) 与
> [PCL Community Edition (PCL CE)](https://github.com/PCL-Community/PCL-CE)
> 独立进行二次创作的衍生修改版，与龙腾猫跃及 PCL 官方无关联。请勿向 PCL / PCL CE 上游反馈本分支的问题。

## 当前阶段（Phase 2）已实现能力

- **跨平台底座**：基于 .NET 10 / C# 15 / Avalonia 12，支持 macOS（ARM64 / x64）、Windows 与 Linux
- **合规底座与隔离**：
  - 严格保留双许可架构与原作者首位署名、赞助链及启动劝导（`McLaunchPrecheck`）
  - Windows 主色相锁定 PCL 蓝色系，禁用上游 Sentry 与联机遥测上报
  - 应用数据目录独立隔离为 `PCL-DreamPort`，不影响原有 PCL CE 数据
- **仅正版游戏账号**：屏蔽离线登录与外置登录入口，保留微软正版登录并内置设备码扫描二维码
- **联机能力**：完全移除 EasyTier 与 Natayark ID 相关模块及配置，仅保留梦港联机（TCP/TLS 中继）与局域网广播发现入口（房间创建/加入/中继转发在 Phase 4 全量接入）
- **下载与更新源分工**：游戏数据下载沿用 PCL 原生官方源 / BMCLAPI 并隔离下载层 User-Agent（`PCL-DreamPort/{version}`）；梦港加速源（`https://dl.xmcraft.cn`，`DP_UPDATE_MIRROR_URL`）仅用于启动器更新等配套服务
- **完整梦港账号系统与多设备管理**：「工具 → 梦港账号」支持配置站点地址（切换站点自动向旧站点 `/api/logout` 注销并清理旧凭据与主页缓存）、浏览器确认 + 同码扫码授权登录（`/api/qr-login/*`）、网页注册/安全与通行密钥/封禁申诉快捷直达、系统加密存储（DPAPI / Keychain）、严格区分会话过期（401）与网络波动，以及活跃设备查看与单设备/批量踢出（`/api/auth/sessions*`）
- **六步入服引导流程（原生 PCL 控件）**：对接 `/api/user/progress` 实时展示六步进度，内置 QQ 绑定/解绑（`/api/user/qq/*`）、使用当前启动器已登录微软正版档案一键验证正版 ID（`POST /api/user/minecraft/verify-token`）及微软设备码备用验证、邀请码入服（`POST /api/invite/apply`）、可交互入服问卷与 SSE 实时逐题 AI 评分反馈（`/api/questionnaire/{config,stream,submit}`），以及被拒绝/封禁状态引导与问卷人工复核申诉（`/api/questionnaire/appeal*`）
- **PCL 原生自定义主页推送**：启动器首页默认接入站点 `GET /api/client/pcl-homepage` 与 `/version`（支持版本比对与服务端自动从网页端主页编排翻译或自定义 XAML 下发），不可达或离线时静默回退至本地 XAML 原型

## 运行环境要求

本仓保持轻量非自包含（`--no-self-contained`）构建策略：

| 平台 | 架构 | 运行时要求 |
|---|---|---|
| macOS 12+ | Apple Silicon (ARM64) / Intel (x64) | [.NET 10 Runtime](https://get.dot.net/10) |
| Windows 10 1809+ / Windows 11 | x64 / ARM64 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Linux（Ubuntu 22.04+ / Arch 等） | x64 / ARM64 | [.NET 10 Runtime](https://get.dot.net/10) |

## 构建与环境变量

构建本仓库需安装 **.NET 11 SDK**（溶液 `TargetFramework` 为 `net10.0`，`LangVersion 15.0`）。

```bash
# 构建 Debug / Release
dotnet build "PCL Community Edition.slnx" -c Debug
dotnet build "PCL Community Edition.slnx" -c Release

# 运行测试
dotnet test DreamPort.Test/DreamPort.Test.csproj -c Debug
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug

# 打包 macOS 轻量 .app（需传入实际版本号，本地可加 PCL_SKIP_CODESIGN=1）
PCL_SKIP_CODESIGN=1 bash scripts/package-macos.sh osx-arm64 Release 0.1.0-poc dist
```

| 环境变量 / 构建参数 | 默认值 | 说明 |
|---|---|---|
| `DP_BASE_URL` | `https://xmcraft.cn` | DreamPort 站点根地址 |
| `DP_REPO_URL` | `https://github.com/Xiameng/PCL-DreamPort` | 开源仓库地址 |
| `DP_UPDATE_MIRROR_URL` | `https://dl.xmcraft.cn` | 更新等配套服务下载加速源（不用于游戏数据下载） |
| `PCL_MS_CLIENT_ID` | （必填） | 自有 Azure 正版登录 Client ID（构建期注入；Debug 支持 `DP_MS_CLIENT_ID`） |

## 开源许可

- `PCL.Launcher/` 与 `PCL.Launcher.Core/` 目录严格适用并延续 [PCL 自定义分发有限许可及合理使用指南](PCL.Launcher.Core/LICENCE)
- `DreamPort.Core/`、`DreamPort.UI/`、`DreamPort.Test/` 及其余全部目录适用 [Apache License 2.0](LICENSE)（变更说明见 [NOTICE](NOTICE)）
