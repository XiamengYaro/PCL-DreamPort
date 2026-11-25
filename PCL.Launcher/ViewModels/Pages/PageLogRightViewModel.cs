using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Diagnostics;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Platform;
using PCL.Core.UI;
using PCL.Core.UI.Dialogs;
using PCL.Core.UI.Hints;
using PCL.Core.ViewModel;
using PCL.Views.Pages.Log;

namespace PCL;

/// <summary>游戏实时日志页右栏的视图模型：日志行、计数、自动滚动与最大行数。</summary>
public partial class PageLogRightViewModel : ViewModelBase
{
    private uint _debugCount;
    private uint _errorCount;
    private uint _fatalCount;
    private uint _infoCount;
    private bool _isAiDiagnoseRunning;
    private PageLogLeft? _left;
    private uint _warnCount;

    [ObservableProperty] public partial bool AutoScroll { get; set; } = true;

    public string DebugCountText => $"{_debugCount} {Lang.Text("LogPage.Level.Debug")}";

    public string ErrorCountText => $"{_errorCount} {Lang.Text("LogPage.Level.Error")}";

    public string FatalCountText => $"{_fatalCount} {Lang.Text("LogPage.Level.Fatal")}";

    public string InfoCountText => $"{_infoCount} {Lang.Text("LogPage.Level.Info")}";

    public string WarnCountText => $"{_warnCount} {Lang.Text("LogPage.Level.Warn")}";

    [ObservableProperty] public partial bool HasLog { get; set; }

    [ObservableProperty] public partial bool IsAiDiagnoseEnabled { get; set; }

    [ObservableProperty] public partial bool IsExportStackDumpEnabled { get; set; }

    [ObservableProperty] public partial bool IsKillEnabled { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<PageLogLeft.LogLine>? Lines { get; set; }

    [ObservableProperty]
    public partial int MaxLogLines { get; set; } = Math.Clamp(Config.System.MaxGameLog, 0, 29);

    partial void OnMaxLogLinesChanged(int value)
    {
        if (value >= 0 && Config.System.MaxGameLog != value)
            Config.System.MaxGameLog = value;
    }

    /// <summary>接入左栏当前选中的日志缓冲区。</summary>
    public void Load(PageLogLeft? left)
    {
        _left = left;
        HasLog = left is { CurrentUuid: > 0, CurrentLog: not null };
        if (!HasLog)
        {
            Lines = null;
            IsKillEnabled = false;
            IsExportStackDumpEnabled = false;
            IsAiDiagnoseEnabled = false;
            RefreshCounters();
            return;
        }

        MaxLogLines = Math.Clamp(Config.System.MaxGameLog, 0, 29);
        Lines = left!.LogBuffers.GetValueOrDefault(left.CurrentUuid)?.Lines;
        RefreshOperationButtons();
        RefreshCounters();
    }

    /// <summary>左栏追加日志后刷新计数；调用方负责滚动。</summary>
    public bool OnLogAppended(PageLogLeft.LogBuffer buffer)
    {
        var left = _left;
        if (left is null || left.CurrentUuid <= 0)
            return false;
        if (!ReferenceEquals(left.LogBuffers.GetValueOrDefault(left.CurrentUuid), buffer))
            return false;

        RefreshCounters();
        return true;
    }

    public void RefreshOperationButtons()
    {
        var log = _left?.CurrentLog;
        if (log is null)
        {
            IsKillEnabled = false;
            IsExportStackDumpEnabled = false;
            IsAiDiagnoseEnabled = false;
            return;
        }

        IsKillEnabled = !log.gameProcess.HasExited;
        IsExportStackDumpEnabled =
            !log.gameProcess.HasExited && !string.IsNullOrWhiteSpace(log.jStackPath);
        IsAiDiagnoseEnabled = !_isAiDiagnoseRunning;
    }

    public void RefreshCounters()
    {
        var log = _left?.CurrentLog;
        var fatal = log?.countFatal ?? 0u;
        var error = log?.countError ?? 0u;
        var warn = log?.countWarn ?? 0u;
        var info = log?.countInfo ?? 0u;
        var debug = log?.countDebug ?? 0u;
        _fatalCount = fatal;
        _errorCount = error;
        _warnCount = warn;
        _infoCount = info;
        _debugCount = debug;
        OnPropertyChanged(nameof(FatalCountText));
        OnPropertyChanged(nameof(ErrorCountText));
        OnPropertyChanged(nameof(WarnCountText));
        OnPropertyChanged(nameof(InfoCountText));
        OnPropertyChanged(nameof(DebugCountText));
    }

    public void OnGameExit()
    {
        IsKillEnabled = false;
        IsExportStackDumpEnabled = false;
    }

    /// <summary>按级别与最大行数过滤日志；当前页面未暴露级别筛选 UI，保留给命令/宿主复用。</summary>
    public static IReadOnlyList<PageLogLeft.LogLine> FilterLines(
        IEnumerable<PageLogLeft.LogLine> source,
        GameLogLevel? level = null)
    {
        var filtered = level is null
            ? source
            : source.Where(line => line.Level == level);
        var max = PageLogLeft.GetMaxLogCount();
        return max == ulong.MaxValue
            ? filtered.ToList()
            : filtered.TakeLast((int)Math.Min(max, int.MaxValue)).ToList();
    }

    /// <summary>清空当前缓冲区；返回是否确实清空了内容。</summary>
    public bool ClearLog()
    {
        var left = _left;
        if (left is null || left.CurrentUuid <= 0)
            return false;
        if (!left.LogBuffers.TryGetValue(left.CurrentUuid, out var buffer))
            return false;
        buffer.Lines.Clear();
        return true;
    }

    public async Task ExportLogAsync()
    {
        var log = _left?.CurrentLog;
        if (log is null)
            return;

        var savePath = await SystemDialogs.SelectSaveFileAsync(
            Lang.Text("LogPage.Export.SelectLocation"),
            Lang.Text("LogPage.Export.GameLog.FileName", log.version?.Name ?? ""),
            Lang.Text("LogPage.Export.GameLog.Filter"));
        if (savePath.Length < 3)
            return;

        try
        {
            await Task.Run(() => File.WriteAllLines(savePath, log.fullLog));
            HintService.Show(Hints.Success(Lang.Text("LogPage.Export.Success")));
            Basics.OpenPath(savePath);
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                Lang.Text("LogPage.Error.OperationFailed"),
                LauncherLogLevel.Feedback,
                userSummary: Lang.Text("LogPage.Error.OperationFailed"));
        }
    }

    public void KillProcess()
    {
        var log = _left?.CurrentLog;
        if (log is null)
            return;
        if (log.State > GameWatcher.Watcher.MinecraftState.Running)
            return;

        log.Kill();
        HintService.Show(
            Hints.Success(
                Lang.Text("LogPage.Action.GameClosed", log.version?.Name ?? string.Empty)));
    }

    public async Task ExportStackDumpAsync()
    {
        var log = _left?.CurrentLog;
        if (log is null)
            return;

        var formattedDate = DateTime
            .Now.ToString("G", CultureInfo.InvariantCulture)
            .Replace("/", "-")
            .Replace(":", ".")
            .Replace(" ", "_");
        var savePath = await SystemDialogs.SelectSaveFileAsync(
            Lang.Text("LogPage.Export.SelectLocation"),
            Lang.Text("LogPage.ExportStack.FileName", formattedDate),
            Lang.Text("LogPage.ExportStack.Filter"));
        if (savePath.Length < 3)
            return;

        HintService.Show(Hints.Info(Lang.Text("LogPage.ExportStack.Progress")));
        IsExportStackDumpEnabled = false;
        try
        {
            var dump = await Task.Run(log.ExportStackDump);
            await Task.Run(() => File.WriteAllLines(savePath, dump));
            HintService.Show(Hints.Success(Lang.Text("LogPage.ExportStack.Success")));
            Basics.OpenPath(savePath);
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                Lang.Text("LogPage.Error.OperationFailed"),
                LauncherLogLevel.Feedback,
                userSummary: Lang.Text("LogPage.Error.OperationFailed"));
        }
        finally
        {
            RefreshOperationButtons();
        }
    }

    public async Task AiDiagnoseAsync()
    {
        var log = _left?.CurrentLog;
        if (log is null || _isAiDiagnoseRunning)
            return;

        var session = new DreamPortSessionStore().Load();
        if (string.IsNullOrWhiteSpace(session.Token))
        {
            HintService.Show(Hints.Warning("请先在「工具 → 梦港账号」登录后再使用 AI 日志诊断"));
            return;
        }

        string? instDir = null;
        try
        {
            instDir = log.version?.PathIndie ?? log.version?.PathInstance;
        }
        catch
        {
            instDir = log.version?.PathInstance;
        }

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(session.Username))
            identities.Add(session.Username);
        try
        {
            if (ProfileService.Current is { } cur)
            {
                if (!string.IsNullOrWhiteSpace(cur.UserName))
                    identities.Add(cur.UserName);
                if (!string.IsNullOrWhiteSpace(cur.Uuid))
                    identities.Add(cur.Uuid);
            }
            foreach (var p in ProfileService.Profiles)
            {
                if (!string.IsNullOrWhiteSpace(p.UserName))
                    identities.Add(p.UserName);
                if (!string.IsNullOrWhiteSpace(p.Uuid))
                    identities.Add(p.Uuid);
            }
        }
        catch
        {
            // ignore
        }

        int? modCount = null;
        if (!string.IsNullOrWhiteSpace(instDir))
        {
            var modsDir = Path.Combine(instDir, "mods");
            if (Directory.Exists(modsDir))
            {
                try
                {
                    modCount = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly).Count();
                }
                catch
                {
                    modCount = null;
                }
            }
        }

        string? exitCode = null;
        try
        {
            if (log.gameProcess.HasExited)
                exitCode = log.gameProcess.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            exitCode = null;
        }

        var runtimeLines = log.fullLog.ToArray();
        var info = log.version?.Info;
        var loader = info switch
        {
            null => null,
            { HasCleanroom: true } => "cleanroom",
            { HasNeoForge: true } => "neoforge",
            { HasForge: true } => "forge",
            { HasFabric: true } => "fabric",
            { HasQuilt: true } => "quilt",
            { HasLegacyFabric: true } => "legacyfabric",
            { HasLiteLoader: true } => "liteloader",
            { HasLabyMod: true } => "labymod",
            { HasOptiFine: true } => "optifine",
            _ => "vanilla"
        };

        var payload = DreamPortLogDiagnoseService.BuildPayload(new DreamPortLogDiagnoseBuildInput(
            InstanceDir: instDir,
            InstanceName: log.version?.Name ?? "未命名实例",
            GameVersion: info?.VanillaName,
            Loader: loader,
            ModCount: modCount,
            ExitCode: exitCode,
            RuntimeLines: runtimeLines,
            ExtraIdentities: [.. identities]));

        var confirm = await DialogService.Instance.ShowAsync(
            new TextDialogRequest
            {
                Title = "确认发起 AI 游戏日志诊断",
                Content = $"即将向 DreamPort AI 发送已脱敏的日志片段（{payload.CharCount} 字符，{(payload.Truncated ? "已按预算裁剪" : "完整片段")}）。\n"
                          + "发送前已自动隐去玩家名、用户目录路径及各类访问令牌，仅携带实例版本、加载器与模组数量。",
                Buttons = DialogButtons.From(
                    DialogButton.Create("开始 AI 诊断"),
                    DialogButton.Create("取消"))
            });
        if (confirm.Button != 1)
            return;

        _isAiDiagnoseRunning = true;
        RefreshOperationButtons();
        HintService.Show(Hints.Info("正在提交脱敏日志并等待 AI 分析，请稍候..."));

        try
        {
            using var api = new DreamPortApiClient(session.BaseUrl);
            api.SetToken(session.Token);
            var res = await DreamPortLogDiagnoseService.RunAsync(api, payload);
            if (!res.Ok)
            {
                HintService.Show(Hints.Warning(res.Error ?? res.DisplayText));
                return;
            }

            var dialogChoice = await DialogService.Instance.ShowAsync(
                new TextDialogRequest
                {
                    Title = "DreamPort AI 日志诊断结果",
                    Content = res.DisplayText,
                    Buttons = DialogButtons.From(
                        DialogButton.Create("复制诊断结果"),
                        DialogButton.Create("关闭"))
                });
            if (dialogChoice.Button == 1 && ApplicationRuntime.GetService<IClipboardService>() is { } clipboard)
            {
                await clipboard.SetTextAsync(res.DisplayText);
                HintService.Show(Hints.Success("已复制 AI 诊断结果", false));
            }
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "AI 日志诊断失败", LauncherLogLevel.Feedback, userSummary: ex.Message);
        }
        finally
        {
            _isAiDiagnoseRunning = false;
            RefreshOperationButtons();
        }
    }

    public static object GetMaxLogHintText(object value)
    {
        return value switch
        {
            _ when (int)value <= 5 => ((int)value * 10 + 50).ToString(
                CultureInfo.CurrentCulture),
            _ when (int)value <= 13 => ((int)value * 50 - 150).ToString(
                CultureInfo.CurrentCulture),
            _ when (int)value <= 28 => ((int)value * 100 - 800).ToString(
                CultureInfo.CurrentCulture),
            _ => Lang.Text("LogPage.MaxLines.Unlimited")
        };
    }
}