// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md

namespace VelaShell.Ssh.Sftp;

/// <summary>本库实现了的 SFTP 扩展名（公开面是 <see cref="SftpCapabilities"/> 的 <c>Has*</c> 与 <see cref="SftpCapabilities.RawExtensions"/>）。</summary>
internal static class SftpExtensionNames
{
    /// <summary>原子重命名（覆盖目标）。</summary>
    public const string PosixRename = "posix-rename@openssh.com";

    /// <summary>建硬链接。</summary>
    public const string HardLink = "hardlink@openssh.com";

    /// <summary>强制落盘。</summary>
    public const string Fsync = "fsync@openssh.com";

    /// <summary>文件系统用量。</summary>
    public const string StatVfs = "statvfs@openssh.com";

    /// <summary>服务端宣告的报文与读写长度上限。</summary>
    public const string Limits = "limits@openssh.com";

    /// <summary>服务端内复制，不经过网络。</summary>
    public const string CopyData = "copy-data";

    /// <summary>取指定用户的家目录。</summary>
    public const string HomeDirectory = "home-directory";

    /// <summary>展开 <c>~</c>。</summary>
    public const string ExpandPath = "expand-path@openssh.com";

    /// <summary>设属性但不跟随符号链接（改链接自身）。</summary>
    public const string LSetStat = "lsetstat@openssh.com";

    /// <summary>把数字 uid / gid 翻成用户名与组名。</summary>
    public const string UsersGroupsById = "users-groups-by-id@openssh.com";
}
