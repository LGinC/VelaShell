// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.4

using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Tests.HostKeys;

[TestClass]
[TestCategory("HostKeys")]
public sealed class PinnedFingerprintPolicyTests
{
    private static async Task<bool> AcceptsAsync(string pinned, SshPublicKey key)
    {
        PinnedFingerprintHostKeyPolicy policy = new([pinned]);
        SshHostKeyVerdict verdict = await policy.EvaluateAsync(new SshHostKeyContext
        {
            Host = "example.test",
            Port = 22,
            Key = key,
            NegotiatedAlgorithm = key.KeyType,
        });
        return verdict.IsAccepted;
    }

    /// <summary>
    /// 〔AU-D3〕从别处复制来的指纹常带 base64 的 <c>=</c> 填充、前后有空白、前缀大小写不一：都认。
    /// 曾经只补 <c>SHA256:</c> 前缀，带 <c>=</c> 的指纹永远比对不上 —— 钉住的主机一个也连不上。
    /// </summary>
    [TestMethod]
    public async Task 带填充或空白的指纹照样认()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();
        string fingerprint = signer.PublicKey.Sha256Fingerprint;   // SHA256:xxxx（不带填充）
        string body = fingerprint["SHA256:".Length..];

        Assert.IsTrue(await AcceptsAsync(fingerprint, signer.PublicKey));
        Assert.IsTrue(await AcceptsAsync(body, signer.PublicKey), "不带前缀的");
        Assert.IsTrue(await AcceptsAsync(fingerprint + "=", signer.PublicKey), "带 base64 填充的");
        Assert.IsTrue(await AcceptsAsync(body + "=", signer.PublicKey), "不带前缀、带填充的");
        Assert.IsTrue(await AcceptsAsync("  " + fingerprint + "\n", signer.PublicKey), "前后有空白的");
        Assert.IsTrue(await AcceptsAsync("sha256:" + body, signer.PublicKey), "前缀小写的");
    }

    /// <summary>base64 本身区分大小写：主体部分改了大小写就不是同一个指纹。</summary>
    [TestMethod]
    public async Task 指纹主体照样逐字比对()
    {
        using var signer = InMemorySshSigner.GenerateEd25519();
        string fingerprint = signer.PublicKey.Sha256Fingerprint;
        string flipped = "SHA256:" + new string([.. fingerprint["SHA256:".Length..].Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))]);

        Assert.IsFalse(await AcceptsAsync(flipped, signer.PublicKey));
    }
}
