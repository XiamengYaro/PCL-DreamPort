namespace DreamPort.Core.Api;

public enum DreamPortLoginOutcome
{
    Ok,
    Need2Fa,
    Need2FaSetup,
    Banned,
    NeedCaptcha,
    Error
}

public enum DreamPortFailureReason
{
    Expired,
    Network
}

public sealed record DreamPortBanEvidence(
    string Id,
    string FileUrl,
    string? Note,
    string? MediaType);

public sealed record DreamPortLoginResult
{
    public required DreamPortLoginOutcome Outcome { get; init; }
    public string? Token { get; init; }
    public string? Username { get; init; }
    public string? Status { get; init; }
    public bool IsAdmin { get; init; }
    public string? ChallengeId { get; init; }
    public bool SupportsPasskey { get; init; }
    public string? BanReason { get; init; }
    public long? BanUntil { get; init; }
    public IReadOnlyList<DreamPortBanEvidence> Evidence { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

public sealed record DreamPortCaptchaResult(
    string CaptchaToken,
    string ImageDataUri);

public sealed record DreamPortConfigResult(
    string? SiteName,
    bool LoginCaptchaRequired,
    string? PublicUrl);

public sealed record DreamPortValidateResult(
    bool Valid,
    string? Username = null,
    bool IsAdmin = false,
    DreamPortFailureReason? Reason = null,
    string? Message = null);

public sealed record DreamPortRenewResult(
    bool Ok,
    string? Token = null,
    DreamPortFailureReason? Reason = null,
    string? Message = null);

public sealed record DreamPortProfileResult(
    string Username,
    string? Email,
    string? Status,
    bool IsAdmin,
    string? MinecraftName,
    string? McUuid,
    int? Points,
    int? CheckinStreak);

public sealed record DreamPortStoredSession(
    string BaseUrl,
    string? Username,
    bool IsAdmin,
    bool IsTokenPersistent,
    string? Token);
