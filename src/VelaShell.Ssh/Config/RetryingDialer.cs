// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  ConnectionAttempts(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Config;

/// <summary><c>ConnectionAttempts</c>：拨号失败时再试，每次隔一秒（脚本里偶尔连不上的那种）。</summary>
/// <remarks>
/// 只重试<b>拨号</b>本身（TCP 连不上、超时、DNS、代理与跳板这一层的失败）；拨通之后的版本交换、密钥交换、认证不在这里 ——
/// 那些失败重来一遍也是一样的结果。取消立刻停，不再等下一次。
/// </remarks>
internal sealed class RetryingDialer(ISshTransportDialer inner, int attempts) : ISshTransportDialer, ISshDialKindSource
{
    /// <summary>两次之间隔多久（ssh_config(5)：一秒一次）。</summary>
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>里面那个拨号器。</summary>
    internal ISshTransportDialer Inner => inner;

    /// <summary>一共试几次。</summary>
    internal int Attempts => attempts;

    /// <inheritdoc />
    public SshDialKind Kind => DialHops.KindOf(inner);

    /// <inheritdoc />
    public async ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await inner.DialAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts && !cancellationToken.IsCancellationRequested
                && ex is SshConnectException or IOException or System.Net.Sockets.SocketException)
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
