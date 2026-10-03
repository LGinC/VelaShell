// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.5.8

namespace VelaShell.Ssh.Forwarding;

/// <summary>开会话时请求的某项转发（X11、agent）没开成，这次启动怎么办。</summary>
/// <remarks>
/// <para>
/// 「请求不请求」由选项本身有没有给出来表达（<see cref="Channels.SshSessionRequestOptions.X11Forwarding"/> /
/// <see cref="Channels.SshSessionRequestOptions.AgentForwarding"/> 为 <see langword="null"/> 就是不请求），
/// 这里只管请求了之后的失败 —— 所以没有「关」这一档：再有一个「关」，就有两种互相矛盾的写法表达同一件事。
/// </para>
/// <para>
/// 曾经是一个布尔（<c>BestEffort</c>）。布尔在调用点上读不出含义（<c>true</c> 是什么为真？），
/// 也没法再加第三种做法；枚举两样都解决。
/// </para>
/// <para>
/// 零值是 <see cref="Fail"/>：调用方明确要了这项转发，没开成就静默降级等于骗他（spec/07 §7.5.8）。
/// </para>
/// </remarks>
public enum ForwardFailureMode
{
    /// <summary>
    /// 让这次启动失败：抛 <see cref="SshForwardException"/>，通道随之关掉。调用方在这一次执行上显式要求的转发用它。
    /// </summary>
    Fail = 0,

    /// <summary>
    /// 不带这项转发、照常启动；没开成的原因放在结果对象上（<c>X11SetupFailure</c> / <c>AgentSetupFailure</c>）。
    /// 连接级开关（<c>ssh_config</c> 的 <c>ForwardX11 yes</c> / <c>ForwardAgent yes</c>、宿主的连接配置）打开的转发用它 ——
    /// 否则一份存量配置会让这台主机上的所有会话都起不来。
    /// </summary>
    Continue = 1,
}
