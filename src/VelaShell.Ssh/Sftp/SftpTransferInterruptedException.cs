// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §3.3、§6.2、§九

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Sftp;

/// <summary>传输被中断，但我们知道**确切**已经落盘了多少。</summary>
/// <remarks>
/// <para>
/// 这是 <see cref="DurableLength"/> 存在的全部意义。
/// </para>
/// <para>
/// 流水线满载时有 N 个在途 <c>WRITE</c>，而<b>应答顺序不保证</b>。
/// 中途断开时，服务端报告的文件长度只代表「已确认的<b>最高</b>偏移」，
/// 它之前可能留着读作 0 的空洞。所以不能拿文件长度当续传起点。
/// </para>
/// <para>
/// 常见做法是从文件长度<b>盲退一个完整的在途窗口</b>（比如 2 MiB），
/// 代价是每次续传都要重传那么多，而且那个数字是猜的 ——
/// 换个实现或换个配置就不对了。
/// </para>
/// <para>
/// <see cref="DurableLength"/> 是「从 0 开始连续已确认」的最高偏移，
/// <b>精确，不用猜</b>。从这个数续传即可。
/// </para>
/// </remarks>
public sealed class SftpTransferInterruptedException : SshException
{
    /// <summary>创建一个传输中断异常。原因码随中断的原因（<paramref name="innerException"/>）。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/08 §二〕它承载好几种中断：断线（<see cref="SshFailureReason.ClosedByPeer"/>）、
    /// 服务端拒写（磁盘满、配额、权限 —— 照 <see cref="SftpException"/> 的原因码）、调用方取消与本端释放（<see cref="SshFailureReason.Aborted"/>）。
    /// 曾经一律是 <see cref="SshFailureReason.ClosedByPeer"/>：按原因码判断的调用方会把「磁盘满」当成断线、照样去续传。
    /// 没有内层异常时按断线算。
    /// </remarks>
    public SftpTransferInterruptedException(
        long durableLength, string message, Exception? innerException = null)
        : this(ReasonOf(innerException), durableLength, message, innerException)
    {
    }

    /// <summary>原因码另有出处的中断（关闭超时是 <see cref="SshFailureReason.Timeout"/>）。</summary>
    internal SftpTransferInterruptedException(
        SshFailureReason reason, long durableLength, string message, Exception? innerException = null)
        : base(reason, SshPhase.Open, message, innerException) =>
        DurableLength = durableLength;

    private static SshFailureReason ReasonOf(Exception? inner) => inner switch
    {
        null => SshFailureReason.ClosedByPeer,
        SshException ssh => ssh.Reason,
        OperationCanceledException or ObjectDisposedException => SshFailureReason.Aborted,
        _ => SshFailureReason.Unknown,
    };

    /// <summary>
    /// 从 0 开始<b>连续</b>已确认的字节数。断点续传从这里续。
    /// </summary>
    public long DurableLength { get; }
}
