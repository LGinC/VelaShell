// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL  users-groups-by-id@openssh.com
//   行为规格:         velashell-docs/zh/ssh/spec/06-sftp.md §7.1

namespace VelaShell.Ssh.Sftp;

/// <summary>
/// <see cref="SftpFileSystem.LookupUserAndGroupNamesAsync"/> 的结果：与问的 uid / gid 一一对应的名字，
/// 服务端不认识的为 <see langword="null"/>。名字是不可信文本。
/// </summary>
/// <param name="UserNames">用户名，顺序同问的 uid。</param>
/// <param name="GroupNames">组名，顺序同问的 gid。</param>
public sealed record SftpIdNames(IReadOnlyList<string?> UserNames, IReadOnlyList<string?> GroupNames);
