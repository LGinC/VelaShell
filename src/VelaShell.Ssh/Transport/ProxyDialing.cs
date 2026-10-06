// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §1、§2

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>「先到达代理、再在它上面握手」这一类拨号器共用的骨架。</summary>
/// <remarks>
/// 失败时的跳信息全在这里拼：到不了代理，保留内层给的跳；
/// 到了代理但握手失败，记「到代理成功 + 这一跳失败」（velashell-docs/zh/ssh/spec/09 §2.2）。
/// </remarks>
internal static class ProxyDialing
{
    /// <summary>经 <paramref name="inner"/> 连到 <paramref name="proxy"/>，然后跑 <paramref name="handshake"/>。</summary>
    /// <param name="kind">这一跳的种类（进跳信息）。</param>
    /// <param name="kindName">给人看的代理名称（「SOCKS5 代理」）。</param>
    /// <param name="inner">怎么到达代理。</param>
    /// <param name="proxy">代理的地址。</param>
    /// <param name="target">最终要请代理连的目标。</param>
    /// <param name="handshake">在到代理的流上完成握手，返回之后的透明流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async ValueTask<Stream> DialAsync(
        SshDialKind kind,
        string kindName,
        ISshTransportDialer inner,
        SshEndPoint proxy,
        SshDialTarget target,
        Func<Stream, CancellationToken, ValueTask<Stream>> handshake,
        CancellationToken cancellationToken)
    {
        long startedAt = Environment.TickCount64;
        Stream stream;
        try
        {
            stream = await inner.DialAsync(target with { EndPoint = proxy }, cancellationToken).ConfigureAwait(false);
        }
        catch (SshException ex)
        {
            IReadOnlyList<SshHopInfo> hops = DialHops.FromInnerFailure(ex, DialHops.KindOf(inner), proxy, startedAt);
            string message = $"连不上{kindName} {proxy}：{ex.Message}";

            // 〔velashell-docs/zh/ssh/spec/09 §2.2〕直连代理时网络层的失败（解析、拒绝、超时、不可达）落在代理这一跳：报 ProxyUnreachable，
            // 具体原因留在内层异常与跳信息里。内层本身是代理或跳板时（嵌套），那一跳已经说清了自己的原因，原样往外传。
            if (DialHops.KindOf(inner) == SshDialKind.Tcp && ex.Reason is SshFailureReason.DnsFailure
                or SshFailureReason.TcpRefused or SshFailureReason.TcpTimeout or SshFailureReason.TcpUnreachable)
            {
                throw new SshConnectException(SshFailureReason.ProxyUnreachable, SshPhase.Dialing, message, ex) { Hops = hops };
            }
            throw DialHops.Rewrap(ex, message, hops);
        }

        SshHopInfo reachedProxy = DialHops.Hop(DialHops.KindOf(inner), proxy, succeeded: true, startedAt);
        long handshakeStartedAt = Environment.TickCount64;

        try
        {
            return await handshake(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (target.Deadline is { IsExpired: true })
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);

            // 〔velashell-docs/zh/ssh/spec/09 §2.2〕代理接下了 TCP、却迟迟不回握手：连接的计时器到点了。
            // 原样当取消往外传的话，建连出口只知道自己在「拨号」，报的是「建立 TCP 连接超时」——
            // 而 TCP 早就连上了，卡住的是代理。与跳板同一个做法：说清是哪一跳。
            string detail = $"{kindName} {proxy} 接下了连接，却一直没有回应握手。";
            throw new SshConnectException(
                SshFailureReason.Timeout, SshPhase.Dialing,
                $"经{kindName} {proxy} 连 {target.EndPoint} 时在代理握手这一步超时：{detail}", ex)
            {
                Hops = [reachedProxy, DialHops.Hop(kind, target.EndPoint, succeeded: false, handshakeStartedAt, "超时")],
            };
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);

            // 握手中途断开：代理说话说到一半就走了 —— 那是代理拒绝了我们，只是没说原因。
            string detail = ex is IOException
                ? $"{kindName}在握手中途断开了连接（{ex.Message}）。"
                : ex.Message;

            throw DialHops.Rewrap(
                ex,
                ex is IOException ? $"经{kindName} {proxy} 连 {target.EndPoint} 失败：{detail}" : detail,
                [reachedProxy, DialHops.Hop(kind, target.EndPoint, succeeded: false, handshakeStartedAt, detail)]);
        }
        catch (Exception)
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>一条代理拒绝的失败（<see cref="SshFailureReason.ProxyRefused"/>）。</summary>
    public static SshConnectException Refused(string message) =>
        new(SshFailureReason.ProxyRefused, SshPhase.Dialing, message);

    /// <summary>一条代理要认证的失败（<see cref="SshFailureReason.ProxyAuthRequired"/>）。</summary>
    public static SshConnectException AuthRequired(string message) =>
        new(SshFailureReason.ProxyAuthRequired, SshPhase.Dialing, message);

    /// <summary>一条代理拒绝凭据的失败（<see cref="SshFailureReason.ProxyAuthFailed"/>）。</summary>
    public static SshConnectException AuthFailed(string message) =>
        new(SshFailureReason.ProxyAuthFailed, SshPhase.Dialing, message);

    /// <summary>
    /// 请求在本地就发不出去：主机名放不进代理协议、凭据超长（<see cref="SshFailureReason.InvalidConfiguration"/>）。
    /// </summary>
    /// <remarks>曾经报 <see cref="SshFailureReason.ProxyRefused"/> —— 代理根本没见到请求，而且那个码判为可重试，重试只会再失败一次。</remarks>
    public static SshConnectException Misconfigured(string message) =>
        new(SshFailureReason.InvalidConfiguration, SshPhase.Dialing, message);

    private static async ValueTask DisposeQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 失败路径上的清理不抛 —— 否则真正的原因会被盖住。
        }
    }
}
