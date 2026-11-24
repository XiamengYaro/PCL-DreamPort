using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.Config;
using PCL.Core.App;
using PCL.Core.Logging;

namespace DreamPort.Core.Auth;

/// <summary>系统级令牌加密存储抽象（便于跨平台实现与单测隔离）。</summary>
public interface IDreamPortSecretVault
{
    bool TrySaveSecret(string accountKey, string secret, out string? encryptedPayload);
    bool TryLoadSecret(string accountKey, string? encryptedPayload, out string? secret);
    void DeleteSecret(string accountKey);
}

/// <summary>
/// DreamPort 会话本地存储（<c>dreamport-session.json</c>）。
/// 元数据（BaseUrl / Username / IsAdmin / IsTokenPersistent）明文落盘；
/// 令牌仅在系统安全存储可用时通过 Windows DPAPI 或 macOS Keychain 持久化，否则仅驻留内存，绝不明文落盘。
/// </summary>
public sealed class DreamPortSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _sessionFilePath;
    private readonly IDreamPortSecretVault _vault;
    private string? _inMemoryToken;

    public DreamPortSessionStore(string? dataDirectory = null, IDreamPortSecretVault? vault = null)
    {
        var dir = string.IsNullOrWhiteSpace(dataDirectory) ? Paths.Data : dataDirectory;
        DataDirectory = dir;
        _sessionFilePath = Path.Combine(dir, "dreamport-session.json");
        _vault = vault ?? new OsDreamPortSecretVault();
    }

    public string DataDirectory { get; }

    public DreamPortStoredSession Load()
    {
        SessionMetadata? meta = null;
        try
        {
            if (File.Exists(_sessionFilePath))
            {
                var raw = File.ReadAllText(_sessionFilePath, Encoding.UTF8);
                meta = JsonSerializer.Deserialize<SessionMetadata>(raw);
            }
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "DreamPort", "读取会话元数据失败，将使用默认配置");
        }

        var baseUrl = DreamPortApiClient.TryNormalizeBaseUrl(meta?.BaseUrl, out var normalized, out _)
            ? normalized
            : DreamPortBuildConfig.BaseUrl;

        string? token = _inMemoryToken;
        var persistent = false;
        if (meta is not null && (!string.IsNullOrWhiteSpace(meta.EncryptedToken) || meta.HasKeychainSecret))
        {
            if (_vault.TryLoadSecret(baseUrl, meta.EncryptedToken, out var loaded)
                && !string.IsNullOrWhiteSpace(loaded))
            {
                token = loaded;
                persistent = true;
            }
        }

        return new DreamPortStoredSession(
            BaseUrl: baseUrl,
            Username: meta?.Username,
            IsAdmin: meta?.IsAdmin ?? false,
            IsTokenPersistent: persistent,
            Token: token);
    }

    public DreamPortStoredSession Save(string baseUrl, string? username, bool isAdmin, string? token)
    {
        if (!DreamPortApiClient.TryNormalizeBaseUrl(baseUrl, out var normalizedUrl, out var err))
            throw new ArgumentException(err ?? "站点地址无效", nameof(baseUrl));

        _inMemoryToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        string? encryptedPayload = null;
        var hasKeychain = false;
        var persistent = false;

        if (!string.IsNullOrEmpty(_inMemoryToken))
        {
            if (_vault.TrySaveSecret(normalizedUrl, _inMemoryToken, out encryptedPayload))
            {
                persistent = true;
                hasKeychain = string.IsNullOrEmpty(encryptedPayload);
            }
        }
        else
        {
            _vault.DeleteSecret(normalizedUrl);
        }

        WriteMetadata(new SessionMetadata
        {
            BaseUrl = normalizedUrl,
            Username = username,
            IsAdmin = isAdmin,
            IsTokenPersistent = persistent,
            HasKeychainSecret = hasKeychain,
            EncryptedToken = encryptedPayload
        });

        return new DreamPortStoredSession(
            BaseUrl: normalizedUrl,
            Username: username,
            IsAdmin: isAdmin,
            IsTokenPersistent: persistent,
            Token: _inMemoryToken);
    }

    public DreamPortStoredSession SaveBaseUrlOnly(string baseUrl)
    {
        var current = Load();
        return Save(baseUrl, current.Username, current.IsAdmin, current.Token);
    }

    public DreamPortStoredSession ClearToken(string? baseUrl = null)
    {
        var current = Load();
        var targetUrl = DreamPortApiClient.TryNormalizeBaseUrl(baseUrl, out var norm, out _)
            ? norm
            : current.BaseUrl;

        _inMemoryToken = null;
        _vault.DeleteSecret(targetUrl);

        WriteMetadata(new SessionMetadata
        {
            BaseUrl = targetUrl,
            Username = null,
            IsAdmin = false,
            IsTokenPersistent = false,
            HasKeychainSecret = false,
            EncryptedToken = null
        });

        return new DreamPortStoredSession(targetUrl, null, false, false, null);
    }

    private void WriteMetadata(SessionMetadata meta)
    {
        try
        {
            var dir = Path.GetDirectoryName(_sessionFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = _sessionFilePath + ".tmp";
            var json = JsonSerializer.Serialize(meta, JsonOptions);
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, _sessionFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "DreamPort", "保存会话元数据失败");
        }
    }

    private sealed class SessionMetadata
    {
        public string BaseUrl { get; set; } = DreamPortBuildConfig.BaseUrl;
        public string? Username { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsTokenPersistent { get; set; }
        public bool HasKeychainSecret { get; set; }
        public string? EncryptedToken { get; set; }
    }
}

internal sealed class OsDreamPortSecretVault : IDreamPortSecretVault
{
    private const string MacServiceName = "cn.xmcraft.pcl-dreamport";
    private static readonly byte[] DpapiEntropy = "PCL-DreamPort-Session-v1"u8.ToArray();
    private static readonly TimeSpan KeychainTimeout = TimeSpan.FromSeconds(5);

    public bool TrySaveSecret(string accountKey, string secret, out string? encryptedPayload)
    {
        encryptedPayload = null;
        if (OperatingSystem.IsWindows())
            return TryWindowsProtect(secret, out encryptedPayload);

        if (OperatingSystem.IsMacOS())
            return TryMacKeychainWrite(accountKey, secret);

        return false;
    }

    public bool TryLoadSecret(string accountKey, string? encryptedPayload, out string? secret)
    {
        secret = null;
        if (OperatingSystem.IsWindows())
            return TryWindowsUnprotect(encryptedPayload, out secret);

        if (OperatingSystem.IsMacOS())
            return TryMacKeychainRead(accountKey, out secret);

        return false;
    }

    public void DeleteSecret(string accountKey)
    {
        if (OperatingSystem.IsMacOS())
            RunMacSecurity("delete-generic-password", "-s", MacServiceName, "-a", accountKey);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryWindowsProtect(string secret, out string? encryptedPayload)
    {
        encryptedPayload = null;
        try
        {
            var raw = Encoding.UTF8.GetBytes(secret);
            var cipher = ProtectedData.Protect(raw, DpapiEntropy, DataProtectionScope.CurrentUser);
            encryptedPayload = Convert.ToBase64String(cipher);
            return true;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "DreamPort", "Windows DPAPI 加密令牌失败，本次仅使用内存会话");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryWindowsUnprotect(string? encryptedPayload, out string? secret)
    {
        secret = null;
        if (string.IsNullOrWhiteSpace(encryptedPayload))
            return false;

        try
        {
            var cipher = Convert.FromBase64String(encryptedPayload);
            var raw = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
            secret = Encoding.UTF8.GetString(raw);
            return !string.IsNullOrWhiteSpace(secret);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "DreamPort", "Windows DPAPI 解密令牌失败，需要重新登录");
            return false;
        }
    }

    [SupportedOSPlatform("macos")]
    private static bool TryMacKeychainWrite(string accountKey, string secret)
    {
        var result = RunMacSecurity("add-generic-password", "-U", "-s", MacServiceName, "-a", accountKey, "-w", secret);
        return result.Success;
    }

    [SupportedOSPlatform("macos")]
    private static bool TryMacKeychainRead(string accountKey, out string? secret)
    {
        secret = null;
        var result = RunMacSecurity("find-generic-password", "-s", MacServiceName, "-a", accountKey, "-w");
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
            return false;

        secret = result.StdOut.Trim();
        return !string.IsNullOrEmpty(secret);
    }

    [SupportedOSPlatform("macos")]
    private static (bool Success, string StdOut) RunMacSecurity(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return (false, string.Empty);

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)KeychainTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, string.Empty);
            }

            return (process.ExitCode == 0, stdout);
        }
        catch
        {
            return (false, string.Empty);
        }
    }
}
