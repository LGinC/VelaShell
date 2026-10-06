// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §7

using System.Diagnostics.Metrics;

namespace VelaShell.Ssh.Diagnostics;

/// <summary>连接与通道的计量仪表（转发的那一组另见 <see cref="Forwarding.ForwardMetrics"/>）。</summary>
/// <remarks>
/// <para>
/// 对外只交出仪表源的名字（用 <see cref="MeterListener"/> 或 OpenTelemetry 按名订阅）；仪表本身是 <c>internal</c> ——
/// 交出可写的计数器等于让任何人都能往里记账。没有订阅者时每次记账只是一次判断。
/// </para>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/08 §7〕<c>host</c> 标签是<b>逻辑目标</b>：经跳板时，各跳的连接也记在最终目标的名下 ——
/// 跳板链上用户认识的是最终那台。不给用户名、密钥指纹之类打标签；<c>type</c> 只取已知的通道类型，别的一律记成 <c>other</c>
/// （对端开过来的通道类型是对端给的字符串，照单全收就是让对端决定标签基数）。
/// </para>
/// <para>
/// 单个连接、单条通道要看实时数字，直接读 <c>SshConnection.BytesSent</c> / <c>SshChannel.BytesSent</c> 这些属性；
/// 这里给的是跨连接汇总、接时序库用的那一路。
/// </para>
/// </remarks>
public static class SshMetrics
{
    /// <summary>仪表源的名字。</summary>
    public const string MeterName = "VelaShell.Ssh";

    private static readonly Meter SharedMeter = new(MeterName);

    /// <summary>活着的连接数（认证完成起，到连接结束为止）。</summary>
    internal static UpDownCounter<long> ConnectionsActive { get; } =
        SharedMeter.CreateUpDownCounter<long>("velashell.ssh.connections.active", "{connection}", "活着的 SSH 连接数");

    /// <summary>一次建连的耗时，按结局与停在哪一步分。</summary>
    internal static Histogram<double> ConnectDuration { get; } =
        SharedMeter.CreateHistogram<double>("velashell.ssh.connect.duration", "ms", "建连耗时（拨号到认证完成或失败）");

    /// <summary>线上字节数：流上实际收发的全部字节（版本标识串、报文头、填充与 MAC，压缩之后的）。</summary>
    internal static Counter<long> Bytes { get; } =
        SharedMeter.CreateCounter<long>("velashell.ssh.bytes", "By", "线上收发的报文字节数");

    /// <summary>报文数。</summary>
    internal static Counter<long> Packets { get; } =
        SharedMeter.CreateCounter<long>("velashell.ssh.packets", "{packet}", "收发的报文数");

    /// <summary>重协商完成的次数。</summary>
    internal static Counter<long> Rekeys { get; } =
        SharedMeter.CreateCounter<long>("velashell.ssh.rekeys", "{rekey}", "完成的密钥重协商次数");

    /// <summary>开着的通道数（对端确认起，到通道收尾为止）。</summary>
    internal static UpDownCounter<long> ChannelsActive { get; } =
        SharedMeter.CreateUpDownCounter<long>("velashell.ssh.channels.active", "{channel}", "开着的通道数");

    /// <summary>接收窗口的额定大小：开通道时记一次，自适应每扩、缩一次再记一次。</summary>
    internal static Histogram<long> ChannelWindow { get; } =
        SharedMeter.CreateHistogram<long>("velashell.ssh.channel.window", "By", "通道接收窗口的实际取值");

    /// <summary>SFTP 在途请求数：每发一个请求记一次此刻的在途数（含它自己）。</summary>
    internal static Histogram<int> SftpInFlight { get; } =
        SharedMeter.CreateHistogram<int>("velashell.ssh.sftp.inflight", "{request}", "SFTP 管线深度的实际取值");

    /// <summary>收到的方向。</summary>
    internal static readonly KeyValuePair<string, object?> DirectionReceived = new("direction", "received");

    /// <summary>发出的方向。</summary>
    internal static readonly KeyValuePair<string, object?> DirectionSent = new("direction", "sent");

    /// <summary><c>host</c> 标签。</summary>
    internal static KeyValuePair<string, object?> HostTag(string host) => new("host", host);

    /// <summary><c>type</c> 标签：已知的通道类型原样，别的记成 <c>other</c>。</summary>
    internal static KeyValuePair<string, object?> ChannelTypeTag(string channelType) => new("type", channelType switch
    {
        "session" or "direct-tcpip" or "forwarded-tcpip" or "x11" or "auth-agent@openssh.com"
            or "direct-streamlocal@openssh.com" or "forwarded-streamlocal@openssh.com" => channelType,
        _ => "other",
    });

    /// <summary>记一次建连：成功时 <paramref name="failure"/> 为 <see langword="null"/>。</summary>
    /// <param name="host">逻辑目标。</param>
    /// <param name="elapsed">从拨号起的耗时。</param>
    /// <param name="failure">失败的原因；成功为 <see langword="null"/>。</param>
    /// <param name="phase">停在哪一步：成功为 <see cref="SshPhase.Open"/>；失败时异常自己带着的优先。</param>
    internal static void RecordConnect(string host, TimeSpan elapsed, Exception? failure, SshPhase phase)
    {
        if (!ConnectDuration.Enabled)
        {
            return;
        }

        // 结局：Success、SshFailureReason 的名字，或者调用方取消（Canceled）、回调自己抛了（CallbackFailed）。
        (string outcome, SshPhase at) = failure switch
        {
            null => ("Success", phase),
            SshException ssh => (ssh.Reason.ToString(), ssh.Phase),
            OperationCanceledException => ("Canceled", phase),
            _ => ("CallbackFailed", phase),
        };
        ConnectDuration.Record(
            elapsed.TotalMilliseconds,
            HostTag(host),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("phase", at.ToString()));
    }
}
