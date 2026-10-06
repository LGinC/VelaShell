// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §一

namespace VelaShell.Ssh.Channels;

/// <summary>连接上一条通道此刻的样子（<see cref="Session.SshConnection.Channels"/>）。</summary>
/// <param name="LocalId">我们给这条通道的编号。</param>
/// <param name="ChannelType">通道类型（<c>session</c> / <c>direct-tcpip</c> / <c>forwarded-tcpip</c> / …）。</param>
/// <param name="State">状态。</param>
/// <param name="OpenedAt">对端确认（或者我们确认对端开过来）的时刻；还在打开时为 <see langword="null"/>。</param>
/// <param name="BytesSent">到目前为止发出的数据字节数。</param>
/// <param name="BytesReceived">到目前为止收到的数据字节数。</param>
public sealed record SshChannelSnapshot(
    uint LocalId,
    string ChannelType,
    SshChannelState State,
    DateTimeOffset? OpenedAt,
    long BytesSent,
    long BytesReceived);
