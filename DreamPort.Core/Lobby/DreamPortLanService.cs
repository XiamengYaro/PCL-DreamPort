using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PCL.Core.Logging;

namespace DreamPort.Core.Lobby;

public sealed record LanAdvertisementCheckResult(bool Ok, int Port, string? Reason = null);

/// <summary>
/// 原版 Minecraft 局域网广播发现与访客侧伪装广播（多播 <c>224.0.2.60:4445</c>）：
/// - 房主探测：逐张非虚拟 IPv4 网卡加入组播组，收到合法本机 <c>[MOTD]...[/MOTD][AD]&lt;port&gt;[/AD]</c> 后执行本机 TCP 探活确认；
/// - 访客广播：仅向 <c>224.0.2.60:4445</c> 发送并绑定临时端口 <c>0</c>（避免占死 4445 端口）。
/// </summary>
public static partial class DreamPortLanService
{
    public const string LanMulticastAddress = "224.0.2.60";
    public const int LanMulticastPort = 4445;

    private static readonly IPAddress MulticastIp = IPAddress.Parse(LanMulticastAddress);

    public static int? ParseLocalPort(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 5)
            return null;
        foreach (var ch in trimmed)
        {
            if (ch < '0' || ch > '9')
                return null;
        }

        return int.TryParse(trimmed, out var port) && port is >= 1 and <= 65535 ? port : null;
    }

    public static string SanitizeMotd(string? name)
    {
        var cleaned = MotdTagPattern().Replace(name ?? string.Empty, string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (cleaned.Length > 48)
            cleaned = cleaned[..48];
        return string.IsNullOrWhiteSpace(cleaned) ? "梦港联机房间" : cleaned;
    }

    public static IReadOnlyList<IPAddress> ListLanIPv4Addresses()
    {
        var candidates = new List<(IPAddress Address, int Score)>();
        var seen = new HashSet<IPAddress>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                if (VirtualInterfacePattern().IsMatch(ni.Name))
                    continue;

                var props = ni.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    var addr = NormalizeIPv4(uni.Address);
                    if (addr is null || IsLoopbackOrLinkLocal(addr))
                        continue;
                    if (!seen.Add(addr))
                        continue;

                    var score = IsPrivateIPv4(addr) ? 20 : 0;
                    if (PhysicalInterfaceNamePattern().IsMatch(ni.Name))
                        score += 10;
                    if (PrimaryInterfaceNamePattern().IsMatch(ni.Name))
                        score += 5;
                    candidates.Add((addr, score));
                }
            }
        }
        catch (Exception ex)
        {
            LogWrapper.Debug(ex, "DreamPort", "枚举局域网网卡失败");
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Address.ToString(), StringComparer.Ordinal)
            .Select(c => c.Address)
            .ToList();
    }

    public static IPAddress? SelectLanIPv4() => ListLanIPv4Addresses().FirstOrDefault();

    public static bool IsLocalIPv4(IPAddress? address)
    {
        var normalized = NormalizeIPv4(address);
        if (normalized is null)
        {
            return address is not null && IPAddress.IsLoopback(address);
        }

        if (IPAddress.IsLoopback(normalized))
            return true;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var uni in ni.GetIPProperties().UnicastAddresses)
                {
                    var local = NormalizeIPv4(uni.Address);
                    if (local is not null && local.Equals(normalized))
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static LanAdvertisementCheckResult CheckLanAdvertisement(
        string? payload,
        IPAddress? sourceAddress,
        Func<IPAddress, bool>? localChecker = null)
    {
        if (sourceAddress is null)
            return new LanAdvertisementCheckResult(false, 0, "来源地址为空");

        var isLocal = localChecker ?? IsLocalIPv4;
        if (!isLocal(sourceAddress))
            return new LanAdvertisementCheckResult(false, 0, $"来源不是本机地址：{sourceAddress}");

        var text = (payload ?? string.Empty).TrimEnd('\0').Trim();
        var match = LanAdvertisementPattern().Match(text);
        if (!match.Success)
            return new LanAdvertisementCheckResult(false, 0, "缺少合法的 [MOTD]/[AD] 广播结构");

        if (!int.TryParse(match.Groups[1].Value, out var port) || port < 1 || port > 65535)
            return new LanAdvertisementCheckResult(false, 0, $"端口越界：{match.Groups[1].Value}");

        return new LanAdvertisementCheckResult(true, port);
    }

    public static async Task<bool> PingLocalPortAsync(
        int port,
        int timeoutMs = 1500,
        CancellationToken ct = default)
    {
        if (port < 1 || port > 65535)
            return false;

        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(Math.Max(200, timeoutMs));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(int? Port, string? ErrorMessage)> DetectLanPortAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        string? lastError = null;
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanMulticastPort));
        }
        catch (Exception ex)
        {
            return (null, $"无法监听组播端口 {LanMulticastPort}（{ex.Message}），请直接填写本地游戏端口");
        }

        var joined = JoinMulticastOnAllInterfaces(udp.Client);
        if (joined == 0)
            lastError = "未能加入本地组播组（请检查系统本地网络权限或直接填写本地游戏端口）";

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);

        var testedPorts = new HashSet<int>();
        while (!deadlineCts.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(deadlineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError ??= $"监听局域网广播异常：{ex.Message}";
                break;
            }

            var text = Encoding.UTF8.GetString(packet.Buffer);
            var check = CheckLanAdvertisement(text, packet.RemoteEndPoint.Address);
            if (!check.Ok)
            {
                if (text.Contains("[AD]", StringComparison.Ordinal))
                    lastError = $"忽略广播包（{check.Reason}）";
                continue;
            }

            if (testedPorts.Count >= 8 || !testedPorts.Add(check.Port))
                continue;

            var alive = await PingLocalPortAsync(check.Port, 900, deadlineCts.Token).ConfigureAwait(false);
            if (alive)
                return (check.Port, null);

            lastError = $"收到广播端口 {check.Port} 但本机连接探活未通";
        }

        return ct.IsCancellationRequested
            ? (null, "已取消局域网端口检测")
            : (null, lastError ?? "未在局域网广播中检测到开放的世界，请先在游戏内点击「对局域网开放」或手动填写端口");
    }

    private static int JoinMulticastOnAllInterfaces(Socket socket)
    {
        var added = 0;
        var seen = new HashSet<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var uni in ni.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    var addr = NormalizeIPv4(uni.Address);
                    if (addr is null || !seen.Add(addr))
                        continue;
                    try
                    {
                        socket.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.AddMembership,
                            new MulticastOption(MulticastIp, addr));
                        added++;
                    }
                    catch
                    {
                        // 某些虚拟/未就绪网卡不支持组播，忽略单卡继续枚举
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return added;
    }

    internal static IPAddress? NormalizeIPv4(IPAddress? addr)
    {
        if (addr is null)
            return null;
        if (addr.AddressFamily == AddressFamily.InterNetwork)
            return addr;
        if (addr.IsIPv4MappedToIPv6)
            return addr.MapToIPv4();
        return null;
    }

    private static bool IsLoopbackOrLinkLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsPrivateIPv4(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
            return false;
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    [GeneratedRegex(@"^(?:utun|tun\d*|tap\d*|awdl|llw|lo\d*|bridge|veth|docker|virbr|vmnet|vboxnet|tailscale|wg\d*)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VirtualInterfacePattern();

    [GeneratedRegex(@"^(?:en\d+|eth\d+|wlan\d+|wi-?fi|ethernet|无线|以太网)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PhysicalInterfaceNamePattern();

    [GeneratedRegex(@"^(?:en0|eth0|wlan0)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PrimaryInterfaceNamePattern();

    [GeneratedRegex(@"\[/?(?:MOTD|AD)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MotdTagPattern();

    [GeneratedRegex(@"^\[MOTD\][\s\S]*?\[/MOTD\]\[AD\](\d{1,5})\[/AD\]$", RegexOptions.Compiled)]
    private static partial Regex LanAdvertisementPattern();
}

/// <summary>
/// 访客侧周期性局域网组播广播器：向 <c>224.0.2.60:4445</c> 发送 <c>[MOTD]房间名[/MOTD][AD]端口[/AD]</c>，
/// 只绑定临时端口 <c>0</c> 以避免与本机其它联机程序冲突。
/// </summary>
public sealed class DreamPortLanAdvertiser : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _stopToken;
    private readonly UdpClient? _udp;
    private readonly byte[] _payload;
    private readonly IPEndPoint _target = new(IPAddress.Parse(DreamPortLanService.LanMulticastAddress), DreamPortLanService.LanMulticastPort);
    private readonly Action<string>? _onWarning;
    private bool _reportedError;
    private bool _disposed;

    public DreamPortLanAdvertiser(
        string motd,
        int localPort,
        Action<string>? onWarning = null,
        IPAddress? overrideInterface = null)
    {
        _stopToken = _cts.Token;
        _onWarning = onWarning;
        _payload = Encoding.UTF8.GetBytes($"[MOTD]{DreamPortLanService.SanitizeMotd(motd)}[/MOTD][AD]{localPort}[/AD]");

        var bindAddr = overrideInterface is not null && DreamPortLanService.IsLocalIPv4(overrideInterface)
            ? DreamPortLanService.NormalizeIPv4(overrideInterface)
            : DreamPortLanService.SelectLanIPv4();

        if (bindAddr is null)
        {
            BoundAddress = null;
            _onWarning?.Invoke($"未找到可用的物理局域网网卡，房间暂不会出现在游戏「多人游戏」局域网列表中；请直接使用 127.0.0.1:{localPort} 添加或直连。");
            return;
        }

        BoundAddress = bindAddr;
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(bindAddr, 0));
            try
            {
                _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, bindAddr.GetAddressBytes());
            }
            catch
            {
                // 部分平台选项受限时继续使用默认接口发送
            }

            _ = RunLoopAsync();
        }
        catch (Exception ex)
        {
            BoundAddress = null;
            _udp?.Dispose();
            _udp = null;
            _onWarning?.Invoke($"无法启动局域网伪装广播（{ex.Message}），可直接使用 127.0.0.1:{localPort} 直连。");
        }
    }

    public IPAddress? BoundAddress { get; }

    public bool IsRunning => !_disposed && _udp is not null;

    private async Task RunLoopAsync()
    {
        if (_udp is null)
            return;

        await SendOnceAsync().ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopToken).ConfigureAwait(false))
            {
                await SendOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task SendOnceAsync()
    {
        if (_disposed || _udp is null)
            return;

        try
        {
            await _udp.SendAsync(_payload, _payload.Length, _target).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_reportedError)
            {
                _reportedError = true;
                _onWarning?.Invoke($"局域网广播发送受阻（{ex.Message}），可直接用 127.0.0.1 连接本地转发端口。");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* ignore */ }
        try
        {
            _udp?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
