using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>敏感字段加密的密钥文件:用不了的旧密钥必须留下来,而不是被新密钥覆盖。</summary>
[TestClass]
public sealed class AesSecretProtectorTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 留给临时目录清理。
        }
    }

    [TestMethod]
    public void RoundTrip_WithSameKeyFile_Decrypts()
    {
        string keyFile = Path.Combine(_root, "secret.key");
        string cipher = new AesSecretProtector(keyFile).Protect("hunter2")!;

        Assert.AreEqual("hunter2", new AesSecretProtector(keyFile).Unprotect(cipher));
    }

    [TestMethod]
    public void UnusableKeyFile_IsMovedAsideBeforeANewKeyIsGenerated()
    {
        // 换了 Windows 账户后 DPAPI 解不开、或者文件被截断:长度不是 32、也解不出 32 字节。
        string keyFile = Path.Combine(_root, "secret.key");
        byte[] unusable = [1, 2, 3, 4, 5, 6, 7];
        File.WriteAllBytes(keyFile, unusable);

        _ = new AesSecretProtector(keyFile).Protect("x");

        string aside = Assert.ContainsSingle(Directory.GetFiles(_root, "secret.key.unreadable-*"));
        CollectionAssert.AreEqual(unusable, File.ReadAllBytes(aside), "旧密钥应原样保留,找回原账户时还有得恢复。");
        Assert.IsTrue(File.Exists(keyFile), "新密钥照常生成,应用不因此起不来。");
    }
}
