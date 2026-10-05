// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §2.2、§3、§4.3

namespace VelaShell.Ssh.Channels;

/// <summary>stderr 怎么处理。</summary>
public enum SshStderrMode
{
    /// <summary>缓冲起来，从 <c>StandardError</c> 读。</summary>
    Buffer,

    /// <summary>
    /// 丢掉。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §4.3〕丢弃时库内部**照常收包并立即回补窗口**，
    /// 只是不缓冲。不这么做的话「丢弃」就变成了死锁：
    /// 窗口被没人读的 stderr 吃空，对端连 stdout 也发不出来了。
    /// </remarks>
    Discard,
}

/// <summary>打开一条通道时的参数。</summary>
public sealed record SshChannelOptions
{
    /// <summary>接收窗口策略。</summary>
    /// <exception cref="ArgumentException">设成 <c>default</c>（没经 <c>Fixed</c> / <c>Adaptive</c> 构造的策略）。</exception>
    public SshWindowPolicy WindowPolicy
    {
        get;
        init => field = value.IsValid
            ? value
            : throw new ArgumentException("窗口策略要经 SshWindowPolicy.Fixed / Adaptive 构造，default 不是一个合法的策略。", nameof(WindowPolicy));
    } = SshWindowPolicy.Default;

    /// <summary>
    /// 我们宣告的单个 <c>CHANNEL_DATA</c> 数据段上限。
    /// </summary>
    /// <remarks>
    /// 它约束的是**对端**发给我们的；对端宣告的值约束我们发给它的，
    /// 而那个值**必须遵守** —— 超了对端会直接断连。
    /// 上限（不超过传输层允许的报文长度）在开通道时由会话核对。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正（曾经照样转成 uint 宣告出去，0 或负数都是一个对端无法遵守的值）。</exception>
    public int ReceiveMaxPacketBytes
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(ReceiveMaxPacketBytes), value, "宣告的 max packet 必须为正。");
    } = 32 * 1024;

    /// <summary>stderr 怎么处理。</summary>
    public SshStderrMode StderrMode { get; init; } = SshStderrMode.Buffer;

    /// <summary>默认参数。</summary>
    public static SshChannelOptions Default { get; } = new();
}
