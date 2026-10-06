// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §6.4

namespace VelaShell.Ssh.HostKeys;

/// <summary>一次主机密钥轮换的结果（<see cref="Session.SshConnection.LastHostKeyUpdate"/>）。</summary>
/// <param name="Added">证实并记下的新钥；没有新钥时为空。</param>
/// <param name="Skipped">没做或没做成的原因；做成了为 <see langword="null"/>。</param>
public sealed record SshHostKeyUpdate(IReadOnlyList<SshPublicKey> Added, string? Skipped);
