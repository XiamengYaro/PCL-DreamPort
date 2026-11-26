using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DreamPort.Core.Api;
using PCL.Core.App;
using PCL.Core.Logging;
using PclConfig = PCL.Core.App.Config;

namespace DreamPort.Core.ProfileSync;

public sealed record DreamPortPortableUpdateSettings(
    LauncherAutoUpdateBehavior UpdateMode,
    UpdateChannel UpdateChannel);

public sealed record DreamPortPortableDownloadSettings(
    int ThreadLimit,
    int FileConnectionLimit,
    int SpeedLimit,
    DownloadHttpMode HttpMode,
    int FileSource,
    int VersionListSource,
    bool AutoSelectInstance,
    bool IgnoreQuilt,
    bool AutoInstallDependencies,
    int NameFormatV1,
    int NameFormatV2);

public sealed record DreamPortPortableLaunchSettings(
    int MemoryAllocationMode,
    int CustomMemorySize,
    int CustomInitialMemorySize,
    bool LockMemory,
    JvmPreferredIpStack PreferredIpStack,
    LauncherVisibility LauncherVisibility,
    GameProcessPriority ProcessPriority,
    int GameWindowWidth,
    int GameWindowHeight,
    GameWindowSizeMode GameWindowMode,
    string Title,
    string TypeInfo,
    int IndieSolutionV2);

public sealed record DreamPortPortablePreferenceSettings(
    bool LockWindowSize,
    bool ShowLaunchingHint,
    LauncherTitleType WindowTitleType,
    string WindowTitleCustomText,
    bool TopBarLeftAlign,
    string Font,
    string MotdFont,
    bool DetailedInstanceClassification,
    string Language,
    string FormatCulture,
    string Region,
    ColorMode ColorMode,
    ColorTheme DarkColor,
    ColorTheme LightColor,
    int WindowOpacity,
    int DreamPortHomepageMode,
    int MusicVolume,
    bool MusicStopInGame,
    bool MusicStartInGame,
    bool MusicStartOnStartup,
    bool MusicShufflePlayback,
    bool AutoChangeLanguage,
    int MaxGameLog,
    bool DisableHardwareAcceleration);

/// <summary>
/// 严格白名单化的多设备可移植配置（schema=1）。
/// 绝不包含账号档案（Profiles）、MirrorChyanKey、代理帐密、SelectedJava、JvmArgs、GameArgs、PreLaunchCommand、登录令牌或本机路径。
/// </summary>
public sealed record DreamPortPortableProfile(
    int Schema,
    DreamPortPortableUpdateSettings Update,
    DreamPortPortableDownloadSettings Download,
    DreamPortPortableLaunchSettings Launch,
    DreamPortPortablePreferenceSettings Preference);

public sealed record DreamPortProfileSyncStatusSummary(
    bool Ok,
    bool Enabled,
    long RemoteRevision,
    long LocalRevision,
    bool HasRemotePayload,
    bool LocalDirty,
    long? RemoteUpdatedAtMs,
    long? LocalLastSyncedAtMs,
    int MaxBytes,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortProfileSyncOpResult(
    bool Ok,
    bool IsConflict,
    DreamPortProfileSyncStatusSummary? Status = null,
    long? ConflictRemoteRevision = null,
    long? ConflictRemoteUpdatedAtMs = null,
    string? ConflictClientVersion = null,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

internal sealed class DreamPortProfileSyncLocalState
{
    public long Revision { get; set; }
    public long LastSyncedAtMs { get; set; }
    public string? LastHash { get; set; }
}

/// <summary>
/// 梦港多设备白名单配置云同步服务（基于乐观锁 revision 防静默覆盖）。
/// </summary>
public sealed class DreamPortProfileSyncService
{
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;
    private readonly Func<DreamPortPortableProfile> _capture;
    private readonly Action<DreamPortPortableProfile> _apply;

    public DreamPortProfileSyncService(
        string? dataDirectory = null,
        Func<DreamPortPortableProfile>? capture = null,
        Action<DreamPortPortableProfile>? apply = null)
    {
        var dir = string.IsNullOrWhiteSpace(dataDirectory) ? Paths.Data : dataDirectory;
        _stateFilePath = Path.Combine(dir, "dreamport-profile-sync.json");
        _capture = capture ?? CaptureFromPclConfig;
        _apply = apply ?? ApplyToPclConfig;
    }

    public static DreamPortPortableProfile CaptureFromPclConfig()
    {
        return new DreamPortPortableProfile(
            Schema: CurrentSchema,
            Update: new DreamPortPortableUpdateSettings(
                UpdateMode: PclConfig.Update.UpdateMode,
                UpdateChannel: PclConfig.Update.UpdateChannel),
            Download: new DreamPortPortableDownloadSettings(
                ThreadLimit: PclConfig.Download.ThreadLimit,
                FileConnectionLimit: PclConfig.Download.FileConnectionLimit,
                SpeedLimit: PclConfig.Download.SpeedLimit,
                HttpMode: PclConfig.Download.HttpMode,
                FileSource: PclConfig.Download.FileSource,
                VersionListSource: PclConfig.Download.VersionListSource,
                AutoSelectInstance: PclConfig.Download.AutoSelectInstance,
                IgnoreQuilt: PclConfig.Download.Comp.IgnoreQuilt,
                AutoInstallDependencies: PclConfig.Download.Comp.AutoInstallDependencies,
                NameFormatV1: PclConfig.Download.Comp.NameFormatV1,
                NameFormatV2: PclConfig.Download.Comp.NameFormatV2),
            Launch: new DreamPortPortableLaunchSettings(
                MemoryAllocationMode: PclConfig.Launch.MemoryAllocationMode,
                CustomMemorySize: PclConfig.Launch.CustomMemorySize,
                CustomInitialMemorySize: PclConfig.Launch.CustomInitialMemorySize,
                LockMemory: PclConfig.Launch.LockMemory,
                PreferredIpStack: PclConfig.Launch.PreferredIpStack,
                LauncherVisibility: PclConfig.Launch.LauncherVisibility,
                ProcessPriority: PclConfig.Launch.ProcessPriority,
                GameWindowWidth: PclConfig.Launch.GameWindowWidth,
                GameWindowHeight: PclConfig.Launch.GameWindowHeight,
                GameWindowMode: PclConfig.Launch.GameWindowMode,
                Title: PclConfig.Launch.Title,
                TypeInfo: PclConfig.Launch.TypeInfo,
                IndieSolutionV2: PclConfig.Launch.IndieSolutionV2),
            Preference: new DreamPortPortablePreferenceSettings(
                LockWindowSize: PclConfig.Preference.LockWindowSize,
                ShowLaunchingHint: PclConfig.Preference.ShowLaunchingHint,
                WindowTitleType: PclConfig.Preference.WindowTitleType,
                WindowTitleCustomText: PclConfig.Preference.WindowTitleCustomText,
                TopBarLeftAlign: PclConfig.Preference.TopBarLeftAlign,
                Font: PclConfig.Preference.Font,
                MotdFont: PclConfig.Preference.MotdFont,
                DetailedInstanceClassification: PclConfig.Preference.DetailedInstanceClassification,
                Language: PclConfig.Preference.Localization.Language,
                FormatCulture: PclConfig.Preference.Localization.FormatCulture,
                Region: PclConfig.Preference.Localization.Region,
                ColorMode: PclConfig.Preference.Theme.ColorMode,
                DarkColor: PclConfig.Preference.Theme.DarkColor,
                LightColor: PclConfig.Preference.Theme.LightColor,
                WindowOpacity: PclConfig.Preference.Theme.WindowOpacity,
                DreamPortHomepageMode: PclConfig.Preference.Homepage.DreamPortMode,
                MusicVolume: PclConfig.Preference.Music.Volume,
                MusicStopInGame: PclConfig.Preference.Music.StopInGame,
                MusicStartInGame: PclConfig.Preference.Music.StartInGame,
                MusicStartOnStartup: PclConfig.Preference.Music.StartOnStartup,
                MusicShufflePlayback: PclConfig.Preference.Music.ShufflePlayback,
                AutoChangeLanguage: PclConfig.Tool.AutoChangeLanguage,
                MaxGameLog: PclConfig.System.MaxGameLog,
                DisableHardwareAcceleration: PclConfig.System.DisableHardwareAcceleration));
    }

    public static void ApplyToPclConfig(DreamPortPortableProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (Enum.IsDefined(profile.Update.UpdateMode))
            PclConfig.Update.UpdateMode = profile.Update.UpdateMode;
        if (Enum.IsDefined(profile.Update.UpdateChannel))
            PclConfig.Update.UpdateChannel = profile.Update.UpdateChannel;

        PclConfig.Download.ThreadLimit = Math.Clamp(profile.Download.ThreadLimit, 0, 255);
        PclConfig.Download.FileConnectionLimit = Math.Clamp(profile.Download.FileConnectionLimit, 0, 31);
        PclConfig.Download.SpeedLimit = Math.Clamp(profile.Download.SpeedLimit, 0, 100);
        if (Enum.IsDefined(profile.Download.HttpMode))
            PclConfig.Download.HttpMode = profile.Download.HttpMode;
        PclConfig.Download.FileSource = Math.Clamp(profile.Download.FileSource, 0, 2);
        PclConfig.Download.VersionListSource = Math.Clamp(profile.Download.VersionListSource, 0, 2);
        PclConfig.Download.AutoSelectInstance = profile.Download.AutoSelectInstance;
        PclConfig.Download.Comp.IgnoreQuilt = profile.Download.IgnoreQuilt;
        PclConfig.Download.Comp.AutoInstallDependencies = profile.Download.AutoInstallDependencies;
        PclConfig.Download.Comp.NameFormatV1 = Math.Clamp(profile.Download.NameFormatV1, 0, 4);
        PclConfig.Download.Comp.NameFormatV2 = Math.Clamp(profile.Download.NameFormatV2, 0, 4);

        PclConfig.Launch.MemoryAllocationMode = Math.Clamp(profile.Launch.MemoryAllocationMode, 0, 2);
        PclConfig.Launch.CustomMemorySize = Math.Clamp(profile.Launch.CustomMemorySize, 0, 100);
        PclConfig.Launch.CustomInitialMemorySize = Math.Clamp(profile.Launch.CustomInitialMemorySize, 0, 100);
        PclConfig.Launch.LockMemory = profile.Launch.LockMemory;
        if (Enum.IsDefined(profile.Launch.PreferredIpStack))
            PclConfig.Launch.PreferredIpStack = profile.Launch.PreferredIpStack;
        if (Enum.IsDefined(profile.Launch.LauncherVisibility))
            PclConfig.Launch.LauncherVisibility = profile.Launch.LauncherVisibility;
        if (Enum.IsDefined(profile.Launch.ProcessPriority))
            PclConfig.Launch.ProcessPriority = profile.Launch.ProcessPriority;
        PclConfig.Launch.GameWindowWidth = Math.Clamp(profile.Launch.GameWindowWidth, 100, 7680);
        PclConfig.Launch.GameWindowHeight = Math.Clamp(profile.Launch.GameWindowHeight, 100, 4320);
        if (Enum.IsDefined(profile.Launch.GameWindowMode))
            PclConfig.Launch.GameWindowMode = profile.Launch.GameWindowMode;
        PclConfig.Launch.Title = ClampString(profile.Launch.Title, 128);
        PclConfig.Launch.TypeInfo = ClampString(profile.Launch.TypeInfo, 64);
        PclConfig.Launch.IndieSolutionV2 = Math.Clamp(profile.Launch.IndieSolutionV2, 0, 4);

        PclConfig.Preference.LockWindowSize = profile.Preference.LockWindowSize;
        PclConfig.Preference.ShowLaunchingHint = profile.Preference.ShowLaunchingHint;
        if (Enum.IsDefined(profile.Preference.WindowTitleType))
            PclConfig.Preference.WindowTitleType = profile.Preference.WindowTitleType;
        PclConfig.Preference.WindowTitleCustomText = ClampString(profile.Preference.WindowTitleCustomText, 128);
        PclConfig.Preference.TopBarLeftAlign = profile.Preference.TopBarLeftAlign;
        PclConfig.Preference.Font = ClampString(profile.Preference.Font, 128);
        PclConfig.Preference.MotdFont = ClampString(profile.Preference.MotdFont, 128);
        PclConfig.Preference.DetailedInstanceClassification = profile.Preference.DetailedInstanceClassification;
        PclConfig.Preference.Localization.Language = ClampString(profile.Preference.Language, 32, "auto");
        PclConfig.Preference.Localization.FormatCulture = ClampString(profile.Preference.FormatCulture, 32, "auto");
        PclConfig.Preference.Localization.Region = ClampString(profile.Preference.Region, 32, "auto");
        if (Enum.IsDefined(profile.Preference.ColorMode))
            PclConfig.Preference.Theme.ColorMode = profile.Preference.ColorMode;
        // 严格限定合规 PCL 蓝色配色主题（含天依蓝）
        if (Enum.IsDefined(profile.Preference.DarkColor))
            PclConfig.Preference.Theme.DarkColor = profile.Preference.DarkColor;
        if (Enum.IsDefined(profile.Preference.LightColor))
            PclConfig.Preference.Theme.LightColor = profile.Preference.LightColor;
        PclConfig.Preference.Theme.WindowOpacity = Math.Clamp(profile.Preference.WindowOpacity, 0, 1000);
        PclConfig.Preference.Homepage.DreamPortMode = Math.Clamp(profile.Preference.DreamPortHomepageMode, 0, 2);
        PclConfig.Preference.Music.Volume = Math.Clamp(profile.Preference.MusicVolume, 0, 1000);
        PclConfig.Preference.Music.StopInGame = profile.Preference.MusicStopInGame;
        PclConfig.Preference.Music.StartInGame = profile.Preference.MusicStartInGame;
        PclConfig.Preference.Music.StartOnStartup = profile.Preference.MusicStartOnStartup;
        PclConfig.Preference.Music.ShufflePlayback = profile.Preference.MusicShufflePlayback;
        PclConfig.Tool.AutoChangeLanguage = profile.Preference.AutoChangeLanguage;
        PclConfig.System.MaxGameLog = Math.Clamp(profile.Preference.MaxGameLog, 0, 29);
        PclConfig.System.DisableHardwareAcceleration = profile.Preference.DisableHardwareAcceleration;
    }

    public static JsonObject SerializePayload(DreamPortPortableProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var inner = JsonSerializer.SerializeToNode(profile with { Schema = CurrentSchema }, JsonOptions)
            ?? throw new InvalidOperationException("序列化白名单配置失败。");
        return new JsonObject
        {
            ["schema"] = CurrentSchema,
            ["pclDreamPort"] = inner
        };
    }

    public static bool TryParsePayload(string? payloadJson, out DreamPortPortableProfile? profile, out string? error)
    {
        profile = null;
        error = null;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "云端配置内容为空。";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "云端配置格式无效。";
                return false;
            }

            if (!root.TryGetProperty("schema", out var schemaEl)
                || schemaEl.ValueKind != JsonValueKind.Number
                || !schemaEl.TryGetInt32(out var schema)
                || schema != CurrentSchema)
            {
                error = "云端配置 schema 版本不受支持。";
                return false;
            }

            var targetEl = root.TryGetProperty("pclDreamPort", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            var parsed = JsonSerializer.Deserialize<DreamPortPortableProfile>(targetEl.GetRawText(), JsonOptions);
            if (parsed is null || parsed.Update is null || parsed.Download is null || parsed.Launch is null || parsed.Preference is null)
            {
                error = "云端配置缺少 PCL-DreamPort 节点字段。";
                return false;
            }

            if (!Enum.IsDefined(parsed.Preference.DarkColor) || !Enum.IsDefined(parsed.Preference.LightColor))
            {
                error = "云端配置包含非法的配色主题枚举值。";
                return false;
            }

            profile = parsed with { Schema = CurrentSchema };
            return true;
        }
        catch (Exception ex)
        {
            error = $"解析云端配置失败：{ex.Message}";
            return false;
        }
    }

    public async Task<DreamPortProfileSyncStatusSummary> GetStatusAsync(DreamPortApiClient api, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var remote = await api.GetProfileSyncAsync(ct).ConfigureAwait(false);
        var localState = ReadLocalState();
        var currentHash = ComputeProfileHash(_capture());

        if (!remote.Ok)
        {
            return new DreamPortProfileSyncStatusSummary(
                Ok: false,
                Enabled: false,
                RemoteRevision: 0,
                LocalRevision: localState.Revision,
                HasRemotePayload: false,
                LocalDirty: !string.Equals(currentHash, localState.LastHash, StringComparison.Ordinal),
                RemoteUpdatedAtMs: null,
                LocalLastSyncedAtMs: localState.LastSyncedAtMs > 0 ? localState.LastSyncedAtMs : null,
                MaxBytes: remote.MaxBytes,
                Message: remote.Message,
                FailureReason: remote.FailureReason);
        }

        return BuildSummary(remote, localState, currentHash);
    }

    public async Task<DreamPortProfileSyncOpResult> PushAsync(DreamPortApiClient api, bool force = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var currentProfile = _capture();
        var payload = SerializePayload(currentProfile);
        var currentHash = ComputeProfileHash(currentProfile);
        var localState = ReadLocalState();
        var baseRevision = localState.Revision;

        if (force)
        {
            var latest = await api.GetProfileSyncAsync(ct).ConfigureAwait(false);
            if (!latest.Ok)
                return new DreamPortProfileSyncOpResult(false, false, Message: latest.Message ?? "读取远端最新修订号失败", FailureReason: latest.FailureReason);
            baseRevision = latest.Revision;
        }

        var putRes = await api.PutProfileSyncAsync(baseRevision, payload, ct).ConfigureAwait(false);
        if (putRes.IsConflict)
        {
            var conflict = putRes.ConflictCurrent;
            return new DreamPortProfileSyncOpResult(
                Ok: false,
                IsConflict: true,
                ConflictRemoteRevision: conflict?.Revision ?? putRes.Revision,
                ConflictRemoteUpdatedAtMs: conflict?.UpdatedAtMs ?? putRes.UpdatedAtMs,
                ConflictClientVersion: conflict?.ClientVersion ?? putRes.ClientVersion,
                Message: putRes.Message ?? "云端配置已被另一台设备更新，请选择拉取覆盖本机或强制覆盖云端");
        }

        if (!putRes.Ok)
            return new DreamPortProfileSyncOpResult(false, false, Message: putRes.Message ?? "同步本机配置失败", FailureReason: putRes.FailureReason);

        var nextRevision = putRes.Revision > 0 ? putRes.Revision : baseRevision + 1;
        var nextState = new DreamPortProfileSyncLocalState
        {
            Revision = nextRevision,
            LastSyncedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastHash = currentHash
        };
        WriteLocalState(nextState);

        return new DreamPortProfileSyncOpResult(
            Ok: true,
            IsConflict: false,
            Status: BuildSummary(putRes, nextState, currentHash),
            Message: putRes.Message ?? $"本机白名单配置已同步到云端（Revision {nextRevision}）");
    }

    public async Task<DreamPortProfileSyncOpResult> PullAsync(DreamPortApiClient api, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var remote = await api.GetProfileSyncAsync(ct).ConfigureAwait(false);
        if (!remote.Ok)
            return new DreamPortProfileSyncOpResult(false, false, Message: remote.Message ?? "读取云端配置失败", FailureReason: remote.FailureReason);

        if (string.IsNullOrWhiteSpace(remote.PayloadJson))
            return new DreamPortProfileSyncOpResult(false, false, Message: "云端尚未保存任何配置快照，可先点击「上传本机配置」");

        if (!TryParsePayload(remote.PayloadJson, out var parsed, out var parseError) || parsed is null)
            return new DreamPortProfileSyncOpResult(false, false, Message: parseError ?? "云端配置解析失败");

        _apply(parsed);
        var appliedHash = ComputeProfileHash(_capture());
        var nextState = new DreamPortProfileSyncLocalState
        {
            Revision = remote.Revision,
            LastSyncedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LastHash = appliedHash
        };
        WriteLocalState(nextState);

        return new DreamPortProfileSyncOpResult(
            Ok: true,
            IsConflict: false,
            Status: BuildSummary(remote, nextState, appliedHash),
            Message: $"已应用云端配置（Revision {remote.Revision}）");
    }

    public async Task<DreamPortProfileSyncOpResult> ResetAsync(DreamPortApiClient api, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var res = await api.ResetProfileSyncAsync(ct).ConfigureAwait(false);
        if (!res.Ok)
            return new DreamPortProfileSyncOpResult(false, false, Message: res.Message ?? "清除云端配置失败", FailureReason: res.FailureReason);

        ClearLocalState();
        var fresh = new DreamPortProfileSyncLocalState();
        return new DreamPortProfileSyncOpResult(
            Ok: true,
            IsConflict: false,
            Status: new DreamPortProfileSyncStatusSummary(true, true, 0, 0, false, true, null, null, 256 * 1024, res.Message),
            Message: res.Message ?? "已清除云端配置并重置本机同步状态");
    }

    public static string ComputeProfileHash(DreamPortPortableProfile profile)
    {
        var json = SerializePayload(profile).ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static DreamPortProfileSyncStatusSummary BuildSummary(
        DreamPortProfileSyncState remote,
        DreamPortProfileSyncLocalState local,
        string currentHash)
    {
        return new DreamPortProfileSyncStatusSummary(
            Ok: true,
            Enabled: remote.Enabled,
            RemoteRevision: remote.Revision,
            LocalRevision: local.Revision,
            HasRemotePayload: !string.IsNullOrWhiteSpace(remote.PayloadJson),
            LocalDirty: !string.Equals(currentHash, local.LastHash, StringComparison.Ordinal),
            RemoteUpdatedAtMs: remote.UpdatedAtMs,
            LocalLastSyncedAtMs: local.LastSyncedAtMs > 0 ? local.LastSyncedAtMs : null,
            MaxBytes: remote.MaxBytes,
            Message: remote.Message);
    }

    private DreamPortProfileSyncLocalState ReadLocalState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return new DreamPortProfileSyncLocalState();
            var raw = File.ReadAllText(_stateFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<DreamPortProfileSyncLocalState>(raw, JsonOptions)
                ?? new DreamPortProfileSyncLocalState();
        }
        catch
        {
            return new DreamPortProfileSyncLocalState();
        }
    }

    private void WriteLocalState(DreamPortProfileSyncLocalState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "DreamPort", "保存多设备配置同步状态失败");
        }
    }

    private void ClearLocalState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
                File.Delete(_stateFilePath);
        }
        catch
        {
            // ignore
        }
    }

    private static string ClampString(string? raw, int maxLen, string fallback = "")
    {
        var s = (raw ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(s))
            return fallback;
        return s.Length > maxLen ? s[..maxLen] : s;
    }
}
