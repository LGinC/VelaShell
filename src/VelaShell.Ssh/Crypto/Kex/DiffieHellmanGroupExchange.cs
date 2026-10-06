// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4419 §3    diffie-hellman-group-exchange:群由服务端按客户端的 min / n / max 现给
//   RFC 4419 §4.2  diffie-hellman-group-exchange-sha256
//   RFC 8270       最小模数提到 2048 位
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §3.5、§4.2
//
// 模幂与素性检验全部走 BouncyCastle(Primes.IsMRProbablePrime)—— 不自己写 Miller-Rabin(AGENTS 3.3)。

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using BcBigInteger = Org.BouncyCastle.Math.BigInteger;

namespace VelaShell.Ssh.Crypto.Kex;

/// <summary><c>diffie-hellman-group-exchange-sha256</c>：群（<c>p</c>、<c>g</c>）由服务端现给的有限域 DH。</summary>
/// <remarks>
/// <para>
/// 比别的方法多一轮：先发 <c>GEX_REQUEST</c>（34）说要多大的群，服务端回 <c>GEX_GROUP</c>（31），
/// 之后才是 <c>GEX_INIT</c>（32）/ <c>GEX_REPLY</c>（33）。一些老设备与加固过的服务端只开这一种。
/// </para>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/03 §3.5〕请求 <c>min = 2048</c>、<c>n = 3072</c>、<c>max = 8192</c>；
/// 服务端给的 <c>p</c> 位数不在 [min, max] 内一律不接（logjam 之后 1024 位不可接受，太大的群是给我们做 CPU 拒绝服务）。
/// <c>g</c> 必须满足 <c>1 &lt; g &lt; p-1</c>，<c>p</c> 必须是素数。
/// </para>
/// <para>
/// 素性检验是这一种方法真正的成本：Miller-Rabin 64 轮（随机底，合数蒙混过关的概率 ≤ 4⁻⁶⁴），
/// 单线程在 3072 位上约 2 秒、8192 位上半分钟以上。三件事把它压下来：
/// 各轮彼此独立，按核数并行跑；收到群就在后台起跑，与发 <c>GEX_INIT</c>、等应答重叠；
/// 检验过的 <c>p</c> 按 SHA-256 记在进程里，服务端复用同一个群时不再检验。
/// </para>
/// </remarks>
internal sealed class DiffieHellmanGroupExchange : ISshKeyExchange, ISshGroupExchange
{
    /// <summary>请求的最小位数（RFC 8270）。</summary>
    public const uint RequestMinimumBits = 2048;

    /// <summary>请求的首选位数。</summary>
    public const uint RequestPreferredBits = 3072;

    /// <summary>请求的最大位数。</summary>
    public const uint RequestMaximumBits = 8192;

    /// <summary>Miller-Rabin 的轮数（spec/03 §3.5：≥ 64）。</summary>
    internal const int PrimalityRounds = 64;

    /// <summary>私指数的位数：2 × 哈希输出长度（与 DH 标准群同一条决策，spec/03 §3.4）。</summary>
    private const int PrivateExponentBits = 512;

    /// <summary>检验过的素数（按 SHA-256(p) 的十六进制）。只记通过的；满了整个清掉，不做淘汰。</summary>
    private static readonly ConcurrentDictionary<string, byte> VerifiedPrimes = new(StringComparer.Ordinal);

    private const int MaxVerifiedPrimes = 64;

    private DHParameters? _parameters;
    private AsymmetricCipherKeyPair? _keyPair;
    private Task? _primality;
    private bool _disposed;

    /// <summary>按算法名创建一次群交换。</summary>
    public DiffieHellmanGroupExchange(string name)
    {
        if (name != SshAlgorithmNames.DiffieHellmanGroupExchangeSha256)
        {
            throw new ArgumentException($"不是已知的群交换算法名：{name}", nameof(name));
        }
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public HashAlgorithmName HashAlgorithm => HashAlgorithmName.SHA256;

    /// <inheritdoc />
    public SshKexValueEncoding PublicValueEncoding => SshKexValueEncoding.Mpint;

    /// <inheritdoc />
    public SshKexValueEncoding SharedSecretEncoding => SshKexValueEncoding.Mpint;

    /// <inheritdoc />
    public uint MinimumBits => RequestMinimumBits;

    /// <inheritdoc />
    public uint PreferredBits => RequestPreferredBits;

    /// <inheritdoc />
    public uint MaximumBits => RequestMaximumBits;

    /// <inheritdoc />
    public byte[] Prime { get; private set; } = [];

    /// <inheritdoc />
    public byte[] Generator { get; private set; } = [];

    /// <inheritdoc />
    public void AcceptGroup(ReadOnlySpan<byte> prime, ReadOnlySpan<byte> generator)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_parameters is not null)
        {
            throw new InvalidOperationException("群已经收过了。");
        }

        BcBigInteger p = new(1, prime.ToArray());
        BcBigInteger g = new(1, generator.ToArray());

        // 便宜的先查：位数、奇偶、g 的范围。都过了才值得花素性检验的时间。
        int bits = p.BitLength;
        if (bits < MinimumBits || bits > MaximumBits)
        {
            throw new SshConnectException(
                SshFailureReason.NegotiationFailed, SshPhase.KeyExchange,
                $"{Name}：服务端给的群是 {bits} 位，不在请求的 [{MinimumBits}, {MaximumBits}] 之内" +
                (bits < MinimumBits ? "（2048 位以下的群不安全，logjam / RFC 8270）。" : "。"));
        }
        if (!p.TestBit(0))
        {
            throw new SshKeyExchangeException($"{Name}：服务端给的 p 是偶数，不是素数。");
        }
        BcBigInteger pMinusOne = p.Subtract(BcBigInteger.One);
        if (g.CompareTo(BcBigInteger.One) <= 0 || g.CompareTo(pMinusOne) >= 0)
        {
            throw new SshKeyExchangeException($"{Name}：服务端给的生成元越界，必须满足 1 < g < p-1。");
        }

        _parameters = new DHParameters(p, g, null, PrivateExponentBits);
        Prime = p.ToByteArrayUnsigned();
        Generator = g.ToByteArrayUnsigned();

        string key = Convert.ToHexString(SHA256.HashData(Prime));
        if (VerifiedPrimes.ContainsKey(key))
        {
            _primality = Task.CompletedTask;
            return;
        }

        _primality = Task.Run(() => VerifyPrime(p, key));

        // 交换在别处先失败了（应答格式不对、签名没过）就没人等它：看一眼，不让「不是素数」变成未观察的任务异常。
        _primality.ContinueWith(
            static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <inheritdoc />
    public async Task EnsureGroupValidAsync(CancellationToken cancellationToken)
    {
        Task primality = _primality ?? throw new InvalidOperationException("还没收到群。");
        await primality.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Miller-Rabin，各轮按核数并行（每轮一个随机底，彼此独立）。</summary>
    private void VerifyPrime(BcBigInteger p, string key)
    {
        if (Primes.HasAnySmallFactors(p))
        {
            throw new SshKeyExchangeException($"{Name}：服务端给的 p 有小因子，不是素数。");
        }

        int witnesses = 0;
        Parallel.For(
            0, PrimalityRounds,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new SecureRandom(),
            (_, state, random) =>
            {
                if (!Primes.IsMRProbablePrime(p, random, 1))
                {
                    Interlocked.Increment(ref witnesses);
                    state.Stop();
                }
                return random;
            },
            _ => { });

        if (witnesses > 0)
        {
            throw new SshKeyExchangeException($"{Name}：服务端给的 p 不是素数（Miller-Rabin 找到了合数的证据）。");
        }

        if (VerifiedPrimes.Count >= MaxVerifiedPrimes)
        {
            VerifiedPrimes.Clear();
        }
        VerifiedPrimes.TryAdd(key, 0);
    }

    /// <inheritdoc />
    public byte[] CreateClientPublicValue()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DHParameters parameters = _parameters ?? throw new InvalidOperationException("还没收到群（GEX_GROUP）。");

        DHKeyPairGenerator generator = new();
        generator.Init(new DHKeyGenerationParameters(new SecureRandom(), parameters));
        _keyPair = generator.GenerateKeyPair();
        return ((DHPublicKeyParameters)_keyPair.Public).Y.ToByteArrayUnsigned();
    }

    /// <inheritdoc />
    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> serverPublicValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DHParameters parameters = _parameters ?? throw new InvalidOperationException("还没收到群（GEX_GROUP）。");
        AsymmetricCipherKeyPair keyPair = _keyPair ?? throw new InvalidOperationException("还没产出客户端公开值。");

        // **必须校验 1 < f < p-1**（与 DH 标准群同一条，小子群攻击）。
        BcBigInteger f = new(1, serverPublicValue.ToArray());
        BcBigInteger pMinusOne = parameters.P.Subtract(BcBigInteger.One);
        if (f.CompareTo(BcBigInteger.One) <= 0 || f.CompareTo(pMinusOne) >= 0)
        {
            throw new SshKeyExchangeException($"{Name} 的服务端公钥越界：必须满足 1 < f < p-1。");
        }

        try
        {
            DHBasicAgreement agreement = new();
            agreement.Init(keyPair.Private);
            return agreement.CalculateAgreement(new DHPublicKeyParameters(f, parameters)).ToByteArrayUnsigned();
        }
        catch (Exception ex) when (ex is not SshKeyExchangeException)
        {
            throw new SshKeyExchangeException($"{Name} 协商失败。", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;
}
