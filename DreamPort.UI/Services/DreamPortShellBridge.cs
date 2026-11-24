namespace DreamPort.UI.Services;

/// <summary>
/// 壳层全局状态桥：将后台自动拉取、账号页与公告通知页中的未读站内通知数实时同步至标题栏角标。
/// </summary>
public static class DreamPortShellBridge
{
    private static int _unreadNotificationCount;

    public static int UnreadNotificationCount => Volatile.Read(ref _unreadNotificationCount);

    public static event Action<int>? UnreadCountChanged;

    public static void PublishUnreadCount(int count)
    {
        var clamped = Math.Max(0, count);
        Interlocked.Exchange(ref _unreadNotificationCount, clamped);
        UnreadCountChanged?.Invoke(clamped);
    }

    public static void ResetUnreadCount() => PublishUnreadCount(0);
}
