// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测:SshAgentClient 列身份(draft-miller-ssh-agent 的 REQUEST_IDENTITIES / IDENTITIES_ANSWER)
//
// 真实 agent 里常有库不认识的身份:FIDO 钥是 sk-*,老机器上还有 ssh-dss,证书也可能坏掉或是不支持的类型。
// 其中任何一把都不该让整个列表失败 —— 列表失败意味着 agent 认证、agent 转发、自动加钥三条路一起断。
// ssh-add 会顺手把 id_*-cert.pub 证书一起加进去:那些证书现在认得,作为证书身份列出来。

using System.Buffers;
using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentListIdentitiesTests
{
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Task _serving;

        public Rig()
        {
            (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
            _serving = Task.Run(() => Agent.ServeAsync(theirs, _cts.Token));
            Client = SshAgentClient.FromStream(ours, "(测试 agent)");
        }

        public TestAgent Agent { get; } = new();

        public SshAgentClient Client { get; }

        public CancellationToken Token => _cts.Token;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _cts.CancelAsync();
            await _serving;
            _cts.Dispose();
        }
    }

    [TestMethod]
    [DataRow(new byte[] { 12, 0, 0 }, DisplayName = "身份列表:计数只有半截")]
    [DataRow(new byte[] { 12, 0, 0, 0, 1, 0, 0, 0, 9, 1, 2 }, DisplayName = "身份列表:公钥 blob 被截断")]
    public async Task agent回了畸形的身份列表时报SshAgentException(byte[] reply)
    {
        // 曾经让解析层的 internal 异常原样漏出去：宿主只接 SshAgentException，那就一路漏到了界面上。
        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            async () => await WithScriptedReplyAsync(reply, client => client.ListIdentitiesAsync().AsTask()));

        Assert.AreEqual(Diagnostics.SshFailureReason.ProtocolError, error.Reason);
    }

    [TestMethod]
    public async Task agent回了畸形的签名应答时报SshAgentException()
    {
        byte[] reply = [14, 0, 0, 0, 50, 1];   // SIGN_RESPONSE，签名 blob 声称 50 字节、只有 1 个

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            async () => await WithScriptedReplyAsync(reply, client => client.SignAsync(new byte[] { 1 }, new byte[] { 2 }, "ssh-ed25519").AsTask()));

        Assert.AreEqual(Diagnostics.SshFailureReason.ProtocolError, error.Reason);
    }

    /// <summary>
    /// 〔spec/04 §4.3〕不认 SHA-2 标志位的老 agent 照旧回 ssh-rsa 签名：不交出去（交出去的话认证器当 rsa-sha2-512 发，
    /// 用户只看到 Permission denied，「不许 SHA-1」的意图也被绕过）。
    /// </summary>
    [TestMethod]
    public async Task agent签名的算法与请求的不一致时不交出去()
    {
        byte[] reply = SignResponse("ssh-rsa");

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            async () => await WithScriptedReplyAsync(reply, client => client.SignAsync(new byte[] { 1 }, new byte[] { 2 }, "rsa-sha2-512").AsTask()));

        Assert.AreEqual(Diagnostics.SshFailureReason.Unsupported, error.Reason);
        Assert.Contains("ssh-rsa", error.Message);
    }

    /// <summary>证书的签名算法名不带证书后缀：请求 rsa-sha2-512-cert-v01 时回 rsa-sha2-512 是对的。</summary>
    [TestMethod]
    public async Task agent签名的算法与请求一致时照常交出()
    {
        byte[] reply = SignResponse("rsa-sha2-512");
        byte[]? signature = null;

        await WithScriptedReplyAsync(reply, async client =>
            signature = await client.SignAsync(new byte[] { 1 }, new byte[] { 2 }, "rsa-sha2-512-cert-v01@openssh.com"));

        Assert.IsNotNull(signature);
    }

    /// <summary>一条 SIGN_RESPONSE：签名 blob 是 <c>string 算法名 ‖ string 签名</c>。</summary>
    private static byte[] SignResponse(string algorithm)
    {
        System.Buffers.ArrayBufferWriter<byte> blob = new();
        Ssh.Protocol.SshDataWriter blobWriter = new(blob);
        blobWriter.WriteUtf8String(algorithm);
        blobWriter.WriteString(new byte[64]);

        System.Buffers.ArrayBufferWriter<byte> reply = new();
        Ssh.Protocol.SshDataWriter writer = new(reply);
        writer.WriteByte(14);   // SSH_AGENT_SIGN_RESPONSE
        writer.WriteString(blob.WrittenSpan);
        return reply.WrittenSpan.ToArray();
    }

    /// <summary>对端读掉一条请求、回一条写好的应答（不经 TestAgent，好造畸形的）。</summary>
    private static async Task WithScriptedReplyAsync(byte[] reply, Func<SshAgentClient, Task> act)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        (InMemoryDuplexStream ours, InMemoryDuplexStream theirs) = InMemoryTransport.CreatePair();
        await using SshAgentClient client = SshAgentClient.FromStream(ours, "(脚本 agent)");

        Task answering = Task.Run(async () =>
        {
            byte[] length = new byte[4];
            await theirs.ReadExactlyAsync(length, cts.Token);
            byte[] request = new byte[System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(length)];
            await theirs.ReadExactlyAsync(request, cts.Token);

            byte[] framed = new byte[4 + reply.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)reply.Length);
            reply.CopyTo(framed, 4);
            await theirs.WriteAsync(framed, cts.Token);
            await theirs.FlushAsync(cts.Token);
        }, cts.Token);

        try
        {
            await act(client);
        }
        finally
        {
            await answering;
        }
    }

    /// <summary>造一个只有类型串对、内容随意的公钥 blob。</summary>
    internal static byte[] OpaqueBlob(string keyType)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(keyType);
        writer.WriteString(RandomNumberGenerator.GetBytes(32));
        return buffer.WrittenSpan.ToArray();
    }

    [TestMethod]
    [DataRow("ssh-ed25519-cert-v01@openssh.com")]
    [DataRow("sk-ssh-ed25519@openssh.com")]
    [DataRow("ssh-dss")]
    public async Task 不认识的身份被跳过_其余照常列出(string opaqueType)
    {
        await using Rig rig = new();
        rig.Agent.AddOpaque(OpaqueBlob(opaqueType), "排在前面的那一把");
        using var key = InMemorySshSigner.GenerateEd25519();
        rig.Agent.Add(key, "id_ed25519");

        IReadOnlyList<SshAgentIdentity> identities = await rig.Client.ListIdentitiesAsync(rig.Token);

        SshAgentIdentity only = identities.Single();
        Assert.AreSequenceEqual(key.PublicKey.Blob.ToArray(), only.PublicKey.Blob.ToArray());
        Assert.AreEqual("id_ed25519", only.Comment);
    }

    [TestMethod]
    public async Task 证书身份被列出且RSA证书按SHA2签()
    {
        await using Rig rig = new();
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures");
        ISshSigner rsa = await SshPrivateKeyFile.LoadAsync(Path.Combine(fixtures, "hostcert-rsa"), cancellationToken: rig.Token);
        byte[] certificate = Convert.FromBase64String(
            File.ReadAllText(Path.Combine(fixtures, "hostcert-rsa-cert.pub")).Split(' ')[1]);
        rig.Agent.AddCertificate(rsa, certificate, "hostcert-rsa-cert.pub");

        SshAgentIdentity only = (await rig.Client.ListIdentitiesAsync(rig.Token)).Single();
        Assert.IsTrue(only.PublicKey.IsCertificate);
        Assert.AreEqual(SshAlgorithmNames.RsaSha512CertV01, only.PublicKey.SignatureAlgorithms[0]);

        // 证书的算法名带后缀：标志位要按去掉后缀的名字定，否则 agent 会签成 SHA-1 的 ssh-rsa。
        byte[] data = "agent 里的证书签的数据"u8.ToArray();
        byte[] signature = await rig.Client.SignAsync(only.PublicKey.Blob, data, SshAlgorithmNames.RsaSha512CertV01, rig.Token);
        Assert.IsTrue(only.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.RsaSha512CertV01));
    }

    [TestMethod]
    public async Task 格式坏掉的blob也只是跳过()
    {
        await using Rig rig = new();
        rig.Agent.AddOpaque([0, 0, 0, 11, (byte)'s', (byte)'s', (byte)'h'], "截断的 blob");
        using var key = InMemorySshSigner.GenerateEd25519();
        rig.Agent.Add(key, "id_ed25519");

        IReadOnlyList<SshAgentIdentity> identities = await rig.Client.ListIdentitiesAsync(rig.Token);

        Assert.HasCount(1, identities);
    }

    [TestMethod]
    public async Task 取凭据时证书不会让整条agent认证失败()
    {
        await using Rig rig = new();
        rig.Agent.AddOpaque(OpaqueBlob("ssh-ed25519-cert-v01@openssh.com"), "id_ed25519-cert.pub");
        using var key = InMemorySshSigner.GenerateEd25519();
        rig.Agent.Add(key, "id_ed25519");

        IReadOnlyList<SshCredential> credentials = await rig.Client.GetCredentialsAsync(rig.Token);

        Assert.HasCount(1, credentials);
    }
}
