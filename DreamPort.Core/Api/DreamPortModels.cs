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

public sealed record DreamPortSigninResult(
    bool Ok,
    bool First,
    bool AlreadySigned,
    string Message,
    string? SignedDate,
    int Streak,
    int MonthDays,
    int Total,
    int PointsAwarded,
    IReadOnlyList<string> Rewards,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortPointsCenterResult(
    bool Ok,
    int Balance,
    bool SignedToday,
    int Streak,
    int MonthDays,
    int PendingClaimCount,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortTitleItem(
    string Code,
    string Name,
    string Desc,
    string Color,
    long? ObtainedAtMs);

public sealed record DreamPortTitlesMineResult(
    bool Ok,
    string? EquippedCode,
    IReadOnlyList<DreamPortTitleItem> Owned,
    int LockedCount,
    int CompletedAchievements,
    int TotalAchievements,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortAnnouncementItem(
    string Title,
    string Body,
    string DateText,
    string? Tag,
    bool Important);

public sealed record DreamPortAnnouncementsResult(
    bool Ok,
    IReadOnlyList<DreamPortAnnouncementItem> Items,
    string? Message = null);

public sealed record DreamPortNotificationItem(
    long Id,
    string Type,
    string Title,
    string Body,
    string? Link,
    bool Read,
    long? ReadAtMs,
    long CreatedAtMs);

public sealed record DreamPortNotificationListResult(
    bool Ok,
    IReadOnlyList<DreamPortNotificationItem> Items,
    int Total,
    int Unread,
    int Page,
    int PageSize,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortUnreadCountResult(
    bool Ok,
    int Unread,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortNotificationMarkReadResult(
    bool Ok,
    int Unread,
    int Marked = 0,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortHeartbeatResult(
    bool Ok,
    int NextIntervalSec,
    DreamPortFailureReason? FailureReason = null,
    string? Message = null);

public sealed record DreamPortCloudSaveItem(
    long Id,
    string InstanceName,
    string WorldName,
    string DisplayName,
    string GameVersion,
    long SizeBytes,
    string Sha256,
    int ChunkSize,
    int ChunksTotal,
    string Status,
    string Note,
    long CreatedAtMs,
    long UpdatedAtMs,
    long? CompletedAtMs,
    long? ExpiresAtMs);

public sealed record DreamPortCloudSaveListResult(
    bool Ok,
    IReadOnlyList<DreamPortCloudSaveItem> Items,
    long UsedBytes,
    long QuotaBytes,
    long MaxBackupBytes,
    int ChunkBytes,
    int RetentionDays,
    int MaxBackupsPerUser,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortCloudSaveBeginResult(
    bool Ok,
    long Id,
    int ChunkSize,
    int ChunksTotal,
    IReadOnlyList<int> UploadedChunks,
    bool Duplicate,
    string Status,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortCloudSaveOpResult(
    bool Ok,
    DreamPortCloudSaveItem? Item = null,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortProfileSyncState(
    bool Ok,
    bool Enabled,
    long Revision,
    string? PayloadJson,
    string? ClientVersion,
    long? UpdatedAtMs,
    int MaxBytes,
    bool IsConflict = false,
    DreamPortProfileSyncState? ConflictCurrent = null,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortLogDiagnoseStatusResult(
    bool Ok,
    bool Enabled,
    int DailyPerUser,
    int TimeoutHintMs,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortLogDiagnoseSubmitResult(
    bool Ok,
    bool Accepted,
    string? JobId,
    int PollAfterMs,
    string? Error = null,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortLogDiagnoseItem(
    string Title,
    string Evidence,
    string Advice,
    double Confidence);

public sealed record DreamPortLogDiagnoseVerdict(
    string Verdict,
    string Summary,
    IReadOnlyList<DreamPortLogDiagnoseItem> Items,
    IReadOnlyList<string> AskFor);

public sealed record DreamPortLogDiagnosePollResult(
    bool Ok,
    bool Found,
    string Status,
    DreamPortLogDiagnoseVerdict? Result = null,
    string? Failure = null,
    string? Reason = null,
    int TotalTokens = 0,
    long LatencyMs = 0,
    DreamPortFailureReason? FailureReason = null);

public sealed record DreamPortResourceExplainRequest(
    string Kind,
    string Name,
    string Summary,
    string Description,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Mods);

public sealed record DreamPortResourceExplainResult(
    bool Ok,
    string Overview,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Notes,
    bool Cached,
    string? Message = null,
    DreamPortFailureReason? FailureReason = null);
