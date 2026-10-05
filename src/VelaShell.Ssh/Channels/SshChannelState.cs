// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §5.1  CHANNEL_OPEN / CONFIRMATION / FAILURE
//   RFC 4254 §5.2  CHANNEL_WINDOW_ADJUST / DATA / EXTENDED_DATA
//   RFC 4254 §5.3  CHANNEL_EOF / CHANNEL_CLOSE
//   RFC 4254 §5.4  CHANNEL_REQUEST / SUCCESS / FAILURE
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §1、§3、§4、§5

namespace VelaShell.Ssh.Channels;

/// <summary>通道的状态。</summary>
public enum SshChannelState
{
    /// <summary>已发 <c>CHANNEL_OPEN</c>，还没收到应答。没开成（<c>OPEN_FAILURE</c>）时直接进 <see cref="Closed"/>。</summary>
    Opening,

    /// <summary>双向都能收发。</summary>
    Open,

    /// <summary>我们发过 <c>CHANNEL_EOF</c>，不再发数据；<b>仍然可以收</b>。</summary>
    LocalEof,

    /// <summary>对端发过 <c>CHANNEL_EOF</c>；<b>我们仍然可以发</b>。</summary>
    RemoteEof,

    /// <summary>双向都发过 EOF，但通道还没关。</summary>
    BothEof,

    /// <summary>本端先发了 <c>CHANNEL_CLOSE</c>（<c>CloseAsync</c>），在等对端的那一个。</summary>
    /// <remarks>
    /// 只有本端先关才经过这一态。对端先发 <c>CLOSE</c> 时收到即回，直接进 <see cref="Closed"/>。
    /// </remarks>
    Closing,

    /// <summary>通道在本端已经收尾，不会再有任何事件。</summary>
    /// <remarks>
    /// <para>进入它的路有四条：双向 <c>CHANNEL_CLOSE</c> 都走完；本端释放了通道；会话没了；没开成。</para>
    /// <para>
    /// <b>到了这里，通道号不一定已经还给会话。</b>本端释放通道时立刻收尾，但对端的 <c>CLOSE</c> 可能还在路上 ——
    /// 号一直扣着等它（velashell-docs/zh/ssh/spec/05 §1 第 2 条），期间迟到的数据照常计窗口、丢弃。
    /// </para>
    /// </remarks>
    Closed,
}
