// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.agent §2  restrict-destination-v00@openssh.com 的主机描述
//   行为规格:                  velashell-docs/zh/ssh/spec/07-forwarding.md §7.3.2

using System.Collections.ObjectModel;
using System.Text;
using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Keys;

/// <summary>目的地约束里的一台主机（<see cref="SshAgentHop"/> 的终点或起点）：名字，加上认它用的主机钥与 CA 公钥。</summary>
/// <remarks>
/// <para>
/// 〔velashell-docs/zh/ssh/spec/07 §7.3.2〕agent 靠<b>主机钥</b>认主机：会话声明交给它的服务端主机公钥在 <see cref="HostKeys"/> 里；
/// 或者服务端出示的是一张主机证书，签发它的 CA 在 <see cref="CertificateAuthorities"/> 里、证书的 principals 接受 <see cref="Name"/>。
/// 只凭主机钥认时名字不参与比较；凭 CA 认时名字原样拿去对 principals（区分大小写，不当通配模式展开）。
/// </para>
/// <para>
/// 属性只读，校验在构造时一次做完 —— <c>with</c> 改不出不合法的组合。两张表抄成只读的存下；相等按内容比，顺序算在内。
/// </para>
/// </remarks>
public sealed record SshAgentHopHost
{
    /// <summary>名字的 UTF-8 字节上限。</summary>
    internal const int MaxNameBytes = 255;

    /// <summary>一台主机最多带多少把钥（主机钥与 CA 公钥合计，各自去重之后）。</summary>
    internal const int MaxKeys = 32;

    /// <summary>构造一台主机。</summary>
    /// <param name="name">名字。凭 CA 认这台主机时拿去对主机证书的 principals；只凭主机钥认时不参与比较。</param>
    /// <param name="hostKeys">这台主机的主机公钥（给证书里那把钥，不给证书）。按公钥 blob 去重，保留第一次出现的位置。</param>
    /// <param name="certificateAuthorities">为这台主机签主机证书的 CA 公钥；<see langword="null"/> 视同空表。同样去重。</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 或 <paramref name="hostKeys"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">
    /// 名字为空、UTF-8 超过 255 字节、含空白 / 控制字符 / <c>,</c>；表里有 <see langword="null"/> 或证书；去重之后两张表合计不在 1–32 把之间。
    /// </exception>
    public SshAgentHopHost(string name, IReadOnlyList<SshPublicKey> hostKeys, IReadOnlyList<SshPublicKey>? certificateAuthorities = null)
    {
        ValidateName(name, nameof(name));
        ArgumentNullException.ThrowIfNull(hostKeys);

        Name = name;
        HostKeys = Distinct(hostKeys, nameof(hostKeys));
        CertificateAuthorities = Distinct(certificateAuthorities ?? [], nameof(certificateAuthorities));

        int total = HostKeys.Count + CertificateAuthorities.Count;
        if (total is 0 or > MaxKeys)
        {
            throw new ArgumentException(
                $"一台主机要带 1–{MaxKeys} 把钥（主机钥与 CA 公钥合计，去重之后），这里是 {total} 把。" +
                (total == 0 ? "没有钥的主机 agent 认不出来，这把钥加进去就用不了。" : ""),
                nameof(hostKeys));
        }
    }

    /// <summary>名字：凭 CA 认这台主机时拿去对主机证书的 principals。</summary>
    public string Name { get; }

    /// <summary>这台主机的主机公钥（去重之后，只读）。</summary>
    public IReadOnlyList<SshPublicKey> HostKeys { get; }

    /// <summary>为这台主机签主机证书的 CA 公钥（去重之后，只读；没有为空表）。</summary>
    public IReadOnlyList<SshPublicKey> CertificateAuthorities { get; }

    /// <summary>照 <c>known_hosts</c> 拼出一台主机（<c>ssh-add -h</c> 找主机钥的那一步）。</summary>
    /// <param name="entries">已解析的条目（<see cref="KnownHostsFile.LoadAsync"/> / <see cref="KnownHostsFile.Parse"/> 的结果；几份文件的条目接在一起传进来）。</param>
    /// <param name="host">主机名。</param>
    /// <param name="port">端口；不是 22 时只认 <c>[host]:port</c> 的写法。</param>
    /// <returns>拼出的主机；<c>known_hosts</c> 里找不到这台主机的钥时为 <see langword="null"/>（先连一次、信任它）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> 或 <paramref name="host"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> 不在 1–65535。</exception>
    /// <exception cref="ArgumentException">主机名不合 <see cref="SshAgentHopHost(string, IReadOnlyList{SshPublicKey}, IReadOnlyList{SshPublicKey}?)"/> 的规则，或者去重之后超过 32 把。</exception>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/07 §7.3.2〕主机对不对得上与 <see cref="KnownHostsFile.Lookup(IReadOnlyList{KnownHostEntry}, string, int, SshPublicKey)"/>
    /// 完全同一套规则（明文、散列行、通配与取反、不分大小写）。普通行的钥进 <see cref="HostKeys"/>，<c>@cert-authority</c> 的进
    /// <see cref="CertificateAuthorities"/>；本库解不了的钥、普通行里写的证书跳过；两张表各自去重，保留文件里的顺序。
    /// </para>
    /// <para>
    /// <b>对得上的 <c>@revoked</c> 行把它的钥从两张表里剔掉</b>，不论排在前面还是后面 —— 这一点与 <c>ssh-add</c> 不同：
    /// 它会把一把同时被吊销的主机钥照样发给 agent，等于替一把我们自己都不认的钥开门。
    /// </para>
    /// <para>
    /// 名字是传入的主机名转小写（principals 照惯例是小写，agent 比较时区分大小写），<b>不带端口</b> —— principals 里没有端口。
    /// </para>
    /// </remarks>
    public static SshAgentHopHost? FromKnownHosts(IReadOnlyList<KnownHostEntry> entries, string host, int port = 22)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        string name = host.ToLowerInvariant();
        ValidateName(name, nameof(host));

        List<SshPublicKey> hostKeys = [];
        List<SshPublicKey> authorities = [];
        List<byte[]> revoked = [];
        foreach (KnownHostEntry entry in entries)
        {
            if (!KnownHostsFile.MatchesHost(entry, host, port))
            {
                continue;
            }
            if (entry.IsRevoked)
            {
                revoked.Add(entry.KeyBlob.ToArray());
                continue;
            }

            SshPublicKey key;
            try
            {
                key = SshPublicKey.Decode(entry.KeyBlob);
            }
            catch (SshPublicKeyException)
            {
                continue;   // 本库认不得的钥
            }
            if (key.IsCertificate)
            {
                continue;   // 钉住一张证书，过一阵子重签了就对不上
            }
            (entry.IsCertificateAuthority ? authorities : hostKeys).Add(key);
        }

        hostKeys.RemoveAll(key => revoked.Any(blob => key.Blob.Span.SequenceEqual(blob)));
        authorities.RemoveAll(key => revoked.Any(blob => key.Blob.Span.SequenceEqual(blob)));
        return hostKeys.Count == 0 && authorities.Count == 0
            ? null
            : new SshAgentHopHost(name, hostKeys, authorities);
    }

    /// <summary>逐项、按顺序比较名字与两张表的内容。</summary>
    /// <remarks>记录默认按成员的引用比较，而表在构造时抄了一份 —— 内容一样的两台主机引用不同，按引用比较就说不通了。</remarks>
    public bool Equals(SshAgentHopHost? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && HostKeys.SequenceEqual(other.HostKeys)
            && CertificateAuthorities.SequenceEqual(other.CertificateAuthorities));

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name, StringComparer.Ordinal);
        foreach (IReadOnlyList<SshPublicKey> list in (IReadOnlyList<SshPublicKey>[])[HostKeys, CertificateAuthorities])
        {
            hash.Add(list.Count);
            foreach (SshPublicKey key in list)
            {
                hash.Add(key);
            }
        }
        return hash.ToHashCode();
    }

    /// <summary>主机名：非空、UTF-8 不超过 255 字节、不含空白 / 控制字符 / <c>,</c>。</summary>
    /// <remarks>
    /// 这几样出现在主机名里只可能是写错了（证书的 principals 由 <c>,</c> 分隔，自身不含 <c>,</c>），agent 却照单全收 ——
    /// 这把钥就悄悄成了哪儿也用不了。<c>*</c> / <c>?</c> 照收：principal 本身可以是通配模式，名字照抄它是放行的。
    /// </remarks>
    private static void ValidateName(string name, string parameter)
    {
        ArgumentNullException.ThrowIfNull(name, parameter);
        if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
        {
            throw new ArgumentException($"主机名要非空、UTF-8 不超过 {MaxNameBytes} 字节。", parameter);
        }
        if (name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ','))
        {
            throw new ArgumentException(
                "主机名里不许有空白、控制字符与「,」—— agent 会照单收下，这把钥却从此对不上任何一台主机。", parameter);
        }
    }

    /// <summary>抄一份去重、只读的表：元素非 <see langword="null"/>、不是证书。</summary>
    private static ReadOnlyCollection<SshPublicKey> Distinct(IReadOnlyList<SshPublicKey> keys, string parameter)
    {
        List<SshPublicKey> distinct = [];
        foreach (SshPublicKey key in keys)
        {
            if (key is null)
            {
                throw new ArgumentException("表里不许有 null。", parameter);
            }
            if (key.IsCertificate)
            {
                throw new ArgumentException(
                    $"{key.KeyType} 是证书：主机证书每次重签 blob 都会变，钉住它过一阵子这把钥就用不了。" +
                    "给证书里那把钥，或者签它的 CA（known_hosts 里的 @cert-authority）。", parameter);
            }
            if (!distinct.Any(seen => seen.Blob.Span.SequenceEqual(key.Blob.Span)))
            {
                distinct.Add(key);
            }
        }
        return Array.AsReadOnly([.. distinct]);
    }
}
