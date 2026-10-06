// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §2.4(跳板那一跳等人时外层停表)、§5

using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Transport;

/// <summary>
/// 经跳板拨号、跳板连接由调用方自己建时，交给那个回调的上下文。
/// 见 <see cref="DialerChain.Jump(SshEndPoint, Func{SshJumpContext, CancellationToken, ValueTask{SshConnection}})"/>。
/// </summary>
/// <remarks>
/// <para>
/// 跳板这一跳的整个建连都发生在外层连接的拨号阶段里。跳板上的主机密钥裁决与认证常常是在等人
/// （看指纹、输口令、看手机上的动态码），那段时间外层的连接计时器必须停表。
/// 回调里用 <see cref="ConnectAsync"/> 连这一跳，外层的计时器就一起传进去了。
/// </para>
/// <para>
/// 曾经回调只拿到一个取消令牌、自己调 <see cref="SshConnection.ConnectAsync"/>：外层的计时器传不进去，
/// 用户在跳板上输动态码超过外层的连接超时，认证就被当场掐断 —— 而报错还写着「等人输入的时间不算在内」。
/// </para>
/// </remarks>
public sealed class SshJumpContext
{
    private readonly SshConnectDeadline? _outerDeadline;
    private readonly string? _metricsHost;

    internal SshJumpContext(SshEndPoint jumpHost, SshConnectDeadline? outerDeadline, string? metricsHost = null)
    {
        JumpHost = jumpHost;
        _outerDeadline = outerDeadline;
        _metricsHost = metricsHost;
    }

    /// <summary>这一跳跳板的地址。</summary>
    public SshEndPoint JumpHost { get; }

    /// <summary>用调用方准备好的参数连上这一跳跳板。</summary>
    /// <param name="options">这一跳的连接参数：它自己的凭据、主机密钥策略与拨号器。</param>
    /// <param name="cancellationToken">取消令牌（回调收到的那一个）。</param>
    /// <returns>一条已经认证的跳板连接，归拨出来的流所有。</returns>
    /// <remarks>
    /// 与 <see cref="SshConnection.ConnectAsync"/> 相同，只多一件事：这一跳在等人时，外层的连接计时器一并停表。
    /// </remarks>
    public ValueTask<SshConnection> ConnectAsync(SshConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SshConnection.ConnectAsync(
            options with { OuterDeadline = _outerDeadline, MetricsHost = _metricsHost ?? options.MetricsHost }, cancellationToken);
    }
}
