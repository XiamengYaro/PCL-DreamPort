using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DreamPort.Core.Updates;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;

namespace PCL;

public static class UpdateManager
{
    public static bool isUpdateWaitingRestart;

    public static UpdatesWrapperModel remoteServer = new([new DreamPortUpdateSource()]);

    public static bool HasConfiguredUpdateSource => remoteServer.IsAvailable();

    public static Loading.LoaderCombo<JsonObject> updateLoader;

    public static Loading.LoaderTask<int, int> serverLoader =
        new(
            Lang.Text("Update.Service.PclCe"),
            _ => LoadOnlineInfo(),
            priority: ThreadPriority.BelowNormal);

    public static bool SupportsInAppUpdate => UpdatePolicies.Current.SupportsInAppUpdate;

    public static bool IsCurrentVersionBeta
    {
        get
        {
            if (LauncherCommon.VersionBaseName.Contains("beta"))
                return true;
            return Config.Update.UpdateChannel == UpdateChannel.Beta;
        }
    }

    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        if (!HasConfiguredUpdateSource)
            return UpdateEnums.VersionStatus.Latest;

        try
        {
            if (!IsCurrentVersionBeta || Config.Update.UpdateChannel == UpdateChannel.Beta)
                return remoteServer.IsLatest(
                    IsCurrentVersionBeta
                        ? UpdateSourceChannel.Beta
                        : UpdateSourceChannel.Stable,
                    SystemInfo.IsArm64System
                        ? UpdateArch.Arm64
                        : UpdateArch.X64,
                    SemVer.Parse(LauncherCommon.VersionBaseName),
                    LauncherCommon.VersionCode)
                    ? UpdateEnums.VersionStatus.Latest
                    : UpdateEnums.VersionStatus.NotLatest;

            var isNewerThanStable = remoteServer.IsLatest(
                UpdateSourceChannel.Stable,
                SystemInfo.IsArm64System
                    ? UpdateArch.Arm64
                    : UpdateArch.X64,
                SemVer.Parse(LauncherCommon.VersionBaseName),
                LauncherCommon.VersionCode);
            var isBetaLatest = remoteServer.IsLatest(
                UpdateSourceChannel.Beta,
                SystemInfo.IsArm64System
                    ? UpdateArch.Arm64
                    : UpdateArch.X64,
                SemVer.Parse(LauncherCommon.VersionBaseName),
                LauncherCommon.VersionCode);

            return isNewerThanStable && isBetaLatest
                ? UpdateEnums.VersionStatus.Latest
                : UpdateEnums.VersionStatus.NotLatest;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                Lang.Text("Update.Check.Failed"),
                LauncherLogLevel.Hint,
                userSummary: Lang.Text("Update.Check.Failed"));
            return UpdateEnums.VersionStatus.Unknown;
        }
    }

    public static void UpdateStart(
        UpdateEnums.UpdateType type,
        string? receivedKey = null,
        bool forceValidated = false)
    {
        if (!HasConfiguredUpdateSource)
        {
            LauncherCommon.Log("[Update] 未配置 PCL-梦港 远程更新源，已跳过应用内更新");
            if (type != UpdateEnums.UpdateType.Silent)
                LauncherCommon.RunInUi(() => UpdateUi.Current?.ShowHint(
                    Lang.Text("Setup.Update.NoRemoteSource")));
            return;
        }

        if (!SupportsInAppUpdate && type != UpdateEnums.UpdateType.PromptOnly)
        {
            LauncherCommon.Log("[Update] 当前平台不支持应用内安装，已改为手动下载");
            LauncherCommon.RunInUi(() => UpdateUi.Current?.ShowHint(
                Lang.Text("Update.ManualDownloadRequired")));
            return;
        }

        var dlTargetPath = GetUpdateDownloadPath();
        LauncherCommon.RunInNewThread(() =>
        {
            try
            {
                var version = remoteServer.GetLatestVersion(
                    IsCurrentVersionBeta ? UpdateSourceChannel.Beta : UpdateSourceChannel.Stable,
                    SystemInfo.IsArm64System ? UpdateArch.Arm64 : UpdateArch.X64
                );

                Files.WriteAllText($"{LauncherPaths.Temp}DreamPortUpdateLog.md", version.Changelog);
                LauncherCommon.Log(
                    $"[Update] 远程最新版本: {version.VersionName}, 当前版本: {LauncherCommon.VersionBaseName}");
                if (!(SemVer.Parse(version.VersionName)
                        > SemVer.Parse(LauncherCommon.VersionBaseName)))
                    return;
                if (type == UpdateEnums.UpdateType.PromptOnly)
                {
                    if ((UpdateUi.Current?.ShowMessageBox(
                                Lang.Text(
                                    "Update.Available",
                                    LauncherCommon.VersionBaseName,
                                    version.VersionName),
                                Lang.Text("Update.Title"),
                                Lang.Text("Update.Action"),
                                Lang.Text("Common.Action.Cancel"))
                            ?? 0)
                        == 1)
                        UpdateUi.Current?.NavigateSetupUpdate();
                    return;
                    // 构造步骤加载器
                }

                List<Loading.LoaderBase> loaders =
                [
                    .. remoteServer.GetDownloadLoader(
                        IsCurrentVersionBeta
                            ? UpdateSourceChannel.Beta
                            : UpdateSourceChannel.Stable,
                        SystemInfo.IsArm64System ? UpdateArch.Arm64 : UpdateArch.X64,
                        dlTargetPath),


                    new Loading.LoaderTask<int, int>(
                        Lang.Text("Update.Task.Check"),
                        _ =>
                        {
                            var curHash = Files.GetFileSha256(dlTargetPath);
                            if (!string.Equals(curHash, version.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new Exception(
                                    Lang.Text(
                                        "Update.Error.Sha256Mismatch",
                                        version.Sha256,
                                        curHash));
                        })
                ];
                // 下载
                switch (type)
                {
                    case UpdateEnums.UpdateType.UpdateNow:
                        loaders.Add(
                            new Loading.LoaderTask<int, int>(
                                Lang.Text("Update.Task.Install"),
                                _ => UpdateRestart(true)));
                        break;

                    case UpdateEnums.UpdateType.Silent:
                        loaders.Add(
                            new Loading.LoaderTask<int, int>(
                                Lang.Text("Update.Task.Prepare"),
                                _ => isUpdateWaitingRestart = true));
                        break;

                    case UpdateEnums.UpdateType.DownloadAndPrompt:
                        loaders.Add(
                            new Loading.LoaderTask<int, int>(
                                Lang.Text("Update.Task.ShowButton"),
                                _ =>
                                {
                                    isUpdateWaitingRestart = true;
                                    UpdateUi.Current?.ShowUpdateRestartButton(
                                        "Main.Extra.UpdateRestart.ToolTipWithVersion",
                                        LauncherCommon.VersionBaseName,
                                        version.VersionName);
                                })
                            {
                                show = false
                            });
                        break;

                    case UpdateEnums.UpdateType.PromptOnly:
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(type), type, null);
                }

                loaders.Add(
                    new Loading.LoaderTask<int, int>(
                        Lang.Text("Update.Task.RefreshSettings"),
                        _ =>
                        {
                            UpdateUi.Current?.UpdateSetupButton(
                                "Update.Task.RestartInstall",
                                true);
                        })
                    {
                        show = false
                    });
                // 启动
                updateLoader =
                    new Loading.LoaderCombo<JsonObject>(Lang.Text("Update.Title"), loaders)
                    {
                        DisplayNameFactory = () => Lang.Text("Update.Title")
                    };
                updateLoader.Start();
                if (type == UpdateEnums.UpdateType.UpdateNow)
                {
                    Loading.LoaderTaskbarAdd(updateLoader);
                    UpdateUi.Current?.RefreshDownloadButton();
                }
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[Update] 获取启动器更新失败");
                if (type != UpdateEnums.UpdateType.Silent)
                    UpdateUi.Current?.ShowHint(
                        Lang.Text("Update.Error.FetchFailed"),
                        NotificationType.Error);
            }
        });
    }

    public static string GetUpdateDownloadPath()
    {
        return OperatingSystem.IsMacOS()
            ? Path.Combine(LauncherPaths.Temp, "Cache", "Update", "PCL-DreamPort-macOS.zip")
            : Path.Combine(LauncherPaths.ExecutableDirectory, "PCL", "PCL-DreamPort.exe");
    }

    public static void UpdateRestart(bool triggerRestartAndByEnd, bool triggerRestart = true)
    {
        if (OperatingSystem.IsMacOS())
        {
            UpdateRestartMacOs(triggerRestartAndByEnd, triggerRestart);
            return;
        }

        try
        {
            var fileName = Path.Combine(
                LauncherPaths.ExecutableDirectory,
                "PCL",
                "PCL-DreamPort.exe");
            if (!File.Exists(fileName))
            {
                LauncherCommon.Log("[System] 更新失败：未找到更新文件");
                return;
            }

            // id old new restart
            var text =
                $"update {Environment.ProcessId} \"{Basics.ExecutablePath}\" \"{fileName}\" {(triggerRestart ? "true" : "false")}";
            LauncherCommon.Log("[System] 更新程序启动，参数：" + text);
            Process.Start(
                new ProcessStartInfo(fileName)
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    Arguments = text
                });
            if (triggerRestartAndByEnd)
            {
                UpdateUi.Current?.ExitApplication(true);
                LauncherCommon.Log("[System] 已由于更新强制结束程序");
            }
        }
        catch (Win32Exception ex)
        {
            LauncherCommon.Log(ex, "自动更新时触发 Win32 错误，疑似被拦截");
            UpdateUi.Current?.ShowMessageBox(
                Lang.Text("Update.Error.UpdateBlockedMessage", LauncherPaths.ExecutableDirectory),
                Lang.Text("Update.Error.UpdateBlocked"),
                Lang.Text("Common.Action.Confirm"),
                "",
                "",
                true);
        }
    }

    private static void UpdateRestartMacOs(bool triggerRestartAndByEnd, bool triggerRestart)
    {
        isUpdateWaitingRestart = false;
        try
        {
            if (!DreamPortUpdateService.TryResolveCurrentMacAppBundlePath(out var targetAppPath))
            {
                LauncherCommon.Log("[System] 更新失败：无法定位可写的 macOS .app 应用包");
                LauncherCommon.RunInUi(() => UpdateUi.Current?.ShowHint(
                    Lang.Text("Update.ManualDownloadRequired"),
                    NotificationType.Error));
                return;
            }

            var zipPath = GetUpdateDownloadPath();
            if (!File.Exists(zipPath))
            {
                LauncherCommon.Log("[System] 更新失败：未找到 macOS 更新压缩包");
                LauncherCommon.RunInUi(() => UpdateUi.Current?.ShowHint(
                    Lang.Text("Update.Error.FetchFailed"),
                    NotificationType.Error));
                return;
            }

            var updateCacheDir = Path.Combine(LauncherPaths.Temp, "Cache", "Update");
            var stagedRoot = Path.Combine(updateCacheDir, "Staged");
            if (Directory.Exists(stagedRoot))
                Directory.Delete(stagedRoot, recursive: true);
            Directory.CreateDirectory(stagedRoot);

            var unzipInfo = new ProcessStartInfo("/usr/bin/ditto")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            unzipInfo.ArgumentList.Add("-x");
            unzipInfo.ArgumentList.Add("-k");
            unzipInfo.ArgumentList.Add(zipPath);
            unzipInfo.ArgumentList.Add(stagedRoot);
            using (var unzipProc = Process.Start(unzipInfo))
            {
                if (unzipProc is null)
                    throw new InvalidOperationException("无法启动 ditto 解压更新包");
                var stderr = unzipProc.StandardError.ReadToEnd();
                unzipProc.WaitForExit();
                if (unzipProc.ExitCode != 0)
                    throw new InvalidOperationException($"ditto 解压更新包失败（exit {unzipProc.ExitCode}）：{stderr}");
            }

            var stagedAppPath = Directory
                .EnumerateDirectories(stagedRoot, "*.app", SearchOption.AllDirectories)
                .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "Contents", "MacOS", "PCL.Launcher")));
            if (string.IsNullOrEmpty(stagedAppPath))
                throw new DirectoryNotFoundException("更新包内未找到有效的 PCL-梦港.app");

            var script = DreamPortUpdateService.BuildMacOsDittoReplaceScript(
                    Environment.ProcessId,
                    stagedAppPath,
                    targetAppPath,
                    relaunch: triggerRestart)
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            var scriptPath = Path.Combine(updateCacheDir, "apply-update.sh");
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LauncherCommon.Log($"[System] 启动 macOS 更新替换脚本：{targetAppPath} (relaunch={triggerRestart})");
            var applyInfo = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            applyInfo.ArgumentList.Add(scriptPath);
            Process.Start(applyInfo);

            if (triggerRestartAndByEnd)
            {
                UpdateUi.Current?.ExitApplication(true);
                LauncherCommon.Log("[System] 已由于更新结束 macOS 应用以完成替换");
            }
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[System] macOS 应用内更新替换失败");
            LauncherCommon.RunInUi(() => UpdateUi.Current?.ShowHint(
                Lang.Text("Update.Error.FetchFailed"),
                NotificationType.Error));
        }
    }

    /// <summary>确保 PathTemp 下的 Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。 如果不是，则下载一个。</summary>
    internal static void DownloadLatestPcl(Loading.LoaderBase? loaderToSyncProgress = null)
    {
        if (!HasConfiguredUpdateSource)
            throw new Exception(Lang.Text("Setup.Update.NoRemoteSource"));

        // 注意：如果要自行实现这个功能，请换用另一个文件路径，以免与官方版本冲突
        var latestPclPath = Path.Combine(LauncherPaths.Temp, "DreamPort-Latest.exe");
        var target = remoteServer.GetLatestVersion(
            UpdateSourceChannel.Stable,
            SystemInfo.IsArm64System ? UpdateArch.Arm64 : UpdateArch.X64);
        if (target is null)
            throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        if (File.Exists(latestPclPath)
            && (Files.GetFileSha256(latestPclPath) ?? "") == (target.Sha256 ?? ""))
        {
            LauncherCommon.Log("[System] 最新版 PCL-梦港 已存在，跳过下载");
            return;
        }

        if ((Files.GetFileSha256(Basics.ExecutablePath) ?? "")
            == (target.Sha256 ?? "")) // 正在使用的版本符合要求，直接拿来用
        {
            Files.CopyFile(Basics.ExecutablePath, latestPclPath);
            return;
        }

        var loaders = remoteServer.GetDownloadLoader(
            UpdateSourceChannel.Stable,
            SystemInfo.IsArm64System ? UpdateArch.Arm64 : UpdateArch.X64,
            latestPclPath);
        var loader =
            new Loading.LoaderCombo<int>(Lang.Text("Update.Task.DownloadLatestStable"), loaders)
            {
                DisplayNameFactory = () => Lang.Text("Update.Task.DownloadLatestStable")
            };
        loader.Start();
        loader.WaitForExit();
    }

    private static void LoadOnlineInfo()
    {
        ScheduleBasedOnConfig();
        AnnouncementService.Load();
    }

    private static void ScheduleBasedOnConfig()
    {
        if (!HasConfiguredUpdateSource)
        {
            LauncherCommon.Log("[Update] 未配置 PCL-梦港 远程更新源，跳过自动检查更新");
            return;
        }

        if (!SupportsInAppUpdate)
        {
            if (Config.Update.UpdateMode == LauncherAutoUpdateBehavior.Disable)
            {
                LauncherCommon.Log("[Update] 更新设置: 不自动检查更新");
                return;
            }

            // macOS/Linux 当前只检测并提示；安装交给 DMG/Flatpak/发布页。
            LauncherCommon.Log("[Update] 当前平台仅检查更新，不自动下载");
            UpdateStart(UpdateEnums.UpdateType.PromptOnly);
            return;
        }

        switch (Config.Update.UpdateMode)
        {
            case LauncherAutoUpdateBehavior.DownloadAndInstall:
                LauncherCommon.Log("[Update] 更新设置: 自动下载并安装更新");
                if (GetVersionStatus() != UpdateEnums.VersionStatus.Latest)
                    UpdateStart(UpdateEnums.UpdateType.Silent);
                break;

            case LauncherAutoUpdateBehavior.DownloadAndAnnounce:
                LauncherCommon.Log("[Update] 更新设置: 自动下载并提示更新");
                UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt);
                break;

            case LauncherAutoUpdateBehavior.AnnounceOnly:
                LauncherCommon.Log("[Update] 更新设置: 提示更新");
                UpdateStart(UpdateEnums.UpdateType.PromptOnly);
                break;

            case LauncherAutoUpdateBehavior.Disable:
            default:
                LauncherCommon.Log("[Update] 更新设置: 不自动检查更新");
                return;
        }
    }

    /// <summary>展示社区版提示</summary>
    /// <param name="IsUpdate">是否为更新时启动</param>
    public static void ShowCeAnnounce()
    {
        UpdateUi.Current?.ShowMessageBox(
            Lang.Text("Update.CommunityNotice.Body"),
            Lang.Text("Update.CommunityNotice.Title"),
            Lang.Text("Update.CommunityNotice.Confirm"));
    }
}