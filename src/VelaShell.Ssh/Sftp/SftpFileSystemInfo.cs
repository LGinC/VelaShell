// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL  statvfs@openssh.com
//   行为规格:         velashell-docs/zh/ssh/spec/06-sftp.md §7.1

namespace VelaShell.Ssh.Sftp;

/// <summary>一个远端文件系统的用量（<c>statvfs@openssh.com</c>，字段与 POSIX 的 <c>statvfs</c> 一一对应）。</summary>
/// <param name="BlockSize">文件系统的块大小（<c>f_bsize</c>）。</param>
/// <param name="FragmentSize">基本块大小（<c>f_frsize</c>）—— 下面几个块数的单位。</param>
/// <param name="TotalBlocks">总块数（<c>f_blocks</c>）。</param>
/// <param name="FreeBlocks">空闲块数，含只留给 root 的那部分（<c>f_bfree</c>）。</param>
/// <param name="AvailableBlocks">普通用户可用的空闲块数（<c>f_bavail</c>）。</param>
/// <param name="TotalFiles">inode 总数（<c>f_files</c>）。</param>
/// <param name="FreeFiles">空闲 inode 数（<c>f_ffree</c>）。</param>
/// <param name="AvailableFiles">普通用户可用的空闲 inode 数（<c>f_favail</c>）。</param>
/// <param name="FileSystemId">文件系统标识（<c>f_fsid</c>）。</param>
/// <param name="Flags">标志位（<c>f_flag</c>）：<c>0x1</c> 只读，<c>0x2</c> 不认 setuid。</param>
/// <param name="MaxNameLength">文件名的最大长度（<c>f_namemax</c>）。</param>
public sealed record SftpFileSystemInfo(
    ulong BlockSize,
    ulong FragmentSize,
    ulong TotalBlocks,
    ulong FreeBlocks,
    ulong AvailableBlocks,
    ulong TotalFiles,
    ulong FreeFiles,
    ulong AvailableFiles,
    ulong FileSystemId,
    ulong Flags,
    ulong MaxNameLength)
{
    /// <summary><c>f_flag</c> 的只读位（<c>SSH_FXE_STATVFS_ST_RDONLY</c>）。</summary>
    public const ulong ReadOnlyFlag = 0x1;

    /// <summary><c>f_flag</c> 的不认 setuid 位（<c>SSH_FXE_STATVFS_ST_NOSUID</c>）。</summary>
    public const ulong NoSetUidFlag = 0x2;

    /// <summary>总容量（字节）。溢出时饱和到 <see cref="ulong.MaxValue"/>。</summary>
    public ulong TotalBytes => Bytes(TotalBlocks);

    /// <summary>空闲空间（字节），含只留给 root 的那部分。</summary>
    public ulong FreeBytes => Bytes(FreeBlocks);

    /// <summary>
    /// 这个登录用户还能写多少字节 —— 上传前预检「放得下吗」看的是这个，而不是 <see cref="FreeBytes"/>。
    /// </summary>
    public ulong AvailableBytes => Bytes(AvailableBlocks);

    /// <summary>挂载成只读的。</summary>
    public bool IsReadOnly => (Flags & ReadOnlyFlag) != 0;

    /// <remarks>块数的单位是 <c>f_frsize</c>；有的实现把它填成 0，那时按 <c>f_bsize</c> 算（与 <c>df</c> 一致）。</remarks>
    private ulong Bytes(ulong blocks)
    {
        ulong unit = FragmentSize != 0 ? FragmentSize : BlockSize;
        return unit != 0 && blocks > ulong.MaxValue / unit ? ulong.MaxValue : blocks * unit;
    }
}
