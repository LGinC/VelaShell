// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §1、§2.2、§6.3

using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Tests.Session;

/// <summary>配置的非法值在设值时就抛，而不是等连上了才以一种难以理解的方式出事。</summary>
[TestClass]
[TestCategory("Session")]
public sealed class OptionValidationTests
{
    /// <summary>曾经 <c>MaxQueuedReplyBytes = 0</c> 会让第一条应答就把连接判死。</summary>
    [TestMethod]
    public void 连接限额的非法值在设值时就抛()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { MaxChannels = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { SessionWindowBudgetBytes = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { MaxQueuedReplyBytes = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { MaxQueuedReplyBytes = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshConnectionLimits { ChannelIdReuseDelay = TimeSpan.FromTicks(-1) });

        SshConnectionLimits edge = new() { MaxChannels = 1, SessionWindowBudgetBytes = 1, MaxQueuedReplyBytes = 1, ChannelIdReuseDelay = TimeSpan.Zero };
        Assert.AreEqual(1, edge.MaxChannels);
        Assert.AreEqual(TimeSpan.Zero, edge.ChannelIdReuseDelay);
    }

    /// <summary>曾经 0 或负数照样转成 uint 宣告给对端 —— 一个对端无法遵守的值。</summary>
    [TestMethod]
    public void 通道宣告的max_packet不为正时设值就抛()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshChannelOptions { ReceiveMaxPacketBytes = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshChannelOptions { ReceiveMaxPacketBytes = -1 });
        Assert.AreEqual(1, new SshChannelOptions { ReceiveMaxPacketBytes = 1 }.ReceiveMaxPacketBytes);
    }

    /// <summary>曾经超过约 24.8 天的间隔让保活循环里的 <c>(int)</c> 溢出，循环静默退出 —— 设了保活等于没设。</summary>
    [TestMethod]
    public void 保活间隔超过上限时构造就抛()
    {
        Assert.AreEqual(SshKeepAlivePolicy.MaxInterval, new SshKeepAlivePolicy(SshKeepAlivePolicy.MaxInterval).Interval);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SshKeepAlivePolicy(SshKeepAlivePolicy.MaxInterval + TimeSpan.FromTicks(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshKeepAlivePolicy(TimeSpan.FromDays(30)));
    }
}
