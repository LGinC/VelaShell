// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: RFC 8032 §5.1(Ed25519);RFC 8709(SSH 里的 Ed25519);velashell-docs/zh/ssh/spec/04-authentication.md §5.2(密钥材料用完清零)

using System.Security.Cryptography;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Auth;

[TestClass]
[TestCategory("Auth")]
public sealed class InMemorySignerTests
{
    [TestMethod]
    public async Task Ed25519签名用自己的公钥验得过()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();
        byte[] data = RandomNumberGenerator.GetBytes(100);

        byte[] signature = await signer.SignAsync(data, SshAlgorithmNames.SshEd25519);

        Assert.IsTrue(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519));
        data[0] ^= 1;
        Assert.IsFalse(signer.PublicKey.VerifySignature(signature, data, SshAlgorithmNames.SshEd25519));
    }

    /// <summary>RFC 8032 §7.1 的第一组测试向量：私钥、公钥与空消息的签名逐字节对得上。</summary>
    [TestMethod]
    public async Task Ed25519对得上RFC8032的测试向量()
    {
        byte[] secret = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        byte[] expectedPublic = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        byte[] expectedSignature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

        using var signer = InMemorySshSigner.FromEd25519(secret);
        byte[] blob = await signer.SignAsync(ReadOnlyMemory<byte>.Empty, SshAlgorithmNames.SshEd25519);

        // 公钥 blob 的末尾 32 字节是公钥，签名 blob 的末尾 64 字节是签名。
        Assert.AreSequenceEqual(expectedPublic, signer.PublicKey.Blob.Span[^32..].ToArray());
        Assert.AreSequenceEqual(expectedSignature, blob[^64..]);
    }

    /// <summary>
    /// 释放即清零：曾经持有 BouncyCastle 的参数对象，释放时只处理 RSA / ECDsa，Ed25519 的种子一直留在堆上。
    /// </summary>
    [TestMethod]
    public async Task Ed25519签名器释放时清零种子且之后不能再签()
    {
        var signer = InMemorySshSigner.GenerateEd25519();
        signer.Dispose();

        Assert.IsTrue(signer.IsKeyMaterialCleared, "释放之后种子还在");
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await signer.SignAsync(new byte[1], SshAlgorithmNames.SshEd25519));
    }

    /// <summary>〔AGENTS 4.3〕接收可释放对象的参数配 <c>ownsXxx</c>，默认交进来就归它。</summary>
    [TestMethod]
    public void 交进来的RSA与ECDSA私钥按ownsKey决定释放不释放()
    {
        using var rsa = RSA.Create(2048);
        InMemorySshSigner.FromRsa(rsa, ownsKey: false).Dispose();
        _ = rsa.ExportParameters(includePrivateParameters: false);
        InMemorySshSigner.FromRsa(rsa).Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => rsa.ExportParameters(includePrivateParameters: false), "默认交进来就归签名器");

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        InMemorySshSigner.FromEcdsa(ecdsa, ownsKey: false).Dispose();
        _ = ecdsa.ExportParameters(includePrivateParameters: false);
        InMemorySshSigner.FromEcdsa(ecdsa).Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => ecdsa.ExportParameters(includePrivateParameters: false), "默认交进来就归签名器");
    }
}
