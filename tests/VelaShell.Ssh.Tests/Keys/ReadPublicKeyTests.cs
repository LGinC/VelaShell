// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: OpenSSH PROTOCOL.key(公钥段);PuTTY .ppk 的 Public-Lines;RFC 8017/5915/5958

using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Tests.Keys;

/// <summary>
/// 不用口令读出私钥文件里的公钥:导入只有私钥、没有 .pub 的文件(PuTTY 用户手里通常只有一个 .ppk)时用。
/// </summary>
[TestClass]
[TestCategory("Keys")]
public sealed class ReadPublicKeyTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);

    /// <summary>真工具写的 <c>.pub</c> 里的公钥 blob —— 标准答案。</summary>
    private static byte[] PublicBlobOf(string pubFile) =>
        Convert.FromBase64String(File.ReadAllText(FixturePath(pubFile)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

    /// <summary>加了密的 OpenSSH 私钥与 .ppk 也读得出:公钥段本来就是明文。</summary>
    [TestMethod]
    [DataRow("ed25519-aes256ctr", "ed25519-aes256ctr.pub", DisplayName = "OpenSSH · ed25519 · 加密")]
    [DataRow("rsa-aes256ctr", "rsa-aes256ctr.pub", DisplayName = "OpenSSH · rsa · 加密")]
    [DataRow("ecdsa-aes256ctr", "ecdsa-aes256ctr.pub", DisplayName = "OpenSSH · ecdsa · 加密")]
    [DataRow("ed25519-plain", "ed25519-plain.pub", DisplayName = "OpenSSH · ed25519 · 不加密")]
    [DataRow("putty-ed25519-v3-hi-enc.ppk", "putty-ed25519-v3-hi-enc.pub", DisplayName = ".ppk v3 · 加密")]
    [DataRow("putty-ed25519-v2-lo.ppk", "putty-ed25519-v2-lo.pub", DisplayName = ".ppk v2 · 不加密")]
    public void 不用口令读出与真工具写的公钥逐字节相同(string keyFile, string pubFile)
    {
        Assert.IsTrue(SshPrivateKeyFile.TryReadPublicKey(File.ReadAllText(FixturePath(keyFile)), out SshPublicKey? key));
        Assert.AreSequenceEqual(PublicBlobOf(pubFile), key.Blob.ToArray());
    }

    /// <summary>未加密的 PKCS#1 / SEC1 / PKCS#8 由私钥导出。</summary>
    [TestMethod]
    public void 未加密的PEM由私钥导出公钥()
    {
        using RSA rsa = RSA.Create(2048);
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        foreach (string pem in new[] { rsa.ExportRSAPrivateKeyPem(), ec.ExportECPrivateKeyPem(), ec.ExportPkcs8PrivateKeyPem() })
        {
            using InMemorySshSigner expected = SshPrivateKeyFile.Parse(pem);
            Assert.IsTrue(SshPrivateKeyFile.TryReadPublicKey(pem, out SshPublicKey? key), pem[..40]);
            Assert.AreSequenceEqual(expected.PublicKey.Blob.ToArray(), key.Blob.ToArray());
        }
    }

    /// <summary>公钥也在密文里的格式读不出;认不出、不完整的文件同样只是读不出,不抛。</summary>
    [TestMethod]
    [DataRow("pkcs8-rsa-enc", DisplayName = "加密 PKCS#8")]
    [DataRow("legacy-rsa-aes128", DisplayName = "传统加密 PEM")]
    [DataRow("ed25519-plain.pub", DisplayName = "选错成了 .pub")]
    public void 读不出时返回false(string file)
    {
        Assert.IsFalse(SshPrivateKeyFile.TryReadPublicKey(File.ReadAllText(FixturePath(file)), out SshPublicKey? key));
        Assert.IsNull(key);
    }

    [TestMethod]
    public void 截断与篡改的文件读不出也不抛()
    {
        string openSsh = File.ReadAllText(FixturePath("ed25519-aes256ctr"));
        string ppk = File.ReadAllText(FixturePath("putty-ed25519-v3-hi-enc.ppk"));

        Assert.IsFalse(SshPrivateKeyFile.TryReadPublicKey(openSsh[..120] + "\n-----END OPENSSH PRIVATE KEY-----\n", out _));
        Assert.IsFalse(SshPrivateKeyFile.TryReadPublicKey(
            ppk.Replace("Public-Lines: 2", "Public-Lines: abc", StringComparison.Ordinal), out _));
        Assert.IsFalse(SshPrivateKeyFile.TryReadPublicKey("PuTTY-User-Key-File-3: ssh-ed25519\n", out _));
    }
}
