// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL        auth-agent-req@openssh.com / auth-agent@openssh.com
//   行为规格:               velashell-docs/zh/ssh/spec/07-forwarding.md §七

using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Forwarding;

/// <summary>agent 转发（<c>ssh -A</c>）的参数与安全约束。</summary>
/// <remarks>
/// <para>
/// <b>agent 转发是一把上膛的枪。</b>
/// 转发期间，远端主机上的 root 可以用你的私钥<b>签任何东西</b> ——
/// 包括拿你的身份去登录别的机器。私钥本身不会离开本机，
/// 但「用私钥做事的能力」离开了。
/// </para>
/// <para>
/// 所以这里的默认值全部朝着「更少暴露」那一侧 —— 除了 <see cref="AllowedKeys"/>：
/// 它的默认是「不限」，因为「只转发指定的密钥」需要使用者先说出是哪几把。
/// </para>
/// </remarks>
public sealed record AgentForwardOptions
{
    /// <summary>
    /// 只转发这些公钥。<see langword="null"/> 表示把整个 agent 暴露出去；
    /// <b>空列表表示一把都不给</b>。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/07 §7.2〕<b>必须支持「只转发指定的密钥」。</b>
    /// 一台跳板机没有理由能用到你所有的密钥 —— 它只需要下一跳那一把。
    /// 空列表曾经表示「不限」，于是「用户勾掉了所有钥」会悄悄变成「全部暴露」—— 现在两者分开。
    /// <para>
    /// 设值时抄一份只读的存下来：曾经原样存下调用方的集合，转发开着的时候往那个 <c>List</c> 里加一把钥，远端就能用上它。
    /// </para>
    /// </remarks>
    public IReadOnlyList<SshPublicKey>? AllowedKeys
    {
        get;
        init => field = value is null ? null : Array.AsReadOnly([.. value]);
    }

    /// <summary>
    /// 每次远端请求签名时问一次使用者。
    /// </summary>
    /// <remarks>
    /// 返回 <see langword="false"/> 就拒签。对跳板场景这是唯一能让人安心的做法 ——
    /// 否则你根本不知道那台机器拿你的身份做了什么、做了几次。
    /// </remarks>
    public Func<AgentSignatureRequest, CancellationToken, ValueTask<bool>>? ConfirmEachSignature { get; init; }

    /// <summary>同时允许几条 agent 通道。</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>开会话时 agent 转发没开成（本机 agent 连不上、服务端拒绝），这次启动怎么办。</summary>
    /// <remarks>
    /// <b>默认 <see cref="ForwardFailureMode.Fail"/></b>，与 X11 同一条规矩（<see cref="X11ForwardOptions.FailureMode"/>）：
    /// 显式要求的失败就抛；连接级开关打开的（<c>ssh_config</c> 的 <c>ForwardAgent yes</c>、宿主的连接配置）
    /// 用 <see cref="ForwardFailureMode.Continue"/>，原因放在 <see cref="Channels.SshShell.AgentSetupFailure"/> /
    /// <see cref="Channels.SshCommand.AgentSetupFailure"/> 上。
    /// </remarks>
    public ForwardFailureMode FailureMode { get; init; }

    /// <summary>本机 agent 的位置；<see langword="null"/> 取 <c>SSH_AUTH_SOCK</c> / Windows 的 OpenSSH agent 管道。</summary>
    /// <exception cref="ArgumentException">已经给了 <see cref="LocalConnector"/>。</exception>
    public string? AgentEndpoint
    {
        get;
        init => field = value is not null && LocalConnector is not null ? throw EndpointConflict() : value;
    }

    /// <summary>
    /// 自己去连本机 agent —— agent 可能在一条隧道的另一头，或者由别的软件以自定义方式提供。
    /// 与 <see cref="AgentEndpoint"/> 只能给一个。
    /// </summary>
    /// <exception cref="ArgumentException">已经给了 <see cref="AgentEndpoint"/>。</exception>
    /// <remarks>曾经两个都给时静默忽略 <see cref="AgentEndpoint"/>：配了一个端点，连的却是另一个 agent。</remarks>
    public Func<CancellationToken, ValueTask<SshAgentClient>>? LocalConnector
    {
        get;
        init => field = value is not null && AgentEndpoint is not null ? throw EndpointConflict() : value;
    }

    private static ArgumentException EndpointConflict() =>
        new("AgentEndpoint 与 LocalConnector 只能给一个：两个都给时连哪一个 agent 说不清。");

    /// <summary>默认参数：不限密钥、不逐次确认。</summary>
    /// <remarks>
    /// 它只在使用者已经显式打开 agent 转发之后才生效 ——
    /// 转发本身默认是关的。
    /// </remarks>
    public static AgentForwardOptions Default { get; } = new();
}
