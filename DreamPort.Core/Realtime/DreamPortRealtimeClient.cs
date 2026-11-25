using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PCL.Core.Logging;

namespace DreamPort.Core.Realtime;

/// <summary>
/// 梦港后端 <c>/realtime</c> WebSocket 双向实时通道（仅接收轻量变更信号，不代替 REST 状态权威）。
/// 协议：<c>auth {kind:"client",token}</c> → <c>auth_success</c> / <c>changed {event}</c>，20 秒 <c>ping</c> 保活，断连指数退避重连。
/// </summary>
public sealed class DreamPortRealtimeClient : IAsyncDisposable
{
    private readonly Func<string> _getBaseUrl;
    private readonly Func<string?> _getToken;
    private readonly Action<string> _onChanged;
    private readonly Action<bool>? _onStatusChanged;
    private readonly Action<string>? _onLog;
    private readonly int _baseReconnectMs;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>>? _webSocketFactory;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationToken _disposeToken;

    private CancellationTokenSource? _runCts;
    private WebSocket? _activeSocket;
    private long _generation;
    private int _failures;
    private bool _stopped = true;
    private int _disposed;

    public DreamPortRealtimeClient(
        Func<string> getBaseUrl,
        Func<string?> getToken,
        Action<string> onChanged,
        Action<bool>? onStatusChanged = null,
        Action<string>? onLog = null,
        int baseReconnectMs = 2000,
        Func<Uri, CancellationToken, Task<WebSocket>>? webSocketFactory = null)
    {
        _getBaseUrl = getBaseUrl ?? throw new ArgumentNullException(nameof(getBaseUrl));
        _getToken = getToken ?? throw new ArgumentNullException(nameof(getToken));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onStatusChanged = onStatusChanged;
        _onLog = onLog;
        _baseReconnectMs = Math.Clamp(baseReconnectMs, 20, 10_000);
        _webSocketFactory = webSocketFactory;
        _disposeToken = _disposeCts.Token;
    }

    public bool IsConnected => Volatile.Read(ref _activeSocket) is { State: WebSocketState.Open };

    public int FailureCount => Volatile.Read(ref _failures);

    public static Uri BuildRealtimeUri(string baseUrl)
    {
        var raw = string.IsNullOrWhiteSpace(baseUrl) ? "https://xmcraft.cn" : baseUrl.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var source))
            source = new Uri("https://xmcraft.cn");

        var builder = new UriBuilder(source)
        {
            Scheme = string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws",
            Port = source.IsDefaultPort ? -1 : source.Port,
            Path = "/realtime",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!_stopped)
            return;

        var token = _getToken();
        if (string.IsNullOrWhiteSpace(token))
            return;

        _stopped = false;
        var generation = Interlocked.Increment(ref _generation);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeToken);
        var runToken = runCts.Token;
        var prev = Interlocked.Exchange(ref _runCts, runCts);
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { /* ignore */ }
        }

        _ = RunSessionLoopAsync(generation, runCts, runToken);
    }

    public async Task StopAsync()
    {
        _stopped = true;
        Interlocked.Increment(ref _generation);

        var runCts = Interlocked.Exchange(ref _runCts, null);
        if (runCts is not null)
        {
            try { runCts.Cancel(); } catch { /* ignore */ }
        }

        var sock = Interlocked.Exchange(ref _activeSocket, null);
        if (sock is not null)
        {
            await CloseSocketQuietlyAsync(sock).ConfigureAwait(false);
        }

        SafeInvokeStatus(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await StopAsync().ConfigureAwait(false);
        try { _disposeCts.Cancel(); } catch { /* ignore */ }
    }

    private async Task RunSessionLoopAsync(long generation, CancellationTokenSource runCts, CancellationToken runToken)
    {
        try
        {
            while (!runToken.IsCancellationRequested
                && !_stopped
                && generation == Volatile.Read(ref _generation))
            {
                var token = _getToken();
                if (string.IsNullOrWhiteSpace(token))
                    break;

                var connectedOnce = false;
                try
                {
                    connectedOnce = await RunSingleConnectionAsync(generation, token, runToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogWrapper.Debug(ex, "DreamPort", "Realtime WebSocket 连接循环异常");
                }

                SafeInvokeStatus(false);

                if (runToken.IsCancellationRequested
                    || _stopped
                    || generation != Volatile.Read(ref _generation))
                {
                    break;
                }

                var failures = connectedOnce ? 1 : Interlocked.Increment(ref _failures);
                var delayMs = Math.Min(60_000, _baseReconnectMs * (1 << Math.Min(Math.Max(0, failures), 5)));
                try
                {
                    await Task.Delay(delayMs, runToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _runCts, null, runCts);
            runCts.Dispose();
        }
    }

    private async Task<bool> RunSingleConnectionAsync(long generation, string token, CancellationToken runToken)
    {
        var uri = BuildRealtimeUri(_getBaseUrl());
        WebSocket socket;
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(runToken))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(8));
            if (_webSocketFactory is not null)
            {
                socket = await _webSocketFactory(uri, connectCts.Token).ConfigureAwait(false);
            }
            else
            {
                var client = new ClientWebSocket();
                client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                try
                {
                    await client.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                socket = client;
            }
        }

        if (runToken.IsCancellationRequested || _stopped || generation != Volatile.Read(ref _generation))
        {
            await CloseSocketQuietlyAsync(socket).ConfigureAwait(false);
            return false;
        }

        Interlocked.Exchange(ref _activeSocket, socket);
        var connCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        var connToken = connCts.Token;
        var connectedOnce = false;

        try
        {
            await SendJsonAsync(socket, new { type = "auth", kind = "client", token }, connToken).ConfigureAwait(false);
            var pingTask = RunKeepaliveLoopAsync(socket, connToken);

            var buffer = new byte[8 * 1024];
            using var msgBuffer = new MemoryStream();
            while (!connToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, connToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                msgBuffer.Write(buffer, 0, result.Count);
                if (msgBuffer.Length > 256 * 1024)
                    break;

                if (!result.EndOfMessage)
                    continue;

                var text = Encoding.UTF8.GetString(msgBuffer.GetBuffer(), 0, (int)msgBuffer.Length);
                msgBuffer.SetLength(0);

                if (HandleIncomingMessage(text))
                    connectedOnce = true;
            }

            try { connCts.Cancel(); } catch { /* ignore */ }
            try { await pingTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
        finally
        {
            try { connCts.Cancel(); } catch { /* ignore */ }
            Interlocked.CompareExchange(ref _activeSocket, null, socket);
            await CloseSocketQuietlyAsync(socket).ConfigureAwait(false);
            connCts.Dispose();
        }

        return connectedOnce;
    }

    private bool HandleIncomingMessage(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var type = doc.RootElement.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : null;

            if (string.Equals(type, "auth_success", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _failures, 0);
                SafeInvokeStatus(true);
                SafeInvokeLog("[实时] 后端双向通道已连接");
                SafeInvokeChanged("sync");
                return true;
            }

            if (string.Equals(type, "changed", StringComparison.Ordinal)
                && doc.RootElement.TryGetProperty("event", out var evEl)
                && evEl.ValueKind == JsonValueKind.String)
            {
                var ev = evEl.GetString();
                if (!string.IsNullOrWhiteSpace(ev))
                    SafeInvokeChanged(ev);
            }
        }
        catch
        {
            // 非约定消息忽略
        }

        return false;
    }

    private static async Task RunKeepaliveLoopAsync(WebSocket socket, CancellationToken connToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(connToken).ConfigureAwait(false))
            {
                if (socket.State != WebSocketState.Open)
                    break;
                await SendJsonAsync(socket, new { type = "ping" }, connToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // socket loop exits
        }
    }

    private static Task SendJsonAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task CloseSocketQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stop", cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            try { socket.Abort(); } catch { /* ignore */ }
        }
        finally
        {
            try { socket.Dispose(); } catch { /* ignore */ }
        }
    }

    private void SafeInvokeChanged(string ev)
    {
        try { _onChanged(ev); } catch { /* ignore */ }
    }

    private void SafeInvokeStatus(bool connected)
    {
        try { _onStatusChanged?.Invoke(connected); } catch { /* ignore */ }
    }

    private void SafeInvokeLog(string msg)
    {
        try { _onLog?.Invoke(msg); } catch { /* ignore */ }
    }
}
