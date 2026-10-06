// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §2.4

namespace VelaShell.Ssh.Session;

/// <summary>一次成功建连各阶段用了多久（<see cref="SshConnection.ConnectTimings"/>）。</summary>
/// <param name="Dialing">拨号：直连 TCP，或者经代理 / 跳板到达。</param>
/// <param name="VersionExchange">版本交换。</param>
/// <param name="KeyExchange">首次密钥交换，<b>含等主机密钥裁决的时间</b>（问用户的那段也在里面）。</param>
/// <param name="Authentication">用户认证，含等用户输口令、动态码的时间。</param>
/// <remarks>
/// 排障时回答「连得慢，慢在哪一步」：DNS 与代理慢在拨号，对端负载高慢在版本交换与密钥交换，PAM、LDAP 慢在认证。
/// 失败时每一跳的结果在异常的 <c>Hops</c> 里。
/// </remarks>
public readonly record struct SshConnectTimings(
    TimeSpan Dialing,
    TimeSpan VersionExchange,
    TimeSpan KeyExchange,
    TimeSpan Authentication)
{
    /// <summary>四段之和。</summary>
    public TimeSpan Total => Dialing + VersionExchange + KeyExchange + Authentication;
}
