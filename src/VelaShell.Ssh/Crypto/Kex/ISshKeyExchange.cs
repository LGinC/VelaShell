// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §8    diffie-hellman-group*
//   RFC 5656 §4    ecdh-sha2-*
//   RFC 8731 §3    curve25519-sha256
//   RFC 4419 §3    diffie-hellman-group-exchange-*(多一轮,见 ISshGroupExchange)
//   draft-kampanakis-curdle-ssh-pq-ke  后量子混合
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §3、§4

using System.Security.Cryptography;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary>
/// 一个值进入交换哈希时的编码方式。
/// </summary>
/// <remarks>
/// <b>这是整份 KEX 规格里最容易出错、也最难排查的一处</b>（velashell-docs/zh/ssh/spec/03 §4.1）：
/// <list type="bullet">
///   <item>curve25519 / ECDH：公钥是 <c>string</c>，共享密钥是 <b><c>mpint</c></b>。</item>
///   <item>DH group：公钥与共享密钥**都**是 <c>mpint</c>。</item>
///   <item>后量子混合：公钥是 <c>string</c>，共享密钥是 <b><c>string</c></b>（定长哈希输出）。</item>
/// </list>
/// 把共享密钥按 <c>string</c> 写进本该是 <c>mpint</c> 的位置，
/// 在最高位为 0 时**恰好正确**、最高位为 1 时失败 —— 也就是大约 1/256 的连接
/// 报「签名验证不过」，其余全对。这种概率性失败极难排查，所以它由类型系统区分。
/// </remarks>
internal enum SshKexValueEncoding
{
    /// <summary>按 <c>string</c>（4 字节长度前缀 + 原始字节）。</summary>
    ByteString,

    /// <summary>按 <c>mpint</c>（去前导零，最高位为 1 时补 <c>0x00</c>）。</summary>
    Mpint,
}

/// <summary>
/// 一次密钥交换。**一个实例只用于一次交换**，用完即弃。
/// </summary>
/// <remarks>
/// 我们支持的方法几乎都是同一个两步形状：客户端发一个公钥（编号 30），
/// 服务端回「主机公钥 ‖ 服务端公钥 ‖ 对交换哈希的签名」（编号 31）。
/// <c>diffie-hellman-group-exchange-*</c>（RFC 4419）先多一轮要群，之后的两步换成 32 / 33 —— 它另外实现 <see cref="ISshGroupExchange"/>。
/// </remarks>
internal interface ISshKeyExchange : IDisposable
{
    /// <summary>算法的注册名。</summary>
    string Name { get; }

    /// <summary>交换哈希与密钥派生用的哈希算法。</summary>
    HashAlgorithmName HashAlgorithm { get; }

    /// <summary>公钥（<c>Q_C</c> / <c>e</c>）进交换哈希时的编码方式。</summary>
    SshKexValueEncoding PublicValueEncoding { get; }

    /// <summary>共享密钥（<c>K</c>）进交换哈希时的编码方式。</summary>
    SshKexValueEncoding SharedSecretEncoding { get; }

    /// <summary>
    /// 产出客户端的公开值，作为 <c>SSH_MSG_KEX_*_INIT</c>（30）的内容发出。
    /// </summary>
    byte[] CreateClientPublicValue();

    /// <summary>
    /// 用服务端的公开值算出共享密钥。
    /// </summary>
    /// <param name="serverPublicValue">服务端在编号 31 的报文里给出的公开值。</param>
    /// <returns>共享密钥的原始字节（编码方式见 <see cref="SharedSecretEncoding"/>）。</returns>
    /// <exception cref="SshKeyExchangeException">
    /// 公开值长度不对、不在曲线上、越界，或结果落在弱值上（如 X25519 的全零）。
    /// </exception>
    byte[] ComputeSharedSecret(ReadOnlySpan<byte> serverPublicValue);
}

/// <summary>
/// 群由服务端现给的密钥交换（RFC 4419）：在 <see cref="ISshKeyExchange.CreateClientPublicValue"/> 之前，
/// 先发 <c>GEX_REQUEST</c>（34）、收 <c>GEX_GROUP</c>（31）；之后的两步用 32 / 33，交换哈希多五项（spec/03 §4.2）。
/// </summary>
internal interface ISshGroupExchange
{
    /// <summary>请求的最小位数。</summary>
    uint MinimumBits { get; }

    /// <summary>请求的首选位数。</summary>
    uint PreferredBits { get; }

    /// <summary>请求的最大位数。</summary>
    uint MaximumBits { get; }

    /// <summary>收下服务端给的群：当场查位数与生成元的范围，素性检验在后台起跑。</summary>
    /// <exception cref="Diagnostics.SshConnectException">位数不在请求的范围内（<c>NegotiationFailed</c>）。</exception>
    /// <exception cref="SshKeyExchangeException">生成元越界、<c>p</c> 是偶数。</exception>
    void AcceptGroup(ReadOnlySpan<byte> prime, ReadOnlySpan<byte> generator);

    /// <summary>等素性检验的结论。用群算出的东西（共享密钥）被信任之前必须等它。</summary>
    /// <exception cref="SshKeyExchangeException"><c>p</c> 不是素数。</exception>
    Task EnsureGroupValidAsync(CancellationToken cancellationToken);

    /// <summary>收下的 <c>p</c>（无符号大端），进交换哈希。</summary>
    byte[] Prime { get; }

    /// <summary>收下的 <c>g</c>（无符号大端），进交换哈希。</summary>
    byte[] Generator { get; }
}
