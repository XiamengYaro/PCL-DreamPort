using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DreamPort.Core.Config;
using DreamPort.Core.Lobby;
using DreamPort.Core.ServerPack;

namespace DreamPort.Core.Api;

/// <summary>
/// DreamPort 站点 API 客户端（浏览器授权登录 / 会话与设备管理 / 六步入服引导流程 / AI 问卷 SSE / 签到称号公告通知）。
/// 严格区分会话过期（401 -> Expired）与网络错误（Status=0 -> Network）。
/// </summary>
public sealed partial class DreamPortApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuestionnaireStreamTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxSseWait = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private string _baseUrl;
    private string? _token;

    public DreamPortApiClient(
        string? baseUrl = null,
        HttpMessageHandler? handler = null,
        bool disposeHandler = false)
    {
        _baseUrl = TryNormalizeBaseUrl(baseUrl, out var normalized, out _)
            ? normalized
            : DreamPortBuildConfig.BaseUrl;

        // 设为无限超时，由单请求 LinkedTokenSource 按普通请求(20s)/SSE 问卷流(5min)分别限时
        _http = handler is null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = true;
    }

    public string BaseUrl => _baseUrl;

    public string? CurrentToken => _token;

    public void SetBaseUrl(string? baseUrl)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var normalized, out var error))
            throw new ArgumentException(error ?? "站点地址无效", nameof(baseUrl));
        _baseUrl = normalized;
    }

    public void SetToken(string? token)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    /// <summary>校验并规范化 DreamPort 服务地址（仅允许 HTTP/HTTPS，去除末尾斜杠）。</summary>
    public static bool TryNormalizeBaseUrl(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "站点地址不能为空";
            return false;
        }

        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "请输入以 http:// 或 https:// 开头的有效地址";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
            normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');

        return true;
    }

    public async Task<DreamPortConfigResult?> GetConfigAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/api/config", auth: false, ct: ct).ConfigureAwait(false);
        if (res.Status != 200)
            return null;

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Object)
            return null;

        var siteName = GetString(data, "siteName") ?? GetString(data, "name");
        var publicUrl = GetString(data, "publicUrl");
        var loginCaptcha = data.TryGetProperty("captcha", out var captchaEl)
            && captchaEl.ValueKind == JsonValueKind.Object
            && GetBool(captchaEl, "login");

        return new DreamPortConfigResult(siteName, loginCaptcha, publicUrl);
    }

    /// <summary>创建浏览器/扫码授权登录会话，返回轮询凭据与浏览器确认地址。</summary>
    public async Task<(DreamPortBrowserLoginSession? Session, string? ErrorMessage)> CreateBrowserLoginAsync(
        CancellationToken ct = default)
    {
        var res = await SendAsync(
            HttpMethod.Post,
            "/api/qr-login/create",
            jsonBody: new { },
            auth: false,
            ct: ct).ConfigureAwait(false);

        if (res.Status == 0)
            return (null, BuildNetworkError(res, "无法连接站点"));

        if (res.Status != 200)
            return (null, ExtractMessage(res.Body, $"发起授权登录失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        var qrToken = GetString(data, "qrToken");
        var approveCode = GetString(data, "approveCode");
        if (string.IsNullOrWhiteSpace(qrToken) || string.IsNullOrWhiteSpace(approveCode))
            return (null, "服务端未返回有效的授权会话凭据");

        var relUrl = GetString(data, "qrUrl");
        if (string.IsNullOrWhiteSpace(relUrl))
            relUrl = "/qr-login?code=" + Uri.EscapeDataString(approveCode);

        var confirmUrl = ResolveAbsoluteUrl(relUrl);
        var expiresAt = GetLong(data, "expiresAt")
            ?? DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

        return (new DreamPortBrowserLoginSession(qrToken, approveCode, confirmUrl, expiresAt), null);
    }

    /// <summary>轮询浏览器/扫码授权状态（不取令牌）。</summary>
    public async Task<DreamPortBrowserLoginPollResult> PollBrowserLoginAsync(
        string qrToken,
        CancellationToken ct = default)
    {
        var path = "/api/qr-login/poll?token=" + Uri.EscapeDataString(qrToken);
        var res = await SendAsync(HttpMethod.Get, path, auth: false, ct: ct).ConfigureAwait(false);

        if (res.Status == 0)
            return new DreamPortBrowserLoginPollResult(DreamPortBrowserLoginStatus.NetworkError, Message: BuildNetworkError(res, "网络请求失败"));

        if (res.Status == 404)
            return new DreamPortBrowserLoginPollResult(DreamPortBrowserLoginStatus.NotFound, Message: ExtractMessage(res.Body, "登录授权会话不存在"));

        if (res.Status != 200)
            return new DreamPortBrowserLoginPollResult(DreamPortBrowserLoginStatus.NetworkError, Message: ExtractMessage(res.Body, $"查询授权状态失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        var status = ParseBrowserLoginStatus(GetString(data, "status"));
        return new DreamPortBrowserLoginPollResult(
            Status: status,
            Username: GetString(data, "username"),
            IsAdmin: GetBool(data, "isAdmin"));
    }

    /// <summary>
    /// 通过 SSE（/api/qr-login/wait）阻塞等待状态变化事件；超时、EOF 或非取消网络错误返回 false，
    /// 交由上层退避后继续轮询兜底。
    /// </summary>
    public async Task<bool> WaitBrowserLoginChangeAsync(
        string qrToken,
        TimeSpan maxWait,
        CancellationToken ct = default)
    {
        var effectiveWait = maxWait <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : (maxWait > MaxSseWait ? MaxSseWait : maxWait);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(effectiveWait);

        var path = "/api/qr-login/wait?token=" + Uri.EscapeDataString(qrToken);
        using var req = CreateRequest(HttpMethod.Get, path, accept: "text/event-stream", auth: false);

        try
        {
            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return false;

            await using var stream = await resp.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (true)
            {
                var line = await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                if (line is null)
                    return false;

                if (line.StartsWith("data:", StringComparison.Ordinal))
                    return true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return false;
        }
    }

    /// <summary>单次消费已批准的授权会话并换取站点访问令牌（自动携带设备名与平台头）。</summary>
    public async Task<DreamPortBrowserLoginConsumeResult> ConsumeBrowserLoginAsync(
        string qrToken,
        CancellationToken ct = default)
    {
        var path = "/api/qr-login/consume?token=" + Uri.EscapeDataString(qrToken);
        var res = await SendAsync(HttpMethod.Post, path, auth: false, ct: ct).ConfigureAwait(false);

        if (res.Status == 0)
            return new DreamPortBrowserLoginConsumeResult(false, DreamPortBrowserLoginStatus.NetworkError, Message: BuildNetworkError(res, "获取登录凭据失败"));

        if (res.Status == 404)
            return new DreamPortBrowserLoginConsumeResult(false, DreamPortBrowserLoginStatus.NotFound, Message: ExtractMessage(res.Body, "登录授权会话不存在"));

        if (res.Status != 200)
            return new DreamPortBrowserLoginConsumeResult(false, DreamPortBrowserLoginStatus.NetworkError, Message: ExtractMessage(res.Body, $"获取登录凭据失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        var token = GetString(data, "token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            SetToken(token);
            return new DreamPortBrowserLoginConsumeResult(
                Ok: true,
                Status: DreamPortBrowserLoginStatus.Approved,
                Token: _token,
                Username: GetString(data, "username"),
                IsAdmin: GetBool(data, "isAdmin"));
        }

        var status = ParseBrowserLoginStatus(GetString(data, "status"));
        return new DreamPortBrowserLoginConsumeResult(
            Ok: false,
            Status: status,
            Message: ExtractMessage(res.Body, "尚未批准"));
    }

    public async Task<DreamPortRenewResult> RenewAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortRenewResult(false, Reason: DreamPortFailureReason.Expired, Message: "未登录");

        var res = await SendAsync(HttpMethod.Post, "/api/auth/renew", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 0)
            return new DreamPortRenewResult(false, Reason: DreamPortFailureReason.Network, Message: BuildNetworkError(res, "续期失败"));

        if (res.Status != 200)
            return new DreamPortRenewResult(false, Reason: DreamPortFailureReason.Expired, Message: ExtractMessage(res.Body, "续期失败"));

        var data = UnwrapData(res.Body);
        var token = GetString(data, "token");
        if (string.IsNullOrWhiteSpace(token))
            return new DreamPortRenewResult(false, Reason: DreamPortFailureReason.Expired, Message: "续期响应未包含令牌");

        SetToken(token);
        return new DreamPortRenewResult(true, Token: _token);
    }

    public async Task<DreamPortValidateResult> ValidateSessionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortValidateResult(false, Reason: DreamPortFailureReason.Expired, Message: "未登录");

        var res = await SendAsync(HttpMethod.Get, "/api/auth/validate", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 401)
        {
            var renewed = await RenewAsync(ct).ConfigureAwait(false);
            if (!renewed.Ok)
            {
                return new DreamPortValidateResult(
                    false,
                    Reason: renewed.Reason ?? DreamPortFailureReason.Expired,
                    Message: renewed.Message);
            }

            res = await SendAsync(HttpMethod.Get, "/api/auth/validate", auth: true, ct: ct).ConfigureAwait(false);
        }

        if (res.Status == 0)
            return new DreamPortValidateResult(false, Reason: DreamPortFailureReason.Network, Message: BuildNetworkError(res, "无法连接站点"));

        if (res.Status != 200)
            return new DreamPortValidateResult(false, Reason: DreamPortFailureReason.Expired, Message: ExtractMessage(res.Body, "登录已过期"));

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Object)
            return new DreamPortValidateResult(false, Reason: DreamPortFailureReason.Expired, Message: "登录校验响应无效");

        if (data.TryGetProperty("valid", out var validEl) && validEl.ValueKind == JsonValueKind.False)
            return new DreamPortValidateResult(false, Reason: DreamPortFailureReason.Expired, Message: "登录已过期");

        return new DreamPortValidateResult(
            true,
            Username: GetString(data, "username"),
            IsAdmin: GetBool(data, "isAdmin"));
    }

    public async Task<DreamPortProfileResult?> GetProfileAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return null;

        var res = await SendAsync(HttpMethod.Get, "/api/user/profile", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status != 200)
            return null;

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Object)
            return null;

        return new DreamPortProfileResult(
            Username: GetString(data, "username") ?? string.Empty,
            Email: GetString(data, "email"),
            Status: GetString(data, "status"),
            IsAdmin: GetBool(data, "isAdmin"),
            MinecraftName: GetString(data, "minecraftName") ?? GetString(data, "mcName"),
            McUuid: GetString(data, "minecraftUuid") ?? GetString(data, "mcUuid"),
            Points: GetInt(data, "points"),
            CheckinStreak: GetInt(data, "checkinStreak"),
            BedrockName: GetString(data, "bedrockName"),
            QuestionnaireScore: GetInt(data, "questionnaireScore"),
            QuestionnairePassed: GetNullableBool(data, "questionnairePassed"),
            InvitedBy: GetString(data, "invitedBy"),
            RegTimeMs: GetLong(data, "regTime"));
    }

    public async Task<DreamPortSessionsResult> GetSessionsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortSessionsResult(false, [], DreamPortFailureReason.Expired, "未登录");

        var res = await SendAsync(HttpMethod.Get, "/api/auth/sessions", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 0)
            return new DreamPortSessionsResult(false, [], DreamPortFailureReason.Network, BuildNetworkError(res, "读取设备列表失败"));
        if (res.Status == 401)
            return new DreamPortSessionsResult(false, [], DreamPortFailureReason.Expired, ExtractMessage(res.Body, "登录已过期"));
        if (res.Status != 200)
            return new DreamPortSessionsResult(false, [], null, ExtractMessage(res.Body, $"读取设备列表失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Array)
            return new DreamPortSessionsResult(true, []);

        var items = new List<DreamPortSessionItem>();
        foreach (var el in data.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var sid = GetString(el, "sessionId");
            if (string.IsNullOrWhiteSpace(sid)) continue;

            items.Add(new DreamPortSessionItem(
                SessionId: sid,
                DeviceName: GetString(el, "deviceName") ?? "未知设备",
                Platform: GetString(el, "platform") ?? string.Empty,
                ClientVersion: GetString(el, "clientVersion") ?? string.Empty,
                Ip: GetString(el, "ip") ?? string.Empty,
                CreatedAtMs: GetLong(el, "createdAt") ?? 0L,
                LastSeenAtMs: GetLong(el, "lastSeenAt") ?? 0L,
                Revoked: GetBool(el, "revoked"),
                IsCurrent: GetBool(el, "current")));
        }

        return new DreamPortSessionsResult(true, items);
    }

    public async Task<DreamPortApiActionResult> RevokeSessionAsync(
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        object body = string.IsNullOrWhiteSpace(sessionId)
            ? new { }
            : new { sessionId = sessionId.Trim() };
        var res = await SendAsync(HttpMethod.Post, "/api/auth/sessions/revoke", body, auth: true, ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "操作成功", "踢出设备失败");
    }

    public async Task<DreamPortProgressResult> GetProgressAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortProgressResult(false, null, DreamPortFailureReason.Expired, "未登录");

        var res = await SendAsync(HttpMethod.Get, "/api/user/progress", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 0)
            return new DreamPortProgressResult(false, null, DreamPortFailureReason.Network, BuildNetworkError(res, "获取入服进度失败"));
        if (res.Status == 401)
            return new DreamPortProgressResult(false, null, DreamPortFailureReason.Expired, ExtractMessage(res.Body, "登录已过期"));
        if (res.Status != 200)
            return new DreamPortProgressResult(false, null, null, ExtractMessage(res.Body, $"获取入服进度失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Object)
            return new DreamPortProgressResult(false, null, null, "入服进度响应格式无效");

        var steps = new List<DreamPortProgressStep>();
        if (data.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in stepsEl.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                steps.Add(new DreamPortProgressStep(
                    Key: GetString(s, "key") ?? string.Empty,
                    Label: GetString(s, "label") ?? string.Empty,
                    Done: GetBool(s, "done"),
                    AtMs: GetLong(s, "at"),
                    Available: !s.TryGetProperty("available", out var avEl) || avEl.ValueKind != JsonValueKind.False,
                    UnavailableReason: GetString(s, "unavailableReason") ?? string.Empty,
                    Via: GetString(s, "via")));
            }
        }

        var progress = new DreamPortUserProgress(
            Steps: steps,
            CurrentStep: GetString(data, "currentStep"),
            DoneCount: GetInt(data, "doneCount") ?? steps.Count(x => x.Done),
            TotalCount: GetInt(data, "totalCount") ?? steps.Count,
            Gated: GetBool(data, "gated"),
            ShowBar: GetBool(data, "showBar"),
            NeedsAccountVerification: GetBool(data, "needsAccountVerification"),
            QqBindMode: GetString(data, "qqBindMode") ?? "required",
            Rejected: GetBool(data, "rejected"),
            Banned: GetBool(data, "banned"),
            BanReason: GetString(data, "banReason"),
            BanUntilMs: GetLong(data, "banUntil"),
            BindHint: GetBool(data, "bindHint"));

        return new DreamPortProgressResult(true, progress);
    }

    public async Task<(DreamPortQqStatus? Status, DreamPortFailureReason? FailureReason, string? ErrorMessage)> GetQqStatusAsync(
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return (null, DreamPortFailureReason.Expired, "未登录");

        var res = await SendAsync(HttpMethod.Get, "/api/user/qq/status", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 0)
            return (null, DreamPortFailureReason.Network, BuildNetworkError(res, "获取 QQ 绑定状态失败"));
        if (res.Status == 401)
            return (null, DreamPortFailureReason.Expired, ExtractMessage(res.Body, "登录已过期"));
        if (res.Status != 200)
            return (null, null, ExtractMessage(res.Body, $"获取 QQ 绑定状态失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        return (new DreamPortQqStatus(
            Bound: GetBool(data, "bound"),
            MaskedQq: GetString(data, "qq") ?? string.Empty,
            BoundAtMs: GetLong(data, "boundAt"),
            PendingQq: GetString(data, "pendingQq") ?? string.Empty), null, null);
    }

    public async Task<DreamPortApiActionResult> BindQqAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(
            HttpMethod.Post,
            "/api/user/qq/bind",
            new { code = code.Trim() },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "QQ 绑定成功", "QQ 绑定失败");
    }

    public async Task<DreamPortApiActionResult> UnbindQqAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Post, "/api/user/qq/unbind", new { }, auth: true, ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "已解绑 QQ", "解绑 QQ 失败");
    }

    public async Task<(DreamPortMinecraftStatus? Status, DreamPortFailureReason? FailureReason, string? ErrorMessage)> GetMinecraftStatusAsync(
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return (null, DreamPortFailureReason.Expired, "未登录");

        var res = await SendAsync(HttpMethod.Get, "/api/user/minecraft/status", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 0)
            return (null, DreamPortFailureReason.Network, BuildNetworkError(res, "获取 Minecraft 绑定状态失败"));
        if (res.Status == 401)
            return (null, DreamPortFailureReason.Expired, ExtractMessage(res.Body, "登录已过期"));
        if (res.Status != 200)
            return (null, null, ExtractMessage(res.Body, $"获取 Minecraft 绑定状态失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        return (new DreamPortMinecraftStatus(
            MinecraftName: GetString(data, "minecraftName"),
            MinecraftUuid: GetString(data, "minecraftUuid"),
            Verified: GetBool(data, "verified"),
            VerifiedAtMs: GetLong(data, "verifiedAt"),
            RulesAccepted: GetBool(data, "rulesAccepted"),
            Status: GetString(data, "status"),
            MsaEnabled: GetBool(data, "msaEnabled")), null, null);
    }

    public async Task<DreamPortApiActionResult> VerifyMinecraftByTokenAsync(
        string mcAccessToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(
            HttpMethod.Post,
            "/api/user/minecraft/verify-token",
            new { token = mcAccessToken.Trim() },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "Minecraft 正版验证成功", "正版验证失败");
    }

    public async Task<DreamPortMsaStartResult> StartMsaVerifyAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortMsaStartResult(false, FailureReason: DreamPortFailureReason.Expired, Message: "未登录");

        var res = await SendAsync(HttpMethod.Post, "/api/user/minecraft/msa/start", new { }, auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 0)
            return new DreamPortMsaStartResult(false, FailureReason: DreamPortFailureReason.Network, Message: BuildNetworkError(res, "发起微软验证失败"));
        if (res.Status == 401)
            return new DreamPortMsaStartResult(false, FailureReason: DreamPortFailureReason.Expired, Message: ExtractMessage(res.Body, "登录已过期"));
        if (res.Status != 200)
            return new DreamPortMsaStartResult(false, Message: ExtractMessage(res.Body, $"发起微软验证失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        return new DreamPortMsaStartResult(
            Ok: true,
            FlowId: GetString(data, "flowId"),
            UserCode: GetString(data, "userCode"),
            VerificationUri: GetString(data, "verificationUri"),
            ExpiresInSec: GetInt(data, "expiresIn") ?? 900,
            IntervalSec: Math.Max(2, GetInt(data, "interval") ?? 5));
    }

    public async Task<DreamPortMsaPollResult> PollMsaVerifyAsync(string flowId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortMsaPollResult("failed", FailureReason: DreamPortFailureReason.Expired, Message: "未登录");

        var res = await SendAsync(
            HttpMethod.Post,
            "/api/user/minecraft/msa/poll",
            new { flowId = flowId.Trim() },
            auth: true,
            ct: ct).ConfigureAwait(false);

        if (res.Status == 0)
            return new DreamPortMsaPollResult("network", FailureReason: DreamPortFailureReason.Network, Message: BuildNetworkError(res, "微软验证轮询网络波动"));
        if (res.Status == 401)
            return new DreamPortMsaPollResult("failed", FailureReason: DreamPortFailureReason.Expired, Message: ExtractMessage(res.Body, "登录已过期"));

        var data = UnwrapData(res.Body);
        var status = GetString(data, "status") ?? (res.Status == 200 ? "pending" : "failed");
        var interval = GetInt(data, "interval") ?? 5;
        var msg = GetString(data, "message") ?? ExtractMessage(res.Body, string.Empty);
        return new DreamPortMsaPollResult(status, interval, null, string.IsNullOrWhiteSpace(msg) ? null : msg);
    }

    public async Task<DreamPortApiActionResult> ApplyInviteCodeAsync(string inviteCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(
            HttpMethod.Post,
            "/api/invite/apply",
            new { inviteCode = inviteCode.Trim() },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "已提交邀请码申请", "邀请码提交失败");
    }

    public async Task<(DreamPortQuestionnaireConfig? Config, DreamPortFailureReason? FailureReason, string? ErrorMessage)> GetQuestionnaireConfigAsync(
        CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/api/questionnaire/config", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 0)
            return (null, DreamPortFailureReason.Network, BuildNetworkError(res, "读取入服问卷失败"));
        if (res.Status == 401)
            return (null, DreamPortFailureReason.Expired, ExtractMessage(res.Body, "登录已过期"));
        if (res.Status != 200)
            return (null, null, ExtractMessage(res.Body, $"读取入服问卷失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Object)
            return (null, null, "问卷配置格式无效");

        var enabled = GetBool(data, "enabled");
        var passScore = GetInt(data, "passScore") ?? GetInt(data, "questionnairePassScore") ?? 0;
        var questions = new List<DreamPortQuestionItem>();

        if (data.TryGetProperty("questions", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object) continue;
                var inputEl = q.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object ? inp : default;
                var opts = new List<DreamPortQuestionOption>();
                if (q.TryGetProperty("options", out var oArr) && oArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in oArr.EnumerateArray())
                    {
                        if (o.ValueKind != JsonValueKind.Object) continue;
                        var oid = GetLong(o, "id") ?? 0L;
                        var otext = GetString(o, "text") ?? GetString(o, "text_zh") ?? GetString(o, "text_en") ?? string.Empty;
                        opts.Add(new DreamPortQuestionOption(oid, otext));
                    }
                }

                questions.Add(new DreamPortQuestionItem(
                    Id: GetLong(q, "id") ?? 0L,
                    Text: GetString(q, "text") ?? GetString(q, "question_zh") ?? GetString(q, "question_en") ?? string.Empty,
                    Type: (GetString(q, "type") ?? "text").Trim().ToLowerInvariant(),
                    Required: GetBool(q, "required"),
                    MaxScore: GetInt(q, "max_score") ?? 0,
                    Multiline: GetBool(q, "multiline") || (inputEl.ValueKind == JsonValueKind.Object && GetBool(inputEl, "multiline")),
                    MinLength: inputEl.ValueKind == JsonValueKind.Object ? GetInt(inputEl, "min_length") ?? 0 : 0,
                    MaxLength: GetInt(q, "maxLength") ?? (inputEl.ValueKind == JsonValueKind.Object ? GetInt(inputEl, "max_length") ?? 0 : 0),
                    MinSelections: inputEl.ValueKind == JsonValueKind.Object ? GetInt(inputEl, "min_selections") ?? 0 : 0,
                    MaxSelections: inputEl.ValueKind == JsonValueKind.Object ? GetInt(inputEl, "max_selections") ?? 0 : 0,
                    Placeholder: GetString(q, "placeholder")
                        ?? (inputEl.ValueKind == JsonValueKind.Object ? GetString(inputEl, "placeholder_zh") : null)
                        ?? string.Empty,
                    Options: opts));
            }
        }

        return (new DreamPortQuestionnaireConfig(enabled, passScore, questions), null, null);
    }

    public async Task<DreamPortQuestionnaireOutcome> SubmitQuestionnaireAsync(
        IReadOnlyDictionary<string, object> answers,
        string language = "zh",
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortQuestionnaireOutcome(false, 0, 0, false, string.Empty, DreamPortFailureReason.Expired, "未登录");

        var res = await SendAsync(
            HttpMethod.Post,
            "/api/questionnaire/submit",
            new { answers, language },
            auth: true,
            timeout: QuestionnaireStreamTimeout,
            ct: ct).ConfigureAwait(false);

        if (res.Status == 0)
            return new DreamPortQuestionnaireOutcome(false, 0, 0, false, string.Empty, DreamPortFailureReason.Network, BuildNetworkError(res, "提交问卷失败"));
        if (res.Status == 401)
            return new DreamPortQuestionnaireOutcome(false, 0, 0, false, string.Empty, DreamPortFailureReason.Expired, ExtractMessage(res.Body, "登录已过期"));
        if (res.Status != 200)
            return new DreamPortQuestionnaireOutcome(false, 0, 0, false, string.Empty, null, ExtractMessage(res.Body, $"提交问卷失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        return new DreamPortQuestionnaireOutcome(
            Ok: true,
            TotalScore: GetInt(data, "totalScore") ?? 0,
            MaxScore: GetInt(data, "maxScore") ?? 0,
            Passed: GetBool(data, "passed"),
            Summary: GetString(data, "summary") ?? string.Empty);
    }

    /// <summary>
    /// 通过 SSE（/api/questionnaire/stream）提交问卷并实时回调逐题 AI 评分；
    /// 若服务端未返回 SSE 或中途断流，自动回退同步 /api/questionnaire/submit。
    /// </summary>
    public async Task<DreamPortQuestionnaireOutcome> SubmitQuestionnaireStreamAsync(
        IReadOnlyDictionary<string, object> answers,
        Action<DreamPortQuestionScoreEvent>? onScored = null,
        Action? onSummarizing = null,
        string language = "zh",
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortQuestionnaireOutcome(false, 0, 0, false, string.Empty, DreamPortFailureReason.Expired, "未登录");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(QuestionnaireStreamTimeout);

        using var req = CreateRequest(HttpMethod.Post, "/api/questionnaire/stream", accept: "text/event-stream", auth: true);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { answers, language }, JsonOptions),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token).ConfigureAwait(false);

            if ((int)resp.StatusCode == 401)
                return new DreamPortQuestionnaireOutcome(false, 0, 0, false, string.Empty, DreamPortFailureReason.Expired, "登录已过期");

            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (!resp.IsSuccessStatusCode
                || !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return await SubmitQuestionnaireAsync(answers, language, ct).ConfigureAwait(false);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (true)
            {
                var line = await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
                if (line is null)
                    break;

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var payloadText = line["data:".Length..].Trim();
                if (string.IsNullOrEmpty(payloadText))
                    continue;

                using var evDoc = TryParseJson(payloadText);
                if (evDoc is null || evDoc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                var root = evDoc.RootElement;
                var type = GetString(root, "type");
                switch (type)
                {
                    case "question_scored":
                        onScored?.Invoke(new DreamPortQuestionScoreEvent(
                            Index: GetInt(root, "index") ?? 0,
                            Total: GetInt(root, "total") ?? 0,
                            QuestionId: GetLong(root, "questionId") ?? 0L,
                            QuestionText: GetString(root, "questionText") ?? string.Empty,
                            Score: GetInt(root, "score") ?? 0,
                            MaxScore: GetInt(root, "maxScore") ?? 0,
                            Reason: GetString(root, "reason") ?? string.Empty,
                            TotalScore: GetInt(root, "totalScore") ?? 0,
                            MaxScoreTotal: GetInt(root, "maxScoreTotal") ?? 0));
                        break;

                    case "summarizing":
                        onSummarizing?.Invoke();
                        break;

                    case "complete":
                        return new DreamPortQuestionnaireOutcome(
                            Ok: true,
                            TotalScore: GetInt(root, "totalScore") ?? 0,
                            MaxScore: GetInt(root, "maxScore") ?? 0,
                            Passed: GetBool(root, "passed"),
                            Summary: GetString(root, "summary") ?? string.Empty);

                    case "error":
                        return new DreamPortQuestionnaireOutcome(
                            Ok: false,
                            TotalScore: 0,
                            MaxScore: 0,
                            Passed: false,
                            Summary: string.Empty,
                            FailureReason: null,
                            ErrorMessage: GetString(root, "message") ?? "问卷评分失败");
                }
            }

            // 未收到 complete/error 即断开：自动走同步提交兜底
            return await SubmitQuestionnaireAsync(answers, language, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DreamPortQuestionnaireOutcome(false, 0, 0, false, string.Empty, DreamPortFailureReason.Network, "问卷评分超时（5 分钟），请检查网络或重试");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return await SubmitQuestionnaireAsync(answers, language, ct).ConfigureAwait(false);
        }
    }

    public async Task<(DreamPortAppealStatus? Appeal, DreamPortFailureReason? FailureReason, string? ErrorMessage)> GetMyAppealAsync(
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return (null, DreamPortFailureReason.Expired, "未登录");

        var res = await SendAsync(HttpMethod.Get, "/api/questionnaire/appeal/mine", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 0)
            return (null, DreamPortFailureReason.Network, BuildNetworkError(res, "获取申诉状态失败"));
        if (res.Status == 401)
            return (null, DreamPortFailureReason.Expired, ExtractMessage(res.Body, "登录已过期"));
        if (res.Status != 200)
            return (null, null, ExtractMessage(res.Body, $"获取申诉状态失败（HTTP {res.Status}）"));

        var data = UnwrapData(res.Body);
        return (new DreamPortAppealStatus(
            Found: GetBool(data, "found"),
            Status: GetString(data, "status") ?? string.Empty,
            AdminReply: GetString(data, "adminReply") ?? string.Empty,
            CreatedAtMs: GetLong(data, "createdAt") ?? 0L), null, null);
    }

    public async Task<DreamPortApiActionResult> SubmitAppealAsync(string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(
            HttpMethod.Post,
            "/api/questionnaire/appeal",
            new { reason = reason.Trim() },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "申诉已提交，等待管理员处理", "提交申诉失败");
    }

    public async Task<DreamPortLobbyConfigResult?> GetLobbyConfigAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return null;

        var res = await SendAsync(HttpMethod.Get, "/api/lobby/config", auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status != 200)
            return null;

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Object)
            return null;

        return new DreamPortLobbyConfigResult(
            Enabled: GetBool(data, "enabled"),
            RelayHost: GetString(data, "relayHost"),
            RelayPort: GetInt(data, "relayPort") ?? 0,
            RelayTls: GetBool(data, "relayTls"),
            MaxPlayersPerRoom: GetInt(data, "maxPlayersPerRoom") ?? 0);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_token))
        {
            await SendAsync(HttpMethod.Post, "/api/logout", new { }, auth: true, ct: ct).ConfigureAwait(false);
        }

        SetToken(null);
    }

    public async Task<DreamPortPointsCenterResult> GetPointsCenterAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortPointsCenterResult(false, 0, false, 0, 0, 0, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Get, "/api/points/center", auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取积分与签到信息失败", out var reason, out var error))
            return new DreamPortPointsCenterResult(false, 0, false, 0, 0, 0, error, reason);

        var data = UnwrapData(res.Body);
        var balance = GetInt(data, "balance") ?? 0;

        var signedToday = false;
        var streak = 0;
        var monthDays = 0;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("signin", out var signinEl)
            && signinEl.ValueKind == JsonValueKind.Object)
        {
            signedToday = GetBool(signinEl, "today");
            streak = GetInt(signinEl, "streak") ?? 0;
            monthDays = GetInt(signinEl, "monthDays") ?? 0;
        }

        var pendingClaim = 0;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("tasks", out var tasksEl)
            && tasksEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in tasksEl.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var task in group.Value.EnumerateArray())
                {
                    if (task.ValueKind == JsonValueKind.Object
                        && GetBool(task, "completed")
                        && !GetBool(task, "claimed"))
                    {
                        pendingClaim++;
                    }
                }
            }
        }

        return new DreamPortPointsCenterResult(true, balance, signedToday, streak, monthDays, pendingClaim);
    }

    public async Task<DreamPortSigninResult> SigninAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortSigninResult(false, false, false, "未登录", null, 0, 0, 0, 0, [], DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Post, "/api/points/signin", new { }, auth: true, ct: ct).ConfigureAwait(false);
        if (res.Status == 400)
        {
            var badMsg = ExtractMessage(res.Body, "每日签到失败");
            if (badMsg.Contains("今日已签到", StringComparison.Ordinal))
            {
                return new DreamPortSigninResult(
                    Ok: true,
                    First: false,
                    AlreadySigned: true,
                    Message: badMsg,
                    SignedDate: null,
                    Streak: 0,
                    MonthDays: 0,
                    Total: 0,
                    PointsAwarded: 0,
                    Rewards: []);
            }
        }

        if (TryClassifyFailure(res, "每日签到失败", out var reason, out var error))
            return new DreamPortSigninResult(false, false, false, error, null, 0, 0, 0, 0, [], reason);

        var data = UnwrapData(res.Body);
        var rewards = new List<string>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("rewards", out var rewardsEl)
            && rewardsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rewardsEl.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    rewards.Add(text.Trim());
            }
        }

        var first = GetBool(data, "first");
        var alreadySigned = GetBool(data, "alreadySigned");
        if (!first && !alreadySigned)
            first = true;

        var msg = FirstNonBlank(data, "message") ?? ExtractMessage(res.Body, "签到成功");
        return new DreamPortSigninResult(
            Ok: true,
            First: first,
            AlreadySigned: alreadySigned,
            Message: msg,
            SignedDate: FirstNonBlank(data, "signedDate"),
            Streak: GetInt(data, "streak") ?? 0,
            MonthDays: GetInt(data, "monthDays") ?? 0,
            Total: GetInt(data, "total") ?? 0,
            PointsAwarded: GetInt(data, "pointsAwarded") ?? 0,
            Rewards: rewards);
    }

    public async Task<DreamPortTitlesMineResult> GetMyTitlesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortTitlesMineResult(false, null, [], 0, 0, 0, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Get, "/api/titles/mine", auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取称号列表失败", out var reason, out var error))
            return new DreamPortTitlesMineResult(false, null, [], 0, 0, 0, error, reason);

        var data = UnwrapData(res.Body);
        var owned = new List<DreamPortTitleItem>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("owned", out var ownedEl)
            && ownedEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ownedEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var code = GetString(item, "code") ?? string.Empty;
                var name = GetString(item, "name") ?? code;
                if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name))
                    continue;
                owned.Add(new DreamPortTitleItem(
                    Code: code,
                    Name: name,
                    Desc: GetString(item, "desc") ?? string.Empty,
                    Color: GetString(item, "color") ?? string.Empty,
                    ObtainedAtMs: GetLong(item, "obtainedAt")));
            }
        }

        var lockedCount = 0;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("locked", out var lockedEl)
            && lockedEl.ValueKind == JsonValueKind.Array)
        {
            lockedCount = lockedEl.GetArrayLength();
        }

        var completedAch = 0;
        var totalAch = 0;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("achievements", out var achEl)
            && achEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in achEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                totalAch++;
                if (GetBool(item, "completed"))
                    completedAch++;
            }
        }

        var equipped = FirstNonBlank(data, "equipped");
        return new DreamPortTitlesMineResult(true, equipped, owned, lockedCount, completedAch, totalAch);
    }

    public async Task<DreamPortApiActionResult> EquipTitleAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);
        if (string.IsNullOrWhiteSpace(code))
            return new DreamPortApiActionResult(false, "请选择要佩戴的称号");

        var res = await SendAsync(
            HttpMethod.Post,
            "/api/titles/equip",
            new { code = code.Trim() },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "已佩戴称号", "佩戴称号失败");
    }

    public async Task<DreamPortApiActionResult> UnequipTitleAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Post, "/api/titles/unequip", new { }, auth: true, ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "已脱下称号", "脱下称号失败");
    }

    public async Task<DreamPortAnnouncementsResult> GetAnnouncementsAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/api/announcements", auth: false, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取服务器公告失败", out _, out var error))
            return new DreamPortAnnouncementsResult(false, [], error);

        var data = UnwrapData(res.Body);
        JsonElement listEl = default;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("news", out var newsEl))
            listEl = newsEl;
        else if (data.ValueKind == JsonValueKind.Array)
            listEl = data;

        var items = new List<DreamPortAnnouncementItem>();
        if (listEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in listEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var title = FirstNonBlank(item, "title", "name", "version") ?? "未命名";
                var rawBody = FirstNonBlank(item, "subtitle", "description", "content", "body") ?? string.Empty;
                var body = StripMarkdownForPlainDisplay(rawBody);
                var dateText = FormatAnnouncementDate(FirstNonBlankElement(item, "date", "publishAt", "time", "createdAt"));
                var tag = ShowAnnouncementTag(FirstNonBlank(item, "tag", "category"));
                var important = GetBool(item, "pinned")
                    || GetBool(item, "important")
                    || GetBool(item, "isImportant")
                    || string.Equals(GetString(item, "priority"), "important", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetString(item, "level"), "important", StringComparison.OrdinalIgnoreCase);

                items.Add(new DreamPortAnnouncementItem(title, body, dateText, tag, important));
            }
        }

        return new DreamPortAnnouncementsResult(true, items);
    }

    public async Task<DreamPortNotificationListResult> GetNotificationsAsync(
        int page = 1,
        int size = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortNotificationListResult(false, [], 0, 0, page, size, "未登录", DreamPortFailureReason.Expired);

        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(size, 1, 50);
        var res = await SendAsync(
            HttpMethod.Get,
            $"/api/notifications?page={safePage}&size={safeSize}",
            auth: true,
            ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取站内通知失败", out var reason, out var error))
            return new DreamPortNotificationListResult(false, [], 0, 0, safePage, safeSize, error, reason);

        var data = UnwrapData(res.Body);
        var items = new List<DreamPortNotificationItem>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("items", out var itemsEl)
            && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in itemsEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                items.Add(new DreamPortNotificationItem(
                    Id: GetLong(row, "id") ?? 0,
                    Type: GetString(row, "type") ?? string.Empty,
                    Title: FirstNonBlank(row, "title") ?? "系统通知",
                    Body: GetString(row, "body") ?? string.Empty,
                    Link: FirstNonBlank(row, "link"),
                    Read: GetBool(row, "read"),
                    ReadAtMs: GetLong(row, "readAt"),
                    CreatedAtMs: GetLong(row, "createdAt") ?? 0));
            }
        }

        var total = GetInt(data, "total") ?? items.Count;
        var unread = GetInt(data, "unread") ?? items.Count(i => !i.Read);
        var respPage = GetInt(data, "page") ?? safePage;
        var respSize = GetInt(data, "pageSize") ?? safeSize;
        return new DreamPortNotificationListResult(true, items, total, unread, respPage, respSize);
    }

    public async Task<DreamPortUnreadCountResult> GetUnreadNotificationCountAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortUnreadCountResult(false, 0, DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Get, "/api/notifications/unread", auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取未读通知数失败", out var reason, out _))
            return new DreamPortUnreadCountResult(false, 0, reason);

        var data = UnwrapData(res.Body);
        var unread = Math.Max(0, GetInt(data, "unread") ?? 0);
        return new DreamPortUnreadCountResult(true, unread);
    }

    public async Task<DreamPortNotificationMarkReadResult> MarkNotificationReadAsync(long id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortNotificationMarkReadResult(false, 0, 0, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Post, $"/api/notifications/{id}/read", new { }, auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "标记已读失败", out var reason, out var error))
            return new DreamPortNotificationMarkReadResult(false, 0, 0, error, reason);

        var data = UnwrapData(res.Body);
        var unread = Math.Max(0, GetInt(data, "unread") ?? 0);
        return new DreamPortNotificationMarkReadResult(true, unread, 1, ExtractMessage(res.Body, "已读"));
    }

    public async Task<DreamPortNotificationMarkReadResult> MarkAllNotificationsReadAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortNotificationMarkReadResult(false, 0, 0, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Post, "/api/notifications/read-all", new { }, auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "全部标记已读失败", out var reason, out var error))
            return new DreamPortNotificationMarkReadResult(false, 0, 0, error, reason);

        var data = UnwrapData(res.Body);
        var marked = Math.Max(0, GetInt(data, "marked") ?? 0);
        var unread = Math.Max(0, GetInt(data, "unread") ?? 0);
        return new DreamPortNotificationMarkReadResult(true, unread, marked, ExtractMessage(res.Body, "已全部标记已读"));
    }

    /// <summary>
    /// 安全解析站内相对路径（如 <c>/tasks</c>）或 HTTP/HTTPS 外部链接，拒绝 <c>javascript:</c>、<c>file:</c>、<c>data:</c> 与协议相对路径。
    /// </summary>
    public static bool TryResolveSafeWebUrl(string baseUrl, string? link, out string resolvedUrl)
    {
        resolvedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(link))
            return false;

        var trimmed = link.Trim();
        if (trimmed.Contains('\r') || trimmed.Contains('\n') || trimmed.Contains('\0'))
            return false;
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;

        if (!trimmed.StartsWith('/'))
        {
            if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absUri))
            {
                if (absUri.Scheme != Uri.UriSchemeHttp && absUri.Scheme != Uri.UriSchemeHttps)
                    return false;
                resolvedUrl = absUri.ToString();
                return true;
            }

            if (trimmed.Contains(':', StringComparison.Ordinal))
                return false;
        }

        if (!TryNormalizeBaseUrl(baseUrl, out var normalizedBase, out _))
            return false;
        if (!Uri.TryCreate(normalizedBase + "/", UriKind.Absolute, out var baseUri))
            return false;
        if (!Uri.TryCreate(baseUri, trimmed.TrimStart('/'), out var combined))
            return false;
        if (combined.Scheme != Uri.UriSchemeHttp && combined.Scheme != Uri.UriSchemeHttps)
            return false;

        resolvedUrl = combined.ToString();
        return true;
    }

    /// <summary>将公告 Markdown 正文清理为适合原生 TextBlock 展示的纯文本格式。</summary>
    public static string StripMarkdownForPlainDisplay(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        text = MdLinkPattern().Replace(text, "$1");
        text = MdHeadingPattern().Replace(text, string.Empty);
        text = MdBlockquotePattern().Replace(text, string.Empty);
        text = MdBulletPattern().Replace(text, "$1- ");
        text = MdStrongUnderlinePattern().Replace(text, "$1");
        text = MdEmphasisPattern().Replace(text, "$1");
        text = MdInlineCodePattern().Replace(text, "$1");
        text = MdMultiBlankLinesPattern().Replace(text, "\n\n");
        return text.Trim();
    }

    private const string LobbyCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>规范化 8 位梦港房间码（大写、去连字符/空白/下划线）。</summary>
    public static string NormalizeRoomCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (ch == '-' || ch == '_' || char.IsWhiteSpace(ch))
                continue;
            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>校验是否符合梦港 8 位 Base32 房间码格式（不含 I / O / L / U 以外的合法字符集）。</summary>
    public static bool IsValidRoomCodeFormat(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized) || normalized.Length != 8)
            return false;

        foreach (var ch in normalized)
        {
            if (LobbyCodeAlphabet.IndexOf(ch) < 0)
                return false;
        }

        return true;
    }

    public async Task<DreamPortLobbyRoomsResult> GetLobbyRoomsAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/api/lobby/rooms", auth: false, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取联机大厅列表失败", out var reason, out var error))
            return new DreamPortLobbyRoomsResult(false, [], error, reason);

        var data = UnwrapData(res.Body);
        var rooms = new List<DreamPortLobbyRoom>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("rooms", out var roomsEl)
            && roomsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in roomsEl.EnumerateArray())
            {
                var room = ParseLobbyRoom(row);
                if (room is not null)
                    rooms.Add(room);
            }
        }

        return new DreamPortLobbyRoomsResult(true, rooms);
    }

    public async Task<DreamPortLobbyMyRoomResult> GetMyLobbyRoomAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortLobbyMyRoomResult(false, null, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Get, "/api/lobby/rooms/mine", auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "查询我的房间失败", out var reason, out var error))
            return new DreamPortLobbyMyRoomResult(false, null, error, reason);

        var data = UnwrapData(res.Body);
        DreamPortLobbyRoom? room = null;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("room", out var roomEl)
            && roomEl.ValueKind == JsonValueKind.Object)
        {
            room = ParseLobbyRoom(roomEl);
        }

        return new DreamPortLobbyMyRoomResult(true, room);
    }

    public async Task<DreamPortLobbyCreateResult> CreateLobbyRoomAsync(
        string name,
        string visibility = "public",
        string? mcVersion = null,
        string? modLoader = null,
        string? modHash = null,
        IReadOnlyList<string>? modList = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortLobbyCreateResult(false, null, null, 0, "未登录", DreamPortFailureReason.Expired);

        var payload = new
        {
            name = (name ?? string.Empty).Trim(),
            visibility = string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase) ? "private" : "public",
            mcVersion = (mcVersion ?? string.Empty).Trim(),
            modHash = (modHash ?? string.Empty).Trim(),
            modList = modList ?? [],
            modLoader = (modLoader ?? string.Empty).Trim().ToLowerInvariant()
        };

        var res = await SendAsync(HttpMethod.Post, "/api/lobby/rooms", payload, auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "创建联机房间失败", out var reason, out var error))
            return new DreamPortLobbyCreateResult(false, null, null, 0, error, reason);

        var data = UnwrapData(res.Body);
        var room = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("room", out var roomEl)
            ? ParseLobbyRoom(roomEl)
            : null;
        var ticket = FirstNonBlank(data, "ticket");
        var ttl = GetInt(data, "ticketTtlSeconds") ?? 0;

        if (room is null || string.IsNullOrWhiteSpace(ticket))
            return new DreamPortLobbyCreateResult(false, room, ticket, ttl, "服务端未返回有效房间信息或房票");

        return new DreamPortLobbyCreateResult(true, room, ticket, ttl, ExtractMessage(res.Body, "房间已创建"));
    }

    public async Task<DreamPortLobbyJoinResult> JoinLobbyRoomAsync(
        string code,
        string? mcVersion = null,
        string? modHash = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortLobbyJoinResult(false, null, null, 0, null, null, "未登录", DreamPortFailureReason.Expired);

        var normalized = NormalizeRoomCode(code);
        if (string.IsNullOrEmpty(normalized))
            return new DreamPortLobbyJoinResult(false, null, null, 0, null, null, "请输入有效的房间码");

        var payload = new
        {
            mcVersion = (mcVersion ?? string.Empty).Trim(),
            modHash = (modHash ?? string.Empty).Trim()
        };

        var res = await SendAsync(
            HttpMethod.Post,
            $"/api/lobby/rooms/{Uri.EscapeDataString(normalized)}/join",
            payload,
            auth: true,
            ct: ct).ConfigureAwait(false);

        if (res.Status == 404)
        {
            var data404 = UnwrapData(res.Body);
            var reasonCode = FirstNonBlank(data404, "reason") ?? DreamPortRelayWire.ReasonRoomNotFound;
            var msg404 = ExtractMessage(res.Body, DreamPortRelayWire.RejectMessage(reasonCode));
            return new DreamPortLobbyJoinResult(false, null, null, 0, null, reasonCode, msg404);
        }

        if (TryClassifyFailure(res, "加入联机房间失败", out var reason, out var error))
            return new DreamPortLobbyJoinResult(false, null, null, 0, null, null, error, reason);

        var data = UnwrapData(res.Body);
        var room = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("room", out var roomEl)
            ? ParseLobbyRoom(roomEl)
            : null;
        var ticket = FirstNonBlank(data, "ticket");
        var ttl = GetInt(data, "ticketTtlSeconds") ?? 0;

        DreamPortLobbyJoinDiff? diff = null;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("diff", out var diffEl)
            && diffEl.ValueKind == JsonValueKind.Object)
        {
            diff = new DreamPortLobbyJoinDiff(
                VersionMismatch: GetBool(diffEl, "versionMismatch"),
                RoomVersion: GetString(diffEl, "roomVersion") ?? string.Empty,
                MyVersion: GetString(diffEl, "myVersion") ?? string.Empty);
        }

        if (room is null || string.IsNullOrWhiteSpace(ticket))
            return new DreamPortLobbyJoinResult(false, room, ticket, ttl, diff, null, "服务端未返回有效房间或访客房票");

        return new DreamPortLobbyJoinResult(true, room, ticket, ttl, diff, null, ExtractMessage(res.Body, "可以加入"));
    }

    public async Task<DreamPortApiActionResult> CloseLobbyRoomAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var normalized = NormalizeRoomCode(code);
        if (string.IsNullOrEmpty(normalized))
            return new DreamPortApiActionResult(false, "房间码不能为空");

        var res = await SendAsync(
            HttpMethod.Post,
            $"/api/lobby/rooms/{Uri.EscapeDataString(normalized)}/close",
            new { },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "房间已关闭", "关闭房间失败");
    }

    public async Task<DreamPortLobbyInvitesResult> GetLobbyInvitesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortLobbyInvitesResult(false, [], [], 0, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Get, "/api/lobby/invites", auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取联机邀请失败", out var reason, out var error))
            return new DreamPortLobbyInvitesResult(false, [], [], 0, error, reason);

        var data = UnwrapData(res.Body);
        var incoming = ParseLobbyInviteArray(data, "incoming");
        var outgoing = ParseLobbyInviteArray(data, "outgoing");
        var ttl = GetInt(data, "ttlSeconds") ?? 1800;
        return new DreamPortLobbyInvitesResult(true, incoming, outgoing, ttl);
    }

    public async Task<DreamPortApiActionResult> InviteFriendToRoomAsync(
        string code,
        string username,
        string? message = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var normalized = NormalizeRoomCode(code);
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrWhiteSpace(username))
            return new DreamPortApiActionResult(false, "请选择要邀请的好友");

        var res = await SendAsync(
            HttpMethod.Post,
            $"/api/lobby/rooms/{Uri.EscapeDataString(normalized)}/invite",
            new
            {
                username = username.Trim(),
                message = string.IsNullOrWhiteSpace(message) ? null : message.Trim()
            },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "已向好友发送联机邀请", "发送联机邀请失败");
    }

    public async Task<DreamPortLobbyAcceptInviteResult> AcceptLobbyInviteAsync(long inviteId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortLobbyAcceptInviteResult(false, null, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(
            HttpMethod.Post,
            $"/api/lobby/invites/{inviteId}/accept",
            new { },
            auth: true,
            ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "接受联机邀请失败", out var reason, out var error))
            return new DreamPortLobbyAcceptInviteResult(false, null, error, reason);

        var data = UnwrapData(res.Body);
        var code = FirstNonBlank(data, "code");
        if (string.IsNullOrEmpty(code))
            return new DreamPortLobbyAcceptInviteResult(false, null, "未返回房间码");

        return new DreamPortLobbyAcceptInviteResult(true, NormalizeRoomCode(code), ExtractMessage(res.Body, "已接受邀请"));
    }

    public async Task<DreamPortApiActionResult> RejectLobbyInviteAsync(long inviteId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(
            HttpMethod.Post,
            $"/api/lobby/invites/{inviteId}/reject",
            new { },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "已拒绝邀请", "拒绝邀请失败");
    }

    public async Task<DreamPortApiActionResult> CancelLobbyInviteAsync(long inviteId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortApiActionResult(false, "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(
            HttpMethod.Post,
            $"/api/lobby/invites/{inviteId}/cancel",
            new { },
            auth: true,
            ct: ct).ConfigureAwait(false);
        return ToActionResult(res, "已撤回邀请", "撤回邀请失败");
    }

    public async Task<DreamPortFriendsResult> GetFriendsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token))
            return new DreamPortFriendsResult(false, [], "未登录", DreamPortFailureReason.Expired);

        var res = await SendAsync(HttpMethod.Get, "/api/friends", auth: true, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取好友列表失败", out var reason, out var error))
            return new DreamPortFriendsResult(false, [], error, reason);

        var data = UnwrapData(res.Body);
        var friends = new List<string>();
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("friends", out var friendsEl)
            && friendsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in friendsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var username = FirstNonBlank(item, "username");
                if (!string.IsNullOrEmpty(username))
                    friends.Add(username);
            }
        }

        return new DreamPortFriendsResult(true, friends);
    }

    private static DreamPortLobbyRoom? ParseLobbyRoom(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        var code = NormalizeRoomCode(GetString(el, "code"));
        if (string.IsNullOrEmpty(code))
            return null;

        var modList = new List<string>();
        if (el.TryGetProperty("modList", out var modsEl) && modsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in modsEl.EnumerateArray())
            {
                var name = m.ValueKind == JsonValueKind.String ? m.GetString() : m.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    modList.Add(name.Trim());
            }
        }

        return new DreamPortLobbyRoom(
            Code: code,
            Host: GetString(el, "host") ?? string.Empty,
            Name: GetString(el, "name") ?? code,
            Visibility: GetString(el, "visibility") ?? "public",
            MaxPlayers: GetInt(el, "maxPlayers") ?? 8,
            McVersion: GetString(el, "mcVersion") ?? string.Empty,
            ModLoader: GetString(el, "modLoader") ?? string.Empty,
            ModHash: GetString(el, "modHash") ?? string.Empty,
            ModList: modList,
            Status: GetString(el, "status") ?? "open",
            CreatedAtMs: GetLong(el, "createdAt") ?? 0,
            Occupancy: GetInt(el, "occupancy"),
            HostGone: GetBool(el, "hostGone"));
    }

    private static IReadOnlyList<DreamPortLobbyInviteItem> ParseLobbyInviteArray(JsonElement parent, string propertyName)
    {
        var items = new List<DreamPortLobbyInviteItem>();
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(propertyName, out var listEl)
            || listEl.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var row in listEl.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            var id = GetLong(row, "id") ?? 0;
            var code = NormalizeRoomCode(GetString(row, "code"));
            if (id <= 0 || string.IsNullOrEmpty(code))
                continue;

            items.Add(new DreamPortLobbyInviteItem(
                Id: id,
                Code: code,
                Inviter: GetString(row, "inviter") ?? string.Empty,
                Invitee: GetString(row, "invitee") ?? string.Empty,
                Message: FirstNonBlank(row, "message"),
                CreatedAtMs: GetLong(row, "createdAt") ?? 0,
                ExpiresAtMs: GetLong(row, "expiresAt") ?? 0,
                RoomOpen: GetBool(row, "roomOpen"),
                RoomName: FirstNonBlank(row, "roomName"),
                McVersion: FirstNonBlank(row, "mcVersion"),
                MaxPlayers: GetInt(row, "maxPlayers")));
        }

        return items;
    }

    public string BuildServerPackFileUrl(string relativePath)
    {
        var segments = (relativePath ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return _baseUrl + "/api/client/serverpack/file/" + string.Join("/", segments);
    }

    public async Task<DreamPortServerPackManifestResult> GetServerPackManifestAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "/api/client/serverpack", auth: false, ct: ct).ConfigureAwait(false);
        if (TryClassifyFailure(res, "获取服务器配套模组清单失败", out var reason, out var error))
        {
            return new DreamPortServerPackManifestResult(
                Ok: false,
                Version: string.Empty,
                GeneratedAtMs: 0,
                GameVersion: string.Empty,
                Loaders: [],
                ModrinthMods: [],
                Files: [],
                TotalBytes: 0,
                Message: error,
                FailureReason: reason);
        }

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Object)
        {
            return new DreamPortServerPackManifestResult(
                Ok: false,
                Version: string.Empty,
                GeneratedAtMs: 0,
                GameVersion: string.Empty,
                Loaders: [],
                ModrinthMods: [],
                Files: [],
                TotalBytes: 0,
                Message: ExtractMessage(res.Body, "服务端返回配套模组清单格式无效"));
        }

        var loaders = new List<string>();
        if (data.TryGetProperty("loaders", out var loadersEl) && loadersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in loadersEl.EnumerateArray())
            {
                var l = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(l))
                    loaders.Add(l.Trim().ToLowerInvariant());
            }
        }

        var modrinthMods = new List<DreamPortModrinthModSpec>();
        if (data.TryGetProperty("modrinthMods", out var mrEl) && mrEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in mrEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                var projectId = FirstNonBlank(row, "projectId") ?? string.Empty;
                var slug = FirstNonBlank(row, "slug") ?? projectId;
                if (string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(slug))
                    continue;

                var title = FirstNonBlank(row, "title", "name") ?? (string.IsNullOrEmpty(slug) ? projectId : slug);
                var versionId = FirstNonBlank(row, "versionId");
                var required = !row.TryGetProperty("required", out var reqEl) || reqEl.ValueKind != JsonValueKind.False;
                modrinthMods.Add(new DreamPortModrinthModSpec(projectId, slug, title, versionId, required));
            }
        }

        var files = new List<DreamPortServerPackFile>();
        if (data.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in filesEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                var path = GetString(row, "path");
                var sha = GetString(row, "sha256");
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha))
                    continue;

                files.Add(new DreamPortServerPackFile(
                    Path: path.Trim(),
                    Sha256: sha.Trim(),
                    Size: GetLong(row, "size") ?? 0,
                    Kind: FirstNonBlank(row, "kind"),
                    Loader: FirstNonBlank(row, "loader"),
                    GameVersion: FirstNonBlank(row, "gameVersion")));
            }
        }

        return new DreamPortServerPackManifestResult(
            Ok: true,
            Version: GetString(data, "version") ?? "0",
            GeneratedAtMs: GetLong(data, "generatedAt") ?? 0,
            GameVersion: GetString(data, "gameVersion") ?? string.Empty,
            Loaders: loaders,
            ModrinthMods: modrinthMods,
            Files: files,
            TotalBytes: GetLong(data, "totalBytes") ?? files.Sum(f => f.Size),
            Message: "已获取配套模组清单");
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }

    private string ResolveAbsoluteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        // Unix 平台下 "/qr-login..." 会被 Uri.TryCreate 识别为 file:// 绝对 URI，必须限定 http/https
        if (Uri.TryCreate(url, UriKind.Absolute, out var existing)
            && (existing.Scheme == Uri.UriSchemeHttp || existing.Scheme == Uri.UriSchemeHttps))
        {
            return url;
        }

        return Uri.TryCreate(_baseUrl + "/", UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, url.TrimStart('/'), out var combined)
                ? combined.ToString()
                : _baseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string accept,
        bool auth)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        req.Headers.TryAddWithoutValidation("User-Agent", DreamPortBuildConfig.UserAgent);
        req.Headers.TryAddWithoutValidation("X-DreamPort-Surface", "desktop");
        req.Headers.TryAddWithoutValidation("X-Device-Name", $"PCL-DreamPort ({SanitizeHeaderValue(Environment.MachineName)})");
        req.Headers.TryAddWithoutValidation("X-Device-Platform", GetPlatformTag());
        req.Headers.TryAddWithoutValidation("X-Client-Version", DreamPortBuildConfig.AppVersion);

        if (auth && !string.IsNullOrEmpty(_token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        return req;
    }

    private async Task<ApiResponse> SendAsync(
        HttpMethod method,
        string path,
        object? jsonBody = null,
        bool auth = true,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? RequestTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        using var req = CreateRequest(method, path, accept: "application/json", auth: auth);

        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, JsonOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            using var resp = await _http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            var parsed = TryParseJson(raw);
            return new ApiResponse((int)resp.StatusCode, parsed, raw, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ApiResponse(0, null, null, $"请求超时（{(int)effectiveTimeout.TotalSeconds} 秒）");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new ApiResponse(0, null, null, ex.Message);
        }
    }

    private static DreamPortApiActionResult ToActionResult(
        ApiResponse res,
        string successFallback,
        string errorPrefix)
    {
        if (res.Status == 0)
            return new DreamPortApiActionResult(false, BuildNetworkError(res, errorPrefix), DreamPortFailureReason.Network);
        if (res.Status == 401)
            return new DreamPortApiActionResult(false, ExtractMessage(res.Body, "登录已过期，请重新登录"), DreamPortFailureReason.Expired);

        if (res.Status == 200)
        {
            var ok = !IsExplicitFailure(res.Body);
            var msg = ExtractMessage(res.Body, ok ? successFallback : errorPrefix);
            return new DreamPortApiActionResult(ok, msg, null);
        }

        return new DreamPortApiActionResult(false, ExtractMessage(res.Body, $"{errorPrefix}（HTTP {res.Status}）"), null);
    }

    private static bool IsExplicitFailure(JsonDocument? doc)
    {
        return doc is not null
            && doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("success", out var el)
            && el.ValueKind == JsonValueKind.False;
    }

    private static DreamPortBrowserLoginStatus ParseBrowserLoginStatus(string? raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "approved" => DreamPortBrowserLoginStatus.Approved,
            "denied" => DreamPortBrowserLoginStatus.Denied,
            "expired" => DreamPortBrowserLoginStatus.Expired,
            "consumed" => DreamPortBrowserLoginStatus.Consumed,
            _ => DreamPortBrowserLoginStatus.Pending
        };
    }

    private static JsonDocument? TryParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonDocument.Parse(raw);
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement UnwrapData(JsonDocument? doc)
    {
        if (doc is null)
            return default;

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var dataEl)
            && dataEl.ValueKind != JsonValueKind.Undefined
            && dataEl.ValueKind != JsonValueKind.Null)
        {
            return dataEl;
        }

        return root;
    }

    private static string ExtractMessage(JsonDocument? doc, string fallback)
    {
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            var msg = GetString(doc.RootElement, "message")
                ?? GetString(doc.RootElement, "error");
            if (!string.IsNullOrWhiteSpace(msg))
                return msg;
        }

        return fallback;
    }

    private static string BuildNetworkError(ApiResponse res, string prefix)
    {
        var detail = string.IsNullOrWhiteSpace(res.NetError) ? prefix : res.NetError;
        return $"{detail}，请检查服务地址与网络";
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty(name, out var prop)
            || prop.ValueKind == JsonValueKind.Null
            || prop.ValueKind == JsonValueKind.Undefined)
            return null;

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    private static bool GetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => prop.TryGetInt64(out var n) && n != 0,
            JsonValueKind.String => bool.TryParse(prop.GetString(), out var b) && b,
            _ => false
        };
    }

    private static bool? GetNullableBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(prop.GetString(), out var b) => b,
            _ => null
        };
    }

    private static int? GetInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
    }

    private static long? GetLong(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var v) ? v : null;
    }

    private static bool TryClassifyFailure(
        ApiResponse res,
        string errorPrefix,
        out DreamPortFailureReason? reason,
        out string message)
    {
        if (res.Status == 0)
        {
            reason = DreamPortFailureReason.Network;
            message = BuildNetworkError(res, errorPrefix);
            return true;
        }

        if (res.Status == 401)
        {
            reason = DreamPortFailureReason.Expired;
            message = ExtractMessage(res.Body, "登录已过期，请重新登录");
            return true;
        }

        if (res.Status != 200 || IsExplicitFailure(res.Body))
        {
            reason = null;
            message = ExtractMessage(res.Body, $"{errorPrefix}（HTTP {res.Status}）");
            return true;
        }

        reason = null;
        message = string.Empty;
        return false;
    }

    private static string? FirstNonBlank(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            var val = GetString(el, name);
            if (!string.IsNullOrWhiteSpace(val))
                return val.Trim();
        }

        return null;
    }

    private static JsonElement? FirstNonBlankElement(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var prop)
                || prop.ValueKind == JsonValueKind.Null
                || prop.ValueKind == JsonValueKind.Undefined)
                continue;

            if (prop.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(prop.GetString()))
                continue;

            return prop;
        }

        return null;
    }

    private static string? ShowAnnouncementTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        if (trimmed.Equals("published", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("draft", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("archived", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("hidden", StringComparison.OrdinalIgnoreCase))
            return null;
        return trimmed;
    }

    private static string FormatAnnouncementDate(JsonElement? element)
    {
        if (element is not { } prop)
            return string.Empty;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var numMs))
        {
            return FormatUnixMsOrFallback(numMs, prop.ToString());
        }

        var raw = (prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString())?.Trim();
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        if (raw.Length >= 10 && raw.All(char.IsDigit) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMs))
            return FormatUnixMsOrFallback(parsedMs, raw);

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return raw;
    }

    private static string FormatUnixMsOrFallback(long ms, string fallback)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static string GetPlatformTag()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows";
        return "linux";
    }

    private static string SanitizeHeaderValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var sb = new StringBuilder(Math.Min(value.Length, 64));
        foreach (var ch in value)
        {
            if (sb.Length >= 64)
                break;
            if (ch >= 0x20 && ch <= 0x7E)
                sb.Append(ch);
        }

        return sb.Length > 0 ? sb.ToString() : "unknown";
    }

    [GeneratedRegex(@"!?\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.Compiled)]
    private static partial Regex MdLinkPattern();

    [GeneratedRegex(@"^[ \t]*#{1,6}[ \t]+", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MdHeadingPattern();

    [GeneratedRegex(@"^[ \t]*>[ \t]?", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MdBlockquotePattern();

    [GeneratedRegex(@"^([ \t]*)[-*+][ \t]+", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MdBulletPattern();

    [GeneratedRegex(@"\*\*([^*\r\n]+)\*\*", RegexOptions.Compiled)]
    private static partial Regex MdStrongUnderlinePattern();

    [GeneratedRegex(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)", RegexOptions.Compiled)]
    private static partial Regex MdEmphasisPattern();

    [GeneratedRegex(@"`([^`\r\n]+)`", RegexOptions.Compiled)]
    private static partial Regex MdInlineCodePattern();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex MdMultiBlankLinesPattern();

    private sealed record ApiResponse(int Status, JsonDocument? Body, string? RawBody, string? NetError);
}
