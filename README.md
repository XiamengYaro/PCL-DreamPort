# PCL-梦港（PCL-DreamPort）

> **独立二次创作声明**：`PCL-梦港`（仓库名 `PCL-DreamPort`）是第三方基于
> [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL) 与
> [PCL Community Edition (PCL CE)](https://github.com/PCL-Community/PCL-CE)
> 独立进行二次创作的衍生修改版，与龙腾猫跃及 PCL 官方无关联。请勿向 PCL / PCL CE 上游反馈本分支的问题。

## 当前阶段（PoC）已实现能力

- **跨平台底座**：基于 .NET 10 / C# 15 / Avalonia 12，支持 macOS（ARM64 / x64）、Windows 与 Linux
- **合规底座与隔离**：
  - 严格保留双许可架构与原作者首位署名、赞助链及启动劝导（`McLaunchPrecheck`）
  - Windows 主色相锁定 PCL 蓝色系，禁用上游 Sentry 与联机遥测上报
  - 应用数据目录独立隔离为 `PCL-DreamPort`，不影响原有 PCL CE 数据
- **仅正版游戏账号**：屏蔽离线登录与外置登录入口，保留微软正版登录并内置设备码扫描二维码
- **联机能力**：移除 Natayark ID 门控与上游遥测，保留 `EasyTier` 联机核心开箱可用（后续阶段接入「梦港联机」TCP/TLS 中继）
- **自建梦港镜像源**：支持 `自动（优先梦港源） / 梦港源 / BMCLAPI / 官方源`（默认 `https://dl.xmcraft.cn`），严格对齐 16 条映射规则并隔离下载层 User-Agent
- **梦港账号登录原型**：「工具 → 梦港账号」支持设置站点地址、密码 + 图形验证码登录、TOTP 两步验证与安全存储会话
- **PCL 原生自定义主页原型**：首次启动默认承载梦港 PCL XAML 原型，并支持对接服务端 `GET /api/client/pcl-homepage`（不可达时优雅回退本地原型）

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
| `DP_MIRROR_HOST` | `https://dl.xmcraft.cn` | 自建镜像基址；设为 `off` 可隐藏梦港源 |
| `DP_REPO_URL` | `https://github.com/Xiameng/PCL-DreamPort` | 开源仓库地址 |
| `DP_UPDATE_MIRROR_URL` | `https://dl.xmcraft.cn` | 更新下载加速源 |
| `PCL_MS_CLIENT_ID` | （必填） | 自有 Azure 正版登录 Client ID（构建期注入；Debug 支持 `DP_MS_CLIENT_ID`） |

## 开源许可

- `PCL.Launcher/` 与 `PCL.Launcher.Core/` 目录严格适用并延续 [PCL 自定义分发有限许可及合理使用指南](PCL.Launcher.Core/LICENCE)
- `DreamPort.Core/`、`DreamPort.UI/`、`DreamPort.Test/` 及其余全部目录适用 [Apache License 2.0](LICENSE)（变更说明见 [NOTICE](NOTICE)）
