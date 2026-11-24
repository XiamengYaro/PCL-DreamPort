namespace DreamPort.Core.Api;

public enum DreamPortFailureReason
{
    Expired,
    Network
}

public enum DreamPortBrowserLoginStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed,
    NotFound,
    NetworkError
}

public sealed record DreamPortBrowserLoginSession(
    string QrToken,
    string ApproveCode,
    string ConfirmUrl,
    long ExpiresAtMs);

public sealed record DreamPortBrowserLoginPollResult(
    DreamPortBrowserLoginStatus Status,
    string? Username = null,
    bool IsAdmin = false,
    string? Message = null);

public sealed record DreamPortBrowserLoginConsumeResult(
    bool Ok,
    DreamPortBrowserLoginStatus Status,
    string? Token = null,
    string? Username = null,
    bool IsAdmin = false,
    string? Message = null);

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
