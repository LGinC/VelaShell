using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 建连读出的私钥签名器归这一次建连所有,用完就释放(释放时清零私钥);交给后台「自动加钥」的那一把除外。
/// 曾经从不释放:私钥材料在托管堆上一直留到 GC。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class SshConnectionAssemblerSignerTests
{
    [TestMethod]
    public async Task 建连用完的私钥签名器被释放_交给自动加钥的那一把留着()
    {
        var used = InMemorySshSigner.GenerateEd25519();
        var handedOff = InMemorySshSigner.GenerateEd25519();
        try
        {
            IReadOnlyList<SshCredential> credentials =
            [
                new PasswordCredential("x"),
                new PublicKeyCredential(used),
                new PublicKeyCredential(handedOff),
            ];

            SshConnectionAssembler.DisposeOwnedSigners(credentials, except: handedOff);

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await used.SignAsync(new byte[1], "ssh-ed25519"));
            Assert.IsNotNull(await handedOff.SignAsync(new byte[1], "ssh-ed25519"),
                "交给后台加钥的那一把被提前释放了");
        }
        finally
        {
            handedOff.Dispose();
        }
    }

    /// <summary>
    /// 读加密私钥(口令派生是 CPU 计算)不占发起建连的线程:调用当场返回一个没完成的任务,解出来的钥照样对。
    /// </summary>
    /// <remarks>
    /// 守的是宿主自己的保证。去掉 <c>Task.Run</c> 这条在本机上仍然通过 —— 库里读文件那一步是异步完成的,
    /// 解密本来就落在线程池上;要等库改成同步读,少了这一层才会让它变红。
    /// </remarks>
    [TestMethod]
    public async Task 读加密私钥不占调用线程()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Ssh", "Fixtures", "ed25519-rounds64");

        ValueTask<InMemorySshSigner> pending = SshConnectionAssembler.LoadSignerAsync(
            path, "correct horse battery staple", TestContext.CancellationToken);
        bool completedInline = pending.IsCompleted;

        using InMemorySshSigner signer = await pending;
        Assert.IsFalse(completedInline, "口令派生跑在了调用线程上");
        Assert.AreEqual("ssh-ed25519", signer.PublicKey.KeyType);
    }

    public TestContext TestContext { get; set; } = null!;
}
