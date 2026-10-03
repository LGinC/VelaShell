// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.agent  session-bind@openssh.com 的 is_forwarding 字段
//   行为规格:               velashell-docs/zh/ssh/spec/07-forwarding.md §7.4

namespace VelaShell.Ssh.Keys;

/// <summary>一条 agent 连接在一个 SSH 会话里拿来做什么 —— 决定会话声明里 <c>is_forwarding</c> 的取值。</summary>
/// <remarks>
/// 零值是认证:声明成认证的连接,agent 不再接受它的任何后续声明,远端那一跳想把转发链接长也接不上 ——
/// 填错时落在「约束钥用不了」这一侧,而不是「约束失效」那一侧。
/// </remarks>
internal enum SshAgentConnectionPurpose
{
    /// <summary>本机这一跳用 agent 里的钥做公钥认证(<c>is_forwarding = false</c>)。</summary>
    Authentication = 0,

    /// <summary>把 agent 转发给远端(<c>is_forwarding = true</c>)。</summary>
    Forwarding = 1,
}
