// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  ProxyCommand(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

namespace VelaShell.Ssh.Config;

/// <summary>配置里写着 <c>ProxyCommand</c>、要调用方批准才执行的那一条（<see cref="SshConfigConnectOptions.ApproveProxyCommand"/>）。</summary>
/// <param name="Host">用户输入的主机名（配置里 <c>Host</c> 匹配的对象）；跳板上的 <c>ProxyCommand</c> 是那个跳板的名字。</param>
/// <param name="Command">
/// 展开 <c>%h</c> <c>%p</c> <c>%r</c> <c>%n</c> 之后、将交给系统 shell（<c>/bin/sh -c</c> / <c>cmd.exe /c</c>）执行的命令行 ——
/// 批准的就是这一行，原样摆给使用者看。
/// </param>
public sealed record SshProxyCommandRequest(string Host, string Command);
