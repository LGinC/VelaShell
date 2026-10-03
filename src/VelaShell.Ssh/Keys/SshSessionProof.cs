// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §7.2,§8        会话标识 = 首次交换的 H;服务端对 H 的签名
//   OpenSSH PROTOCOL.agent  session-bind@openssh.com 的 hostkey / session identifier / signature 三个字段
//   行为规格:               velashell-docs/zh/ssh/spec/07-forwarding.md §7.4

namespace VelaShell.Ssh.Keys;

/// <summary>首次密钥交换里服务端证明自己身份的那三样东西:主机公钥、会话标识、对会话标识的签名。</summary>
/// <param name="HostKeyBlob">服务端主机公钥的 wire 编码(首次交换的 <c>K_S</c>)。</param>
/// <param name="SessionId">会话标识,即首次交换的 <c>H</c>。</param>
/// <param name="Signature">服务端在首次交换的应答里对 <c>H</c> 的签名 blob。</param>
/// <remarks>
/// <para>
/// 用途只有一个:向 ssh-agent 声明「这条 agent 连接属于哪个 SSH 会话」,agent 据此执行
/// <c>ssh-add -h</c> 给钥加的目的地约束。agent 会用主机公钥验这个签名,所以三样必须都来自<b>首次</b>交换 ——
/// 会话标识此后不变,而重协商的签名签的是那一轮自己的 <c>H</c>,与会话标识对不上。
/// </para>
/// <para>
/// 三样都是公开信息(服务端在握手里明文发过),不需要清零。数组只在库内传递,不交给调用方。
/// </para>
/// </remarks>
internal sealed record SshSessionProof(byte[] HostKeyBlob, byte[] SessionId, byte[] Signature);
