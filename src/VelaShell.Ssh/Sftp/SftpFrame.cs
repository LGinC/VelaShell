// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §3、§4、§6、§7
//   OpenSSH PROTOCOL              扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md §二、§三、§四
//
// 这一层是**纯函数**:bytes ↔ 报文,无状态、无 I/O。
// 所以它可以用报文样本逐字节断言,不需要任何对端。

using System.Buffers;

namespace VelaShell.Ssh.Sftp;

/// <summary>一个解出来的 SFTP 报文。</summary>
/// <remarks>
/// <see cref="Payload"/> 借的是调用方的缓冲，**只在这一轮处理期间有效**。
/// 要留着就自己复制。
/// </remarks>
internal readonly ref struct SftpFrame
{
    internal SftpFrame(SftpMessageType type, ReadOnlySequence<byte> payload)
    {
        Type = type;
        Payload = payload;
    }

    /// <summary>报文类型。</summary>
    public SftpMessageType Type { get; }

    /// <summary>类型之后的全部内容（<b>含 request-id</b>，若这个类型有的话）。</summary>
    public ReadOnlySequence<byte> Payload { get; }
}
