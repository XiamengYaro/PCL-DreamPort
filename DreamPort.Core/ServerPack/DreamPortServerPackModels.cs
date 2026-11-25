using DreamPort.Core.Api;

namespace DreamPort.Core.ServerPack;

public sealed record DreamPortServerPackFile(
    string Path,
    string Sha256,
    long Size,
    string? Kind = null,
    string? Loader = null,
    string? GameVersion = null);

public sealed record DreamPortModrinthModSpec(
    string ProjectId,
    string Slug,
    string Title,
    string? VersionId,
    bool Required);

public sealed record DreamPortServerPackManifestResult(
    bool Ok,
    string Version,
    long GeneratedAtMs,
    string GameVersion,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<DreamPortModrinthModSpec> ModrinthMods,
    IReadOnlyList<DreamPortServerPackFile> Files,
    long TotalBytes,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortServerPackCompatibilityResult(
    bool Compatible,
    string? Reason = null);

public sealed record DreamPortServerPackDownloadItem(
    string Path,
    string Sha256,
    long Size,
    string Reason);

public sealed record DreamPortServerPackPlan(
    IReadOnlyList<DreamPortServerPackDownloadItem> Downloads,
    IReadOnlyList<string> Deletes,
    IReadOnlyList<string> SkippedDisabled,
    int UpToDateCount,
    IReadOnlyList<string> Rejected);

public sealed record DreamPortModrinthPlanItem(
    string ProjectId,
    string Slug,
    string Title,
    string? VersionId,
    bool Required,
    string? FileName,
    string? DownloadUrl,
    string? Sha512,
    string? Sha1,
    long Size,
    bool AlreadyInstalled,
    bool Disabled,
    string? ResolveError = null);

public sealed record DreamPortServerPackProgress(
    int Done,
    int Total,
    string Message,
    string? CurrentPath = null);

public sealed record DreamPortServerPackApplyResult(
    bool Ok,
    bool Cancelled,
    string Message,
    int DownloadedCount,
    int DeletedCount,
    int ModrinthInstalledCount,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Rejected);
