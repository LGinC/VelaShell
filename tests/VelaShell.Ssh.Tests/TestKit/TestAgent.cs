// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 一个在内存里说 agent 协议的服务端,以及一个「假装是远端」的 agent 客户端。
//
// ⚠️ **只为测试存在,绝不发布。**

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>测试 agent 收到会话声明（<c>session-bind@openssh.com</c>）时怎么回。</summary>
internal enum TestDeclarationReply
{
    /// <summary>回 SUCCESS。</summary>
    Accept,

    /// <summary>回 FAILURE —— 不支持这个扩展的 agent 就这么回。</summary>
    Reject,

    /// <summary>不回、直接断开这条连接 —— 个别 agent 收到不认识的报文就这么干。</summary>
    Disconnect,
}

/// <summary>测试 agent 收到的一条会话声明。</summary>
/// <param name="Connection">在第几条 agent 连接上收到的（从 1 起）。</param>
/// <param name="HostKeyBlob">声明里的主机公钥。</param>
/// <param name="SessionId">声明里的会话标识。</param>
/// <param name="IsForwarding">声明里的 <c>is_forwarding</c>。</param>
/// <param name="SignatureVerified">声明里的签名能不能用主机公钥对会话标识验过 —— 真 agent 会这么验。</param>
internal sealed record TestSessionDeclaration(
    int Connection, byte[] HostKeyBlob, byte[] SessionId, bool IsForwarding, bool SignatureVerified);

/// <summary>在一条流上说 agent 协议的测试 agent。</summary>
internal sealed class TestAgent
{
    private const int MaxMessage = 256 * 1024;

    private readonly List<TestSessionDeclaration> _declarations = [];
    private int _connections;

    private readonly List<(InMemorySshSigner Signer, string Comment)> _keys = [];

    // 只列不签的身份：证书、FIDO、DSA 这类库不认识的 blob。列表里排在可签的钥之前 ——
    // 模拟真实 agent 里「先加了证书、后加了普通钥」的顺序。
    private readonly List<(byte[] Blob, string Comment)> _opaque = [];

    // 证书身份：列出的是证书 blob，签名由证书里那把钥来做 —— 与 ssh-add 加进 id_*-cert.pub 之后一样。
    private readonly List<(ISshSigner Signer, byte[] Blob, string Comment)> _certificates = [];

    /// <summary>收到的签名请求次数。</summary>
    public int SignRequests { get; private set; }

    /// <summary>收到的列身份请求次数。</summary>
    public int ListRequests { get; private set; }

    /// <summary>收到的加钥请求次数（17 与 25 合计）。</summary>
    public int AddRequests { get; private set; }

    /// <summary>为 <see langword="true"/> 时加钥一律回 <see cref="RejectionCode"/>（模拟被锁定或不支持的 agent）。</summary>
    public bool RejectAdditions { get; set; }

    /// <summary>拒绝加钥时回的报文号：默认 <c>5</c> FAILURE，也可以设成 <c>28</c> EXTENSION_FAILURE。</summary>
    public byte RejectionCode { get; set; } = 5;

    /// <summary>最近一次成功加钥带的扩展约束（扩展名、扩展自己的内容），按报文里的顺序。</summary>
    public IReadOnlyList<(string Name, byte[] Payload)> LastConstraintExtensions { get; private set; } = [];

    /// <summary>最近一次成功加钥时注释之后的全部字节（整段约束）。</summary>
    public byte[] LastConstraintBytes { get; private set; } = [];

    /// <summary>最近一次成功加钥用的报文号。</summary>
    public byte? LastAddMessageType { get; private set; }

    /// <summary>最近一次成功加钥带的约束编号，按报文里的顺序。</summary>
    public IReadOnlyList<byte> LastConstraints { get; private set; } = [];

    /// <summary>最近一次带的有效期约束（秒）。</summary>
    public uint? LastLifetimeSeconds { get; private set; }

    /// <summary>收到会话声明时怎么回。</summary>
    public TestDeclarationReply DeclarationReply { get; set; } = TestDeclarationReply.Accept;

    /// <summary>设了就把每一条应答扣到它完成再发 —— 模拟「应答迟到」。</summary>
    public Task? HoldRepliesUntil { get; set; }

    /// <summary>收到的会话声明，按到达顺序。</summary>
    public IReadOnlyList<TestSessionDeclaration> Declarations
    {
        get
        {
            lock (_declarations)
            {
                return [.. _declarations];
            }
        }
    }

    /// <summary>收到的、不是会话声明的扩展请求次数。</summary>
    public int OtherExtensionRequests { get; private set; }

    /// <summary>服务过几条连接。</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>agent 里现有的密钥与注释。</summary>
    public IReadOnlyList<(SshPublicKey Key, string Comment)> Keys =>
        [.. _keys.Select(k => (k.Signer.PublicKey, k.Comment))];

    /// <summary>加一把密钥。</summary>
    public SshPublicKey Add(InMemorySshSigner signer, string comment)
    {
        _keys.Add((signer, comment));
        return signer.PublicKey;
    }

    /// <summary>加一条只出现在列表里的身份（原样的公钥 blob）。</summary>
    public void AddOpaque(byte[] blob, string comment) => _opaque.Add((blob, comment));

    /// <summary>加一张证书：列出证书 blob，签名请求拿证书 blob 来时用 <paramref name="signer"/> 签。</summary>
    public void AddCertificate(ISshSigner signer, byte[] certificateBlob, string comment) =>
        _certificates.Add((signer, certificateBlob, comment));

    /// <summary>在一条流上服务，直到对端关闭。</summary>
    public async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        int connection = Interlocked.Increment(ref _connections);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, cancellationToken);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

                if (length is 0 or > MaxMessage)
                {
                    return;
                }

                byte[] request = new byte[length];
                await stream.ReadExactlyAsync(request, cancellationToken);

                byte[]? response = request is [27, ..]
                    ? HandleExtension(request, connection)
                    : await HandleAsync(request, cancellationToken);

                if (response is null)
                {
                    // 模拟「收到不认识的报文就断开」的 agent。
                    await stream.DisposeAsync();
                    return;
                }

                if (HoldRepliesUntil is { } hold)
                {
                    await hold.WaitAsync(cancellationToken);
                }

                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)response.Length);
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(response, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        catch (EndOfStreamException)
        {
            // 对端走了。
        }
        catch (OperationCanceledException)
        {
            // 测试收尾。
        }
        catch (IOException)
        {
            // 同上。
        }
    }

    /// <summary>
    /// <c>SSH_AGENTC_EXTENSION</c>：只认 <c>session-bind@openssh.com</c>，按 <see cref="DeclarationReply"/> 回；
    /// 返回 <see langword="null"/> 表示断开连接。
    /// </summary>
    private byte[]? HandleExtension(byte[] request, int connection)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(request));
        reader.ReadByte();
        string name = reader.ReadUtf8String(MaxMessage);

        if (name != "session-bind@openssh.com")
        {
            OtherExtensionRequests++;
            return [5];
        }

        byte[] hostKey = reader.ReadStringAsArray(MaxMessage);
        byte[] sessionId = reader.ReadStringAsArray(MaxMessage);
        byte[] signature = reader.ReadStringAsArray(MaxMessage);
        bool isForwarding = reader.ReadBoolean();

        // 与真 agent 一样：用声明里的主机公钥验它对会话标识的签名。
        bool verified;
        try
        {
            SshDataReader signatureReader = new(new ReadOnlySequence<byte>(signature));
            string algorithm = signatureReader.ReadUtf8String(MaxMessage);
            verified = SshPublicKey.Decode(hostKey).VerifySignature(signature, sessionId, algorithm);
        }
        catch (Exception)
        {
            verified = false;
        }

        lock (_declarations)
        {
            _declarations.Add(new TestSessionDeclaration(connection, hostKey, sessionId, isForwarding, verified));
        }

        return DeclarationReply switch
        {
            TestDeclarationReply.Accept => [6],
            TestDeclarationReply.Reject => [5],
            _ => null,
        };
    }

    /// <summary>锁着时的口令；<see langword="null"/> 是没锁。</summary>
    public string? LockPassphrase { get; private set; }

    private async Task<byte[]> HandleAsync(byte[] request, CancellationToken cancellationToken)
    {
        if (request.Length == 0)
        {
            return [5];   // FAILURE
        }

        if (request[0] is 22 or 23)   // LOCK / UNLOCK
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(request));
            reader.ReadByte();
            string passphrase = reader.ReadUtf8String(MaxMessage);
            if (request[0] == 22)
            {
                if (LockPassphrase is not null)
                {
                    return [5];
                }
                LockPassphrase = passphrase;
                return [6];
            }
            if (LockPassphrase is null || LockPassphrase != passphrase)
            {
                return [5];
            }
            LockPassphrase = null;
            return [6];
        }

        // 锁着的时候别的一律拒绝。
        if (LockPassphrase is not null)
        {
            return [5];
        }

        if (request[0] == 18)   // REMOVE_IDENTITY
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(request));
            reader.ReadByte();
            byte[] blob = reader.ReadStringAsArray(MaxMessage);
            int removed = _keys.RemoveAll(k => k.Signer.PublicKey.Blob.Span.SequenceEqual(blob))
                + _certificates.RemoveAll(c => c.Blob.AsSpan().SequenceEqual(blob))
                + _opaque.RemoveAll(o => o.Blob.AsSpan().SequenceEqual(blob));
            return removed > 0 ? [6] : [5];
        }

        if (request[0] == 19)   // REMOVE_ALL_IDENTITIES
        {
            _keys.Clear();
            _certificates.Clear();
            _opaque.Clear();
            return [6];
        }

        if (request[0] == 11)   // REQUEST_IDENTITIES
        {
            ListRequests++;

            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter writer = new(buffer);
            writer.WriteByte(12);   // IDENTITIES_ANSWER
            writer.WriteUInt32((uint)(_opaque.Count + _certificates.Count + _keys.Count));

            foreach ((byte[] blob, string comment) in _opaque)
            {
                writer.WriteString(blob);
                writer.WriteUtf8String(comment);
            }

            foreach ((_, byte[] blob, string comment) in _certificates)
            {
                writer.WriteString(blob);
                writer.WriteUtf8String(comment);
            }

            foreach ((InMemorySshSigner signer, string comment) in _keys)
            {
                writer.WriteString(signer.PublicKey.Blob.Span);
                writer.WriteUtf8String(comment);
            }

            return buffer.WrittenSpan.ToArray();
        }

        if (request[0] == 13)   // SIGN_REQUEST
        {
            SignRequests++;

            SshDataReader reader = new(new ReadOnlySequence<byte>(request));
            reader.ReadByte();
            byte[] keyBlob = reader.ReadStringAsArray(MaxMessage);
            byte[] data = reader.ReadStringAsArray(MaxMessage);
            uint flags = reader.ReadUInt32();

            // RSA 按标志位选哈希；没有标志位就是 SHA-1 的 ssh-rsa（draft-miller-ssh-agent）。
            string AlgorithmFor(SshPublicKey key) => key.PlainKeyType == SshAlgorithmNames.SshRsa
                ? (flags & 0x04) != 0
                    ? SshAlgorithmNames.RsaSha512
                    : (flags & 0x02) != 0
                        ? SshAlgorithmNames.RsaSha256
                        : SshAlgorithmNames.SshRsa
                : key.PlainKeyType;

            IEnumerable<(ISshSigner Signer, byte[] Blob)> candidates =
                [.. _certificates.Select(c => (c.Signer, c.Blob)), .. _keys.Select(k => ((ISshSigner)k.Signer, k.Signer.PublicKey.Blob.ToArray()))];

            foreach ((ISshSigner signer, byte[] blob) in candidates)
            {
                if (!blob.AsSpan().SequenceEqual(keyBlob))
                {
                    continue;
                }

                byte[] signature = await signer.SignAsync(data, AlgorithmFor(signer.PublicKey), cancellationToken);

                ArrayBufferWriter<byte> buffer = new();
                SshDataWriter writer = new(buffer);
                writer.WriteByte(14);   // SIGN_RESPONSE
                writer.WriteString(signature);
                return buffer.WrittenSpan.ToArray();
            }

            return [5];   // 没有这把钥
        }

        if (request[0] is 17 or 25)   // ADD_IDENTITY / ADD_ID_CONSTRAINED
        {
            AddRequests++;
            if (RejectAdditions)
            {
                return [RejectionCode];
            }

            SshDataReader reader = new(new ReadOnlySequence<byte>(request));
            reader.ReadByte();
            InMemorySshSigner added = ReadPrivateKey(ref reader);
            string comment = reader.ReadUtf8String(MaxMessage);
            byte[] constraintBytes = request[(int)reader.Consumed..];

            List<byte> constraints = [];
            List<(string, byte[])> extensions = [];
            while (!reader.IsEmpty)
            {
                byte constraint = reader.ReadByte();
                constraints.Add(constraint);
                if (constraint == 1)
                {
                    LastLifetimeSeconds = reader.ReadUInt32();
                }
                else if (constraint == 255)   // 扩展约束：string 扩展名 + string 内容
                {
                    string name = reader.ReadUtf8String(MaxMessage);
                    if (name != "restrict-destination-v00@openssh.com")
                    {
                        return [5];   // 不认识的扩展：整条拒绝
                    }
                    extensions.Add((name, reader.ReadStringAsArray(MaxMessage)));
                }
                else if (constraint != 2)
                {
                    return [5];   // 不认识的约束：整条拒绝
                }
            }

            LastAddMessageType = request[0];
            LastConstraints = constraints;
            LastConstraintExtensions = extensions;
            LastConstraintBytes = constraintBytes;

            // 同一把钥再加一次：更新注释，不重复登记。
            _keys.RemoveAll(k => k.Signer.PublicKey.Blob.Span.SequenceEqual(added.PublicKey.Blob.Span));
            _keys.Add((added, comment));
            return [6];   // SUCCESS
        }

        // 别的一律拒绝。
        return [5];
    }

    /// <summary>按 spec/07 §7.3 的「私钥内容」表把一把私钥读回来。</summary>
    private static InMemorySshSigner ReadPrivateKey(ref SshDataReader reader)
    {
        string type = reader.ReadUtf8String(MaxMessage);

        if (type == SshAlgorithmNames.SshEd25519)
        {
            byte[] publicKey = reader.ReadStringAsArray(MaxMessage);
            byte[] secret = reader.ReadStringAsArray(MaxMessage);
            if (publicKey.Length != 32 || secret.Length != 64 || !secret.AsSpan(32).SequenceEqual(publicKey))
            {
                throw new InvalidDataException("ed25519 私钥内容的布局不对。");
            }
            return InMemorySshSigner.FromEd25519(secret.AsSpan(0, 32));
        }

        if (type == SshAlgorithmNames.SshRsa)
        {
            byte[] n = reader.ReadMpint(MaxMessage).ToArray();
            byte[] e = reader.ReadMpint(MaxMessage).ToArray();
            byte[] d = reader.ReadMpint(MaxMessage).ToArray();
            byte[] iqmp = reader.ReadMpint(MaxMessage).ToArray();
            byte[] p = reader.ReadMpint(MaxMessage).ToArray();
            byte[] q = reader.ReadMpint(MaxMessage).ToArray();

            BigInteger dValue = new(d, isUnsigned: true, isBigEndian: true);
            BigInteger pValue = new(p, isUnsigned: true, isBigEndian: true);
            BigInteger qValue = new(q, isUnsigned: true, isBigEndian: true);
            int half = (n.Length + 1) / 2;

            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = n,
                Exponent = e,
                D = Pad(d, n.Length),
                P = Pad(p, half),
                Q = Pad(q, half),
                DP = Pad((dValue % (pValue - 1)).ToByteArray(isUnsigned: true, isBigEndian: true), half),
                DQ = Pad((dValue % (qValue - 1)).ToByteArray(isUnsigned: true, isBigEndian: true), half),
                InverseQ = Pad(iqmp, half),
            });
            return InMemorySshSigner.FromRsa(rsa);
        }

        string curveName = reader.ReadUtf8String(MaxMessage);
        byte[] point = reader.ReadStringAsArray(MaxMessage);
        byte[] scalar = reader.ReadMpint(MaxMessage).ToArray();
        (ECCurve curve, int size) = curveName switch
        {
            "nistp256" => (ECCurve.NamedCurves.nistP256, 32),
            "nistp384" => (ECCurve.NamedCurves.nistP384, 48),
            "nistp521" => (ECCurve.NamedCurves.nistP521, 66),
            _ => throw new InvalidDataException($"不认识的曲线 {curveName}。"),
        };
        if (type != "ecdsa-sha2-" + curveName)
        {
            throw new InvalidDataException($"密钥类型 {type} 与曲线 {curveName} 对不上。");
        }

        var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(new ECParameters
        {
            Curve = curve,
            Q = new ECPoint { X = point[1..(1 + size)], Y = point[(1 + size)..] },
            D = Pad(scalar, size),
        });
        return InMemorySshSigner.FromEcdsa(ecdsa);
    }

    private static byte[] Pad(byte[] value, int length)
    {
        if (value.Length >= length)
        {
            return value;
        }
        byte[] padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }
}

/// <summary>假装自己是远端主机上的 <c>ssh-add -l</c> / 签名调用方。</summary>
/// <remarks>
/// 它说的是 agent 协议，但字节从一条 SSH 通道上走 ——
/// 也就是 <c>auth-agent@openssh.com</c> 那条通道的服务端一侧。
/// </remarks>
internal static class TestRemoteAgentClient
{
    private const int MaxMessage = 256 * 1024;

    /// <summary>发一条 agent 请求并读回应答。</summary>
    public static async Task<byte[]> ExchangeAsync(
        Stream stream, byte[] request, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)request.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        await stream.ReadExactlyAsync(header, cancellationToken);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);

        byte[] response = new byte[length];
        await stream.ReadExactlyAsync(response, cancellationToken);
        return response;
    }

    /// <summary>列出远端能看到的密钥。</summary>
    public static async Task<IReadOnlyList<(SshPublicKey Key, string Comment)>> ListAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        byte[] response = await ExchangeAsync(stream, [11], cancellationToken);

        SshDataReader reader = new(new ReadOnlySequence<byte>(response));
        byte type = reader.ReadByte();

        if (type != 12)
        {
            return [];
        }

        uint count = reader.ReadUInt32();
        List<(SshPublicKey, string)> keys = [];

        for (uint i = 0; i < count; i++)
        {
            byte[] blob = reader.ReadStringAsArray(MaxMessage);
            string comment = reader.ReadUtf8String(MaxMessage);
            keys.Add((SshPublicKey.Decode(blob), comment));
        }

        return keys;
    }

    /// <summary>请远端的 agent 签一段数据。</summary>
    /// <returns>签名 blob；被拒时为 <see langword="null"/>。</returns>
    public static async Task<byte[]?> SignAsync(
        Stream stream, SshPublicKey key, byte[] data, uint flags, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteByte(13);
        writer.WriteString(key.Blob.Span);
        writer.WriteString(data);
        writer.WriteUInt32(flags);

        byte[] response = await ExchangeAsync(stream, buffer.WrittenSpan.ToArray(), cancellationToken);

        SshDataReader reader = new(new ReadOnlySequence<byte>(response));
        return reader.ReadByte() == 14 ? reader.ReadStringAsArray(MaxMessage) : null;
    }
}
