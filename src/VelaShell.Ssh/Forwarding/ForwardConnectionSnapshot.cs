// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §5.1

using System.Net;

namespace VelaShell.Ssh.Forwarding;

/// <summary>转发器上一条正在搬的连接（快照）。</summary>
/// <param name="Id">这条连接在本转发器内的序号（与连接事件里的一致）。</param>
/// <param name="Source">来源（本地转发是本机客户端；远程转发是服务端那头的 originator；可能为 <see langword="null"/>）。</param>
/// <param name="Target">目标的名字。</param>
/// <param name="StartedAt">开始搬运的时刻。</param>
/// <param name="BytesSent">到目前为止本机送进隧道的应用字节数。</param>
/// <param name="BytesReceived">到目前为止从隧道收回的应用字节数。</param>
public sealed record ForwardConnectionSnapshot(
    long Id, EndPoint? Source, string Target, DateTimeOffset StartedAt, long BytesSent, long BytesReceived);
