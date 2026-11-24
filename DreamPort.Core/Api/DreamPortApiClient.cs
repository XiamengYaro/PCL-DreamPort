using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Config;

namespace DreamPort.Core.Api;

/// <summary>
/// DreamPort 站点 API 客户端（浏览器授权登录 / 会话与设备管理 / 六步入服引导流程 / AI 问卷 SSE）。
/// 严格区分会话过期（401 -> Expired）与网络错误（Status=0 -> Network）。
/// </summary>
public sealed class DreamPortApiClient : IDisposable
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

    private sealed record ApiResponse(int Status, JsonDocument? Body, string? RawBody, string? NetError);
}
