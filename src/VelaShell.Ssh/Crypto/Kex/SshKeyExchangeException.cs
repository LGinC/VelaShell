// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md;velashell-docs/zh/ssh/spec/08-failures.md

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary>密钥交换失败。</summary>
/// <remarks>
/// <para>
/// 是 <see cref="SshConnectException"/>（建连阶段的失败），与 <see cref="SshNegotiationException"/> 同一层：
/// 曾经直接继承 <see cref="SshException"/>，而 getting-started 写的是「握手阶段的失败是 <see cref="SshConnectException"/>」——
/// 按那个类型分流的调用方（宿主的异常翻译）把它落进了兜底分支，建连失败被当成一般错误。重协商时失败也是它，
/// 那时会话随之断开，与重协商时协商失败一样。
/// </para>
/// <para>
/// 原因记成 <see cref="SshFailureReason.ProtocolError"/>：它报的是对端给的公开值不合法（长度不对、不在曲线上、弱值）。
/// 「算法名没实现」那一类在连接前的 <c>SshAlgorithmSet.Validate()</c> 就挡住了；清单与实现表万一对不上，
/// 那是库自己的编程错误，报 <see cref="InvalidOperationException"/>，不借这个异常的名义。
/// </para>
/// </remarks>
public sealed class SshKeyExchangeException : SshConnectException
{
    /// <summary>用给定消息创建异常。</summary>
    public SshKeyExchangeException(string message)
        : base(SshFailureReason.ProtocolError, SshPhase.KeyExchange, message)
    {
    }

    /// <summary>用给定消息与内部异常创建异常。</summary>
    public SshKeyExchangeException(string message, Exception innerException)
        : base(SshFailureReason.ProtocolError, SshPhase.KeyExchange, message, innerException)
    {
    }
}
