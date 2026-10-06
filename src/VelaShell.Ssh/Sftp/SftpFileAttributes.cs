// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §5  ATTRS
//   行为规格:                        velashell-docs/zh/ssh/spec/06-sftp.md §4.2

using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Sftp;

/// <summary>一个文件或目录的属性。</summary>
/// <remarks>
/// <para><b>三个坑，都在 draft-02 §5 里：</b></para>
/// <list type="number">
///   <item><c>uid</c> 与 <c>gid</c> 共用<b>一个</b>标志位，<c>atime</c> 与
///   <c>mtime</c> 也共用一个。只想改 mtime 时必须一并给 atime ——
///   <c>SftpFileSystem.SetLastWriteTimeAsync</c> 会先 stat 回来再写回。</item>
///   <item>时间是 <b>32 位 Unix 秒</b>，2038 年溢出。v3 没有解法。
///   按**有符号**读，与 OpenSSH 一致。</item>
///   <item><b>v3 没有单独的文件类型字段</b> —— 是文件还是目录，
///   只能从 <see cref="Permissions"/> 的高位（<c>S_IFMT</c>）取。</item>
/// </list>
/// </remarks>
public readonly record struct SftpFileAttributes
{
    /// <summary>哪些字段是有效的。</summary>
    public SftpAttributeFields Flags { get; init; }

    /// <summary>文件长度（字节）。<see cref="SftpAttributeFields.Size"/> 置位时有效。</summary>
    public ulong Size { get; init; }

    /// <summary>属主。<see cref="SftpAttributeFields.UidGid"/> 置位时有效。</summary>
    public uint UserId { get; init; }

    /// <summary>属组。<see cref="SftpAttributeFields.UidGid"/> 置位时有效。</summary>
    public uint GroupId { get; init; }

    /// <summary>权限位，<b>高位还带着文件类型</b>。</summary>
    public uint Permissions { get; init; }

    /// <summary>最后访问时间（Unix 秒，有符号）。</summary>
    public int AccessTime { get; init; }

    /// <summary>最后修改时间（Unix 秒，有符号）。</summary>
    public int ModifyTime { get; init; }

    /// <summary>扩展属性最多留多少对；多出来的读掉、丢弃。</summary>
    internal const int MaxExtendedFields = 1024;

    /// <summary>厂商扩展属性。</summary>
    /// <remarks>
    /// <c>default(SftpFileAttributes)</c> 里它也是空列表而不是 <see langword="null"/>。
    /// 设值时抄一份只读的存下来：曾经原样存下调用方的集合，值类型的「不可变」只到这一层为止。
    /// </remarks>
    public IReadOnlyList<SftpExtendedField> Extended
    {
        get => field ?? [];
        init => field = value is null or { Count: 0 } ? null : Array.AsReadOnly([.. value]);
    }

    /// <summary>什么都没带的空属性（与 <c>default</c> 相同）。</summary>
    public static SftpFileAttributes Empty => default;

    /// <summary>只带权限的属性（创建文件/目录时用）。</summary>
    public static SftpFileAttributes WithPermissions(uint permissions) => new()
    {
        Flags = SftpAttributeFields.Permissions,
        Permissions = permissions,
    };

    /// <summary>只带长度的属性（截断用）。</summary>
    public static SftpFileAttributes WithSize(ulong size) => new()
    {
        Flags = SftpAttributeFields.Size,
        Size = size,
    };

    /// <summary>带访问与修改时间的属性。</summary>
    /// <remarks>
    /// 两个时间**必须一起给** —— 它们共用一个标志位，
    /// 只给一个的话另一个会被服务端当成 0（1970 年）。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 时间超出 v3 能表示的范围（有符号 32 位秒：1901-12-13 到 2038-01-19，见 velashell-docs/zh/ssh/spec/06 §4.2）。
    /// </exception>
    public static SftpFileAttributes WithTimes(DateTimeOffset accessTime, DateTimeOffset modifyTime) => new()
    {
        Flags = SftpAttributeFields.Times,
        AccessTime = ToWireSeconds(accessTime, nameof(accessTime)),
        ModifyTime = ToWireSeconds(modifyTime, nameof(modifyTime)),
    };

    /// <summary>换成线上的有符号 32 位秒；装不下就抛，不悄悄绕回去。</summary>
    /// <remarks>
    /// 曾经直接 <c>(int)</c> 截断：2038 年之后的时间被写成 1901 年 —— 宿主「保留时间戳」时会把它写到服务端。
    /// </remarks>
    private static int ToWireSeconds(DateTimeOffset time, string parameter)
    {
        long seconds = time.ToUnixTimeSeconds();
        return seconds is >= int.MinValue and <= int.MaxValue
            ? (int)seconds
            : throw new ArgumentOutOfRangeException(
                parameter, time, "SFTP v3 的时间是有符号 32 位秒，只能表示 1901-12-13 到 2038-01-19 之间的时刻。");
    }

    /// <summary>长度是否有效。</summary>
    public bool HasSize => (Flags & SftpAttributeFields.Size) != 0;

    /// <summary>属主属组是否有效。</summary>
    public bool HasUidGid => (Flags & SftpAttributeFields.UidGid) != 0;

    /// <summary>权限是否有效。</summary>
    public bool HasPermissions => (Flags & SftpAttributeFields.Permissions) != 0;

    /// <summary>时间是否有效。</summary>
    public bool HasTimes => (Flags & SftpAttributeFields.Times) != 0;

    /// <summary>这是不是一个目录。</summary>
    /// <remarks>权限字段无效时恒为 <see langword="false"/> —— 我们不知道，不能猜。</remarks>
    public bool IsDirectory =>
        HasPermissions && (Permissions & SftpProtocol.FileTypeMask) == SftpProtocol.FileTypeDirectory;

    /// <summary>这是不是一个符号链接。</summary>
    public bool IsSymbolicLink =>
        HasPermissions && (Permissions & SftpProtocol.FileTypeMask) == SftpProtocol.FileTypeSymbolicLink;

    /// <summary>这是不是一个普通文件。</summary>
    public bool IsRegularFile =>
        HasPermissions && (Permissions & SftpProtocol.FileTypeMask) == SftpProtocol.FileTypeRegular;

    /// <summary>去掉文件类型之后的权限位（<c>0644</c> 那部分）。</summary>
    public uint PermissionBits => Permissions & 0xFFF;

    /// <summary>最后修改时间。</summary>
    public DateTimeOffset LastWriteTime => DateTimeOffset.FromUnixTimeSeconds(ModifyTime);

    /// <summary>最后访问时间。</summary>
    public DateTimeOffset LastAccessTime => DateTimeOffset.FromUnixTimeSeconds(AccessTime);

    /// <summary>v3 认得、也写得出对应字段的那几个标志位。</summary>
    private const SftpAttributeFields WritableFields =
        SftpAttributeFields.Size | SftpAttributeFields.UidGid | SftpAttributeFields.Permissions
        | SftpAttributeFields.Times | SftpAttributeFields.Extended;

    /// <summary>写进报文。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/06 §4.2〕<b>只写认得的标志位。</b>stat 回来的属性里可能带着本库不认识的位（v4 起的字段），
    /// 曾经原样写回而不写对应的字段：拿 stat 的结果改一项再 <c>SetAttributesAsync</c>，发出去的就是一个畸形的 SETSTAT ——
    /// 服务端按那个位去读一个不存在的字段。
    /// </remarks>
    internal void Write(ref SshDataWriter writer)
    {
        writer.WriteUInt32((uint)(Flags & WritableFields));

        if (HasSize)
        {
            writer.WriteUInt64(Size);
        }

        if (HasUidGid)
        {
            writer.WriteUInt32(UserId);
            writer.WriteUInt32(GroupId);
        }

        if (HasPermissions)
        {
            writer.WriteUInt32(Permissions);
        }

        if (HasTimes)
        {
            writer.WriteUInt32((uint)AccessTime);
            writer.WriteUInt32((uint)ModifyTime);
        }

        if ((Flags & SftpAttributeFields.Extended) != 0)
        {
            IReadOnlyList<SftpExtendedField> extended = Extended;
            writer.WriteUInt32((uint)extended.Count);
            foreach ((string type, ReadOnlyMemory<byte> data) in extended)
            {
                writer.WriteUtf8String(type);
                writer.WriteString(data.Span);
            }
        }
    }

    /// <summary>v3 定义了字段的那几个标志位。</summary>
    private const SftpAttributeFields KnownFields =
        SftpAttributeFields.Size | SftpAttributeFields.UidGid | SftpAttributeFields.Permissions
        | SftpAttributeFields.Times | SftpAttributeFields.Extended;

    /// <summary>从报文里读出来。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/06 §4.2〕带着不认识的标志位就不读了：v3 没定义那些位的字段，它们的字节在哪、多长都说不清，
    /// 接着读就是从错位的地方解析 —— 在 NAME 应答里，后面每一项的名字与属性都是错的。曾经默默忽略那些位。
    /// 报出去是 <see cref="SshWireFormatException"/>，解应答的入口把它包成公开的 <see cref="Diagnostics.SshProtocolException"/>。
    /// </remarks>
    internal static SftpFileAttributes Read(ref SshDataReader reader)
    {
        var flags = (SftpAttributeFields)reader.ReadUInt32();
        if ((flags & ~KnownFields) != 0)
        {
            throw new SshWireFormatException(
                $"ATTRS 带着 v3 没定义的标志位 0x{(uint)(flags & ~KnownFields):X8}：它们的字段没法对齐，不往下读。");
        }

        ulong size = 0;
        uint uid = 0;
        uint gid = 0;
        uint permissions = 0;
        int accessTime = 0;
        int modifyTime = 0;

        // 列目录时每一项都要解一次 ATTRS，绝大多数不带扩展属性：没带就不分配。
        List<SftpExtendedField>? extended = null;

        if ((flags & SftpAttributeFields.Size) != 0)
        {
            size = reader.ReadUInt64();
        }

        if ((flags & SftpAttributeFields.UidGid) != 0)
        {
            uid = reader.ReadUInt32();
            gid = reader.ReadUInt32();
        }

        if ((flags & SftpAttributeFields.Permissions) != 0)
        {
            permissions = reader.ReadUInt32();
        }

        if ((flags & SftpAttributeFields.Times) != 0)
        {
            // 按**有符号**读：服务端（OpenSSH）就是这么发的。
            // 按无符号读能撑到 2106 年，但那会与对端对不上账。
            accessTime = (int)reader.ReadUInt32();
            modifyTime = (int)reader.ReadUInt32();
        }

        if ((flags & SftpAttributeFields.Extended) != 0)
        {
            uint count = reader.ReadUInt32();
            extended = [];

            // 只留前 MaxExtendedFields 对，但**每一对都要读掉**：曾经读到上限就停，剩下的字节留在原地 ——
            // 在 NAME 应答里，下一项就从这些字节中间开始解析，名字、属性全是错的。
            // 不会空转：每一对至少 8 个字节，计数再大，循环次数也被报文长度封住（读到头就抛）。
            for (uint i = 0; i < count; i++)
            {
                if (extended.Count < MaxExtendedFields)
                {
                    extended.Add(new SftpExtendedField(
                        reader.ReadUtf8String(SftpProtocol.MaxPathLength),
                        reader.ReadStringAsArray(SftpProtocol.MaxPathLength)));
                }
                else
                {
                    reader.ReadString(SftpProtocol.MaxPathLength);
                    reader.ReadString(SftpProtocol.MaxPathLength);
                }
            }
        }

        return new SftpFileAttributes
        {
            Flags = flags,
            Size = size,
            UserId = uid,
            GroupId = gid,
            Permissions = permissions,
            AccessTime = accessTime,
            ModifyTime = modifyTime,
            // 只读视图交出去（init 里抄成只读的）：曾经直接交出 List，下转型就能改一个「只读」属性背后的内容。
            Extended = extended ?? [],
        };
    }
}
