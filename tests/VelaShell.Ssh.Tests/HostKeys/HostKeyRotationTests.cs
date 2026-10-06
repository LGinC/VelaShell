// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §6.4

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.HostKeys;

/// <summary>
/// 主机密钥轮换（UpdateHostKeys）：证实了的新钥补记、证明有一把是假的就一把都不记、没打开或者当前的钥没记着就不做。
/// 签的是什么另由对真 sshd 的互通用例裁决（测试桩与实现出自同一份理解）。
/// </summary>
[TestClass]
[TestCategory("HostKeys")]
public sealed class HostKeyRotationTests
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class Rig(string knownHosts) : IAsyncDisposable
    {
        public string KnownHosts => knownHosts;

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            File.Delete(knownHosts);
        }
    }

    private async Task<SshHostKeyUpdate?> ConnectAsync(
        IHostKeyPolicy policy, TestHostKey primary, TestHostKey[] announced, bool corruptProof = false)
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];
        SshConnectionOptions options = new("joe@rotate.example:22")
        {
            Dialer = InMemoryTransport.CreateDialer((stream, _, _) =>
            {
                servers.Add(Task.Run(async () =>
                {
                    try
                    {
                        await using TestSshServer server = new(stream, new TestSshServerOptions { HostKey = primary });
                        TestSshServerHandshake handshake = await server.HandshakeAsync(lifetime.Token);
                        TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
                        await auth.RunAsync(lifetime.Token);
                        TestChannelServer channels = new(server.Transport, new TestChannelScript
                        {
                            AnnouncedHostKeys = announced,
                            HostKeySessionId = handshake.ExchangeHash,
                            CorruptHostKeyProof = corruptProof,
                        });
                        await channels.RunAsync(lifetime.Token);
                    }
                    catch (Exception)
                    {
                        // 拆场。
                    }
                }, CancellationToken.None));
                return ValueTask.CompletedTask;
            }),
            HostKeyPolicy = policy,
            Credentials = [new PasswordCredential("hunter2")],
        };

        SshHostKeyUpdate? update;
        await using (SshConnection connection = await SshConnection.ConnectAsync(options, TestContext.CancellationToken))
        {
            // 宣告在认证之后才到：给它一点时间；不做轮换的情形就是一直为 null。
            for (int i = 0; i < 100 && connection.LastHostKeyUpdate is null; i++)
            {
                await Task.Delay(10, TestContext.CancellationToken);
            }
            await connection.HostKeyRotation;
            update = connection.LastHostKeyUpdate;
        }
        await lifetime.CancelAsync();
        await Task.WhenAll(servers);
        return update;
    }

    private static Rig NewKnownHosts() => new(Path.Combine(Path.GetTempPath(), $"vela-kh-{Guid.NewGuid():N}"));

    /// <summary>头一次连接按 TOFU 记下当前那把，轮换补记另外两把（RSA 用 SHA-2 签）；再连一次没有新钥。</summary>
    [TestMethod]
    public async Task 证实了的新钥补记进known_hosts()
    {
        await using Rig rig = NewKnownHosts();
        using TestHostKey ed25519 = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        using TestHostKey ecdsa = TestHostKey.Create(SshAlgorithmNames.EcdsaSha2Nistp256);
        using TestHostKey rsa = TestHostKey.Create(SshAlgorithmNames.RsaSha512);
        KnownHostsPolicy policy = new(rig.KnownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, AllowHostKeyUpdates = true };

        SshHostKeyUpdate? first = await ConnectAsync(policy, ed25519, [ed25519, ecdsa, rsa]);
        Assert.IsNotNull(first);
        Assert.IsNull(first.Skipped, first.Skipped);
        Assert.AreSequenceEqual([ecdsa.KeyType, rsa.KeyType], [.. first.Added.Select(k => k.KeyType)]);
        Assert.HasCount(3, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken));

        SshHostKeyUpdate? second = await ConnectAsync(policy, ed25519, [ed25519, ecdsa, rsa]);
        Assert.IsNotNull(second);
        Assert.IsEmpty(second.Added);
    }

    /// <summary>证明里有一把签不过：一把都不记（同一个应答里有假的，其余的也不可信）。</summary>
    [TestMethod]
    public async Task 证明有一把是假的就一把都不记()
    {
        await using Rig rig = NewKnownHosts();
        using TestHostKey ed25519 = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        using TestHostKey ecdsa = TestHostKey.Create(SshAlgorithmNames.EcdsaSha2Nistp256);
        using TestHostKey rsa = TestHostKey.Create(SshAlgorithmNames.RsaSha512);
        KnownHostsPolicy policy = new(rig.KnownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist, AllowHostKeyUpdates = true };

        SshHostKeyUpdate? update = await ConnectAsync(policy, ed25519, [ed25519, ecdsa, rsa], corruptProof: true);

        Assert.IsNotNull(update);
        Assert.IsEmpty(update.Added);
        StringAssert.Contains(update.Skipped, "验不过");
        Assert.HasCount(1, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken), "只有 TOFU 记下的那一把");
    }

    /// <summary>没打开 AllowHostKeyUpdates：宣告来了也不理；当前的钥没有记在 known_hosts 里（不用文件的策略）：不做。</summary>
    [TestMethod]
    public async Task 没打开或者当前的钥没记着就不做()
    {
        await using Rig rig = NewKnownHosts();
        using TestHostKey ed25519 = TestHostKey.Create(SshAlgorithmNames.SshEd25519);
        using TestHostKey ecdsa = TestHostKey.Create(SshAlgorithmNames.EcdsaSha2Nistp256);

        KnownHostsPolicy off = new(rig.KnownHosts) { UnknownHost = UnknownHostBehavior.AcceptAndPersist };
        Assert.IsNull(await ConnectAsync(off, ed25519, [ed25519, ecdsa]), "没打开就一直为 null");
        Assert.HasCount(1, await File.ReadAllLinesAsync(rig.KnownHosts, TestContext.CancellationToken));

        SshHostKeyUpdate? update = await ConnectAsync(
            new RotatingWithoutRecord(), ed25519, [ed25519, ecdsa]);
        Assert.IsNotNull(update);
        Assert.IsEmpty(update.Added);
        StringAssert.Contains(update.Skipped, "没有作为普通钥记在 known_hosts 里");
    }

    /// <summary>接受一切、却什么都没记着的轮换策略：当前那把不在「记着的」里，轮换不该做。</summary>
    private sealed class RotatingWithoutRecord : IHostKeyPolicy, IHostKeyRotationPolicy
    {
        public bool AllowHostKeyUpdates => true;

        public ValueTask<SshHostKeyVerdict> EvaluateAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SshHostKeyVerdict.Accept);

        public ValueTask<IReadOnlyList<string>> GetKnownHostKeyFingerprintsAsync(string host, int port, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask RecordHostKeysAsync(string host, int port, IReadOnlyList<SshPublicKey> keys, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("不该记");
    }
}
