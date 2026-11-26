# PCL-梦港（PCL-DreamPort）

> **独立二次创作声明**：`PCL-梦港`（仓库名 `PCL-DreamPort`）是第三方基于
> [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL) 与
> [PCL Community Edition (PCL CE)](https://github.com/PCL-Community/PCL-CE)
> 独立进行二次创作的衍生修改版，与龙腾猫跃及 PCL 官方无关联。请勿向 PCL / PCL CE 上游反馈本分支的问题。

## 当前阶段（Phase 5 / 3b）已实现能力

- **跨平台底座**：基于 .NET 10 / C# 15 / Avalonia 12，支持 macOS（ARM64 / x64）、Windows 与 Linux；Java 自动发现覆盖 PATH、`JAVA_HOME` 与 macOS 系统/用户级 `JavaVirtualMachines` 目录
- **合规底座与隔离**：
  - 严格保留双许可架构与原作者首位署名、赞助链及启动劝导（`McLaunchPrecheck`）
  - Windows 主色相锁定 PCL 蓝色系，禁用上游 Sentry 与联机遥测上报
  - 应用数据目录独立隔离为 `PCL-DreamPort`，不影响原有 PCL CE 数据
- **仅正版游戏账号**：屏蔽离线登录与外置登录入口，保留微软正版登录并内置设备码扫描二维码
- **DreamPort 品牌与独立排障入口**：窗口标题栏、应用图标与关于页使用 DreamPort 官网标识，游戏启动自定义信息默认 `PCL-DreamPort`；AI 日志排障独立为「工具 → AI 排障」页面
- **梦港联机（TCP/TLS 中继 + 局域网广播 + 实时同步）**：完全移除 EasyTier 与 Natayark ID，原生实现仅基于 TCP/TLS 中继的 `DreamPort-Relay` 联机大厅——支持 `/realtime` WebSocket 秒级状态刷新（断连自动回退 30 秒轮询）、单人开房（`224.0.2.60:4445` 组播监听 + 游戏 `latest.log` 端口正则兜底 + 本机探活、SLP/本地模组扫描上报、10 秒 `host_status` 宽限保活、成员踢出/解除拉黑、好友勾选邀请与撤回、残留房间自救与退出自动关房）、好友/公开大厅加房（按版本/模组兼容度排序、Forge/NeoForge 加载器或模组差异硬拦截、版本差异二次确认、多连接并发转发与局域网伪装广播）
- **服务器配套模组双源同步**：「工具 → 同步与工具」接入 `/api/client/serverpack` 与 Modrinth 双源清单，同步前强制校验实例 MC 版本与加载器，支持路径越界与核心文件保护、SHA-256/SHA-512 校验、保留 `.disabled` 与玩家自装模组、仅清理受管集合以及损坏状态文件备份拦截
- **存档云备份**：「工具 → 同步与工具」支持流式 ZIP 打包（自动跳过 `session.lock`）、SHA-256 校验、分块断点续传上传、重命名与删除（`/api/client/save-backups*`），恢复时校验 SHA-256 与 Zip-Slip 并自动避让同名本地存档（永不静默覆盖）
- **多设备白名单配置云同步**：「工具 → 同步与工具」支持对更新/下载/启动窗口/外观主题等便携非敏感配置进行云端备份与同步（`/api/client/profile-sync`），严格排除账号凭据、MirrorChyan Key、代理帐密、Java 路径与自定义启动参数，并通过乐观锁 `revision` 拦截跨设备冲突交由玩家显式决策
- **AI 启动日志排障**：「工具 → AI 排障」提供单一 `提交 AI 诊断` 动作（`/api/ai/log-diagnose*`），点击后自动读取当前实例日志、完成有界尾部读取与玩家名/用户目录/各类令牌脱敏，再异步短轮询获取结构化结论、依据与可执行建议
- **社区资源 AI 功能解析**：资源详情页提供 `AI 功能解析` 按钮，汇总模组/整合包的功能特性与注意事项（`/api/ai/resource/explain`）
- **下载与更新源分工**：已移除中国大陆地区的 Minecraft 下载、补全与整合包安装区域限制；游戏数据下载沿用 PCL 原生官方源 / BMCLAPI 并隔离下载层 User-Agent（`PCL-DreamPort/{version}`），梦港加速源（`https://dl.xmcraft.cn`，`DP_UPDATE_MIRROR_URL`）仅用于启动器更新等配套服务
- **极简在线心跳与 GitHub/镜像双源自动更新**：「设置 → 更新」与启动检查优先请求 `dl.xmcraft.cn/pcl-dreamport/{beta/}latest.json`（严格校验 schema / product / https / 64 位 SHA-256，任一不通过即静默回落 GitHub Releases + `SHA256SUMS`），严格按 OS+架构匹配安装包（绝不跨架构误下），支持「跳过此版本」持久化、临时 `.dp-update-part` 校验与 macOS `ditto` + `open -n` 替换脚本；登录后仅上报极简在线心跳（`/api/client/heartbeat` 仅包含 `clientVersion`/`platform`/`clientStartedAt`，绝不上传模组或本机设置，也不应用任何远端下发动作），登出、切站或退出自动触发 `/api/client/offline` 下线通知
- **完整梦港账号系统与多设备管理**：「工具 → 梦港账号」支持浏览器确认 + 同码扫码授权登录（`/api/qr-login/*`）、网页注册/安全与通行密钥/封禁申诉快捷直达、系统加密存储（DPAPI / Keychain）、严格区分会话过期（401）与网络波动，以及活跃设备查看与单设备/批量踢出（`/api/auth/sessions*`）
- **六步入服引导流程（原生 PCL 控件）**：对接 `/api/user/progress` 实时展示六步进度，内置 QQ 绑定/解绑（`/api/user/qq/*`）、使用当前启动器已登录微软正版档案一键验证正版 ID（`POST /api/user/minecraft/verify-token`）及微软设备码备用验证、邀请码入服（`POST /api/invite/apply`）、可交互入服问卷与 SSE 实时逐题 AI 评分反馈（`/api/questionnaire/{config,stream,submit}`），以及被拒绝/封禁状态引导与问卷人工复核申诉（`/api/questionnaire/appeal*`）
- **每日自动签到、积分与称号管理**：启动器启动与账号校验时自动为已过审玩家执行每日一次静默签到并按自然日去重（`POST /api/points/signin`）；「梦港账号」页展示积分余额、签到天数、待领任务数并支持手动签到、跳转任务中心及查看/佩戴/卸下已拥有称号（`/api/points/center`、`/api/titles/*`）
- **服务器公告与站内通知中心**：新增「工具 → 公告与通知」页及标题栏右上角通知图标与实时未读数字角标，支持免登录浏览官网服务器公告资讯（`GET /api/announcements`），以及登录后按未读筛选通知、单条/全部已读与安全链接跳转（`/api/notifications*`）
- **PCL 原生自定义主页推送**：启动器首页默认接入站点 `GET /api/client/pcl-homepage` 与 `/version`（支持版本比对与服务端自动从网页端主页编排翻译或自定义 XAML 下发），自动翻译支持 Hero 背景图、轮播图片与照片墙，公告挂件直接同步官网已发布公告，站内图片地址自动补全；网络图片通过 `MyImage` 下载并缓存。对版本接口与缓存内容校验防污染，不可达或离线时静默回退至本地 XAML 原型

## 运行环境要求

本仓保持轻量非自包含（`--no-self-contained`）构建策略：

| 平台 | 架构 | 运行时要求 |
|---|---|---|
| macOS 12+ | Apple Silicon (ARM64) / Intel (x64) | [.NET 10 Runtime](https://get.dot.net/10) |
| Windows 10 1809+ / Windows 11 | x64 / ARM64 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Linux（Ubuntu 22.04+ / Arch 等） | x64 / ARM64 | [.NET 10 Runtime](https://get.dot.net/10) |

### macOS 首次启动

当前公开构建使用 ad-hoc 签名，尚未使用 Apple Developer ID 公证。通过浏览器下载后，如果系统提示无法验证开发者或拒绝启动，可移除已安装应用的下载隔离标记后重新打开：

```bash
xattr -dr com.apple.quarantine '/Applications/PCL-梦港.app'
open '/Applications/PCL-梦港.app'
```

只对从项目官方 Release 或 `dl.xmcraft.cn` 下载的包执行上述操作。

## 构建与环境变量

构建本仓库需安装 **.NET 11 SDK**（溶液 `TargetFramework` 为 `net10.0`，`LangVersion 15.0`）。

```bash
# 构建 Debug / Release
dotnet build "PCL Community Edition.slnx" -c Debug
dotnet build "PCL Community Edition.slnx" -c Release

# 运行测试
dotnet test DreamPort.Test/DreamPort.Test.csproj -c Debug
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug

# 打包与跨平台轻量发布（--no-self-contained，CI 发布工作流会自动生成 SHA256SUMS 供自动更新器校验）
PCL_SKIP_CODESIGN=1 bash scripts/package-macos.sh osx-arm64 Release 0.1.6 dist
bash scripts/publish-windows.sh win-x64 Release dist
bash scripts/publish.sh linux-x64 Release dist
```

Windows 发布物为单文件 `PCL.Launcher.exe`，发布流程会将其重命名为带版本和架构的 `.exe`。

| 环境变量 / 构建参数 | 默认值 | 说明 |
|---|---|---|
| `DP_BASE_URL` | `https://xmcraft.cn` | DreamPort 站点根地址 |
| `DP_REPO_URL` | `https://github.com/XiamengYaro/PCL-DreamPort` | 开源仓库地址 |
| `DP_UPDATE_MIRROR_URL` | `https://dl.xmcraft.cn` | 更新等配套服务下载加速源（不用于游戏数据下载） |
| `PCL_MS_CLIENT_ID` | （必填） | 自有 Azure 正版登录 Client ID（构建期注入；Debug 支持 `DP_MS_CLIENT_ID`） |

## 开源许可

- `PCL.Launcher/` 与 `PCL.Launcher.Core/` 目录严格适用并延续 [PCL 自定义分发有限许可及合理使用指南](PCL.Launcher.Core/LICENCE)
- `DreamPort.Core/`、`DreamPort.UI/`、`DreamPort.Test/` 及其余全部目录适用 [Apache License 2.0](LICENSE)（变更说明见 [NOTICE](NOTICE)）
