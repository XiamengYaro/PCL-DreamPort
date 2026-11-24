using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using PCL.Core.App.IoC;
using PCL.Core.IO.Net;
using PCL.Core.Logging;
using PCL.Core.Platform;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Secret;
using Sentry;
using Sentry.Extensibility;

namespace PCL.Core.App.Essentials;

[LifecycleScope("telemetry", "遥测")]
[LifecycleService(LifecycleState.Running)]
public sealed partial class TelemetryService
{
    public static bool IsBuildUntrusted { get; private set; }

    private static void _InitSentry()
    {
        // PCL-DreamPort 禁用向 PCL Community 上游 Sentry 上报遥测数据
        Context.Info("上游 Sentry 遥测已禁用（PCL-DreamPort）");
    }

    // 错误上报
    public static void ReportException(Exception ex, string plain, LogLevel level)
    {
        // 上游遥测已禁用
    }

    private static ulong _GetPhysicalMemoryBytes()
    {
        return ApplicationRuntime.Current.Platform.SystemFacts.GetMemoryInfo().TotalBytes;
    }

    // 设备环境上报
    private static void _ReportDeviceEnvironment(TelemetryDeviceEnvironment content)
    {
        Context.Info("正在上报设备环境调查数据");

        SentrySdk.ConfigureScope(scope => { scope.Contexts["Telemetry"] = content; });

        try
        {
            SentrySdk.CaptureMessage("设备环境调查");
            Context.Info("已发送设备环境调查数据");
        }
        catch (Exception ex)
        {
            Context.Error("设备环境调查数据发送失败，请检查网络连接以及使用的版本", ex);
        }
    }

    [LifecycleStart]
    private static Task _StartAsync()
    {
        Context.Info("已跳过上游遥测环境上报（PCL-DreamPort）");
        return Task.CompletedTask;
    }

    [LifecycleStop]
    private static void _StopAsync()
    {
        SentrySdk.Close();
    }

    // 用来细分过滤 SocketException 的过滤器，我觉得应该除了遥测服务之外没有其他东西会用到这破玩意儿
    private sealed class SocketExceptionFilter : IExceptionFilter
    {
        public bool Filter(Exception ex)
        {
            if (ex is SocketException socketEx)
                return socketEx.SocketErrorCode is
                    SocketError.ConnectionRefused
                    or SocketError.TimedOut
                    or SocketError.HostNotFound
                    or SocketError.NetworkUnreachable
                    or SocketError.ConnectionReset;

            return false;
        }
    }

    // ReSharper disable UnusedAutoPropertyAccessor.Local

    private class TelemetryDeviceEnvironment
    {
        public required string Tag { get; set; }
        public required string Id { get; set; }
        [JsonPropertyName("OS")] public required int Os { get; set; }
        public required bool Is64Bit { get; set; }
        [JsonPropertyName("IsARM64")] public required bool IsArm64 { get; set; }
        public required string Launcher { get; set; }
        public required string LauncherBranch { get; set; }
        [JsonPropertyName("UsedOfficialPCL")] public required bool UsedOfficialPcl { get; set; }
        [JsonPropertyName("UsedHMCL")] public required bool UsedHmcl { get; set; }
        [JsonPropertyName("UsedBakaXL")] public required bool UsedBakaXl { get; set; }
        public required ulong Memory { get; set; }
        [JsonPropertyName("IPv6Status")] public required string Ipv6Status { get; set; }
    }

    // ReSharper disable once InconsistentNaming
    private const string STUN_SERVER_ADDR = "stun.miwifi.com";

    // ReSharper restore UnusedAutoPropertyAccessor.Local
}