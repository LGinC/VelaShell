// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §1、§2.2

namespace VelaShell.Ssh.Session;

/// <summary>会话级的通道限额。</summary>
/// <remarks>非法值在设值时就抛（AGENTS 4.3）：曾经不校验，<see cref="MaxQueuedReplyBytes"/> 设成 0 会让第一条应答就把连接判死。</remarks>
public sealed record SshConnectionLimits
{
    /// <summary>同时存在的通道数上限。</summary>
    /// <exception cref="ArgumentOutOfRangeException">小于 1。</exception>
    public int MaxChannels
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxChannels), value, "通道数上限至少为 1。");
    } = 512;

    /// <summary>
    /// 所有通道的接收窗口之和不得超过这个数。
    /// </summary>
    /// <remarks>
    /// 没有它的话，开 100 条自适应窗口的通道就能把进程撑爆 ——
    /// 每条最坏 64 MiB，100 条就是 6.4 GiB。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public long SessionWindowBudgetBytes
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(SessionWindowBudgetBytes), value, "窗口总预算必须为正。");
    } = 256L * 1024 * 1024;

    /// <summary>
    /// 通道号回收后延迟多久才允许复用。
    /// </summary>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §1 规则 3〕通道号回收过早会串话：
    /// 对端可能还在路上发这个号的数据，号一旦被新通道复用，
    /// 那些数据就会被投递到错误的通道上。这是对端实现不规范时的兜底。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">为负。</exception>
    public TimeSpan ChannelIdReuseDelay
    {
        get;
        init => field = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(ChannelIdReuseDelay), value, "复用延迟不能为负。");
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 接收循环要回给对端、还排在发送队列里的应答最多攒多少字节。超过就判对端违规、断开。
    /// </summary>
    /// <remarks>
    /// 对端可以不读我们发的东西、同时不停地发要应答的报文（全局请求、开通道、未知报文号）——
    /// 接收循环不能在背压上等（那会让它等它自己），所以这些应答必须有一个硬上限，
    /// 否则就是一条不花对端任何代价的内存放大。正常的应答只有几个字节，默认值远到碰不上。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public long MaxQueuedReplyBytes
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(MaxQueuedReplyBytes), value, "应答积压上限必须为正。");
    } = 16L * 1024 * 1024;

    /// <summary>默认限额。</summary>
    public static SshConnectionLimits Default { get; } = new();
}
