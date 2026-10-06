// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §5  ATTRS 的 extended_type / extended_data
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md

namespace VelaShell.Ssh.Sftp;

/// <summary>ATTRS 里的一条厂商扩展字段（<c>extended_type</c> / <c>extended_data</c>）。</summary>
/// <param name="Type">扩展名（<c>name@domain</c>）。</param>
/// <param name="Data">
/// 扩展数据，<b>原样的字节</b>。draft-02 里它是二进制的 <c>string</c>：曾经按 UTF-8 解成 .NET 字符串，
/// 不是合法 UTF-8 的数据解出来就变了，写回去也不再是原来那串字节。
/// </param>
public readonly record struct SftpExtendedField(string Type, ReadOnlyMemory<byte> Data);
