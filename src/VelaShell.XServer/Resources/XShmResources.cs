// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The MIT Shared Memory Extension, Version 1.1 —— Attach / Detach(段按 shmid 附加进服务端进程)

using System.Runtime.Versioning;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>一个附加上的 System V 共享内存段。</summary>
/// <param name="id">XID。</param>
/// <param name="owner">附加它的客户端。</param>
/// <param name="shmid">System V 段号。</param>
/// <param name="readOnly">只读附加。</param>
/// <param name="address">映射进服务端进程的地址。</param>
/// <param name="access">附加时段的大小、属主、创建者与权限(/proc/sysvipc/shm):别的客户端拿这个 XID 来用时按它核对。</param>
internal sealed class XShmSegment(uint id, XClient owner, int shmid, bool readOnly, nint address, XShmAccess access) : XResource(id, owner)
{
    public int ShmId { get; } = shmid;

    public XShmAccess Access { get; } = access;

    public bool ReadOnly { get; } = readOnly;

    public nint Address { get; private set; } = address;

    public long Size => Access.Size;

    /// <summary>从服务端进程里摘下(shmdt)。重复调用无害。</summary>
    [SupportedOSPlatform("linux")]
    public void Detach()
    {
        if (Address != 0)
        {
            X11Server.ShmDetach(Address);
            Address = 0;
        }
    }
}

/// <summary>System V 段的大小、属主、创建者与权限位(/proc/sysvipc/shm 的 size、uid、cuid、perms)。</summary>
internal readonly record struct XShmAccess(long Size, uint Uid, uint Cuid, int Perms);
