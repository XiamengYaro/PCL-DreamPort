using System.Runtime.InteropServices;
using DreamPort.Core.Api;
using DreamPort.Core.Config;
using PCL.Core.Logging;

namespace DreamPort.Core.Services;

/// <summary>
/// 极简客户端在线心跳服务：仅上报客户端版本、平台与启动时间（绝不上传模组/实例/设置快照，也绝不消费服务端下发的 defaults/actions/assist）。
/// </summary>
public sealed class DreamPortHeartbeatService : IAsyncDisposable
{
    public const int DefaultIntervalSec = 60;
    private const int MaxBackoffMs = 10 * 60 * 1000;

    private readonly Func<DreamPortApiClient?> _getClient;
    private readonly bool _ownsClientInstances;
    private readonly Func<bool> _isLoggedIn;
    private readonly Func<string> _getClientVersion;
    private readonly Func<string> _getPlatform;
    private readonly int _baseBackoffMs;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationToken _disposeToken;
    private readonly Lock _lock = new();

    private CancellationTokenSource? _runCts;
    private Task? _loopTask;
    private long _generation;
    private bool _stopped = true;
    private int _disposed;

    public DreamPortHeartbeatService(
        Func<DreamPortApiClient?> getClient,
        Func<bool> isLoggedIn,
        bool ownsClientInstances = false,
        int baseBackoffMs = 30_000,
        Func<string>? getClientVersion = null,
        Func<string>? getPlatform = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _getClient = getClient ?? throw new ArgumentNullException(nameof(getClient));
        _isLoggedIn = isLoggedIn ?? throw new ArgumentNullException(nameof(isLoggedIn));
        _ownsClientInstances = ownsClientInstances;
        _baseBackoffMs = Math.Clamp(baseBackoffMs, 1, MaxBackoffMs);
        _getClientVersion = getClientVersion ?? (() => DreamPortBuildConfig.AppVersion);
        _getPlatform = getPlatform ?? ResolvePlatformTag;
        _delayAsync = delayAsync ?? Task.Delay;
        _disposeToken = _disposeCts.Token;
        ClientStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public long ClientStartedAtMs { get; }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return !_stopped && _disposed == 0;
            }
        }
    }

    public int CurrentIntervalSec { get; private set; } = DefaultIntervalSec;

    public int ConsecutiveFailures { get; private set; }

    public void Start(string reason = "startup")
    {
        lock (_lock)
        {
            if (_disposed != 0 || !_stopped || !_isLoggedIn())
                return;

            _stopped = false;
            var gen = ++_generation;
            var runCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeToken);
            var runToken = runCts.Token;
            var prev = _runCts;
            _runCts = runCts;
            if (prev is not null)
            {
                try { prev.Cancel(); } catch { /* ignore */ }
            }

            LogWrapper.Debug("DreamPort", $"启动极简在线心跳（{reason}），默认间隔 {CurrentIntervalSec}s");
            _loopTask = RunLoopAsync(gen, runCts, runToken);
        }
    }

    public async Task StopAndNotifyOfflineAsync(TimeSpan? timeout = null)
    {
        CancellationTokenSource? runCts;
        Task? loopTask;
        bool wasLoggedIn;
        lock (_lock)
        {
            wasLoggedIn = _isLoggedIn();
            _stopped = true;
            _generation++;
            runCts = _runCts;
            _runCts = null;
            loopTask = _loopTask;
            _loopTask = null;
        }

        if (runCts is not null)
        {
            try { runCts.Cancel(); } catch { /* ignore */ }
        }

        if (wasLoggedIn)
        {
            using var offlineCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(1.5));
            DreamPortApiClient? client = null;
            try
            {
                client = _getClient();
                if (client is not null && !string.IsNullOrEmpty(client.CurrentToken))
                    await client.SendOfflineAsync(offlineCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 退出下线通知尽力而为，不阻断关闭流程
            }
            finally
            {
                if (_ownsClientInstances)
                    client?.Dispose();
            }
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // ignore wait timeout
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CancellationTokenSource? runCts;
        lock (_lock)
        {
            _stopped = true;
            _generation++;
            runCts = _runCts;
            _runCts = null;
        }

        if (runCts is not null)
        {
            try { runCts.Cancel(); } catch { /* ignore */ }
        }

        try { _disposeCts.Cancel(); } catch { /* ignore */ }
        await ValueTask.CompletedTask;
    }

    private async Task RunLoopAsync(long gen, CancellationTokenSource runCts, CancellationToken runToken)
    {
        try
        {
            while (!runToken.IsCancellationRequested)
            {
                lock (_lock)
                {
                    if (_stopped || _disposed != 0 || gen != _generation)
                        break;
                }

                if (!_isLoggedIn())
                    break;

                DreamPortApiClient? client = null;
                DreamPortHeartbeatResult res;
                try
                {
                    client = _getClient();
                    if (client is null || string.IsNullOrEmpty(client.CurrentToken))
                        break;

                    res = await client.SendMinimalHeartbeatAsync(
                        _getClientVersion(),
                        _getPlatform(),
                        ClientStartedAtMs,
                        runToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    res = new DreamPortHeartbeatResult(false, DefaultIntervalSec, DreamPortFailureReason.Network, ex.Message);
                }
                finally
                {
                    if (_ownsClientInstances)
                        client?.Dispose();
                }

                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    lock (_lock)
                    {
                        if (gen == _generation)
                            _stopped = true;
                    }
                    break;
                }

                TimeSpan waitDelay;
                if (res.Ok)
                {
                    ConsecutiveFailures = 0;
                    CurrentIntervalSec = Math.Clamp(res.NextIntervalSec, 15, 600);
                    waitDelay = TimeSpan.FromSeconds(CurrentIntervalSec);
                }
                else
                {
                    ConsecutiveFailures++;
                    var exp = Math.Min(ConsecutiveFailures - 1, 5);
                    var backoffMs = Math.Min(MaxBackoffMs, _baseBackoffMs * (1 << exp));
                    waitDelay = TimeSpan.FromMilliseconds(backoffMs);
                }

                try
                {
                    await _delayAsync(waitDelay, runToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_runCts, runCts))
                    _runCts = null;
            }
            runCts.Dispose();
        }
    }

    private static string ResolvePlatformTag()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows";
        return "linux";
    }
}
