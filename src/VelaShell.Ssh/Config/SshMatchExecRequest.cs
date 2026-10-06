// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  Match exec 的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7.1

namespace VelaShell.Ssh.Config;

/// <summary><c>Match exec</c> 要求值的那一条（交给 <see cref="SshConfigMatchContext.ExecEvaluator"/>）。</summary>
/// <param name="Command">配置里原样写的命令（记号没展开）。</param>
/// <param name="ExpandedCommand">
/// 展开了 <c>%h %n %r %u %%</c> 的命令 —— <b>执行这一条</b>。代入的值都过了 <c>ProxyCommand</c> 同一套白名单
/// （不以 <c>-</c> 开头、只有字母数字与 <c>. - _</c>），不能安全代入的那一条根本不会交过来。
/// </param>
/// <param name="Host">要连的主机名（<c>HostName</c> 改写之后的那个，<c>%h</c>）。</param>
/// <param name="OriginalHost">使用者输入的那个主机名（<c>%n</c>）。</param>
/// <param name="User">要登录的远端用户名（<c>%r</c>）；上下文里没有时为 <see langword="null"/>。</param>
/// <param name="LocalUser">本机当前用户名（<c>%u</c>）；上下文里没有时为 <see langword="null"/>。</param>
public sealed record SshMatchExecRequest(
    string Command, string ExpandedCommand, string Host, string OriginalHost, string? User, string? LocalUser);
