// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: RFC 5958（PKCS#8 / EncryptedPrivateKeyInfo）;RFC 8018（PBES2）;RFC 8410（PKCS#8 里的 Ed25519）;
//           velashell-docs/zh/ssh/spec/04-authentication.md §4.6
//
// **样本是真 openssl 生成的，或是 RFC 8410 原文里的例子**（见 Fixtures/README.md），公钥标准答案由 ssh-keygen -y 给出。

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
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
    [DataRow("pkcs8-ed25519-enc", DisplayName = "Ed25519 · PBES2 / AES-256-CBC")]
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

    /// <summary>
    /// 明文 PKCS#8 里的 Ed25519（RFC 8410）：openssl 的产物，以及 RFC 原文的三个例子 —— 只有私钥的 v1、
    /// 带属性与公钥的 v2、BER 不定长编码。曾经一律报「不支持」。不用口令读公钥（补 <c>.pub</c>）也得到同一把。
    /// </summary>
    [TestMethod]
    [DataRow("pkcs8-ed25519", "pkcs8-ed25519", DisplayName = "openssl genpkey")]
    [DataRow("pkcs8-ed25519-rfc8410", "pkcs8-ed25519-rfc8410", DisplayName = "RFC 8410 §10.3 · v1")]
    [DataRow("pkcs8-ed25519-rfc8410-v2", "pkcs8-ed25519-rfc8410", DisplayName = "RFC 8410 §10.3 · v2，带属性与公钥")]
    [DataRow("pkcs8-ed25519-rfc8410-ber", "pkcs8-ed25519-rfc8410", DisplayName = "RFC 8410 附录 A · BER")]
    public void 明文PKCS8里的Ed25519读得出_公钥与ssh_keygen的一致(string name, string answer)
    {
        using InMemorySshSigner signer = SshPrivateKeyFile.Parse(Fixture(name));
        Assert.AreSequenceEqual(ReadPublicBlob(answer), signer.PublicKey.Blob.ToArray());

        Assert.IsTrue(SshPrivateKeyFile.TryReadPublicKey(Fixture(name), out SshPublicKey? publicKey));
        Assert.AreSequenceEqual(ReadPublicBlob(answer), publicKey.Blob.ToArray());
    }

    /// <summary>
    /// 带着的公钥与种子导出的不是同一把：报格式不对。RFC 8410 附录 A 的两个错例（公钥少一个字节），
    /// 以及把 v2 例子里公钥的末字节改掉一位的。真 <c>ssh-keygen</c> 对前两个照样交出文件里写的那个公钥 ——
    /// 拿着「不是你以为的那把」钥，症状只是服务端一句「不接受这把公钥」。
    /// </summary>
    [TestMethod]
    [DataRow("pkcs8-ed25519-rfc8410-badpub1", false, DisplayName = "RFC 8410 附录 A · 公钥少了首字节")]
    [DataRow("pkcs8-ed25519-rfc8410-badpub2", false, DisplayName = "RFC 8410 附录 A · 公钥少了尾字节")]
    [DataRow("pkcs8-ed25519-rfc8410-v2", true, DisplayName = "RFC 8410 §10.3 · v2，公钥末字节改一位")]
    public void PKCS8里Ed25519的公钥对不上时报格式不对(string name, bool flipLastByte)
    {
        string pem = Fixture(name);
        if (flipLastByte)
        {
            string body = string.Concat(pem.Split('\n').Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal)));
            byte[] der = Convert.FromBase64String(body);
            der[^1] ^= 0x01;
            pem = $"-----BEGIN PRIVATE KEY-----\n{Convert.ToBase64String(der)}\n-----END PRIVATE KEY-----\n";
        }

        SshPrivateKeyException error = Assert.ThrowsExactly<SshPrivateKeyException>(() => SshPrivateKeyFile.Parse(pem));

        Assert.AreEqual(SshFailureReason.KeyFormatInvalid, error.Reason, error.Message);
        Assert.IsFalse(error.NeedsPassphrase);
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
