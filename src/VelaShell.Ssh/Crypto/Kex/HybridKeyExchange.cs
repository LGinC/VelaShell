// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 10042                       mlkem768x25519-sha256 / mlkem768nistp256-sha256 / mlkem1024nistp384-sha384
//   OpenSSH PROTOCOL                sntrup761x25519-sha512
//   FIPS 203                        ML-KEM-768 / ML-KEM-1024
//   RFC 7748                        X25519
//   RFC 5656 §4、SEC 1 §2.3.3–§2.3.5  NIST 曲线的点编码、域元素的定长编码
//   行为规格:                       velashell-docs/zh/ssh/spec/03-key-exchange.md §3.6、§3.7

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pqc.Crypto.NtruPrime;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary>
/// 后量子混合密钥交换：把一个 KEM 与一次椭圆曲线 DH **并联**。
/// </summary>
/// <remarks>
/// <para>
/// 支持 <c>mlkem768x25519-sha256</c>、<c>sntrup761x25519-sha512</c>（含其旧名 <c>sntrup761x25519-sha512@openssh.com</c>），
/// 以及面向 FIPS 的 <c>mlkem768nistp256-sha256</c> 与 <c>mlkem1024nistp384-sha384</c>（经典的那一半换成 NIST 曲线的 ECDH）。
/// </para>
/// <para>
/// <b>为什么是「混合」而不是纯后量子</b>：后量子算法还年轻，
/// 万一某个被攻破，椭圆曲线那一半仍然撑着；反过来量子计算机来了，KEM 撑着。
/// 两者都被攻破才失效 —— 而这正是「先截获、以后再解」这类攻击今天就需要的防护。
/// </para>
/// <para>
/// 形状（两端的公开值都是**两段拼接后整体按一个 <c>string</c>**，不是两个 string；KEM 在前）：
/// </para>
/// <list type="bullet">
///   <item>客户端 → 服务端：<c>KEM 公钥 ‖ 经典公钥</c></item>
///   <item>服务端 → 客户端：<c>KEM 密文 ‖ 经典公钥</c></item>
///   <item>共享密钥：<c>HASH(K_kem ‖ K_经典)</c>，<b>按 <c>string</c> 编码</b></item>
/// </list>
/// <para>
/// ⚠️ 最后那条是与其它所有方法的**根本差别**：curve25519 与 ECDH 的 <c>K</c> 是
/// <c>mpint</c>，这里是定长 <c>string</c>。混合方法刻意改用定长，正是为了避开
/// <c>mpint</c> 的前导零问题（velashell-docs/zh/ssh/spec/03 §3.6、§3.7.2）。写错同样表现为概率性签名失败。
/// NIST 曲线那一半的 <c>K_经典</c> 也是定长的 X 坐标（前导零保留），不是 §3.3 那种按 <c>mpint</c> 的。
/// </para>
/// </remarks>
internal sealed class HybridKeyExchange : ISshKeyExchange
{
    private readonly IHybridKem _kem;
    private readonly IClassicalPart _classical;
    private bool _disposed;

    /// <summary>按算法名创建一次混合交换。每次交换（含每一次重协商）都新造一个：临时密钥不复用（RFC 10042 §5）。</summary>
    public HybridKeyExchange(string name)
    {
        (_kem, _classical, HashAlgorithm) = name switch
        {
            SshAlgorithmNames.MlKem768X25519Sha256 =>
                (CreateMlKem(MLKemAlgorithm.MLKem768), (IClassicalPart)new X25519Part(name), HashAlgorithmName.SHA256),
            SshAlgorithmNames.MlKem768Nistp256Sha256 =>
                (CreateMlKem(MLKemAlgorithm.MLKem768), new NistCurvePart(name, SshAlgorithmNames.EcdhSha2Nistp256), HashAlgorithmName.SHA256),
            SshAlgorithmNames.MlKem1024Nistp384Sha384 =>
                (CreateMlKem(MLKemAlgorithm.MLKem1024), new NistCurvePart(name, SshAlgorithmNames.EcdhSha2Nistp384), HashAlgorithmName.SHA384),
            SshAlgorithmNames.Sntrup761X25519Sha512 or SshAlgorithmNames.Sntrup761X25519Sha512OpenSsh =>
                (new SNtruPrime761Kem(), new X25519Part(name), HashAlgorithmName.SHA512),
            _ => throw new ArgumentException($"不是已知的混合 KEX 算法名：{name}", nameof(name)),
        };

        Name = name;
    }

    /// <summary>
    /// 〔AGENTS.md 3.3〕BCL 有的走 BCL：平台支持时（Windows 的 CNG、OpenSSL 3.5 起）用 <see cref="MLKem"/>，
    /// 吃得到平台的实现与它的侧信道防护；不支持时退回 BouncyCastle。两条路径总有一条可用，所以算法名不看平台、总在清单里。
    /// </summary>
    private static IHybridKem CreateMlKem(MLKemAlgorithm algorithm) =>
        MLKem.IsSupported
            ? new BclMlKem(algorithm)
            : new BouncyCastleMlKem(algorithm == MLKemAlgorithm.MLKem1024 ? MLKemParameters.ml_kem_1024 : MLKemParameters.ml_kem_768);

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public HashAlgorithmName HashAlgorithm { get; }

    /// <inheritdoc />
    public SshKexValueEncoding PublicValueEncoding => SshKexValueEncoding.ByteString;

    /// <inheritdoc />
    /// <remarks><b>定长 <c>string</c>，不是 <c>mpint</c>。</b>见类型说明。</remarks>
    public SshKexValueEncoding SharedSecretEncoding => SshKexValueEncoding.ByteString;

    /// <inheritdoc />
    public byte[] CreateClientPublicValue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] kemPublic = _kem.GenerateKeyPairAndGetPublicKey();
        byte[] classicalPublic = _classical.CreatePublicValue();

        byte[] combined = new byte[kemPublic.Length + classicalPublic.Length];
        kemPublic.CopyTo(combined, 0);
        classicalPublic.CopyTo(combined, kemPublic.Length);
        return combined;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/03 §3.7.3〕前一步不过就不做后一步：先查总长（解封装之前，RFC 10042 §2.1），
    /// 再验经典那一半（点的编码、在不在曲线上），最后解封装。ML-KEM 的解封装没有失败可言 ——
    /// 密文被改过时交回一个伪随机的结果（隐式拒绝），表现为签名验证失败。
    /// </remarks>
    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> serverPublicValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int expected = _kem.CiphertextBytes + _classical.PublicValueBytes;
        if (serverPublicValue.Length != expected)
        {
            throw new SshKeyExchangeException(
                $"{Name} 的服务端公开值必须是 {expected} 字节" +
                $"（{_kem.CiphertextBytes} 字节 KEM 密文 + {_classical.PublicValueBytes} 字节经典公钥），" +
                $"收到 {serverPublicValue.Length} 字节。");
        }

        byte[] classicalSecret = _classical.ComputeSecret(serverPublicValue[_kem.CiphertextBytes..]);
        byte[] kemSecret;
        try
        {
            kemSecret = _kem.Decapsulate(serverPublicValue[.._kem.CiphertextBytes]);
        }
        catch (Exception ex) when (ex is not SshKeyExchangeException)
        {
            // 平台原语抛什么都按 KEX 失败处理：各平台抛的类型不同（spec/03 §3.7.3 第 5 步）。
            CryptographicOperations.ZeroMemory(classicalSecret);
            throw new SshKeyExchangeException($"{Name} 的 KEM 部分解封装失败。", ex);
        }

        // K = HASH(K_kem ‖ K_经典)：KEM 在前，顺序不能颠倒。
        byte[] combined = new byte[kemSecret.Length + classicalSecret.Length];
        try
        {
            kemSecret.CopyTo(combined, 0);
            classicalSecret.CopyTo(combined, kemSecret.Length);
            return Hash(combined);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kemSecret);
            CryptographicOperations.ZeroMemory(classicalSecret);
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    /// <summary>方法的哈希。三支各写各的：SHA-384 落进 SHA-512 不报任何错，只是每一次都签名失败（spec/03 §3.7.2）。</summary>
    private byte[] Hash(byte[] data)
    {
        if (HashAlgorithm == HashAlgorithmName.SHA256)
        {
            return SHA256.HashData(data);
        }
        if (HashAlgorithm == HashAlgorithmName.SHA384)
        {
            return SHA384.HashData(data);
        }
        if (HashAlgorithm == HashAlgorithmName.SHA512)
        {
            return SHA512.HashData(data);
        }
        throw new InvalidOperationException($"{Name} 配了未知的哈希 {HashAlgorithm.Name}。");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _kem.Dispose();
        _classical.Dispose();
    }

    /// <summary>把几种 KEM 的差异收进一个接口，让上面的混合逻辑只写一遍。</summary>
    internal interface IHybridKem : IDisposable
    {
        int CiphertextBytes { get; }

        byte[] GenerateKeyPairAndGetPublicKey();

        byte[] Decapsulate(ReadOnlySpan<byte> ciphertext);
    }

    /// <summary>经典的那一半：X25519，或者 NIST 曲线的 ECDH。</summary>
    internal interface IClassicalPart : IDisposable
    {
        /// <summary>服务端经典公钥的字节数。</summary>
        int PublicValueBytes { get; }

        /// <summary>本端的经典公钥。</summary>
        byte[] CreatePublicValue();

        /// <summary>验过对端的经典公钥、算出<b>定长</b>的经典共享秘密；不合格抛 <see cref="SshKeyExchangeException"/>。</summary>
        byte[] ComputeSecret(ReadOnlySpan<byte> peerPublicValue);
    }

    /// <summary>X25519：公钥 32 字节，结果 32 字节（本来就定长）。</summary>
    private sealed class X25519Part(string name) : IClassicalPart
    {
        private const int KeyBytes = 32;

        private readonly X25519PrivateKeyParameters _private = new(new SecureRandom());

        public int PublicValueBytes => KeyBytes;

        public byte[] CreatePublicValue() => _private.GeneratePublicKey().GetEncoded();

        public byte[] ComputeSecret(ReadOnlySpan<byte> peerPublicValue)
        {
            byte[] secret = new byte[KeyBytes];
            try
            {
                X25519Agreement agreement = new();
                agreement.Init(_private);
                agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicValue.ToArray()), secret, 0);
            }
            catch (Exception ex) when (ex is not SshKeyExchangeException)
            {
                throw new SshKeyExchangeException($"{name} 的 X25519 部分协商失败。", ex);
            }

            // RFC 7748 §6.1：X25519 结果全零意味着对端给了低阶点。
            // 混合方案下这一条仍然要查 —— 不能因为「反正还有 KEM 撑着」就放过它。
            Span<byte> zero = stackalloc byte[KeyBytes];
            if (CryptographicOperations.FixedTimeEquals(secret, zero))
            {
                CryptographicOperations.ZeroMemory(secret);
                throw new SshKeyExchangeException($"{name}：X25519 协商结果为全零，服务端提供了低阶点。");
            }
            return secret;
        }

        public void Dispose()
        {
            // BouncyCastle 的私钥参数没有可释放的东西；交换对象一用完就丢。
        }
    }

    /// <summary>
    /// NIST 曲线的 ECDH（P-256 / P-384）：点的编码与曲线校验照 §3.3 原样复用 <see cref="EcdhKeyExchange"/>，
    /// 共享秘密是<b>定长</b>的 X 坐标（spec/03 §3.7.2）。
    /// </summary>
    private sealed class NistCurvePart(string name, string ecdhName) : IClassicalPart
    {
        private readonly EcdhKeyExchange _ecdh = new(ecdhName);

        public int PublicValueBytes => 1 + (2 * _ecdh.CoordinateBytes);

        public byte[] CreatePublicValue() => _ecdh.CreateClientPublicValue();

        public byte[] ComputeSecret(ReadOnlySpan<byte> peerPublicValue)
        {
            byte[] x;
            try
            {
                x = _ecdh.ComputeSharedSecret(peerPublicValue);
            }
            catch (SshKeyExchangeException ex)
            {
                throw new SshKeyExchangeException($"{name}：{ex.Message}", ex);
            }
            return ToFixedLength(x, _ecdh.CoordinateBytes);
        }

        /// <summary>
        /// 〔spec/03 §3.7.2〕X 坐标按坐标长度<b>右对齐、左边补零</b>：前导零保留，否则只在 X 的首字节为 0 时
        /// （约 1/256 的交换）算出不同的 <c>K</c>，表现为签名验证失败。去掉前导零之后仍比坐标长，是本库的错，不是对端的错。
        /// </summary>
        private static byte[] ToFixedLength(byte[] x, int length)
        {
            if (x.Length == length)
            {
                return x;
            }

            int start = 0;
            while (x.Length - start > length && x[start] == 0)
            {
                start++;
            }
            if (x.Length - start > length)
            {
                CryptographicOperations.ZeroMemory(x);
                throw new InvalidOperationException($"平台交回的 ECDH 共享 X 坐标有 {x.Length} 字节，比曲线的坐标（{length} 字节）还长。");
            }

            byte[] padded = new byte[length];
            x.AsSpan(start).CopyTo(padded.AsSpan(length - (x.Length - start)));
            CryptographicOperations.ZeroMemory(x);
            return padded;
        }

        public void Dispose() => _ecdh.Dispose();
    }

    /// <summary>ML-KEM（768 / 1024）走 BCL（<see cref="MLKem"/>，平台支持时）。</summary>
    internal sealed class BclMlKem(MLKemAlgorithm algorithm) : IHybridKem
    {
        private MLKem? _key;

        /// <summary>密文长度（FIPS 203 表 3：768 是 1088，1024 是 1568）。</summary>
        public int CiphertextBytes => algorithm.CiphertextSizeInBytes;

        public byte[] GenerateKeyPairAndGetPublicKey()
        {
            _key?.Dispose();
            _key = MLKem.GenerateKey(algorithm);
            return _key.ExportEncapsulationKey();
        }

        public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext)
        {
            if (_key is null)
            {
                throw new InvalidOperationException("必须先生成密钥对。");
            }

            byte[] secret = new byte[algorithm.SharedSecretSizeInBytes];
            _key.Decapsulate(ciphertext, secret);
            return secret;
        }

        public void Dispose()
        {
            _key?.Dispose();
            _key = null;
        }
    }

    /// <summary>ML-KEM（768 / 1024）走 BouncyCastle（平台不支持 <see cref="MLKem"/> 时）。</summary>
    internal sealed class BouncyCastleMlKem(MLKemParameters parameters) : IHybridKem
    {
        private MLKemPrivateKeyParameters? _private;

        /// <summary>密文长度（FIPS 203 表 3）。</summary>
        public int CiphertextBytes { get; } = parameters == MLKemParameters.ml_kem_1024 ? 1568 : 1088;

        public byte[] GenerateKeyPairAndGetPublicKey()
        {
            MLKemKeyPairGenerator generator = new();
            generator.Init(new MLKemKeyGenerationParameters(new SecureRandom(), parameters));
            AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
            _private = (MLKemPrivateKeyParameters)pair.Private;
            return ((MLKemPublicKeyParameters)pair.Public).GetEncoded();
        }

        public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext)
        {
            if (_private is null)
            {
                throw new InvalidOperationException("必须先生成密钥对。");
            }

            MLKemDecapsulator decapsulator = new(parameters);
            decapsulator.Init(_private);
            byte[] secret = new byte[decapsulator.SecretLength];
            decapsulator.Decapsulate(ciphertext.ToArray(), 0, ciphertext.Length, secret, 0, secret.Length);
            return secret;
        }

        public void Dispose() => _private = null;
    }

    private sealed class SNtruPrime761Kem : IHybridKem
    {
        private SNtruPrimePrivateKeyParameters? _private;

        /// <summary>sntrup761 的密文长度。</summary>
        public int CiphertextBytes => 1039;

        public byte[] GenerateKeyPairAndGetPublicKey()
        {
            SNtruPrimeKeyPairGenerator generator = new();
            generator.Init(new SNtruPrimeKeyGenerationParameters(
                new SecureRandom(), SNtruPrimeParameters.sntrup761));
            AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
            _private = (SNtruPrimePrivateKeyParameters)pair.Private;
            return ((SNtruPrimePublicKeyParameters)pair.Public).GetEncoded();
        }

        public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext)
        {
            if (_private is null)
            {
                throw new InvalidOperationException("必须先生成密钥对。");
            }

            SNtruPrimeKemExtractor extractor = new(_private);
            return extractor.ExtractSecret(ciphertext.ToArray());
        }

        public void Dispose() => _private = null;
    }
}
