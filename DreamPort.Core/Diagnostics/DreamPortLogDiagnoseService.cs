using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DreamPort.Core.Api;
using DreamPort.Core.Config;

namespace DreamPort.Core.Diagnostics;

public sealed record DreamPortLogDiagnoseBuildInput(
    string? InstanceDir,
    string InstanceName,
    string? GameVersion = null,
    string? Loader = null,
    string? LoaderVersion = null,
    string? JavaInfo = null,
    int? MinMemoryMb = null,
    int? MaxMemoryMb = null,
    int? ModCount = null,
    string? ExitCode = null,
    IReadOnlyList<string>? RuntimeLines = null,
    IReadOnlyList<string>? ExtraIdentities = null);

public sealed record DreamPortLogDiagnosePayload(
    string Content,
    string ContextText,
    string ExitCode,
    bool Truncated,
    int CharCount,
    string? CrashReportName);

public sealed record DreamPortLogDiagnoseExecutionResult(
    bool Ok,
    DreamPortLogDiagnoseVerdict? Verdict,
    string DisplayText,
    int TotalTokens = 0,
    long LatencyMs = 0,
    string? Error = null,
    DreamPortFailureReason? FailureReason = null);

/// <summary>
/// DreamPort 游戏日志 AI 诊断客户端引擎：
/// - 尾部流式有界读取超大 latest.log + 提取最新 crash-report 头尾关键栈；
/// - 统一在发送前完成隐私脱敏（玩家名/用户目录/JWT/accessToken/微软 refresh token/调用方传入标识）；
/// - 异步提交后按 PollAfterMs 短轮询至 DONE/FAILED，单次取走后即本地保留结论展示。
/// </summary>
public static partial class DreamPortLogDiagnoseService
{
    public const int BudgetChars = 96 * 1024;
    public const int CrashHeadLines = 220;
    public const int CrashTailLines = 120;
    public const int LogTailLines = 700;
    public const int RuntimeTailLines = 400;
    public const int MaxLineChars = 2000;
    private const int DefaultLogReadBytes = 8 * 1024 * 1024;

    [GeneratedRegex(@"(Setting user:\s*)(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SettingUserRegex();

    [GeneratedRegex(@"/(?:Users|home)/[^/\s""'\\]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixHomeRegex();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\\s""'\\]+", RegexOptions.CultureInvariant)]
    private static partial Regex WinHomeRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(access[-_]?token[""'\s:=]+)([A-Za-z0-9_.-]{16,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenRegex();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9_.-]{16,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\bM\.[A-Za-z0-9_~.-]{20,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MicrosoftRefreshTokenRegex();

    public static string Redact(string text, IEnumerable<string>? extraIdentities = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var outText = SettingUserRegex().Replace(text, "$1<玩家名>");
        outText = UnixHomeRegex().Replace(outText, "<用户目录>");
        outText = WinHomeRegex().Replace(outText, "<用户目录>");
        outText = JwtRegex().Replace(outText, "<令牌已隐去>");
        outText = AccessTokenRegex().Replace(outText, "$1<令牌已隐去>");
        outText = BearerTokenRegex().Replace(outText, "Bearer <令牌已隐去>");
        outText = MicrosoftRefreshTokenRegex().Replace(outText, "<令牌已隐去>");

        if (extraIdentities is not null)
        {
            foreach (var rawTerm in extraIdentities)
            {
                var term = rawTerm?.Trim();
                // 避免 1~2 字符短词把正常英文单词替换成乱码
                if (string.IsNullOrEmpty(term) || term.Length < 3)
                    continue;
                outText = Regex.Replace(outText, Regex.Escape(term), "<玩家名>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        return outText;
    }

    public static string ReadTailBounded(string path, int maxBytes = DefaultLogReadBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || maxBytes <= 0)
            return string.Empty;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0)
                return string.Empty;

            var start = Math.Max(0, fs.Length - maxBytes);
            var len = (int)Math.Min(maxBytes, fs.Length - start);
            fs.Seek(start, SeekOrigin.Begin);

            var buf = new byte[len];
            var readTotal = 0;
            while (readTotal < len)
            {
                var n = fs.Read(buf, readTotal, len - readTotal);
                if (n <= 0)
                    break;
                readTotal += n;
            }

            var text = Encoding.UTF8.GetString(buf, 0, readTotal);
            if (start == 0)
                return text;

            var atLineStart = false;
            try
            {
                fs.Seek(start - 1, SeekOrigin.Begin);
                atLineStart = fs.ReadByte() == '\n';
            }
            catch
            {
                // ignore
            }

            if (atLineStart)
                return text;

            var nl = text.IndexOf('\n');
            return nl >= 0 && nl <= 8192 ? text[(nl + 1)..] : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static DreamPortLogDiagnosePayload BuildPayload(DreamPortLogDiagnoseBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sections = new List<string>();
        var truncated = false;
        string? crashReportName = null;

        // ① 崩溃报告（头 220 行 + 尾 120 行）
        if (!string.IsNullOrWhiteSpace(input.InstanceDir))
        {
            var crashDir = Path.Combine(input.InstanceDir, "crash-reports");
            if (Directory.Exists(crashDir))
            {
                var latestCrash = Directory.EnumerateFiles(crashDir, "*.txt", SearchOption.TopDirectoryOnly)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latestCrash is not null)
                {
                    crashReportName = latestCrash.Name;
                    var crashText = ReadTailBounded(latestCrash.FullName, 512 * 1024);
                    if (!string.IsNullOrEmpty(crashText))
                    {
                        var lines = crashText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                        sections.Add($"===== 崩溃报告 {crashReportName} =====");
                        if (lines.Length <= CrashHeadLines + CrashTailLines)
                        {
                            sections.AddRange(lines.Select(ClampLine));
                        }
                        else
                        {
                            sections.AddRange(lines.Take(CrashHeadLines).Select(ClampLine));
                            sections.Add($"...（中间省略 {lines.Length - CrashHeadLines - CrashTailLines} 行）...");
                            sections.AddRange(lines.Skip(lines.Length - CrashTailLines).Select(ClampLine));
                            truncated = true;
                        }
                    }
                }
            }

            // ② logs/latest.log 末尾 700 行（从文件尾有界读取，避免大日志撑爆内存）
            var latestLogPath = Path.Combine(input.InstanceDir, "logs", "latest.log");
            var logText = ReadTailBounded(latestLogPath, DefaultLogReadBytes);
            if (!string.IsNullOrEmpty(logText))
            {
                var lines = logText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var tail = lines.Length > LogTailLines
                    ? lines[^LogTailLines..]
                    : lines;
                if (lines.Length > LogTailLines)
                    truncated = true;

                sections.Add(string.Empty);
                sections.Add($"===== 游戏日志末尾（latest.log 最后 {tail.Length} 行）=====");
                sections.AddRange(tail.Select(ClampLine));
            }
        }

        // ③ 实时日志行（如有）
        if (input.RuntimeLines is { Count: > 0 } runtimeLines)
        {
            var tail = runtimeLines.Count > RuntimeTailLines
                ? runtimeLines.Skip(runtimeLines.Count - RuntimeTailLines).ToArray()
                : runtimeLines.ToArray();
            if (runtimeLines.Count > RuntimeTailLines)
                truncated = true;

            sections.Add(string.Empty);
            sections.Add("===== 启动器捕获日志（最近输出）=====");
            sections.AddRange(tail.Select(ClampLine));
        }

        if (sections.Count == 0)
            sections.Add("（未找到 crash-reports 或 logs/latest.log，也无捕获日志）");

        var content = string.Join('\n', sections);
        if (content.Length > BudgetChars)
        {
            content = content[^BudgetChars..];
            var nl = content.IndexOf('\n');
            if (nl >= 0 && nl < 2000)
                content = content[(nl + 1)..];
            content = "（注：日志内容超出单次诊断上限，已保留最靠近末尾的关键部分）\n" + content;
            truncated = true;
        }

        // 统一脱敏收口
        content = Redact(content, input.ExtraIdentities);
        var contextText = Redact(BuildContextText(input), input.ExtraIdentities);

        return new DreamPortLogDiagnosePayload(
            Content: content,
            ContextText: contextText,
            ExitCode: (input.ExitCode ?? string.Empty).Trim(),
            Truncated: truncated,
            CharCount: content.Length,
            CrashReportName: crashReportName);
    }

    public static string BuildContextText(DreamPortLogDiagnoseBuildInput input)
    {
        var lines = new List<string>
        {
            $"实例：{input.InstanceName}"
        };
        if (!string.IsNullOrWhiteSpace(input.GameVersion))
            lines.Add($"游戏版本：{input.GameVersion.Trim()}");
        lines.Add(string.IsNullOrWhiteSpace(input.Loader)
            ? "加载器：无（原版）"
            : $"加载器：{input.Loader.Trim()}{(string.IsNullOrWhiteSpace(input.LoaderVersion) ? "" : " " + input.LoaderVersion.Trim())}");
        if (!string.IsNullOrWhiteSpace(input.JavaInfo))
            lines.Add($"Java：{input.JavaInfo.Trim()}");
        if (input.MinMemoryMb is not null || input.MaxMemoryMb is not null)
            lines.Add($"内存：{input.MinMemoryMb?.ToString() ?? "?"} ~ {input.MaxMemoryMb?.ToString() ?? "?"} MB");
        if (input.ModCount is not null)
            lines.Add($"模组数量：{input.ModCount.Value}");

        lines.Add($"启动器版本：{DreamPortBuildConfig.AppVersion}（{RuntimeInformation.OSDescription}）");
        return string.Join('\n', lines);
    }

    public static async Task<DreamPortLogDiagnoseExecutionResult> RunAsync(
        DreamPortApiClient api,
        DreamPortLogDiagnosePayload payload,
        Action<string>? onProgress = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(payload);

        var delay = delayAsync ?? Task.Delay;
        onProgress?.Invoke("正在提交脱敏日志至 DreamPort AI 诊断服务...");

        var submit = await api.SubmitLogDiagnoseAsync(
            payload.Content,
            payload.ContextText,
            payload.ExitCode,
            payload.Truncated,
            ct).ConfigureAwait(false);

        if (!submit.Ok)
            return new DreamPortLogDiagnoseExecutionResult(false, null, submit.Error ?? "提交日志诊断失败", Error: submit.Error, FailureReason: submit.FailureReason);

        if (!submit.Accepted || string.IsNullOrWhiteSpace(submit.JobId))
        {
            var reason = submit.Error ?? "当前暂无法发起 AI 日志诊断";
            return new DreamPortLogDiagnoseExecutionResult(false, null, reason, Error: reason);
        }

        var jobId = submit.JobId;
        var pollInterval = TimeSpan.FromMilliseconds(Math.Clamp(submit.PollAfterMs, 1500, 5000));
        var deadline = DateTime.UtcNow.AddSeconds(120);

        onProgress?.Invoke("已受理诊断任务，正在等待 AI 分析结论...");
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await delay(pollInterval, ct).ConfigureAwait(false);

            var poll = await api.PollLogDiagnoseResultAsync(jobId, ct).ConfigureAwait(false);
            if (!poll.Ok)
            {
                return new DreamPortLogDiagnoseExecutionResult(
                    Ok: false,
                    Verdict: null,
                    DisplayText: poll.Failure ?? "查询诊断结果失败",
                    Error: poll.Failure,
                    FailureReason: poll.FailureReason);
            }

            if (!poll.Found)
            {
                return new DreamPortLogDiagnoseExecutionResult(
                    Ok: false,
                    Verdict: null,
                    DisplayText: poll.Reason ?? "诊断任务已过期或结果已被取走",
                    Error: poll.Reason ?? "任务不存在或已过期");
            }

            if (poll.Status == "running")
                continue;

            if (poll.Status == "done" && poll.Result is not null)
            {
                var display = FormatVerdictForDisplay(poll.Result);
                return new DreamPortLogDiagnoseExecutionResult(
                    Ok: true,
                    Verdict: poll.Result,
                    DisplayText: display,
                    TotalTokens: poll.TotalTokens,
                    LatencyMs: poll.LatencyMs);
            }

            var failMsg = poll.Failure ?? "AI 分析未能产生有效结论，请稍后重试";
            return new DreamPortLogDiagnoseExecutionResult(false, null, failMsg, Error: failMsg);
        }

        return new DreamPortLogDiagnoseExecutionResult(false, null, "AI 日志诊断轮询超时，请稍后重试", Error: "等待 AI 分析结果超时");
    }

    public static string FormatVerdictForDisplay(DreamPortLogDiagnoseVerdict verdict)
    {
        var sb = new StringBuilder();
        var conclusionHeader = string.Equals(verdict.Verdict, "likely_cause", StringComparison.OrdinalIgnoreCase)
            ? "定位结果：已发现可能原因"
            : "定位结果：日志信息不足，需进一步排查";
        sb.AppendLine(conclusionHeader);
        if (!string.IsNullOrWhiteSpace(verdict.Summary))
            sb.AppendLine($"总结：{verdict.Summary.Trim()}");

        if (verdict.Items.Count > 0)
        {
            sb.AppendLine();
            for (var i = 0; i < verdict.Items.Count; i++)
            {
                var item = verdict.Items[i];
                sb.AppendLine($"{i + 1}. {item.Title}（置信度 {Math.Round(item.Confidence * 100)}%）");
                if (!string.IsNullOrWhiteSpace(item.Evidence))
                    sb.AppendLine($"   依据：{item.Evidence}");
                sb.AppendLine($"   建议：{item.Advice}");
            }
        }

        if (verdict.AskFor.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("可补充信息：");
            foreach (var a in verdict.AskFor)
                sb.AppendLine($"- {a}");
        }

        return sb.ToString().Trim();
    }

    private static string ClampLine(string line)
        => line.Length > MaxLineChars ? line[..MaxLineChars] + " ...(本行过长已截断)" : line;
}
