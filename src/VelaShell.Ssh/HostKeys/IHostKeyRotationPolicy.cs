// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §6.4

namespace VelaShell.Ssh.HostKeys;

/// <summary>
/// 主机密钥轮换（OpenSSH 的 <c>UpdateHostKeys</c>）：服务端认证之后宣告它的全部主机密钥（<c>hostkeys-00@openssh.com</c>），
/// 客户端让它证明持有其中新的那几把（<c>hostkeys-prove-00@openssh.com</c>），证实了就补记下来。
/// </summary>
/// <remarks>
/// <para>
/// 用处在「之后」：运维把 RSA 主机密钥换成 Ed25519、或者定期轮换时，客户端已经认得新钥，用户不会看到「主机密钥变了，可能有中间人」，
/// 也不用去手工删行 —— 那条告警才能重新变得有分量。
/// </para>
/// <para>
/// 主机密钥策略实现它、并且 <see cref="AllowHostKeyUpdates"/> 为真时，连接才做轮换；只增不删（删掉不再出示的旧钥要改写 <c>known_hosts</c>，不做）。
/// </para>
/// </remarks>
public interface IHostKeyRotationPolicy
{
    /// <summary>做不做轮换。</summary>
    bool AllowHostKeyUpdates { get; }

    /// <summary>这台主机记着的<b>普通</b>主机密钥（不含 CA、不含作废的）。</summary>
    /// <param name="host">主机（与裁决时同一个名字）。</param>
    /// <param name="port">端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<IReadOnlyList<SshPublicKey>> GetKnownHostKeysAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>记下服务端证明过持有的新主机密钥（只追加）。</summary>
    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="keys">要记的钥。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RecordHostKeysAsync(string host, int port, IReadOnlyList<SshPublicKey> keys, CancellationToken cancellationToken = default);
}
