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
        InMemorySshSigner used = InMemorySshSigner.GenerateEd25519();
        InMemorySshSigner handedOff = InMemorySshSigner.GenerateEd25519();
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
}
