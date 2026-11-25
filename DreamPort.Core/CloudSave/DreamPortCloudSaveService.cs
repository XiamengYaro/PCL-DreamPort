using System.IO.Compression;
using System.Security.Cryptography;
using DreamPort.Core.Api;

namespace DreamPort.Core.CloudSave;

public sealed record DreamPortLocalWorldInfo(
    string WorldName,
    string FullPath,
    long SizeBytes,
    DateTimeOffset LastWriteUtc);

public sealed record DreamPortCloudSaveUploadResult(
    bool Ok,
    long BackupId,
    bool Duplicate,
    string Message);

public sealed record DreamPortCloudSaveRestoreResult(
    bool Ok,
    string? RestoredWorldName,
    string? TargetDirectory,
    string Message);

/// <summary>
/// DreamPort 玩家存档云备份核心服务：流式 ZIP 打包（跳过 session.lock）、SHA-256 计算、分块断点续传上传，以及防覆盖 / 防 Zip-Slip 安全恢复。
/// </summary>
public static class DreamPortCloudSaveService
{
    public static IReadOnlyList<DreamPortLocalWorldInfo> ListLocalWorlds(string? instanceDir)
    {
        if (string.IsNullOrWhiteSpace(instanceDir))
            return [];

        var savesDir = Path.Combine(instanceDir, "saves");
        if (!Directory.Exists(savesDir))
            return [];

        var result = new List<DreamPortLocalWorldInfo>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(savesDir))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.'))
                    continue;

                var info = new DirectoryInfo(dir);
                long bytes = 0;
                var latest = info.LastWriteTimeUtc;
                try
                {
                    foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        if (string.Equals(file.Name, "session.lock", StringComparison.OrdinalIgnoreCase))
                            continue;
                        bytes += file.Length;
                        if (file.LastWriteTimeUtc > latest)
                            latest = file.LastWriteTimeUtc;
                    }
                }
                catch
                {
                    // 忽略部分不可访问子文件
                }

                result.Add(new DreamPortLocalWorldInfo(name, dir, bytes, new DateTimeOffset(latest, TimeSpan.Zero)));
            }
        }
        catch
        {
            return [];
        }

        return result.OrderByDescending(w => w.LastWriteUtc).ToList();
    }

    public static async Task<(long SizeBytes, string Sha256)> CreateWorldZipAsync(
        string worldDir,
        string zipPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(worldDir) || !Directory.Exists(worldDir))
            throw new DirectoryNotFoundException($"本地存档目录不存在：{worldDir}");

        var zipParent = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(zipParent))
            Directory.CreateDirectory(zipParent);

        var worldFolderName = SanitizeFolderName(Path.GetFileName(worldDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "world");
        var rootFull = Path.GetFullPath(worldDir);
        var fileCount = 0;

        await Task.Run(() =>
        {
            using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFileName(file), "session.lock", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var rel = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
                    var entryName = $"{worldFolderName}/{rel}";
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    srcStream.CopyTo(entryStream);
                    fileCount++;
                }
            }

            if (fileCount == 0)
                throw new InvalidOperationException("该存档目录内没有可备份的有效文件。");
        }, ct).ConfigureAwait(false);

        var sizeBytes = new FileInfo(zipPath).Length;
        string sha256;
        using (var sha = SHA256.Create())
        await using (var read = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            sha256 = Convert.ToHexStringLower(await sha.ComputeHashAsync(read, ct).ConfigureAwait(false));
        }

        return (sizeBytes, sha256);
    }

    public static async Task<DreamPortCloudSaveUploadResult> UploadWorldAsync(
        DreamPortApiClient api,
        string instanceDir,
        string instanceName,
        string worldName,
        string? gameVersion = null,
        string? displayName = null,
        string? note = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (string.IsNullOrWhiteSpace(instanceDir))
            return new DreamPortCloudSaveUploadResult(false, 0, false, "请先选择游戏实例");
        if (string.IsNullOrWhiteSpace(worldName))
            return new DreamPortCloudSaveUploadResult(false, 0, false, "请选择要备份的本地存档");

        var worldDir = Path.Combine(instanceDir, "saves", worldName);
        if (!Directory.Exists(worldDir))
            return new DreamPortCloudSaveUploadResult(false, 0, false, $"本地存档目录不存在：{worldName}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"dreamport-cloud-save-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempDir, "world.zip");
        try
        {
            Directory.CreateDirectory(tempDir);
            onProgress?.Invoke($"正在打包存档「{worldName}」...");
            var (sizeBytes, sha256) = await CreateWorldZipAsync(worldDir, zipPath, ct).ConfigureAwait(false);

            onProgress?.Invoke("正在向 DreamPort 申请云备份任务...");
            var begin = await api.BeginCloudSaveUploadAsync(
                instanceName: string.IsNullOrWhiteSpace(instanceName) ? Path.GetFileName(instanceDir) : instanceName.Trim(),
                worldName: worldName.Trim(),
                displayName: string.IsNullOrWhiteSpace(displayName) ? worldName.Trim() : displayName.Trim(),
                gameVersion: gameVersion?.Trim() ?? string.Empty,
                sizeBytes: sizeBytes,
                sha256: sha256,
                note: note,
                ct: ct).ConfigureAwait(false);

            if (!begin.Ok || begin.Id <= 0)
                return new DreamPortCloudSaveUploadResult(false, begin.Id, false, begin.Message ?? "创建云备份任务失败");

            if (begin.Duplicate && string.Equals(begin.Status, "ready", StringComparison.OrdinalIgnoreCase))
                return new DreamPortCloudSaveUploadResult(true, begin.Id, true, "云端已有完全相同的存档备份（秒传完成）");

            var chunkSize = begin.ChunkSize > 0 ? begin.ChunkSize : 16 * 1024 * 1024;
            var totalChunks = begin.ChunksTotal > 0
                ? begin.ChunksTotal
                : (int)((sizeBytes + chunkSize - 1) / chunkSize);
            var uploadedSet = new HashSet<int>(begin.UploadedChunks);

            var buffer = new byte[chunkSize];
            await using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                for (var index = 0; index < totalChunks; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (uploadedSet.Contains(index))
                        continue;

                    var offset = (long)index * chunkSize;
                    var expected = (int)Math.Min(chunkSize, sizeBytes - offset);
                    if (expected <= 0)
                        break;

                    fs.Seek(offset, SeekOrigin.Begin);
                    var readTotal = 0;
                    while (readTotal < expected)
                    {
                        var read = await fs.ReadAsync(buffer.AsMemory(readTotal, expected - readTotal), ct).ConfigureAwait(false);
                        if (read <= 0)
                            throw new EndOfStreamException($"读取存档第 {index + 1} 个分块时提前结束");
                        readTotal += read;
                    }

                    onProgress?.Invoke($"正在上传分块 {index + 1}/{totalChunks}...");
                    var chunkRes = await api.UploadCloudSaveChunkAsync(begin.Id, index, buffer, 0, readTotal, ct).ConfigureAwait(false);
                    if (!chunkRes.Ok)
                        return new DreamPortCloudSaveUploadResult(false, begin.Id, false, chunkRes.Message ?? $"第 {index + 1} 个分块上传失败");
                }
            }

            onProgress?.Invoke("正在触发服务端合并与 SHA-256 校验...");
            var completeRes = await api.CompleteCloudSaveUploadAsync(begin.Id, ct).ConfigureAwait(false);
            if (!completeRes.Ok)
                return new DreamPortCloudSaveUploadResult(false, begin.Id, false, completeRes.Message ?? "云备份校验失败");

            return new DreamPortCloudSaveUploadResult(true, begin.Id, false, $"存档「{worldName}」已安全上传至 DreamPort 云端");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new DreamPortCloudSaveUploadResult(false, 0, false, "已取消上传；已上传的分块将在下次续传");
        }
        catch (Exception ex)
        {
            return new DreamPortCloudSaveUploadResult(false, 0, false, ex.Message);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public static async Task<DreamPortCloudSaveRestoreResult> RestoreBackupAsync(
        DreamPortApiClient api,
        string instanceDir,
        long backupId,
        string? preferredTargetName = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (string.IsNullOrWhiteSpace(instanceDir) || !Directory.Exists(instanceDir))
            return new DreamPortCloudSaveRestoreResult(false, null, null, "请先选择有效的本地游戏实例");

        var metaRes = await api.GetCloudSaveMetaAsync(backupId, ct).ConfigureAwait(false);
        if (!metaRes.Ok || metaRes.Item is null)
            return new DreamPortCloudSaveRestoreResult(false, null, null, metaRes.Message ?? "云备份不存在");

        var item = metaRes.Item;
        if (!string.Equals(item.Status, "ready", StringComparison.OrdinalIgnoreCase))
            return new DreamPortCloudSaveRestoreResult(false, null, null, "该云备份尚未完成上传，暂时无法恢复");

        var tempDir = Path.Combine(Path.GetTempPath(), $"dreamport-cloud-restore-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempDir, $"save-{backupId}.zip");
        string? createdTargetDir = null;

        try
        {
            Directory.CreateDirectory(tempDir);
            onProgress?.Invoke($"正在下载云存档「{item.DisplayName}」...");
            var dl = await api.DownloadCloudSaveToFileAsync(
                backupId,
                zipPath,
                expectedSha256: string.IsNullOrWhiteSpace(item.Sha256) ? null : item.Sha256,
                onProgress: (done, total) =>
                {
                    if (total > 0)
                    {
                        var pct = (int)Math.Clamp(done * 100 / total, 0, 100);
                        onProgress?.Invoke($"正在下载云存档（{pct}%）...");
                    }
                },
                ct: ct).ConfigureAwait(false);

            if (!dl.Ok)
                return new DreamPortCloudSaveRestoreResult(false, null, null, dl.Error ?? "下载云存档失败");

            onProgress?.Invoke("正在校验压缩包安全性并解压至本地存档目录...");
            var savesDir = Path.Combine(instanceDir, "saves");
            Directory.CreateDirectory(savesDir);

            var baseName = SanitizeFolderName(
                string.IsNullOrWhiteSpace(preferredTargetName) ? item.WorldName : preferredTargetName,
                $"cloud-save-{backupId}");
            var targetFolderName = ResolveNonOverwritingFolderName(savesDir, baseName);
            var targetDir = Path.Combine(savesDir, targetFolderName);
            createdTargetDir = targetDir;

            await Task.Run(() => ExtractZipSafely(zipPath, targetDir, ct), ct).ConfigureAwait(false);
            createdTargetDir = null; // 恢复成功后再保留目标目录

            return new DreamPortCloudSaveRestoreResult(
                Ok: true,
                RestoredWorldName: targetFolderName,
                TargetDirectory: targetDir,
                Message: $"已恢复云存档至「{targetFolderName}」（未覆盖原有本地存档）");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new DreamPortCloudSaveRestoreResult(false, null, null, "已取消云存档恢复");
        }
        catch (Exception ex)
        {
            return new DreamPortCloudSaveRestoreResult(false, null, null, ex.Message);
        }
        finally
        {
            if (!string.IsNullOrEmpty(createdTargetDir))
                TryDeleteDirectory(createdTargetDir);
            TryDeleteDirectory(tempDir);
        }
    }

    public static void ExtractZipSafely(string zipPath, string targetDir, CancellationToken ct = default)
    {
        var fullTargetDir = Path.GetFullPath(targetDir);
        var fullTargetPrefix = fullTargetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("云存档压缩包为空。");

        // 先对全部条目进行路径安全检查，再判断是否存在统一顶层目录
        var cleanEntries = new List<(ZipArchiveEntry Entry, string[] Segments, bool IsDirectory)>();
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var raw = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (raw.StartsWith('/') || (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':'))
                throw new InvalidDataException($"检测到非法绝对路径条目：{entry.FullName}");

            var isDir = raw.EndsWith('/');
            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            foreach (var seg in parts)
            {
                if (seg is "." or ".." || seg.IndexOf('\0') >= 0)
                    throw new InvalidDataException($"检测到越界路径条目（Zip-Slip）：{entry.FullName}");
            }

            cleanEntries.Add((entry, parts, isDir));
        }

        if (cleanEntries.Count == 0)
            throw new InvalidDataException("云存档压缩包未包含有效文件。");

        // 若根目录没有直接放 level.dat，且所有条目共有单一顶层文件夹，则剥离该顶层目录以免套娃
        var hasRootLevelDat = cleanEntries.Any(e => !e.IsDirectory && e.Segments.Length == 1 && string.Equals(e.Segments[0], "level.dat", StringComparison.OrdinalIgnoreCase));
        string? commonRoot = null;
        if (!hasRootLevelDat && cleanEntries.All(e => e.Segments.Length >= 2 || (e.IsDirectory && e.Segments.Length == 1)))
        {
            var firstRoot = cleanEntries[0].Segments[0];
            if (cleanEntries.All(e => string.Equals(e.Segments[0], firstRoot, StringComparison.Ordinal)))
                commonRoot = firstRoot;
        }

        Directory.CreateDirectory(fullTargetDir);
        foreach (var (entry, segments, isDir) in cleanEntries)
        {
            ct.ThrowIfCancellationRequested();
            var effective = commonRoot is not null && segments.Length > 0 && string.Equals(segments[0], commonRoot, StringComparison.Ordinal)
                ? segments[1..]
                : segments;
            if (effective.Length == 0)
                continue;

            var destPath = Path.GetFullPath(Path.Combine([fullTargetDir, .. effective]));
            if (!destPath.StartsWith(fullTargetPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"拒绝写入越界目标路径：{entry.FullName}");

            if (isDir)
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            var parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            using var src = entry.Open();
            using var dst = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }
    }

    private static string ResolveNonOverwritingFolderName(string savesDir, string baseName)
    {
        var candidate = baseName;
        if (!Directory.Exists(Path.Combine(savesDir, candidate)))
            return candidate;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        candidate = $"{baseName}-{stamp}";
        var seq = 2;
        while (Directory.Exists(Path.Combine(savesDir, candidate)))
        {
            candidate = $"{baseName}-{stamp}-{seq++}";
        }
        return candidate;
    }

    private static string SanitizeFolderName(string? raw, string fallback)
    {
        var cleaned = new string((raw ?? string.Empty).Trim()
            .Where(ch => !char.IsControl(ch) && ch is not '/' and not '\\' and not ':')
            .ToArray());
        while (cleaned.Contains("..", StringComparison.Ordinal))
            cleaned = cleaned.Replace("..", string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Trim('.', ' ');
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
