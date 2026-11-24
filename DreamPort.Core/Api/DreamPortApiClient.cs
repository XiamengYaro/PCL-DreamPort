using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Config;

namespace DreamPort.Core.Api;

/// <summary>
/// DreamPort 站点 API 客户端（Phase 1 原型与通用基础设施）。
/// 对齐服务端 `{success, message, data}` 包装与裸响应例外、四分支登录、20s 超时与网络错误三态语义。
/// </summary>
public sealed class DreamPortApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
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

    public async Task<bool> IsLoginCaptchaRequiredAsync(CancellationToken ct = default)
    {
        var cfg = await GetConfigAsync(ct).ConfigureAwait(false);
        return cfg?.LoginCaptchaRequired == true;
    }

    public async Task<DreamPortCaptchaResult?> GenerateCaptchaAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Post, "/api/captcha/generate", auth: false, ct: ct).ConfigureAwait(false);
        if (res.Status != 200)
            return null;

        var data = UnwrapData(res.Body);
        if (data.ValueKind != JsonValueKind.Object)
            return null;

        var token = GetString(data, "captchaToken");
        var image = GetString(data, "image");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(image))
            return null;

        return new DreamPortCaptchaResult(token, image);
    }

    public async Task<DreamPortLoginResult> LoginAsync(
        string username,
        string password,
        string? captchaToken = null,
        string? captchaAnswer = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["username"] = username,
            ["password"] = password
        };
        if (!string.IsNullOrWhiteSpace(captchaToken) && !string.IsNullOrWhiteSpace(captchaAnswer))
        {
            payload["captchaToken"] = captchaToken;
            payload["captchaAnswer"] = captchaAnswer.Trim();
        }

        var res = await SendAsync(HttpMethod.Post, "/api/login", payload, auth: false, ct: ct).ConfigureAwait(false);
        return ParseLoginResponse(res, username);
    }

    public async Task<DreamPortLoginResult> SubmitTotpAsync(
        string challengeId,
        string code,
        CancellationToken ct = default)
    {
        var res = await SendAsync(
            HttpMethod.Post,
            "/api/login/2fa",
            new { challengeId, code = code.Trim() },
            auth: false,
            ct: ct).ConfigureAwait(false);

        return ParseLoginResponse(res, fallbackUsername: string.Empty, defaultError: "二次验证失败");
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

    private DreamPortLoginResult ParseLoginResponse(
        ApiResponse res,
        string fallbackUsername,
        string defaultError = "登录失败")
    {
        if (res.Status == 0)
        {
            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Error,
                ErrorMessage = BuildNetworkError(res, "无法连接站点")
            };
        }

        if (res.Status == 403)
        {
            var banData = UnwrapData(res.Body);
            if (banData.ValueKind == JsonValueKind.Object && GetBool(banData, "banned"))
            {
                return new DreamPortLoginResult
                {
                    Outcome = DreamPortLoginOutcome.Banned,
                    Username = GetString(banData, "username") ?? fallbackUsername,
                    BanReason = GetString(banData, "reason") ?? "未提供理由",
                    BanUntil = GetLong(banData, "banUntil"),
                    Evidence = ParseEvidence(banData)
                };
            }

            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Error,
                ErrorMessage = ExtractMessage(res.Body, defaultError)
            };
        }

        if (res.Status == 400)
        {
            var msg = ExtractMessage(res.Body, string.Empty);
            if (msg.Contains("验证码", StringComparison.Ordinal))
                return new DreamPortLoginResult { Outcome = DreamPortLoginOutcome.NeedCaptcha, ErrorMessage = msg };

            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Error,
                ErrorMessage = string.IsNullOrWhiteSpace(msg) ? "请求被拒绝" : msg
            };
        }

        if (res.Status == 401)
        {
            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Error,
                ErrorMessage = ExtractMessage(res.Body, "用户名或密码错误")
            };
        }

        if (res.Status == 429)
        {
            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Error,
                ErrorMessage = ExtractMessage(res.Body, "请求过于频繁，请稍后再试")
            };
        }

        if (res.Status != 200)
        {
            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Error,
                ErrorMessage = ExtractMessage(res.Body, $"{defaultError}（HTTP {res.Status}）")
            };
        }

        var data = UnwrapData(res.Body);
        var status = GetString(data, "status");
        if (string.Equals(status, "needs_2fa", StringComparison.Ordinal))
        {
            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Need2Fa,
                Username = GetString(data, "username") ?? fallbackUsername,
                Status = status,
                IsAdmin = GetBool(data, "isAdmin"),
                ChallengeId = GetString(data, "challengeId") ?? string.Empty,
                SupportsPasskey = GetBool(data, "passkey")
            };
        }

        if (string.Equals(status, "needs_2fa_setup", StringComparison.Ordinal))
        {
            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Need2FaSetup,
                Username = GetString(data, "username") ?? fallbackUsername,
                Status = status,
                IsAdmin = GetBool(data, "isAdmin"),
                ChallengeId = GetString(data, "challengeId") ?? string.Empty,
                ErrorMessage = "管理员账号需要先在网页端完成 2FA 绑定后才能登录"
            };
        }

        var token = GetString(data, "token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            SetToken(token);
            return new DreamPortLoginResult
            {
                Outcome = DreamPortLoginOutcome.Ok,
                Token = _token,
                Username = GetString(data, "username") ?? fallbackUsername,
                Status = status ?? string.Empty,
                IsAdmin = GetBool(data, "isAdmin")
            };
        }

        return new DreamPortLoginResult
        {
            Outcome = DreamPortLoginOutcome.Error,
            ErrorMessage = ExtractMessage(res.Body, "登录失败：响应缺少令牌")
        };
    }

    private IReadOnlyList<DreamPortBanEvidence> ParseEvidence(JsonElement banData)
    {
        if (!banData.TryGetProperty("evidence", out var listEl) || listEl.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<DreamPortBanEvidence>();
        foreach (var item in listEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var relUrl = GetString(item, "fileUrl") ?? string.Empty;
            var absUrl = ResolveAbsoluteUrl(relUrl);
            list.Add(new DreamPortBanEvidence(
                Id: GetString(item, "id") ?? string.Empty,
                FileUrl: absUrl,
                Note: GetString(item, "note"),
                MediaType: GetString(item, "mediaType")));
        }

        return list;
    }

    private string ResolveAbsoluteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        // Unix 平台下 "/uploads/..." 会被 Uri.TryCreate 识别为 file:// 绝对 URI，必须限定 http/https
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

    private async Task<ApiResponse> SendAsync(
        HttpMethod method,
        string path,
        object? jsonBody = null,
        bool auth = true,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, _baseUrl + path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("User-Agent", DreamPortBuildConfig.UserAgent);
        req.Headers.TryAddWithoutValidation("X-DreamPort-Surface", "desktop");
        req.Headers.TryAddWithoutValidation("X-Device-Name", SanitizeHeaderValue(Environment.MachineName));
        req.Headers.TryAddWithoutValidation("X-Device-Platform", GetPlatformTag());
        req.Headers.TryAddWithoutValidation("X-Client-Version", DreamPortBuildConfig.AppVersion);

        if (auth && !string.IsNullOrEmpty(_token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

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
