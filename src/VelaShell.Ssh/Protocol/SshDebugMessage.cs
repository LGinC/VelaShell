// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §11.3  "Debug Message"
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §八

using System.Buffers;

namespace VelaShell.Ssh.Protocol;

/// <summary><c>SSH_MSG_DEBUG</c>：对端的调试信息；<see cref="AlwaysDisplay"/> 为真时应当展示给用户。</summary>
/// <param name="AlwaysDisplay">对端要求一定展示（<c>always_display</c>）。</param>
/// <param name="Message">原文（<b>不可信</b>，展示之前要清洗）。</param>
internal readonly record struct SshDebugMessage(bool AlwaysDisplay, string Message)
{
    /// <summary>消息正文最多读多少字节。</summary>
    internal const int MaxMessageBytes = 8 * 1024;

    /// <summary>解一条 <c>SSH_MSG_DEBUG</c>。</summary>
    /// <exception cref="SshWireFormatException">报文格式不对。</exception>
    public static SshDebugMessage Decode(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.Debug);
        bool alwaysDisplay = reader.ReadBoolean();
        string message = reader.ReadUtf8String(MaxMessageBytes);
        return new SshDebugMessage(alwaysDisplay, message);
    }
}
