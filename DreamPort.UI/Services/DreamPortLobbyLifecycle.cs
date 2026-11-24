using PCL.Core.Logging;

namespace DreamPort.UI.Services;

/// <summary>
/// 进程级梦港联机退出清理协调器：应用关闭前触发活跃房主关房与隧道回收，防止残留开启状态房间。
/// </summary>
public static class DreamPortLobbyLifecycle
{
    private static readonly Lock LockObj = new();
    private static Func<Task>? _shutdownHandler;

    public static void RegisterActiveShutdown(Func<Task> shutdownHandler)
    {
        lock (LockObj)
            _shutdownHandler = shutdownHandler;
    }

    public static void ClearActiveShutdown(Func<Task> shutdownHandler)
    {
        lock (LockObj)
        {
            if (ReferenceEquals(_shutdownHandler, shutdownHandler))
                _shutdownHandler = null;
        }
    }

    public static async Task ShutdownActiveLobbyOnExitAsync(TimeSpan timeout)
    {
        Func<Task>? handler;
        lock (LockObj)
            handler = _shutdownHandler;

        if (handler is null)
            return;

        try
        {
            var workTask = handler();
            await workTask.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogWrapper.Warn("DreamPort", "应用退出时清理活跃联机房间超时，已继续退出流程");
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "DreamPort", "应用退出时清理联机状态跳过");
        }
    }
}
