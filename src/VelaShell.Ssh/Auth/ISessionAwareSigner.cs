// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.agent  session-bind@openssh.com
//   行为规格:               velashell-docs/zh/ssh/spec/04-authentication.md §4.1;velashell-docs/zh/ssh/spec/07-forwarding.md §7.4

using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Auth;

/// <summary>签名之前要先知道「为哪个会话签」的签名器 —— 背后是 ssh-agent 的那种。</summary>
/// <remarks>
/// <para>
/// agent 要先收到会话声明（<c>session-bind@openssh.com</c>），才能执行 <c>ssh-add -h</c> 给钥加的目的地约束。
/// 认证器在第一次让这类签名器签名之前调用 <see cref="PrepareForSessionAsync"/>。
/// </para>
/// <para>
/// <c>internal</c>：这是认证器与本库自己的 agent 签名器之间的约定，不是扩展点 ——
/// 第三方签名器背后不是 OpenSSH 的 agent 协议，没有这一步可做。公开类型（<see cref="SshCertificateSigner"/>）
/// 用显式实现把它转给里面那个签名器。
/// </para>
/// </remarks>
internal interface ISessionAwareSigner
{
    /// <summary>告诉签名器它接下来为哪个会话签名。</summary>
    /// <param name="proof">首次密钥交换里服务端的身份证明。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// agent 不支持会话声明不算失败 —— 签名照常，只是目的地约束不生效。
    /// 同一个会话可以被调用多次，实现要让重复的调用不重复发声明。
    /// </remarks>
    ValueTask PrepareForSessionAsync(SshSessionProof proof, CancellationToken cancellationToken);
}
