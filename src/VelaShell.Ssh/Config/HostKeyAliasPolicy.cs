// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  HostKeyAlias(只取行为描述;端口的口径由黑盒核对)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Config;

/// <summary>
/// <c>ssh_config</c> 的 <c>HostKeyAlias</c>：查、记主机密钥时用别名代替真实的主机名与端口。
/// </summary>
/// <remarks>
/// <para>
/// 常见用法：同一台机器经不同的名字、不同的端口转发或跳板连上，主机密钥只记一份。
/// </para>
/// <para>
/// 〔决策 spec 09 §7〕<b>端口不带</b>：记下、查找用的都是别名本身，非 22 端口也不写成 <c>[别名]:端口</c>
/// —— 与 OpenSSH 的实际口径一致（黑盒核对：OpenSSH 10.5 连 2222 端口、<c>HostKeyAlias myalias</c>，记下的是 <c>myalias</c>）。
/// 类型偏好与主机密钥轮换同样按别名转给里面那个策略。
/// </para>
/// </remarks>
internal sealed class HostKeyAliasPolicy(IHostKeyPolicy inner, string alias)
    : IHostKeyPolicy, IHostKeyTypePreference, IHostKeyRotationPolicy
{
    /// <summary>别名查、记时用的端口：默认端口，于是 known_hosts 里写的就是别名本身。</summary>
    private const int AliasPort = 22;

    /// <summary>里面那个策略（测试用）。</summary>
    internal IHostKeyPolicy Inner => inner;

    /// <summary>别名（测试用）。</summary>
    internal string Alias => alias;

    /// <inheritdoc />
    public ValueTask<SshHostKeyVerdict> EvaluateAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
        inner.EvaluateAsync(Aliased(context), cancellationToken);

    /// <inheritdoc />
    public ValueTask PersistAsync(SshHostKeyContext context, CancellationToken cancellationToken = default) =>
        inner.PersistAsync(Aliased(context), cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> GetKnownKeyTypesAsync(string host, int port, CancellationToken cancellationToken = default) =>
        inner is IHostKeyTypePreference preference
            ? preference.GetKnownKeyTypesAsync(alias, AliasPort, cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public bool AllowHostKeyUpdates => inner is IHostKeyRotationPolicy { AllowHostKeyUpdates: true };

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> GetKnownHostKeyFingerprintsAsync(string host, int port, CancellationToken cancellationToken = default) =>
        inner is IHostKeyRotationPolicy rotation
            ? rotation.GetKnownHostKeyFingerprintsAsync(alias, AliasPort, cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public ValueTask RecordHostKeysAsync(string host, int port, IReadOnlyList<SshPublicKey> keys, CancellationToken cancellationToken = default) =>
        inner is IHostKeyRotationPolicy rotation
            ? rotation.RecordHostKeysAsync(alias, AliasPort, keys, cancellationToken)
            : ValueTask.CompletedTask;

    private SshHostKeyContext Aliased(SshHostKeyContext context) => new()
    {
        Host = alias,
        Port = AliasPort,
        Key = context.Key,
        NegotiatedAlgorithm = context.NegotiatedAlgorithm,
        PeerVersion = context.PeerVersion,
    };
}
