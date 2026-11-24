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
    int? CheckinStreak,
    string? BedrockName = null,
    int? QuestionnaireScore = null,
    bool? QuestionnairePassed = null,
    string? InvitedBy = null,
    long? RegTimeMs = null);

public sealed record DreamPortStoredSession(
    string BaseUrl,
    string? Username,
    bool IsAdmin,
    bool IsTokenPersistent,
    string? Token);

public sealed record DreamPortLobbyConfigResult(
    bool Enabled,
    string? RelayHost,
    int RelayPort,
    bool RelayTls,
    int MaxPlayersPerRoom);

public sealed record DreamPortApiActionResult(
    bool Ok,
    string Message,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortSessionItem(
    string SessionId,
    string DeviceName,
    string Platform,
    string ClientVersion,
    string Ip,
    long CreatedAtMs,
    long LastSeenAtMs,
    bool Revoked,
    bool IsCurrent);

public sealed record DreamPortSessionsResult(
    bool Ok,
    IReadOnlyList<DreamPortSessionItem> Sessions,
    DreamPortFailureReason? FailureReason = null,
    string? Message = null);

public sealed record DreamPortProgressStep(
    string Key,
    string Label,
    bool Done,
    long? AtMs,
    bool Available,
    string UnavailableReason,
    string? Via);

public sealed record DreamPortUserProgress(
    IReadOnlyList<DreamPortProgressStep> Steps,
    string? CurrentStep,
    int DoneCount,
    int TotalCount,
    bool Gated,
    bool ShowBar,
    bool NeedsAccountVerification,
    string QqBindMode,
    bool Rejected,
    bool Banned,
    string? BanReason,
    long? BanUntilMs,
    bool BindHint);

public sealed record DreamPortProgressResult(
    bool Ok,
    DreamPortUserProgress? Progress,
    DreamPortFailureReason? FailureReason = null,
    string? Message = null);

public sealed record DreamPortQqStatus(
    bool Bound,
    string MaskedQq,
    long? BoundAtMs,
    string PendingQq);

public sealed record DreamPortMinecraftStatus(
    string? MinecraftName,
    string? MinecraftUuid,
    bool Verified,
    long? VerifiedAtMs,
    bool RulesAccepted,
    string? Status,
    bool MsaEnabled);

public sealed record DreamPortMsaStartResult(
    bool Ok,
    string? FlowId = null,
    string? UserCode = null,
    string? VerificationUri = null,
    int ExpiresInSec = 0,
    int IntervalSec = 5,
    DreamPortFailureReason? FailureReason = null,
    string? Message = null);

public sealed record DreamPortMsaPollResult(
    string Status,
    int IntervalSec = 5,
    DreamPortFailureReason? FailureReason = null,
    string? Message = null);

public sealed record DreamPortQuestionOption(
    long Id,
    string Text);

public sealed record DreamPortQuestionItem(
    long Id,
    string Text,
    string Type,
    bool Required,
    int MaxScore,
    bool Multiline,
    int MinLength,
    int MaxLength,
    int MinSelections,
    int MaxSelections,
    string Placeholder,
    IReadOnlyList<DreamPortQuestionOption> Options);

public sealed record DreamPortQuestionnaireConfig(
    bool Enabled,
    int PassScore,
    IReadOnlyList<DreamPortQuestionItem> Questions);

public sealed record DreamPortQuestionScoreEvent(
    int Index,
    int Total,
    long QuestionId,
    string QuestionText,
    int Score,
    int MaxScore,
    string Reason,
    int TotalScore,
    int MaxScoreTotal);

public sealed record DreamPortQuestionnaireOutcome(
    bool Ok,
    int TotalScore,
    int MaxScore,
    bool Passed,
    string Summary,
    DreamPortFailureReason? FailureReason = null,
    string? ErrorMessage = null);

public sealed record DreamPortAppealStatus(
    bool Found,
    string Status,
    string AdminReply,
    long CreatedAtMs);
