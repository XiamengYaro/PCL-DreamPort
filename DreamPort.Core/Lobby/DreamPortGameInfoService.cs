using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DreamPort.Core.Lobby;

namespace DreamPort.Core.Lobby;

public sealed record DreamPortGameInfo(
    string Version,
    IReadOnlyList<string> Mods,
    string ModHash,
    string Loader,
    string Source);

public sealed record DreamPortRoomCompatibility(
    int MatchScore,
    bool IsHardBlocked,
    bool HasSoftWarning,
    string BadgeText,
    string? Reason,
    IReadOnlyList<string> MissingMods);

/// <summary>
/// 联机游戏信息探测（本地 SLP 握手 → <c>mods/*.jar</c> 元数据 → 启动器实例信息回落）、
/// 模组 FNV-1a 指纹计算与房间兼容性评估（版本排序 + Forge/NeoForge 硬拦截）。
/// </summary>
public static partial class DreamPortGameInfoService
{
    public static string ComputeModHash(IEnumerable<string>? mods)
    {
        if (mods is null)
            return string.Empty;

        var normalized = mods
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .ToArray();
        if (normalized.Length == 0)
            return string.Empty;

        Array.Sort(normalized, StringComparer.Ordinal);
        uint hash = 0x811c9dc5u;
        const uint prime = 0x01000193u;

        foreach (var item in normalized)
        {
            foreach (var ch in item)
            {
                hash ^= ch;
                hash *= prime;
            }

            hash ^= 0x0au;
            hash *= prime;
        }

        return hash.ToString("x8");
    }

    public static (IReadOnlyList<string> Mods, string Loader) ReadLocalMods(string? instanceDir)
    {
        if (string.IsNullOrWhiteSpace(instanceDir))
            return ([], "unknown");

        var modsDir = Path.Combine(instanceDir, "mods");
        if (!Directory.Exists(modsDir))
            return ([], "vanilla");

        var mods = new List<string>();
        var loader = "vanilla";
        try
        {
            foreach (var jarPath in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(jarPath);
                if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('.'))
                    continue;

                var meta = TryReadJarModMeta(jarPath);
                if (meta is not null)
                {
                    mods.Add(meta.Value.ModId);
                    if (!string.Equals(meta.Value.Loader, "unknown", StringComparison.Ordinal))
                        loader = meta.Value.Loader;
                }
                else
                {
                    mods.Add(Path.GetFileNameWithoutExtension(jarPath));
                }
            }
        }
        catch
        {
            // 目录读取失败按空集合处理
        }

        if (mods.Count > 0 && string.Equals(loader, "vanilla", StringComparison.Ordinal))
            loader = "unknown";

        return (mods, loader);
    }

    public static async Task<DreamPortGameInfo?> QueryLocalSlpAsync(
        int port,
        int timeoutMs = 2500,
        CancellationToken ct = default)
    {
        if (port is < 1 or > 65535)
            return null;

        using var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Max(300, timeoutMs));
        var token = cts.Token;

        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            using var stream = tcp.GetStream();

            var packetBytes = BuildSlpQueryPacket((ushort)port);
            await stream.WriteAsync(packetBytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);

            var packetLen = await ReadVarIntFromStreamAsync(stream, token).ConfigureAwait(false);
            if (packetLen <= 0 || packetLen > 512 * 1024)
                return null;

            var payload = new byte[packetLen];
            await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            return ParseSlpResponsePayload(payload);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<DreamPortGameInfo> InspectGameInfoAsync(
        int localGamePort,
        string? instanceDir,
        string? fallbackVersion,
        string? fallbackLoader,
        CancellationToken ct = default)
    {
        var slp = localGamePort is >= 1 and <= 65535
            ? await QueryLocalSlpAsync(localGamePort, 2000, ct).ConfigureAwait(false)
            : null;

        var (localMods, localLoader) = ReadLocalMods(instanceDir);
        var cleanFallbackVer = (fallbackVersion ?? string.Empty).Trim();
        var cleanFallbackLoader = NormalizeLoader(fallbackLoader);

        if (slp is not null)
        {
            var version = !string.IsNullOrWhiteSpace(slp.Version) ? slp.Version : cleanFallbackVer;
            var mods = slp.Mods.Count > 0 ? slp.Mods : localMods;
            var loader = slp.Loader != "vanilla" && slp.Loader != "unknown"
                ? slp.Loader
                : (localLoader is not ("vanilla" or "unknown") ? localLoader : cleanFallbackLoader);
            return new DreamPortGameInfo(
                Version: version,
                Mods: mods,
                ModHash: ComputeModHash(mods),
                Loader: string.IsNullOrWhiteSpace(loader) ? "vanilla" : loader,
                Source: "slp");
        }

        if (localMods.Count > 0 || !string.Equals(localLoader, "vanilla", StringComparison.Ordinal))
        {
            var loader = localLoader is not ("vanilla" or "unknown") ? localLoader : cleanFallbackLoader;
            return new DreamPortGameInfo(
                Version: cleanFallbackVer,
                Mods: localMods,
                ModHash: ComputeModHash(localMods),
                Loader: string.IsNullOrWhiteSpace(loader) ? "vanilla" : loader,
                Source: "local-mods");
        }

        return new DreamPortGameInfo(
            Version: cleanFallbackVer,
            Mods: [],
            ModHash: string.Empty,
            Loader: string.IsNullOrWhiteSpace(cleanFallbackLoader) ? "vanilla" : cleanFallbackLoader,
            Source: "instance");
    }

    public static DreamPortRoomCompatibility EvaluateRoomCompatibility(
        DreamPortLobbyRoom room,
        string? myVersion,
        string? myLoader,
        string? myModHash = null,
        IReadOnlyList<string>? myMods = null)
    {
        var roomVer = (room.McVersion ?? string.Empty).Trim();
        var cleanMyVer = (myVersion ?? string.Empty).Trim();
        var roomLoader = NormalizeLoader(room.ModLoader);
        var cleanMyLoader = NormalizeLoader(myLoader);
        var isHardLoader = roomLoader is "forge" or "neoforge";

        var myModSet = myMods is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : myMods.Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectiveMyHash = !string.IsNullOrWhiteSpace(myModHash)
            ? myModHash.Trim()
            : ComputeModHash(myModSet);

        if (isHardLoader)
        {
            if (!string.Equals(cleanMyLoader, roomLoader, StringComparison.OrdinalIgnoreCase))
            {
                var actualDesc = string.IsNullOrWhiteSpace(cleanMyLoader) || cleanMyLoader == "vanilla"
                    ? "原版/未检测到对应加载器"
                    : cleanMyLoader;
                return new DreamPortRoomCompatibility(
                    MatchScore: 0,
                    IsHardBlocked: true,
                    HasSoftWarning: false,
                    BadgeText: $"不兼容（需 {roomLoader}）",
                    Reason: $"房主使用 {roomLoader} 加载器，你当前实例为 {actualDesc}，无法直接联机，请先切换为 {roomLoader} 实例。",
                    MissingMods: []);
            }

            if (room.ModList.Count > 0)
            {
                var missing = room.ModList
                    .Where(m => !string.IsNullOrWhiteSpace(m) && !myModSet.Contains(m.Trim()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var hashMismatch = !string.IsNullOrWhiteSpace(room.ModHash)
                    && !string.IsNullOrWhiteSpace(effectiveMyHash)
                    && !string.Equals(room.ModHash, effectiveMyHash, StringComparison.OrdinalIgnoreCase);

                if (missing.Length > 0 || hashMismatch)
                {
                    var missingPreview = missing.Length > 0
                        ? $"，缺少 {missing.Length} 个模组：{string.Join("、", missing.Take(5))}{(missing.Length > 5 ? " 等" : string.Empty)}"
                        : "（模组数量或顺序不一致）";
                    return new DreamPortRoomCompatibility(
                        MatchScore: 0,
                        IsHardBlocked: true,
                        HasSoftWarning: false,
                        BadgeText: missing.Length > 0 ? $"不兼容（缺 {missing.Length} 模组）" : "不兼容（模组不一致）",
                        Reason: $"房主 {roomLoader} 房间要求模组完全一致{missingPreview}。",
                        MissingMods: missing);
                }
            }
            else if (!string.IsNullOrWhiteSpace(room.ModHash)
                && !string.Equals(room.ModHash, effectiveMyHash, StringComparison.OrdinalIgnoreCase))
            {
                return new DreamPortRoomCompatibility(
                    MatchScore: 0,
                    IsHardBlocked: true,
                    HasSoftWarning: false,
                    BadgeText: "不兼容（模组指纹不一致）",
                    Reason: $"房主 {roomLoader} 房间模组清单指纹（{room.ModHash}）与当前实例不一致，请核对已安装模组后重试。",
                    MissingMods: []);
            }
        }

        if (!string.IsNullOrEmpty(roomVer)
            && !string.IsNullOrEmpty(cleanMyVer)
            && !string.Equals(roomVer, cleanMyVer, StringComparison.OrdinalIgnoreCase))
        {
            return new DreamPortRoomCompatibility(
                MatchScore: 0,
                IsHardBlocked: false,
                HasSoftWarning: true,
                BadgeText: $"版本不一致（{roomVer}）",
                Reason: $"房间版本为 {roomVer}，你当前实例版本为 {cleanMyVer}。",
                MissingMods: []);
        }

        if (string.IsNullOrEmpty(roomVer))
        {
            return new DreamPortRoomCompatibility(
                MatchScore: 1,
                IsHardBlocked: false,
                HasSoftWarning: false,
                BadgeText: "未标注版本",
                Reason: null,
                MissingMods: []);
        }

        if (string.IsNullOrEmpty(cleanMyVer))
        {
            return new DreamPortRoomCompatibility(
                MatchScore: 1,
                IsHardBlocked: false,
                HasSoftWarning: false,
                BadgeText: $"{roomVer}（本机未选实例）",
                Reason: null,
                MissingMods: []);
        }

        var loaderMatches = string.IsNullOrEmpty(roomLoader)
            || roomLoader == "vanilla"
            || string.Equals(roomLoader, cleanMyLoader, StringComparison.OrdinalIgnoreCase);

        if (!loaderMatches)
        {
            return new DreamPortRoomCompatibility(
                MatchScore: 2,
                IsHardBlocked: false,
                HasSoftWarning: true,
                BadgeText: "版本匹配（加载器有差异）",
                Reason: $"房间加载器为 {roomLoader}，当前实例为 {cleanMyLoader}。",
                MissingMods: []);
        }

        if (!string.IsNullOrWhiteSpace(room.ModHash)
            && !string.Equals(room.ModHash, effectiveMyHash, StringComparison.OrdinalIgnoreCase))
        {
            var missing = room.ModList
                .Where(m => !string.IsNullOrWhiteSpace(m) && !myModSet.Contains(m.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new DreamPortRoomCompatibility(
                MatchScore: 2,
                IsHardBlocked: false,
                HasSoftWarning: true,
                BadgeText: missing.Length > 0 ? $"版本匹配（缺 {missing.Length} 模组）" : "版本匹配（模组有差异）",
                Reason: missing.Length > 0 ? $"检测到与房间模组有差异：{string.Join("、", missing.Take(5))}" : "检测到模组清单与房主不完全一致。",
                MissingMods: missing);
        }

        return new DreamPortRoomCompatibility(
            MatchScore: 3,
            IsHardBlocked: false,
            HasSoftWarning: false,
            BadgeText: "完全兼容",
            Reason: null,
            MissingMods: []);
    }

    internal static DreamPortGameInfo? ParseSlpResponsePayload(byte[] payload)
    {
        var off = 0;
        if (!TryReadVarInt(payload, ref off, out var packetId) || packetId != 0x00)
            return null;
        if (!TryReadVarInt(payload, ref off, out var jsonLen) || jsonLen <= 0 || off + jsonLen > payload.Length)
            return null;

        var json = Encoding.UTF8.GetString(payload, off, jsonLen);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var version = string.Empty;
        if (root.TryGetProperty("version", out var verEl)
            && verEl.ValueKind == JsonValueKind.Object
            && verEl.TryGetProperty("name", out var verName)
            && verName.ValueKind == JsonValueKind.String)
        {
            version = (verName.GetString() ?? string.Empty).Trim();
        }

        var mods = new List<string>();
        var loader = "vanilla";

        if (root.TryGetProperty("forgeData", out var forgeEl) && forgeEl.ValueKind == JsonValueKind.Object)
        {
            loader = "forge";
            if (forgeEl.TryGetProperty("mods", out var modsEl) && modsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var modId = TryGetString(item, "modId") ?? TryGetString(item, "modmarker");
                    if (!string.IsNullOrWhiteSpace(modId))
                        mods.Add(modId.Trim());
                }
            }
        }

        if (root.TryGetProperty("isModded", out var moddedEl)
            && moddedEl.ValueKind == JsonValueKind.True
            && loader == "vanilla")
        {
            loader = "neoforge";
        }

        if (root.TryGetProperty("modinfo", out var fabricEl) && fabricEl.ValueKind == JsonValueKind.Object)
        {
            loader = "fabric";
            if (fabricEl.TryGetProperty("modList", out var listEl) && listEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in listEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var modId = TryGetString(item, "modid") ?? TryGetString(item, "name");
                    if (!string.IsNullOrWhiteSpace(modId))
                        mods.Add(modId.Trim());
                }
            }
        }

        if (mods.Count > 0 && loader == "vanilla")
            loader = "forge";

        return new DreamPortGameInfo(
            Version: version,
            Mods: mods,
            ModHash: ComputeModHash(mods),
            Loader: loader,
            Source: "slp");
    }

    private static (string ModId, string Loader)? TryReadJarModMeta(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var fabricEntry = archive.GetEntry("fabric.mod.json");
            if (fabricEntry is not null)
            {
                using var reader = new StreamReader(fabricEntry.Open(), Encoding.UTF8);
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                var id = TryGetString(doc.RootElement, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    return (id.Trim(), "fabric");
            }

            var neoEntry = archive.GetEntry("META-INF/neoforge.mods.toml");
            if (neoEntry is not null)
            {
                using var reader = new StreamReader(neoEntry.Open(), Encoding.UTF8);
                var match = TomlModIdPattern().Match(reader.ReadToEnd());
                if (match.Success)
                    return (match.Groups[1].Value.Trim(), "neoforge");
            }

            var forgeEntry = archive.GetEntry("META-INF/mods.toml");
            if (forgeEntry is not null)
            {
                using var reader = new StreamReader(forgeEntry.Open(), Encoding.UTF8);
                var text = reader.ReadToEnd();
                var match = TomlModIdPattern().Match(text);
                if (match.Success)
                {
                    var loader = text.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "neoforge" : "forge";
                    return (match.Groups[1].Value.Trim(), loader);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeLoader(string? loader)
    {
        var raw = (loader ?? string.Empty).Trim().ToLowerInvariant();
        return raw is "原版" or "" ? "vanilla" : raw;
    }

    private static string? TryGetString(JsonElement obj, string propName)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(propName, out var el)
            && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static byte[] BuildSlpQueryPacket(ushort port)
    {
        using var hsBody = new MemoryStream();
        hsBody.WriteByte(0x00); // Handshake packet id
        WriteVarInt(hsBody, 0); // Protocol version = 0 for status query
        var hostBytes = Encoding.UTF8.GetBytes("127.0.0.1");
        WriteVarInt(hsBody, hostBytes.Length);
        hsBody.Write(hostBytes);
        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, port);
        hsBody.Write(portBuf);
        WriteVarInt(hsBody, 1); // Next state: status

        using var full = new MemoryStream();
        var bodyBytes = hsBody.ToArray();
        WriteVarInt(full, bodyBytes.Length);
        full.Write(bodyBytes);
        // Status request packet: len=1, id=0x00
        full.WriteByte(0x01);
        full.WriteByte(0x00);
        return full.ToArray();
    }

    private static void WriteVarInt(Stream dst, int value)
    {
        var u = unchecked((uint)value);
        while ((u & ~0x7Fu) != 0)
        {
            dst.WriteByte((byte)((u & 0x7Fu) | 0x80u));
            u >>= 7;
        }

        dst.WriteByte((byte)u);
    }

    private static async Task<int> ReadVarIntFromStreamAsync(Stream stream, CancellationToken ct)
    {
        var value = 0;
        var position = 0;
        var single = new byte[1];
        while (position < 5)
        {
            var read = await stream.ReadAsync(single, ct).ConfigureAwait(false);
            if (read == 0)
                return -1;
            var current = single[0];
            value |= (current & 0x7F) << (7 * position);
            if ((current & 0x80) == 0)
                return value;
            position++;
        }

        return -1;
    }

    private static bool TryReadVarInt(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;
        var position = 0;
        while (offset < data.Length && position < 5)
        {
            var current = data[offset++];
            value |= (current & 0x7F) << (7 * position);
            if ((current & 0x80) == 0)
                return true;
            position++;
        }

        return false;
    }

    [GeneratedRegex(@"modId\s*=\s*""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex TomlModIdPattern();
}
