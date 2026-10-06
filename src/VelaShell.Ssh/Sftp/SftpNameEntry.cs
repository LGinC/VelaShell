// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §3、§4、§6、§7
//   OpenSSH PROTOCOL              扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md §二、§三、§四
//
// 这一层是**纯函数**:bytes ↔ 报文,无状态、无 I/O。
// 所以它可以用报文样本逐字节断言,不需要任何对端。

namespace VelaShell.Ssh.Sftp;

/// <summary><c>SSH_FXP_NAME</c> 里的一项。</summary>
/// <param name="Name">文件名（<b>只是名字，不含路径</b>）。</param>
/// <param name="LongName">
/// <c>ls -l</c> 风格的一行文本。<b>格式未标准化，不要解析它。</b>
/// </param>
/// <param name="Attributes">属性。</param>
internal readonly record struct SftpNameEntry(
    string Name,
    string LongName,
    SftpFileAttributes Attributes);
