using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.CloudSave;
using DreamPort.Core.Config;
using DreamPort.Core.Diagnostics;
using DreamPort.Core.ProfileSync;
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

public sealed partial class DreamPortLocalWorldItemVm : ObservableObject
{
    private readonly Func<DreamPortLocalWorldItemVm, Task> _onUpload;

    public DreamPortLocalWorldItemVm(
        string worldName,
        string fullPath,
        long sizeBytes,
        DateTimeOffset lastWriteUtc,
        Func<DreamPortLocalWorldItemVm, Task> onUpload)
    {
        WorldName = worldName;
        FullPath = fullPath;
        SizeBytes = sizeBytes;
        LastWriteUtc = lastWriteUtc;
        _onUpload = onUpload;
    }

    public string WorldName { get; }
    public string FullPath { get; }
    public long SizeBytes { get; }
    public DateTimeOffset LastWriteUtc { get; }

    public string HeaderText => WorldName;
    public string MetaText => $"大小：{FormatSize(SizeBytes)} | 最近修改：{LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    [RelayCommand]
    private Task Upload() => _onUpload(this);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:F1} KB";
        return $"{bytes / (1024d * 1024d):F2} MB";
    }
}

public sealed partial class DreamPortCloudSaveItemVm : ObservableObject
{
    private readonly Func<DreamPortCloudSaveItemVm, Task> _onRestore;
    private readonly Func<DreamPortCloudSaveItemVm, Task> _onRename;
    private readonly Func<DreamPortCloudSaveItemVm, Task> _onDelete;

    public DreamPortCloudSaveItemVm(
        DreamPortCloudSaveItem item,
        Func<DreamPortCloudSaveItemVm, Task> onRestore,
        Func<DreamPortCloudSaveItemVm, Task> onRename,
        Func<DreamPortCloudSaveItemVm, Task> onDelete)
    {
        Id = item.Id;
        WorldName = item.WorldName;
        InstanceName = item.InstanceName;
        Status = item.Status;
        SizeBytes = item.SizeBytes;
        Sha256 = item.Sha256;
        CreatedAtMs = item.CreatedAtMs;
        Note = item.Note;
        _editableDisplayName = item.DisplayName;
        _onRestore = onRestore;
        _onRename = onRename;
        _onDelete = onDelete;
    }

    public long Id { get; }
    public string WorldName { get; }
    public string InstanceName { get; }
    public string Status { get; }
    public long SizeBytes { get; }
    public string Sha256 { get; }
    public long CreatedAtMs { get; }
    public string Note { get; }

    [ObservableProperty] private string _editableDisplayName;

    public bool IsReady => string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase);

    public string MetaText
    {
        get
        {
            var dt = CreatedAtMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "未知时间";
            var shaShort = Sha256.Length >= 8 ? Sha256[..8] : Sha256;
            var statusLabel = IsReady ? "就绪" : "上传未完成";
            var notePart = string.IsNullOrWhiteSpace(Note) ? string.Empty : $" | 备注：{Note}";
            return $"原存档：{WorldName} | 实例：{InstanceName} | {FormatSize(SizeBytes)} | {statusLabel} | {dt} | sha256:{shaShort}{notePart}";
        }
    }

    [RelayCommand]
    private Task Restore() => _onRestore(this);

    [RelayCommand]
    private Task Rename() => _onRename(this);

    [RelayCommand]
    private Task Delete() => _onDelete(this);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:F1} KB";
        return $"{bytes / (1024d * 1024d):F2} MB";
    }
}

public sealed record DreamPortLogDiagnoseItemVm(
    string HeaderText,
    string EvidenceText,
    string AdviceText);

/// <summary>
/// 梦港「同步与工具」视图模型：
/// 1) 服务器配套模组双源检测/兼容硬拦/增量同步；
/// 2) 玩家存档云备份（分块断点续传 + SHA-256 + 安全防覆盖恢复）；
/// 3) 多设备白名单配置云同步（乐观锁 revision 冲突选择）；
/// 4) AI 启动日志排障（本地脱敏 + 异步提交与短轮询）。
/// </summary>
public partial class DreamPortSyncViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DreamPortSessionStore _store;
    private readonly Func<string, DreamPortApiClient> _clientFactory;
    private readonly Func<(string? InstanceDir, string? InstanceName, string? GameVersion, string? Loader)>? _getSelectedInstance;
    private readonly HttpClient? _httpClient;
    private readonly DreamPortProfileSyncService _profileSync;
    private readonly Func<IReadOnlyList<string>>? _getExtraIdentities;
    private readonly SynchronizationContext? _uiCtx;

    private DreamPortApiClient _api;
    private string _baseUrl = DreamPortBuildConfig.BaseUrl;
    private DreamPortServerPackManifestResult? _latestManifest;
    private DreamPortLogDiagnosePayload? _preparedDiagnosePayload;
    private CancellationTokenSource? _applyCts;
    private CancellationTokenSource? _cloudSaveCts;
    private CancellationTokenSource? _diagnoseCts;
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

    // Cloud Save state
    [ObservableProperty] private string _cloudQuotaSummary = "登录后可查看梦港云存档配额与备份列表。";
    [ObservableProperty] private string _cloudSaveDisplayName = string.Empty;
    [ObservableProperty] private string _cloudSaveNote = string.Empty;
    [ObservableProperty] private string _cloudSaveProgressText = string.Empty;
    [ObservableProperty] private bool _isCloudSaveRunning;

    // Profile Sync state
    [ObservableProperty] private string _profileSyncSummary = "登录后可查看多设备白名单配置云同步状态（仅同步界面/启动/下载等便携偏好，不同步令牌、Java 路径与代理帐密）。";
    [ObservableProperty] private string _profileConflictSummary = string.Empty;
    [ObservableProperty] private bool _hasProfileConflict;

    // AI Log Diagnose state
    [ObservableProperty] private string _logDiagnoseStatusSummary = "点击「检查/准备脱敏日志」读取当前实例的崩溃报告与 latest.log（发送前自动脱敏玩家名、用户目录与令牌）。";
    [ObservableProperty] private string _logDiagnosePreviewSummary = string.Empty;
    [ObservableProperty] private string _logDiagnoseResultSummary = string.Empty;
    [ObservableProperty] private string _logDiagnoseAskForText = string.Empty;
    [ObservableProperty] private bool _isLogDiagnoseRunning;

    public ObservableCollection<DreamPortServerPackFileItemVm> PendingFileActions { get; } = [];
    public ObservableCollection<DreamPortModrinthModItemVm> ModrinthItems { get; } = [];
    public ObservableCollection<DreamPortLocalWorldItemVm> LocalWorlds { get; } = [];
    public ObservableCollection<DreamPortCloudSaveItemVm> CloudBackups { get; } = [];
    public ObservableCollection<DreamPortLogDiagnoseItemVm> LogDiagnoseItems { get; } = [];

    public DreamPortSyncViewModel(
        DreamPortSessionStore? store = null,
        Func<string, DreamPortApiClient>? clientFactory = null,
        Func<(string? InstanceDir, string? InstanceName, string? GameVersion, string? Loader)>? getSelectedInstance = null,
        HttpClient? httpClient = null,
        Func<DreamPortPortableProfile>? captureProfile = null,
        Action<DreamPortPortableProfile>? applyProfile = null,
        Func<IReadOnlyList<string>>? getExtraIdentities = null,
        string? profileSyncDataDir = null)
    {
        _store = store ?? new DreamPortSessionStore();
        _clientFactory = clientFactory ?? (url => new DreamPortApiClient(url));
        _getSelectedInstance = getSelectedInstance;
        _httpClient = httpClient;
        _getExtraIdentities = getExtraIdentities;
        _profileSync = new DreamPortProfileSyncService(
            dataDirectory: profileSyncDataDir,
            capture: captureProfile,
            apply: applyProfile);
        _uiCtx = SynchronizationContext.Current;

        var saved = _store.Load();
        _baseUrl = saved.BaseUrl;
        _api = _clientFactory(_baseUrl);
        if (!string.IsNullOrEmpty(saved.Token))
            _api.SetToken(saved.Token);

        RefreshInstanceSummary();
        ReloadLocalWorlds();
    }

    public bool IsNotBusy => !IsBusy;
    public bool CanCheck => IsNotBusy && !IsApplying;
    public bool CanApply => IsNotBusy && !IsApplying && _latestManifest is { Ok: true } && IsCompatible;
    public bool CanOperatePhase5 => IsNotBusy && !IsApplying && !IsCloudSaveRunning && !IsLogDiagnoseRunning;
    public bool CanSubmitLogDiagnose => CanOperatePhase5 && !string.IsNullOrWhiteSpace(_api.CurrentToken);
    public bool HasPendingFileActions => PendingFileActions.Count > 0;
    public bool HasNoPendingFileActions => PendingFileActions.Count == 0;
    public bool HasModrinthItems => ModrinthItems.Count > 0;
    public bool HasLocalWorlds => LocalWorlds.Count > 0;
    public bool HasNoLocalWorlds => LocalWorlds.Count == 0;
    public bool HasCloudBackups => CloudBackups.Count > 0;
    public bool HasNoCloudBackups => CloudBackups.Count == 0;
    public bool HasLogDiagnoseResult => !string.IsNullOrWhiteSpace(LogDiagnoseResultSummary) || LogDiagnoseItems.Count > 0;
    public bool HasLogDiagnoseItems => LogDiagnoseItems.Count > 0;
    public bool HasLogDiagnoseAskFor => !string.IsNullOrWhiteSpace(LogDiagnoseAskForText);

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanOperatePhase5));
        OnPropertyChanged(nameof(CanSubmitLogDiagnose));
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanOperatePhase5));
        OnPropertyChanged(nameof(CanSubmitLogDiagnose));
    }

    partial void OnIsCloudSaveRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOperatePhase5));
        OnPropertyChanged(nameof(CanSubmitLogDiagnose));
    }

    partial void OnIsLogDiagnoseRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOperatePhase5));
        OnPropertyChanged(nameof(CanSubmitLogDiagnose));
    }

    partial void OnIsCompatibleChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    partial void OnLogDiagnoseResultSummaryChanged(string value)
        => OnPropertyChanged(nameof(HasLogDiagnoseResult));

    partial void OnLogDiagnoseAskForTextChanged(string value)
        => OnPropertyChanged(nameof(HasLogDiagnoseAskFor));

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

    public void ReloadLocalWorlds()
    {
        LocalWorlds.Clear();
        var (dir, _, _, _) = ResolveSelectedInstance();
        foreach (var w in DreamPortCloudSaveService.ListLocalWorlds(dir))
        {
            LocalWorlds.Add(new DreamPortLocalWorldItemVm(
                w.WorldName,
                w.FullPath,
                w.SizeBytes,
                w.LastWriteUtc,
                UploadLocalWorldAsync));
        }

        OnPropertyChanged(nameof(HasLocalWorlds));
        OnPropertyChanged(nameof(HasNoLocalWorlds));
    }

    public void Reload()
    {
        _ = InitializeOrSyncAsync();
    }

    public async Task InitializeOrSyncAsync(CancellationToken ct = default)
    {
        SyncSessionAndInstance();
        var saved = _store.Load();
        if (_loadedOnce && string.Equals(_baseUrl, saved.BaseUrl, StringComparison.Ordinal))
            return;

        _loadedOnce = true;
        await CheckServerPackAsync(ct);

        if (!string.IsNullOrWhiteSpace(_api.CurrentToken))
        {
            await RefreshCloudSavesAsync(ct);
            await RefreshProfileSyncAsync(ct);
        }

        await RefreshLogDiagnoseStatusAsync(ct);
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

    // ---------------------------------------------------------
    // 2) 存档云备份（分块断点续传 + SHA-256 + 安全防覆盖恢复）
    // ---------------------------------------------------------

    [RelayCommand]
    public async Task RefreshCloudSavesAsync(CancellationToken ct = default)
    {
        if (IsCloudSaveRunning)
            return;

        SyncSessionAndInstance();
        ReloadLocalWorlds();

        if (string.IsNullOrWhiteSpace(_api.CurrentToken))
        {
            CloudBackups.Clear();
            CloudQuotaSummary = "请先在「工具 → 梦港账号」登录已过审账号后再管理云存档。";
            NotifyCloudSaveCollections();
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.GetCloudSaveListAsync(ct);
            CloudBackups.Clear();
            if (!res.Ok)
            {
                CloudQuotaSummary = res.Message ?? "读取云存档列表失败。";
                NotifyCloudSaveCollections();
                return;
            }

            foreach (var item in res.Items)
            {
                CloudBackups.Add(new DreamPortCloudSaveItemVm(
                    item,
                    RestoreCloudBackupAsync,
                    RenameCloudBackupAsync,
                    DeleteCloudBackupAsync));
            }

            var readyCount = res.Items.Count(x => string.Equals(x.Status, "ready", StringComparison.OrdinalIgnoreCase));
            CloudQuotaSummary = $"已用：{FormatBytes(res.UsedBytes)} / {FormatBytes(res.QuotaBytes)} | 就绪备份：{readyCount}/{res.MaxBackupsPerUser} 份 | 单份上限：{FormatBytes(res.MaxBackupBytes)} | 保留 {res.RetentionDays} 天";
            NotifyCloudSaveCollections();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UploadLocalWorldAsync(DreamPortLocalWorldItemVm world)
    {
        if (IsCloudSaveRunning || IsApplying)
            return;

        SyncSessionAndInstance();
        if (string.IsNullOrWhiteSpace(_api.CurrentToken))
        {
            SetStatus("请先登录已过审的梦港账号后再上传云存档。", DreamPortHintLevel.Warning);
            return;
        }

        var (instDir, instName, instVer, _) = ResolveSelectedInstance();
        if (string.IsNullOrWhiteSpace(instDir) || !Directory.Exists(instDir))
        {
            SetStatus("请先在「启动」页选择有效的本地游戏实例。", DreamPortHintLevel.Error);
            return;
        }

        var cts = new CancellationTokenSource();
        _cloudSaveCts = cts;
        IsCloudSaveRunning = true;
        IsBusy = true;
        CloudSaveProgressText = $"准备上传存档「{world.WorldName}」...";

        DreamPortCloudSaveUploadResult res;
        try
        {
            var displayName = string.IsNullOrWhiteSpace(CloudSaveDisplayName)
                ? world.WorldName
                : CloudSaveDisplayName.Trim();

            res = await DreamPortCloudSaveService.UploadWorldAsync(
                _api,
                instDir,
                instName ?? Path.GetFileName(instDir),
                world.WorldName,
                instVer,
                displayName,
                CloudSaveNote,
                msg => DispatchUi(() => CloudSaveProgressText = msg),
                cts.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _cloudSaveCts, null, cts);
            cts.Dispose();
            IsCloudSaveRunning = false;
            IsBusy = false;
        }

        CloudSaveProgressText = string.Empty;
        if (res.Ok)
        {
            CloudSaveDisplayName = string.Empty;
            CloudSaveNote = string.Empty;
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await RefreshCloudSavesAsync();
        }
        else
        {
            SetStatus(res.Message, DreamPortHintLevel.Error);
        }
    }

    public async Task RestoreCloudBackupAsync(DreamPortCloudSaveItemVm item)
    {
        if (IsCloudSaveRunning || IsApplying)
            return;

        SyncSessionAndInstance();
        var (instDir, _, _, _) = ResolveSelectedInstance();
        if (string.IsNullOrWhiteSpace(instDir) || !Directory.Exists(instDir))
        {
            SetStatus("请先在「启动」页选择要恢复到的本地游戏实例。", DreamPortHintLevel.Error);
            return;
        }

        var cts = new CancellationTokenSource();
        _cloudSaveCts = cts;
        IsCloudSaveRunning = true;
        IsBusy = true;
        CloudSaveProgressText = $"准备下载并恢复「{item.EditableDisplayName}」...";

        DreamPortCloudSaveRestoreResult res;
        try
        {
            res = await DreamPortCloudSaveService.RestoreBackupAsync(
                _api,
                instDir,
                item.Id,
                item.WorldName,
                msg => DispatchUi(() => CloudSaveProgressText = msg),
                cts.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _cloudSaveCts, null, cts);
            cts.Dispose();
            IsCloudSaveRunning = false;
            IsBusy = false;
        }

        CloudSaveProgressText = string.Empty;
        if (res.Ok)
        {
            ReloadLocalWorlds();
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
        }
        else
        {
            SetStatus(res.Message, DreamPortHintLevel.Error);
        }
    }

    public async Task RenameCloudBackupAsync(DreamPortCloudSaveItemVm item)
    {
        if (IsBusy || IsCloudSaveRunning)
            return;

        SyncSessionAndInstance();
        IsBusy = true;
        try
        {
            var res = await _api.RenameCloudSaveAsync(item.Id, item.EditableDisplayName);
            if (!res.Ok)
            {
                SetStatus(res.Message ?? "重命名云备份失败", DreamPortHintLevel.Error);
                return;
            }

            SetStatus($"✓ {res.Message ?? "云备份已重命名"}", DreamPortHintLevel.Info);
            await RefreshCloudSavesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteCloudBackupAsync(DreamPortCloudSaveItemVm item)
    {
        if (IsBusy || IsCloudSaveRunning)
            return;

        SyncSessionAndInstance();
        IsBusy = true;
        try
        {
            var res = await _api.DeleteCloudSaveAsync(item.Id);
            if (!res.Ok)
            {
                SetStatus(res.Message ?? "删除云备份失败", DreamPortHintLevel.Error);
                return;
            }

            SetStatus($"✓ {res.Message ?? "云备份已删除"}", DreamPortHintLevel.Info);
            await RefreshCloudSavesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelCloudSave()
    {
        var cts = Interlocked.Exchange(ref _cloudSaveCts, null);
        if (cts is null)
            return;
        try { cts.Cancel(); } catch { /* ignore */ }
    }

    // ---------------------------------------------------------
    // 3) 多设备白名单配置云同步（乐观锁 revision 冲突处理）
    // ---------------------------------------------------------

    [RelayCommand]
    public async Task RefreshProfileSyncAsync(CancellationToken ct = default)
    {
        SyncSessionAndInstance();
        if (string.IsNullOrWhiteSpace(_api.CurrentToken))
        {
            ProfileSyncSummary = "请先在「工具 → 梦港账号」登录已过审账号后再检查云端配置状态。";
            HasProfileConflict = false;
            ProfileConflictSummary = string.Empty;
            return;
        }

        IsBusy = true;
        try
        {
            var status = await _profileSync.GetStatusAsync(_api, ct);
            UpdateProfileSyncDisplay(status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task PushProfileSyncAsync() => PushProfileSyncInternalAsync(force: false);

    [RelayCommand]
    public Task ForcePushProfileSyncAsync() => PushProfileSyncInternalAsync(force: true);

    private async Task PushProfileSyncInternalAsync(bool force)
    {
        if (IsBusy)
            return;

        SyncSessionAndInstance();
        if (string.IsNullOrWhiteSpace(_api.CurrentToken))
        {
            SetStatus("请先登录已过审的梦港账号后再同步配置。", DreamPortHintLevel.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _profileSync.PushAsync(_api, force);
            if (res.IsConflict)
            {
                HasProfileConflict = true;
                var remoteUpdated = res.ConflictRemoteUpdatedAtMs is > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(res.ConflictRemoteUpdatedAtMs.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : "未知时间";
                var verNote = string.IsNullOrWhiteSpace(res.ConflictClientVersion) ? string.Empty : $"（客户端 {res.ConflictClientVersion}）";
                ProfileConflictSummary = $"检测到云端配置版本冲突：云端已被另一台设备更新至 Revision {res.ConflictRemoteRevision ?? 0}{verNote}（更新时间 {remoteUpdated}）。请选择「拉取云端覆盖本机」或「强制用本机覆盖云端」。";
                SetStatus(res.Message ?? "云端配置版本冲突，请手动选择合并方向。", DreamPortHintLevel.Warning);
                return;
            }

            if (!res.Ok)
            {
                SetStatus(res.Message ?? "同步本机配置失败", DreamPortHintLevel.Error);
                return;
            }

            HasProfileConflict = false;
            ProfileConflictSummary = string.Empty;
            if (res.Status is not null)
                UpdateProfileSyncDisplay(res.Status);
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task PullProfileSyncAsync()
    {
        if (IsBusy)
            return;

        SyncSessionAndInstance();
        if (string.IsNullOrWhiteSpace(_api.CurrentToken))
        {
            SetStatus("请先登录已过审的梦港账号后再拉取云端配置。", DreamPortHintLevel.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _profileSync.PullAsync(_api);
            if (!res.Ok)
            {
                SetStatus(res.Message ?? "拉取云端配置失败", DreamPortHintLevel.Error);
                return;
            }

            HasProfileConflict = false;
            ProfileConflictSummary = string.Empty;
            if (res.Status is not null)
                UpdateProfileSyncDisplay(res.Status);
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ResetProfileSyncAsync()
    {
        if (IsBusy)
            return;

        SyncSessionAndInstance();
        if (string.IsNullOrWhiteSpace(_api.CurrentToken))
        {
            SetStatus("请先登录梦港账号。", DreamPortHintLevel.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _profileSync.ResetAsync(_api);
            if (!res.Ok)
            {
                SetStatus(res.Message ?? "清除云端配置失败", DreamPortHintLevel.Error);
                return;
            }

            HasProfileConflict = false;
            ProfileConflictSummary = string.Empty;
            if (res.Status is not null)
                UpdateProfileSyncDisplay(res.Status);
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------
    // 4) AI 启动日志排障（脱敏提交 + 异步轮询）
    // ---------------------------------------------------------

    [RelayCommand]
    public async Task RefreshLogDiagnoseStatusAsync(CancellationToken ct = default)
    {
        var status = await _api.GetLogDiagnoseStatusAsync(ct);
        if (!status.Ok)
        {
            LogDiagnoseStatusSummary = status.Message ?? "暂无法获取 AI 日志诊断服务状态。";
            return;
        }

        if (!status.Enabled)
        {
            LogDiagnoseStatusSummary = "站点管理员暂未开启 AI 日志诊断服务。";
            return;
        }

        var quotaLabel = status.DailyPerUser > 0 ? $"每账号每日 {status.DailyPerUser} 次" : "不限次数";
        LogDiagnoseStatusSummary = $"AI 日志诊断服务已启用（{quotaLabel}；登录即可使用，发送前自动脱敏玩家名/用户目录/令牌）。";
    }

    [RelayCommand]
    public void PrepareLogDiagnose()
    {
        SyncSessionAndInstance();
        var (instDir, instName, instVer, instLoader) = ResolveSelectedInstance();
        if (string.IsNullOrWhiteSpace(instDir) || !Directory.Exists(instDir))
        {
            SetStatus("请先在「启动」页选择一个有效的 Minecraft 实例再准备脱敏日志。", DreamPortHintLevel.Warning);
            return;
        }

        int? modCount = null;
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

        var identities = CollectKnownIdentities();
        var input = new DreamPortLogDiagnoseBuildInput(
            InstanceDir: instDir,
            InstanceName: string.IsNullOrWhiteSpace(instName) ? Path.GetFileName(instDir) : instName,
            GameVersion: instVer,
            Loader: instLoader,
            ModCount: modCount,
            ExtraIdentities: identities);

        var payload = DreamPortLogDiagnoseService.BuildPayload(input);
        _preparedDiagnosePayload = payload;
        var crashLabel = string.IsNullOrWhiteSpace(payload.CrashReportName) ? "无崩溃报告" : $"含崩溃报告 {payload.CrashReportName}";
        var truncLabel = payload.Truncated ? "（已按预算裁剪末尾关键片段）" : "（完整片段）";
        LogDiagnosePreviewSummary = $"已生成脱敏日志：{payload.CharCount} 字符，{crashLabel}{truncLabel}。上下文仅包含实例版本、加载器与模组数量，不含文件路径与登录凭据。";
    }

    [RelayCommand]
    public async Task SubmitLogDiagnoseAsync()
    {
        if (IsLogDiagnoseRunning || IsCloudSaveRunning)
            return;

        SyncSessionAndInstance();
        if (string.IsNullOrWhiteSpace(_api.CurrentToken))
        {
            SetStatus("请先在「工具 → 梦港账号」登录梦港账号后再使用 AI 日志诊断。", DreamPortHintLevel.Warning);
            return;
        }

        if (_preparedDiagnosePayload is null)
            PrepareLogDiagnose();
        if (_preparedDiagnosePayload is null)
            return;

        var cts = new CancellationTokenSource();
        _diagnoseCts = cts;
        IsLogDiagnoseRunning = true;
        IsBusy = true;
        LogDiagnoseItems.Clear();
        LogDiagnoseAskForText = string.Empty;
        LogDiagnoseResultSummary = "正在提交脱敏日志并等待 AI 分析...";

        DreamPortLogDiagnoseExecutionResult res;
        try
        {
            res = await DreamPortLogDiagnoseService.RunAsync(
                _api,
                _preparedDiagnosePayload,
                msg => DispatchUi(() => LogDiagnoseResultSummary = msg),
                ct: cts.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _diagnoseCts, null, cts);
            cts.Dispose();
            IsLogDiagnoseRunning = false;
            IsBusy = false;
        }

        if (!res.Ok || res.Verdict is null)
        {
            LogDiagnoseResultSummary = res.DisplayText;
            OnPropertyChanged(nameof(HasLogDiagnoseItems));
            SetStatus(res.Error ?? res.DisplayText, DreamPortHintLevel.Warning);
            return;
        }

        var verdict = res.Verdict;
        var header = string.Equals(verdict.Verdict, "likely_cause", StringComparison.OrdinalIgnoreCase)
            ? "定位结论：已发现可能原因"
            : "定位结论：日志信息不足，建议结合本地诊断或补充更多信息";
        var tokenNote = res.TotalTokens > 0 ? $"（耗时 {res.LatencyMs} ms，{res.TotalTokens} tokens）" : string.Empty;
        LogDiagnoseResultSummary = $"{header}{tokenNote}\n{verdict.Summary}";

        foreach (var item in verdict.Items)
        {
            LogDiagnoseItems.Add(new DreamPortLogDiagnoseItemVm(
                HeaderText: $"{item.Title}（置信度 {Math.Round(item.Confidence * 100)}%）",
                EvidenceText: string.IsNullOrWhiteSpace(item.Evidence) ? string.Empty : $"依据：{item.Evidence}",
                AdviceText: $"建议：{item.Advice}"));
        }

        if (verdict.AskFor.Count > 0)
            LogDiagnoseAskForText = "可补充信息：\n" + string.Join("\n", verdict.AskFor.Select(a => $"- {a}"));

        OnPropertyChanged(nameof(HasLogDiagnoseItems));
        SetStatus("✓ AI 日志诊断完成，结论已保留在当前卡片。", DreamPortHintLevel.Info);
    }

    [RelayCommand]
    private void CancelLogDiagnose()
    {
        var cts = Interlocked.Exchange(ref _diagnoseCts, null);
        if (cts is null)
            return;
        try { cts.Cancel(); } catch { /* ignore */ }
    }

    public ValueTask DisposeAsync()
    {
        CancelServerPack();
        CancelCloudSave();
        CancelLogDiagnose();
        _api.Dispose();
        return ValueTask.CompletedTask;
    }

    private void UpdateProfileSyncDisplay(DreamPortProfileSyncStatusSummary status)
    {
        if (!status.Ok)
        {
            ProfileSyncSummary = status.Message ?? "读取云端配置状态失败。";
            return;
        }

        if (!status.Enabled)
        {
            ProfileSyncSummary = "站点管理员暂未开启多设备配置云同步功能。";
            return;
        }

        var dirtyLabel = status.LocalDirty ? "有未同步的本机更改" : "本机与上次同步一致";
        var remoteTime = status.RemoteUpdatedAtMs is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(status.RemoteUpdatedAtMs.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "尚未上传";
        ProfileSyncSummary = $"云端 Revision：{status.RemoteRevision} | 本机基线 Revision：{status.LocalRevision} | 状态：{dirtyLabel} | 最近云端更新：{remoteTime} | 上限：{FormatBytes(status.MaxBytes)}";
    }

    private IReadOnlyList<string> CollectKnownIdentities()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var session = _store.Load();
        if (!string.IsNullOrWhiteSpace(session.Username))
            set.Add(session.Username.Trim());

        if (_getExtraIdentities is not null)
        {
            try
            {
                foreach (var id in _getExtraIdentities())
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        set.Add(id.Trim());
                }
            }
            catch
            {
                // ignore
            }
        }

        return set.ToArray();
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
        OnPropertyChanged(nameof(CanSubmitLogDiagnose));
    }

    private void NotifyCollectionStates()
    {
        OnPropertyChanged(nameof(HasPendingFileActions));
        OnPropertyChanged(nameof(HasNoPendingFileActions));
        OnPropertyChanged(nameof(HasModrinthItems));
    }

    private void NotifyCloudSaveCollections()
    {
        OnPropertyChanged(nameof(HasLocalWorlds));
        OnPropertyChanged(nameof(HasNoLocalWorlds));
        OnPropertyChanged(nameof(HasCloudBackups));
        OnPropertyChanged(nameof(HasNoCloudBackups));
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
