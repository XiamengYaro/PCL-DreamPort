[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

# PCL-DreamPort

> **Independent Derivative Notice**: `PCL-DreamPort` (in-app display name: `PCL-梦港`) is an independent third-party derivative built upon
> [Plain Craft Launcher (PCL)](https://github.com/Meloong-Git/PCL) and
> [PCL Community Edition (PCL CE)](https://github.com/PCL-Community/PCL-CE).
> It is not affiliated with LTCat or PCL Official. Please do not report issues from this fork to the upstream PCL or PCL CE repositories.

## Current Capabilities (PoC)

- **Cross-Platform Base**: Built on .NET 10 / C# 15 / Avalonia 12, supporting macOS (ARM64 / x64), Windows, and Linux
- **Compliance & Isolation**:
  - Preserves dual licensing, upstream author credits, sponsorship links, and launch precheck prompts (`McLaunchPrecheck`)
  - Locks Windows primary hues to PCL blue themes and disables upstream Sentry and telemetry reporting
  - Isolates launcher data in the `PCL-DreamPort` directory without affecting existing PCL CE installations
- **Microsoft Account Only**: Disables offline and third-party authlib login entries; retains official Microsoft login with built-in device-code QR rendering
- **Multiplayer**: Removes EasyTier and Natayark ID completely; retains the DreamPort TCP/TLS relay lobby entry and LAN broadcast discovery
- **Download & Update Separation**: Game data downloads use PCL native Official / BMCLAPI sources with isolated download User-Agent (`PCL-DreamPort/{version}`); DreamPort mirror (`https://dl.xmcraft.cn`, `DP_UPDATE_MIRROR_URL`) is reserved for launcher updates and companion services only
- **DreamPort Account Prototype**: `Tools -> DreamPort Account` supports configuring the site URL, browser confirmation login (via `/api/qr-login/*` with both desktop browser confirmation and mobile QR scan), and encrypted session persistence
- **Native PCL Custom Homepage Prototype**: Uses the built-in DreamPort PCL XAML homepage on first launch and supports fetching from `GET /api/client/pcl-homepage` with automatic local fallback

## Runtime Requirements

This repository keeps a lightweight `--no-self-contained` distribution strategy:

| Platform | Architecture | Runtime Requirement |
|---|---|---|
| macOS 12+ | Apple Silicon (ARM64) / Intel (x64) | [.NET 10 Runtime](https://get.dot.net/10) |
| Windows 10 1809+ / Windows 11 | x64 / ARM64 | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Linux (Ubuntu 22.04+ / Arch, etc.) | x64 / ARM64 | [.NET 10 Runtime](https://get.dot.net/10) |

## Build & Environment Variables

Building this repository requires **.NET 11 SDK** (solutions target `net10.0`, `LangVersion 15.0`).

```bash
# Build Debug / Release
dotnet build "PCL Community Edition.slnx" -c Debug
dotnet build "PCL Community Edition.slnx" -c Release

# Run tests
dotnet test DreamPort.Test/DreamPort.Test.csproj -c Debug
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug

# Package macOS .app (pass version, set PCL_SKIP_CODESIGN=1 for local builds)
PCL_SKIP_CODESIGN=1 bash scripts/package-macos.sh osx-arm64 Release 0.1.0-poc dist
```

| Environment Variable | Default | Description |
|---|---|---|
| `DP_BASE_URL` | `https://xmcraft.cn` | DreamPort site root URL |
| `DP_REPO_URL` | `https://github.com/XiamengYaro/PCL-DreamPort` | Public repository URL |
| `DP_UPDATE_MIRROR_URL` | `https://dl.xmcraft.cn` | Update mirror base URL (not used for game data downloads) |
| `PCL_MS_CLIENT_ID` | (Required) | Azure Client ID for Microsoft login (build-time; `DP_MS_CLIENT_ID` in Debug) |

## License

- `PCL.Launcher/` and `PCL.Launcher.Core/` strictly follow the [PCL Custom Limited Distribution License](PCL.Launcher.Core/LICENCE)
- `DreamPort.Core/`, `DreamPort.UI/`, `DreamPort.Test/`, and all other directories are licensed under [Apache License 2.0](LICENSE) (see [NOTICE](NOTICE))
