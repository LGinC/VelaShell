// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七(加钥见 §7.3)

namespace VelaShell.Ssh.Keys;

/// <summary>agent 协议的报文编号。</summary>
internal static class SshAgentMessage
{
    public const byte Failure = 5;
    public const byte Success = 6;
    public const byte RequestIdentities = 11;
    public const byte IdentitiesAnswer = 12;
    public const byte SignRequest = 13;
    public const byte SignResponse = 14;
    public const byte AddIdentity = 17;
    public const byte RemoveIdentity = 18;
    public const byte RemoveAllIdentities = 19;
    public const byte Lock = 22;
    public const byte Unlock = 23;
    public const byte AddIdentityConstrained = 25;
    public const byte Extension = 27;
    public const byte ExtensionFailure = 28;

    /// <summary>OpenSSH 的会话声明扩展名（<c>PROTOCOL.agent</c> §1）。</summary>
    public const string SessionBindExtension = "session-bind@openssh.com";

    /// <summary><see cref="SessionBindExtension"/> 的字节形式，给不想先解码字符串的比较用。</summary>
    public static ReadOnlySpan<byte> SessionBindExtensionUtf8 => "session-bind@openssh.com"u8;

    /// <summary>约束：到期自动删除，参数 uint32 秒。</summary>
    public const byte ConstrainLifetime = 1;

    /// <summary>约束：每次签名都要使用者确认，无参数。</summary>
    public const byte ConstrainConfirm = 2;
}
