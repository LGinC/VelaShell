using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Crypto;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 宿主的主机密钥策略把「这台主机记着哪种类型的钥」交给库:库据此把那种类型的算法排到最前,
/// 正常的服务端就谈成同一种,不会因为服务端新增了一把别的类型的钥而误报「指纹已变更」。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class VelaHostKeyPolicyTests
{
    [TestMethod]
    public async Task 交出这台主机记着的那把钥的类型()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.FindKnownHostAsync("old.example", 22, Arg.Any<CancellationToken>())
            .Returns(new KnownHost { Host = "old.example", Port = 22, KeyType = "ssh-rsa", Fingerprint = "SHA256:x" });

        VelaHostKeyPolicy preference = new(store, settings: null, prompt: null, alerts: null);

        Assert.AreSequenceEqual(["ssh-rsa"], (await preference.GetKnownKeyTypesAsync("old.example", 22)).ToArray());
    }

    [TestMethod]
    public async Task 没记过的主机返回空()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.FindKnownHostAsync("new.example", 22, Arg.Any<CancellationToken>()).Returns((KnownHost?)null);

        VelaHostKeyPolicy preference = new(store, settings: null, prompt: null, alerts: null);

        Assert.IsEmpty(await preference.GetKnownKeyTypesAsync("new.example", 22));
    }

    [TestMethod]
    public async Task 信任库出错时返回空而不阻断建连()
    {
        IHostKeyService store = Substitute.For<IHostKeyService>();
        store.FindKnownHostAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("数据库被锁"));

        VelaHostKeyPolicy preference = new(store, settings: null, prompt: null, alerts: null);

        Assert.IsEmpty(await preference.GetKnownKeyTypesAsync("any.example", 22));
    }

    /// <summary>
    /// 端到端的那一半在库里：记着 RSA 时，RSA 的算法排到 Ed25519 前面 —— 协商以客户端的顺序为准。
    /// </summary>
    [TestMethod]
    public void 记着的类型排到主机密钥算法清单最前()
    {
        SshAlgorithmSet preferred = SshAlgorithmSet.Default.PreferHostKeyTypes(["ssh-rsa"]);

        Assert.StartsWith("rsa-sha2-", preferred.HostKey[0]);
        Assert.IsGreaterThan(
            preferred.HostKey.ToList().FindIndex(a => a.StartsWith("rsa-sha2-", StringComparison.Ordinal)),
            preferred.HostKey.ToList().IndexOf("ssh-ed25519"));
    }
}
