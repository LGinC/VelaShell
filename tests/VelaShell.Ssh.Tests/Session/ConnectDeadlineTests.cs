// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3(裁决期间停表);velashell-docs/zh/ssh/spec/09-dialing.md §2.4(外层跟着停)
//
// 计时器的状态机本身。时钟是手动拨的：时间只在测试拨动时才走，到点的回调什么时候执行也由测试决定。

using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
[TestCategory("Session")]
public sealed class ConnectDeadlineTests
{
    private static readonly TimeSpan OneMs = TimeSpan.FromMilliseconds(1);

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    // ------------------------------------------------------------ 到点

    [TestMethod]
    public void 到点才判超时()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        clock.Advance(Seconds(10) - OneMs);
        Assert.IsFalse(deadline.Token.IsCancellationRequested);

        clock.Advance(OneMs);
        Assert.IsTrue(deadline.Token.IsCancellationRequested);
        Assert.IsTrue(deadline.IsExpired);
    }

    [TestMethod]
    public void 不限时的计时器永远不到点()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Timeout.InfiniteTimeSpan, CancellationToken.None, timeProvider: clock);

        deadline.Pause();
        clock.Advance(TimeSpan.FromDays(1));
        deadline.Resume();
        clock.Advance(TimeSpan.FromDays(1));

        Assert.IsFalse(deadline.Token.IsCancellationRequested);
    }

    // ------------------------------------------------------------ 停表与续表

    [TestMethod]
    public void 停着表时不走_续表从剩下的接着走()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        clock.Advance(Seconds(3));
        deadline.Pause();
        clock.Advance(Seconds(100));
        Assert.IsFalse(deadline.Token.IsCancellationRequested, "停着表还在走");

        deadline.Resume();
        clock.Advance(Seconds(7) - OneMs);
        Assert.IsFalse(deadline.Token.IsCancellationRequested, "续表后剩下的时间被扣多了");

        clock.Advance(OneMs);
        Assert.IsTrue(deadline.IsExpired, "续表后剩下的时间被补满了");
    }

    [TestMethod]
    public void 停过几次就扣几次_不补满()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        clock.Advance(Seconds(3));
        deadline.Pause();
        clock.Advance(Seconds(50));
        deadline.Resume();

        clock.Advance(Seconds(2));
        deadline.Pause();
        clock.Advance(Seconds(50));
        deadline.Resume();

        clock.Advance(Seconds(5) - OneMs);
        Assert.IsFalse(deadline.Token.IsCancellationRequested);

        clock.Advance(OneMs);
        Assert.IsTrue(deadline.IsExpired);
    }

    [TestMethod]
    public void 嵌套停表要配齐续表才接着走()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        deadline.Pause();
        deadline.Pause();
        deadline.Resume();
        clock.Advance(Seconds(100));
        Assert.IsFalse(deadline.Token.IsCancellationRequested, "里面那层续表就把整把表续上了");

        deadline.Resume();
        clock.Advance(Seconds(10));
        Assert.IsTrue(deadline.IsExpired);
    }

    [TestMethod]
    public void 没停表时续表什么也不做()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        deadline.Resume();
        clock.Advance(Seconds(10));

        Assert.IsTrue(deadline.IsExpired, "多余的一次续表把表停住了或者重新排了");
    }

    // ------------------------------------------------------------ 停表那一刻已经到点

    /// <summary>
    /// 到点的回调还排在线程池里没执行时停表：预算其实已经用完，要当场判超时，
    /// 不能把表冻住、让一条已经超时的连接再去问一遍用户。
    /// </summary>
    [TestMethod]
    public void 停表时预算已经用完就当场判超时()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        clock.AdvanceWithoutRunningCallbacks(Seconds(10) + OneMs);
        Assert.IsFalse(deadline.Token.IsCancellationRequested, "前提：到点的回调还没执行");

        deadline.Pause();

        Assert.IsTrue(deadline.Token.IsCancellationRequested, "停表把一条已经超时的连接冻住了");
        Assert.IsTrue(deadline.IsExpired);

        // 迟到的回调、之后的续表都不该出事，也不该把结果翻过来。
        clock.RunQueuedCallbacks();
        deadline.Resume();
        Assert.IsTrue(deadline.IsExpired);
    }

    [TestMethod]
    public void 停表时预算刚好用完也算到点()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        clock.AdvanceWithoutRunningCallbacks(Seconds(10));
        deadline.Pause();

        Assert.IsTrue(deadline.IsExpired);
        deadline.Resume();
    }

    [TestMethod]
    public void 已经到点之后停表续表不改变结果()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        clock.Advance(Seconds(10));
        Assert.IsTrue(deadline.IsExpired);

        deadline.Pause();
        deadline.Resume();
        clock.Advance(Seconds(100));

        Assert.IsTrue(deadline.IsExpired);
    }

    // ------------------------------------------------------------ 调用方取消

    [TestMethod]
    public void 停着表时调用方取消照样生效_不算到点()
    {
        ManualTimeProvider clock = new();
        using CancellationTokenSource caller = new();
        using SshConnectDeadline deadline = new(Seconds(10), caller.Token, timeProvider: clock);

        deadline.Pause();
        caller.Cancel();

        Assert.IsTrue(deadline.Token.IsCancellationRequested, "停表把调用方的取消也挡住了");
        Assert.IsFalse(deadline.IsExpired, "调用方取消被当成了超时");

        deadline.Resume();
        clock.Advance(Seconds(100));
        Assert.IsFalse(deadline.IsExpired);
    }

    // ------------------------------------------------------------ 跳板：外层跟着停

    [TestMethod]
    public void 里面停表外层也停_续表各自从剩下的接着走()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline outer = new(Seconds(30), CancellationToken.None, timeProvider: clock);
        using SshConnectDeadline inner = new(Seconds(10), outer.Token, outer, clock);

        clock.Advance(Seconds(2));
        inner.Pause();
        clock.Advance(Seconds(100));
        Assert.IsFalse(outer.Token.IsCancellationRequested, "里面在等人，外层却在走");
        Assert.IsFalse(inner.Token.IsCancellationRequested);

        inner.Resume();

        // 里面剩 8 秒、外层剩 28 秒。
        clock.Advance(Seconds(8));
        Assert.IsTrue(inner.IsExpired);
        Assert.IsFalse(outer.Token.IsCancellationRequested);

        clock.Advance(Seconds(20) - OneMs);
        Assert.IsFalse(outer.Token.IsCancellationRequested);
        clock.Advance(OneMs);
        Assert.IsTrue(outer.IsExpired);
    }

    /// <summary>
    /// 外层的预算在里面那一跳停表时已经用完：判的是外层超时，里面那一跳随之取消 ——
    /// 而且里面那一跳看到的是「上面不要了」，不是它自己到点（拨号器据此报是哪一跳超时）。
    /// </summary>
    [TestMethod]
    public void 外层预算已经用完时里面停表_判外层超时()
    {
        ManualTimeProvider clock = new();
        using SshConnectDeadline outer = new(Seconds(10), CancellationToken.None, timeProvider: clock);
        using SshConnectDeadline inner = new(Seconds(60), outer.Token, outer, clock);

        clock.AdvanceWithoutRunningCallbacks(Seconds(10));
        inner.Pause();

        Assert.IsTrue(outer.IsExpired, "外层的预算用完了却被冻住");
        Assert.IsTrue(inner.Token.IsCancellationRequested, "外层到点了，里面那一跳还在往下走");
        Assert.IsFalse(inner.IsExpired, "里面那一跳自己没到点");

        inner.Resume();
        clock.RunQueuedCallbacks();
        Assert.IsTrue(outer.IsExpired);
    }

    // ------------------------------------------------------------ 释放

    [TestMethod]
    public void 释放之后停表续表与迟到的回调都不抛()
    {
        ManualTimeProvider clock = new();
        SshConnectDeadline deadline = new(Seconds(10), CancellationToken.None, timeProvider: clock);

        clock.AdvanceWithoutRunningCallbacks(Seconds(10));
        deadline.Dispose();

        clock.RunQueuedCallbacks();
        deadline.Pause();
        deadline.Resume();
        deadline.Dispose();
    }
}
