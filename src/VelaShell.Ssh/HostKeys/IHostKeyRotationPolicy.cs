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
/// 主机密钥策略实现它、并且 <see cref="AllowHostKeyUpdates"/> 为真时，连接才做轮换：证实了的新钥补记（<see cref="RecordHostKeysAsync"/>），
/// 服务端不再出示的旧钥忘掉（<see cref="ForgetHostKeysAsync"/>，Q4）。
/// </para>
/// </remarks>
public interface IHostKeyRotationPolicy
{
    /// <summary>做不做轮换。</summary>
    bool AllowHostKeyUpdates { get; }

    /// <summary>这台主机记着的<b>普通</b>主机密钥（不含 CA、不含作废的）的 SHA-256 指纹。</summary>
    /// <param name="host">主机（与裁决时同一个名字）。</param>
    /// <param name="port">端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns><see cref="SshPublicKey.Sha256Fingerprint"/> 的样子；带不带 <c>=</c> 填充、有没有 <c>SHA256:</c> 前缀都认。</returns>
    /// <remarks>
    /// 交指纹而不是整把公钥：信任库常常只存指纹（宿主的就是），而 SHA-256 指纹覆盖整个公钥 blob，比指纹与比 blob 一样。
    /// 曾经要交整把公钥，只存指纹的信任库就实现不了轮换。
    /// </remarks>
    ValueTask<IReadOnlyList<string>> GetKnownHostKeyFingerprintsAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>记下服务端证明过持有的新主机密钥（只追加）。</summary>
    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="keys">要记的钥。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RecordHostKeysAsync(string host, int port, IReadOnlyList<SshPublicKey> keys, CancellationToken cancellationToken = default);

    /// <summary>忘掉服务端不再出示的旧钥（这台主机记着、这次宣告里却没有的那几把）。</summary>
    /// <param name="host">主机。</param>
    /// <param name="port">端口。</param>
    /// <param name="fingerprints">要忘掉的钥的 SHA-256 指纹（<see cref="SshPublicKey.Sha256Fingerprint"/> 的样子）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>真忘掉了的那几把的指纹；删不了的（记在只读的地方、写在管着好几台主机的通配行里）不算。</returns>
    /// <remarks>
    /// <para>
    /// 〔Q4，velashell-docs/zh/ssh/spec/05 §6.4.1〕只在宣告完整（没超过一次看的上限）、要证明的新钥全都证明过了时才调；当前这条连接用的钥一定不在里面。
    /// 不删的话，换下来的钥一直受信 —— 它的私钥哪天流出去，拿着它的人照样能冒充这台主机。
    /// </para>
    /// <para>默认什么都不删（曾经的行为）：删掉一条记录收不回来，没实现这一条的策略照旧只增不删。</para>
    /// </remarks>
    ValueTask<IReadOnlyList<string>> ForgetHostKeysAsync(
        string host, int port, IReadOnlyList<string> fingerprints, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);
}
