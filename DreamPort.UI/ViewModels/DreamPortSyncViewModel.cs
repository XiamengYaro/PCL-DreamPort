using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Config;
using DreamPort.Core.ServerPack;

namespace DreamPort.UI.ViewModels;

public sealed record DreamPortServerPackFileItemVm(
    string ActionLabel,
    string PathText,
    string DetailText);

public sealed record DreamPortModrinthModItemVm(
    string Title,
    string StatusLabel,
    string DetailText);

/// <summary>
/// 梦港「同步与工具」视图模型（Milestone A：服务器配套模组双源检测/兼容硬拦/增量同步与安全清理；后续扩展云存档/配置同步/AI 排障）。
/// </summary>
public partial class DreamPortSyncViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DreamPortSessionStore _store;
    private readonly Func<string, DreamPortApiClient> _clientFactory;
    private readonly Func<(string? InstanceDir, string? InstanceName, string? GameVersion, string? Loader)>? _getSelectedInstance;
    private readonly HttpClient? _httpClient;
    private readonly SynchronizationContext? _uiCtx;

    private DreamPortApiClient _api;
    private string _baseUrl = DreamPortBuildConfig.BaseUrl;
    private DreamPortServerPackManifestResult? _latestManifest;
    private CancellationTokenSource? _applyCts;
    private bool _loadedOnce;

    [ObservableProperty] private string _statusHint = "点击「检查配套模组」获取服务端最新配套资源与兼容要求。";
    [ObservableProperty] private DreamPortHintLevel _hintLevel = DreamPortHintLevel.Info;
    [ObservableProperty] private string _selectedInstanceSummary = "未选择 Minecraft 实例";
    [ObservableProperty] private string _serverPackSummary = "尚未检查服务端配套模组清单（GET /api/client/serverpack）";
    [ObservableProperty] private string _compatibilitySummary = string.Empty;
    [ObservableProperty] private bool _isCompatible;
    [ObservableProperty] private bool _hasCompatibilityWarning;
    [ObservableProperty] private string _progressSummary = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isApplying;

    public ObservableCollection<DreamPortServerPackFileItemVm> PendingFileActions { get; } = [];
    public ObservableCollection<DreamPortModrinthModItemVm> ModrinthItems { get; } = [];

    public DreamPortSyncViewModel(
        DreamPortSessionStore? store = null,
        Func<string, DreamPortApiClient>? clientFactory = null,
        Func<(string? InstanceDir, string? InstanceName, string? GameVersion, string? Loader)>? getSelectedInstance = null,
        HttpClient? httpClient = null)
    {
        _store = store ?? new DreamPortSessionStore();
        _clientFactory = clientFactory ?? (url => new DreamPortApiClient(url));
        _getSelectedInstance = getSelectedInstance;
        _httpClient = httpClient;
        _uiCtx = SynchronizationContext.Current;

        var saved = _store.Load();
        _baseUrl = saved.BaseUrl;
        _api = _clientFactory(_baseUrl);
        if (!string.IsNullOrEmpty(saved.Token))
            _api.SetToken(saved.Token);

        RefreshInstanceSummary();
    }

    public bool IsNotBusy => !IsBusy;
    public bool CanCheck => IsNotBusy && !IsApplying;
    public bool CanApply => IsNotBusy && !IsApplying && _latestManifest is { Ok: true } && IsCompatible;
    public bool HasPendingFileActions => PendingFileActions.Count > 0;
    public bool HasNoPendingFileActions => PendingFileActions.Count == 0;
    public bool HasModrinthItems => ModrinthItems.Count > 0;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnIsCompatibleChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    public void RefreshInstanceSummary()
    {
        var (dir, name, ver, loader) = ResolveSelectedInstance();
        if (string.IsNullOrWhiteSpace(dir))
        {
            SelectedInstanceSummary = "当前未选择 Minecraft 实例，请先在「启动」页选择目标实例后再同步。";
            return;
        }

        var verDesc = string.IsNullOrWhiteSpace(ver) ? "版本未知" : ver;
        var loaderDesc = string.IsNullOrWhiteSpace(loader) ? "vanilla" : loader;
        SelectedInstanceSummary = $"目标实例：{name ?? Path.GetFileName(dir)} | MC {verDesc} | 加载器：{loaderDesc} | 路径：{dir}";
    }

    public void Reload()
    {
        _ = CheckServerPackAsync();
    }

    public async Task InitializeOrSyncAsync(CancellationToken ct = default)
    {
        SyncSessionAndInstance();
        var saved = _store.Load();
        if (_loadedOnce && string.Equals(_baseUrl, saved.BaseUrl, StringComparison.Ordinal))
            return;

        _loadedOnce = true;
        await CheckServerPackAsync(ct);
    }

    [RelayCommand]
    public async Task CheckServerPackAsync(CancellationToken ct = default)
    {
        if (IsBusy || IsApplying)
            return;

        IsBusy = true;
        try
        {
            SyncSessionAndInstance();
            var (instDir, instName, instVer, instLoader) = ResolveSelectedInstance();
            PendingFileActions.Clear();
            ModrinthItems.Clear();
            HasCompatibilityWarning = false;
            CompatibilitySummary = string.Empty;
            ProgressSummary = string.Empty;

            var manifest = await _api.GetServerPackManifestAsync(ct);
            _latestManifest = manifest;

            if (!manifest.Ok)
            {
                IsCompatible = false;
                ServerPackSummary = manifest.Message ?? "服务端未启用或无法获取配套模组清单。";
                SetStatus(ServerPackSummary, manifest.FailureReason == DreamPortFailureReason.Network ? DreamPortHintLevel.Error : DreamPortHintLevel.Warning);
                NotifyCollectionStates();
                return;
            }

            var reqLoaders = manifest.Loaders.Count == 0 ? "不限" : string.Join(" / ", manifest.Loaders);
            var reqVer = string.IsNullOrWhiteSpace(manifest.GameVersion) ? "不限" : manifest.GameVersion;
            ServerPackSummary = $"服务端包版本：{manifest.Version} | 要求：MC {reqVer} ({reqLoaders}) | 直传文件：{manifest.Files.Count} 个 | Modrinth 模组：{manifest.ModrinthMods.Count} 个";

            if (string.IsNullOrWhiteSpace(instDir) || !Directory.Exists(instDir))
            {
                IsCompatible = false;
                HasCompatibilityWarning = true;
                CompatibilitySummary = "请先在「启动」页选择一个有效的本地游戏实例，再执行兼容性检查与同步。";
                SetStatus(CompatibilitySummary, DreamPortHintLevel.Warning);
                NotifyCollectionStates();
                return;
            }

            var compat = DreamPortServerPackService.CheckCompatibility(manifest.GameVersion, manifest.Loaders, instVer, instLoader);
            if (!compat.Compatible)
            {
                IsCompatible = false;
                HasCompatibilityWarning = true;
                CompatibilitySummary = compat.Reason ?? "当前实例不兼容服务器配套模组。";
                SetStatus($"已拦截同步：{CompatibilitySummary}", DreamPortHintLevel.Error);
                NotifyCollectionStates();
                return;
            }

            IsCompatible = true;
            var instanceId = !string.IsNullOrWhiteSpace(instName) ? instName : Path.GetFileName(instDir);
            DreamPortServerPackPlan plan;
            try
            {
                plan = await DreamPortServerPackService.PlanSyncAsync(manifest, instDir, instanceId, ct);
            }
            catch (Exception ex)
            {
                IsCompatible = false;
                HasCompatibilityWarning = true;
                CompatibilitySummary = ex.Message;
                SetStatus(ex.Message, DreamPortHintLevel.Error);
                NotifyCollectionStates();
                return;
            }

            if (plan.Rejected.Count > 0)
            {
                IsCompatible = false;
                HasCompatibilityWarning = true;
                CompatibilitySummary = $"检测到 {plan.Rejected.Count} 条不安全清单路径（{string.Join("、", plan.Rejected.Take(3))}），已整单拒绝。";
                SetStatus(CompatibilitySummary, DreamPortHintLevel.Error);
                NotifyCollectionStates();
                return;
            }

            foreach (var dl in plan.Downloads)
            {
                var action = dl.Reason == "missing" ? "下载新增" : "更新覆盖";
                PendingFileActions.Add(new DreamPortServerPackFileItemVm(action, dl.Path, $"大小：{FormatBytes(dl.Size)}"));
            }

            foreach (var del in plan.Deletes)
            {
                PendingFileActions.Add(new DreamPortServerPackFileItemVm("清理移除", del, "服务端清单已移除（仅清理受管文件）"));
            }

            foreach (var skip in plan.SkippedDisabled)
            {
                PendingFileActions.Add(new DreamPortServerPackFileItemVm("保留禁用", skip, "检测到 .disabled 文件，保持跳过不动"));
            }

            if (manifest.ModrinthMods.Count > 0)
            {
                var ownsHttp = _httpClient is null;
                var mrHttp = _httpClient ?? CreateDefaultModrinthClient();
                try
                {
                    var mrPlan = await DreamPortServerPackService.InspectModrinthModsAsync(
                        manifest.ModrinthMods,
                        instDir,
                        instVer ?? manifest.GameVersion,
                        instLoader ?? manifest.Loaders.FirstOrDefault() ?? "vanilla",
                        mrHttp,
                        ct);

                    foreach (var mr in mrPlan)
                    {
                        var status = mr.Disabled
                            ? "保留禁用"
                            : mr.AlreadyInstalled
                                ? "已安装"
                                : mr.ResolveError is not null
                                    ? "解析失败"
                                    : "待安装";
                        var detail = mr.ResolveError
                            ?? (mr.FileName is not null ? $"{mr.FileName} ({FormatBytes(mr.Size)})" : mr.Slug);
                        ModrinthItems.Add(new DreamPortModrinthModItemVm(mr.Title, status, detail));
                    }
                }
                finally
                {
                    if (ownsHttp)
                        mrHttp.Dispose();
                }
            }

            var pendingMrCount = ModrinthItems.Count(m => m.StatusLabel == "待安装");
            if (plan.Downloads.Count == 0 && plan.Deletes.Count == 0 && pendingMrCount == 0)
                SetStatus($"✓ 当前实例与服务端配套模组已完全一致（已是最新：{plan.UpToDateCount} 个直传文件）。", DreamPortHintLevel.Info);
            else
                SetStatus($"✓ 检查完成：待下载/更新 {plan.Downloads.Count} 项、清理 {plan.Deletes.Count} 项、Modrinth 待安装 {pendingMrCount} 项，可点击「开始同步」。", DreamPortHintLevel.Info);

            NotifyCollectionStates();
            OnPropertyChanged(nameof(CanApply));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ApplyServerPackAsync()
    {
        if (IsBusy || IsApplying)
            return;

        SyncSessionAndInstance();
        var (instDir, instName, instVer, instLoader) = ResolveSelectedInstance();
        if (string.IsNullOrWhiteSpace(instDir) || !Directory.Exists(instDir))
        {
            SetStatus("请先选择有效的 Minecraft 实例目录。", DreamPortHintLevel.Error);
            return;
        }

        var manifest = _latestManifest;
        if (manifest is null || !manifest.Ok)
        {
            manifest = await _api.GetServerPackManifestAsync();
            _latestManifest = manifest;
        }

        if (!manifest.Ok)
        {
            SetStatus(manifest.Message ?? "无法获取配套模组清单。", DreamPortHintLevel.Error);
            return;
        }

        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _applyCts = cts;
        IsApplying = true;
        IsBusy = true;

        try
        {
            SetStatus("正在同步服务器配套模组...", DreamPortHintLevel.Info);
            var instanceId = !string.IsNullOrWhiteSpace(instName) ? instName : Path.GetFileName(instDir);
            var progress = new Progress<DreamPortServerPackProgress>(p =>
                DispatchUi(() => ProgressSummary = $"{p.Done}/{p.Total} · {p.Message}"));

            var res = await DreamPortServerPackService.ApplySyncAsync(
                _api,
                manifest,
                instDir,
                instVer ?? string.Empty,
                instLoader ?? string.Empty,
                instanceId,
                modrinthHttpClient: _httpClient,
                progress: progress,
                ct: token);

            if (res.Cancelled)
            {
                SetStatus(res.Message, DreamPortHintLevel.Warning);
            }
            else if (res.Ok)
            {
                SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            }
            else
            {
                var detail = res.Failed.Count > 0 ? $"（{string.Join("；", res.Failed.Take(3))}）" : string.Empty;
                SetStatus($"{res.Message}{detail}", DreamPortHintLevel.Error);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _applyCts, null, cts);
            cts.Dispose();
            IsApplying = false;
            IsBusy = false;
        }

        await CheckServerPackAsync();
    }

    [RelayCommand]
    private void CancelServerPack()
    {
        var cts = Interlocked.Exchange(ref _applyCts, null);
        if (cts is null)
            return;
        try { cts.Cancel(); } catch { /* ignore */ }
    }

    public ValueTask DisposeAsync()
    {
        CancelServerPack();
        _api.Dispose();
        return ValueTask.CompletedTask;
    }

    private (string? Dir, string? Name, string? GameVersion, string? Loader) ResolveSelectedInstance()
    {
        if (_getSelectedInstance is null)
            return (null, null, null, null);
        try
        {
            return _getSelectedInstance();
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private void SyncSessionAndInstance()
    {
        var saved = _store.Load();
        if (!string.Equals(_baseUrl, saved.BaseUrl, StringComparison.Ordinal))
        {
            _baseUrl = saved.BaseUrl;
            _api.Dispose();
            _api = _clientFactory(_baseUrl);
        }
        _api.SetToken(saved.Token);
        RefreshInstanceSummary();
    }

    private void NotifyCollectionStates()
    {
        OnPropertyChanged(nameof(HasPendingFileActions));
        OnPropertyChanged(nameof(HasNoPendingFileActions));
        OnPropertyChanged(nameof(HasModrinthItems));
    }

    private void DispatchUi(Action action)
    {
        if (_uiCtx is not null && !ReferenceEquals(SynchronizationContext.Current, _uiCtx))
            _uiCtx.Post(_ => action(), null);
        else
            action();
    }

    private void SetStatus(string message, DreamPortHintLevel level)
    {
        StatusHint = message;
        HintLevel = level;
    }

    private static HttpClient CreateDefaultModrinthClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DreamPortBuildConfig.UserAgent);
        return http;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:F1} KB";
        return $"{bytes / (1024d * 1024d):F2} MB";
    }
}
