// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: RFC 5958（PKCS#8 / EncryptedPrivateKeyInfo）;RFC 8018（PBES2）;velashell-docs/zh/ssh/spec/04-authentication.md §4.6
//
// **样本是真 openssl 生成的**（见 Fixtures/README.md），公钥标准答案由 ssh-keygen -y 给出。

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class EncryptedPkcs8KeyTests
{
    /// <summary>生成样本时用的口令，见 Fixtures/README.md。</summary>
    private const string Passphrase = "correct horse battery staple";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name));

    private static byte[] ReadPublicBlob(string name) =>
        Convert.FromBase64String(Fixture(name + ".pub").Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

    [TestMethod]
    [DataRow("pkcs8-rsa-enc", DisplayName = "RSA-2048 · PBES2 / AES-256-CBC")]
    [DataRow("pkcs8-ecdsa-enc", DisplayName = "ECDSA P-384 · PBES2 / AES-256-CBC")]
    public void 口令正确时解出的公钥与ssh_keygen的一致(string name)
    {
        InMemorySshSigner signer = SshPrivateKeyFile.Parse(Fixture(name), Passphrase);

        Assert.AreSequenceEqual(ReadPublicBlob(name), signer.PublicKey.Blob.ToArray());
    }

    /// <summary>
    /// 〔AU-E8〕口令是对的、里面装的钥不受支持：报「不支持」，而不是「口令不对」。
    /// 曾经逐个按 RSA、ECDSA 去试，都失败就报「口令多半不对」—— 界面一遍遍弹口令框，用户一遍遍输对的口令。
    /// </summary>
    [TestMethod]
    [DataRow("pkcs8-ed25519-enc", "Ed25519", DisplayName = "Ed25519")]
    [DataRow("pkcs8-dsa-enc", "DSA", DisplayName = "DSA")]
    [DataRow("pkcs8-brainpool-enc", "曲线", DisplayName = "ECDSA · brainpoolP256r1")]
    public void 口令正确但钥不受支持时报不支持而不是口令不对(string name, string mentioned)
    {
        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => SshPrivateKeyFile.Parse(Fixture(name), Passphrase));

        Assert.AreEqual(SshFailureReason.Unsupported, error.Reason, error.Message);
        Assert.IsFalse(error.NeedsPassphrase, "口令是对的，不该再弹口令框");
        Assert.Contains(mentioned, error.Message);
    }

    /// <summary>明文 PKCS#8 里的 Ed25519 也报「不支持」，而不是「格式不对或已损坏」。</summary>
    [TestMethod]
    public void 明文PKCS8里的Ed25519报不支持()
    {
        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => SshPrivateKeyFile.Parse(Fixture("pkcs8-ed25519")));

        Assert.AreEqual(SshFailureReason.Unsupported, error.Reason, error.Message);
        Assert.Contains("Ed25519", error.Message);
    }

    /// <summary>口令不对仍然说口令不对 —— 不管里面装的是什么钥。</summary>
    [TestMethod]
    [DataRow("pkcs8-ed25519-enc")]
    [DataRow("pkcs8-rsa-enc")]
    public void 口令不对时仍报口令不对(string name)
    {
        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(
            () => SshPrivateKeyFile.Parse(Fixture(name), "wrong passphrase"));

        Assert.AreEqual(SshFailureReason.KeyPassphraseIncorrect, error.Reason, error.Message);
        Assert.IsTrue(error.NeedsPassphrase);
    }
}
