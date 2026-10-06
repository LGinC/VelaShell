// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §9    重协商的建议阈值
//   行为规格:      velashell-docs/zh/ssh/spec/03-key-exchange.md §8.1

namespace VelaShell.Ssh.Session;

/// <summary>我们<b>主动</b>发起密钥重协商的阈值（<c>velashell-docs/zh/ssh/spec/03</c> §8.1）。</summary>
/// <remarks>
/// <para>
/// 主动发起与「接住对端发起」是两件事。后者是必需的 —— 不接就会断线；
/// 前者是<b>安全加固</b>：它把一把会话密钥覆盖的数据量与时间窗压住。
/// </para>
/// <para>
/// <b><see cref="MaxPackets"/> 才是那条不能越过的硬线。</b>
/// SSH 的序号是 32 位的，而 AES-GCM 的 nonce 每个报文推进一次 ——
/// 两者都在 2³² 处出事，而 nonce 重用对 GCM 是<b>灾难性</b>的
/// （可以恢复认证密钥，进而伪造）。字节数与时长是 RFC 4253 §9 的建议，
/// 报文数是密码学上的硬约束。
/// </para>
/// <para>
/// <b>阈值有下限，构造时就校验。</b>重协商本身要做一次非对称运算，调得太频繁
/// 就成了一个自己给自己开的拒绝服务面。
/// </para>
/// <para>
/// <b>三条阈值都是「0 表示不看这一条」</b>，因此
/// <c>default(SshRekeyPolicy)</c> 与 <see cref="Disabled"/> 是同一个东西 ——
/// 一个全零的结构体就该是「什么都不做」。有主张的那一组值在
/// <see cref="Default"/> 里，写全了，不靠参数默认值去暗示。
/// </para>
/// </remarks>
public readonly record struct SshRekeyPolicy
{
    /// <summary>字节数阈值的下限。</summary>
    public const long MinimumBytes = 64L * 1024 * 1024;

    /// <summary>报文数阈值的下限。</summary>
    /// <remarks>
    /// 比字节与时长的下限宽松得多：报文数是密码学硬约束那一侧，
    /// 调低它的动机通常是测试或者极端保守，而不是误配。
    /// </remarks>
    public const long MinimumPackets = 1024;

    /// <summary>报文数阈值的上限，也是与策略无关、关不掉的那条硬线：2³¹。</summary>
    /// <remarks>
    /// 序号是 32 位的，同一套密钥下走满 2³² 个报文就回绕（chacha20-poly1305 的 nonce 重用、HMAC 可被重放）。
    /// 曾经这里可以设得比 2³² 还大，<see cref="Disabled"/> 也会把它一并关掉 —— 一条能被公开 API 关掉的密码学硬约束。
    /// 现在阈值不许超过 2³¹；会话不论策略如何，任一方向在同一套密钥下到 2³¹ 个报文就主动重协商，
    /// 传输层在 2³² 处还有最后一道保险。
    /// </remarks>
    public const long MaximumPackets = 1L << 31;

    /// <summary>建一组重协商阈值。</summary>
    /// <param name="maxBytes">任一方向累计字节数上限；0 表示不看。下限 <see cref="MinimumBytes"/>。</param>
    /// <param name="maxInterval">距上次密钥交换的时长上限；<see cref="TimeSpan.Zero"/> 表示不看。下限 <see cref="MinimumInterval"/>。</param>
    /// <param name="maxPackets">任一方向累计报文数上限；0 表示不看。下限 <see cref="MinimumPackets"/>。</param>
    /// <exception cref="ArgumentOutOfRangeException">某条阈值为负或低于下限。</exception>
    public SshRekeyPolicy(long maxBytes = 0, TimeSpan maxInterval = default, long maxPackets = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPackets);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInterval, TimeSpan.Zero);

        if (maxBytes is > 0 and < MinimumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes), maxBytes,
                $"重协商的字节阈值不能低于 {MinimumBytes} —— 太频繁的重协商本身就是一个拒绝服务面。");
        }

        if (maxPackets is > 0 and < MinimumPackets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPackets), maxPackets,
                $"重协商的报文数阈值不能低于 {MinimumPackets}。");
        }

        if (maxPackets > MaximumPackets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPackets), maxPackets,
                $"重协商的报文数阈值不能高于 {MaximumPackets} —— 序号是 32 位的，再往上就有回绕的风险。");
        }

        if (maxInterval > TimeSpan.Zero && maxInterval < MinimumInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInterval), maxInterval,
                $"重协商的时长阈值不能低于 {MinimumInterval} —— 理由同上。");
        }

        MaxBytes = maxBytes;
        MaxInterval = maxInterval;
        MaxPackets = maxPackets;
    }

    /// <summary>任一方向累计字节数上限；0 表示不看。</summary>
    public long MaxBytes { get; }

    /// <summary>距上次密钥交换的时长上限；<see cref="TimeSpan.Zero"/> 表示不看。</summary>
    public TimeSpan MaxInterval { get; }

    /// <summary>任一方向累计报文数上限；0 表示不看。</summary>
    public long MaxPackets { get; }

    /// <summary>时长阈值的下限。</summary>
    public static TimeSpan MinimumInterval => TimeSpan.FromMinutes(1);

    /// <summary>不按字节、时长或自定的报文数主动发起（仍然会接住对端发起的）。</summary>
    /// <remarks>报文数的硬线（<see cref="MaximumPackets"/>）不受它影响，到了照样主动发起。</remarks>
    public static SshRekeyPolicy Disabled => default;

    /// <summary>默认：1 GiB / 2³¹ 个报文，<b>不按时长</b>（字节阈值是 RFC 4253 §9 的建议，报文数是硬约束）。</summary>
    /// <remarks>
    /// 〔Q1，velashell-docs/zh/ssh/spec/03 §8.1〕时长阈值默认不看：OpenSSH 的客户端与服务端默认也只按数据量换钥（<c>ssh -G</c> 是
    /// <c>rekeylimit 0 0</c>，<c>sshd_config</c> 默认 <c>RekeyLimit default none</c>）。对处理不好客户端发起的重协商的老设备，
    /// 每小时一次等于每小时断一次线（我们的 <c>KEXINIT</c> 发出去之后闸门已关、收不回，等到时限自判超时）。
    /// 曾经默认 1 小时。要按时长换钥的显式给 <c>maxInterval</c>。
    /// </remarks>
    public static SshRekeyPolicy Default =>
        new(maxBytes: 1L << 30, maxPackets: 1L << 31);

    /// <summary>有没有任何一条阈值是开着的。</summary>
    public bool IsEnabled => MaxBytes > 0 || MaxPackets > 0 || MaxInterval > TimeSpan.Zero;
}
