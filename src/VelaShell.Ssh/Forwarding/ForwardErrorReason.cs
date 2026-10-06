// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §五

namespace VelaShell.Ssh.Forwarding;

/// <summary>一条转发连接为什么失败了（<see cref="ForwardErrorEventArgs.Reason"/>，也是 metrics 的 <c>reason</c> 标签值）。</summary>
public enum ForwardErrorReason
{
    /// <summary>说不清（零值）。曾经零值是 <see cref="Accept"/>，<c>default</c> 出来的就是一个具体的原因。</summary>
    Unknown = 0,

    /// <summary>接受入站连接失败（监听套接字出错，如句柄耗尽）。</summary>
    Accept,

    /// <summary>并发连接数已达上限，这一条被拒。</summary>
    ConnectionLimit,

    /// <summary>动态转发的 SOCKS 握手非法、命令不支持，或者超时没完成。</summary>
    SocksHandshake,

    /// <summary>到目标的隧道通道打不开（服务端拒绝 <c>direct-tcpip</c>）。</summary>
    ChannelOpen,

    /// <summary>远程转发连不上本机目标。</summary>
    TargetConnect,

    /// <summary>搬运途中出错（连接被重置、隧道断了）。</summary>
    Relay,

    /// <summary>
    /// 开会话时请求的 X11 / agent 转发没开成，按 <see cref="ForwardFailureMode.Continue"/> 照常启动了会话
    /// （<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8）。是哪一项看 <see cref="ForwardKind"/> 标签。
    /// </summary>
    SetupSkipped,
}
