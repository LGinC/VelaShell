// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3、§5.4
//           velashell-docs/zh/ssh/design/architecture.md §8 第 7 项

namespace VelaShell.Ssh.HostKeys;

/// <summary>只接受指纹在给定集合里的主机密钥。</summary>
/// <remarks>
/// 适合「已知目标、指纹写死在配置里」的自动化场景 ——
/// 比 TOFU 强，因为它连第一次都不盲信。
/// </remarks>
public sealed class PinnedFingerprintHostKeyPolicy : IHostKeyPolicy
{
    private readonly HashSet<string> _fingerprints;

    /// <summary>用给定的 SHA-256 指纹集合构造。</summary>
    /// <param name="sha256Fingerprints">
    /// 形如 <c>SHA256:abc...</c>，与 <see cref="SshPublicKey.Sha256Fingerprint"/> 同格式。
    /// 为方便起见，不带 <c>SHA256:</c> 前缀的、前缀大小写不一的、带 base64 的 <c>=</c> 填充的、前后有空白的都接受；
    /// 前缀之后的主体逐字比对（base64 区分大小写）。
    /// </param>
    public PinnedFingerprintHostKeyPolicy(IEnumerable<string> sha256Fingerprints)
    {
        ArgumentNullException.ThrowIfNull(sha256Fingerprints);
        _fingerprints = new HashSet<string>(
            sha256Fingerprints.Select(SshPublicKey.NormalizeFingerprint), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<SshHostKeyVerdict> EvaluateAsync(
        SshHostKeyContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string actual = context.Key.Sha256Fingerprint;
        return ValueTask.FromResult(_fingerprints.Contains(SshPublicKey.NormalizeFingerprint(actual))
            ? SshHostKeyVerdict.Accept
            : SshHostKeyVerdict.Reject(
                $"{context.Target} 的主机密钥指纹不在允许列表里。" +
                $"对端出示：{actual}（{context.Key.KeyType}, {context.Key.KeyBits} 位）。"));
    }

    // 〔AU-D3〕指纹按 SshPublicKey.NormalizeFingerprint 归一之后比：曾经只补前缀，带 = 填充的指纹永远比对不上 ——
    // 钉住的主机一个也连不上，报的却是「指纹不在允许列表里」。
}
