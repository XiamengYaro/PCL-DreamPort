using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Config;

namespace DreamPort.Core.Api;

/// <summary>
/// DreamPort 站点 API 客户端（浏览器授权登录 / 会话校验与设备登记）。
/// 对齐服务端 `{success, message, data}` 包装、20s 超时与网络错误三态语义。
/// </summary>
public sealed class DreamPortApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
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

        _http = handler is null
            ? new HttpClient { Timeout = RequestTimeout }
            : new HttpClient(handler, disposeHandler) { Timeout = RequestTimeout };
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
            McUuid: GetString(data, "mcUuid"),
            Points: GetInt(data, "points"),
            CheckinStreak: GetInt(data, "checkinStreak"));
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
        CancellationToken ct = default)
    {
        using var req = CreateRequest(method, path, accept: "application/json", auth: auth);

        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, JsonOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = TryParseJson(raw);
            return new ApiResponse((int)resp.StatusCode, parsed, raw, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ApiResponse(0, null, null, "请求超时（20 秒）");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return new ApiResponse(0, null, null, ex.Message);
        }
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
