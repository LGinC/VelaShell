// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 手动拨的时钟：时间只在测试调用 Advance 时往前走，到点的定时器回调由测试决定什么时候执行。

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>一个只在测试拨动时才走的 <see cref="TimeProvider"/>。</summary>
/// <remarks>
/// <para>
/// 真实的定时器到点之后，回调要排进线程池、等轮到它才执行；排上队以后再 <c>Change</c> 也拦不住它。
/// 这里照样建模：到点的回调先进队列，<see cref="Advance"/> 当场执行，
/// <see cref="AdvanceWithoutRunningCallbacks"/> 只拨表、让它们在队列里等，
/// 由 <see cref="RunQueuedCallbacks"/> 晚些再执行 —— 这样才摆得出「到点了、回调还没执行」那一刻。
/// </para>
/// <para>只支持一次性定时器（本库没用周期定时器）。</para>
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _armed = [];
    private readonly List<ManualTimer> _queued = [];
    private long _nowTicks;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _nowTicks;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>往前拨，并执行到点的回调。</summary>
    public void Advance(TimeSpan by)
    {
        AdvanceWithoutRunningCallbacks(by);
        RunQueuedCallbacks();
    }

    /// <summary>只往前拨：到点的回调排进队列，先不执行 —— 模拟线程池忙、回调迟迟轮不到。</summary>
    public void AdvanceWithoutRunningCallbacks(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        lock (_lock)
        {
            _nowTicks += by.Ticks;
            foreach (ManualTimer due in _armed.Where(t => t.DueTicks <= _nowTicks).OrderBy(t => t.DueTicks).ToList())
            {
                _armed.Remove(due);
                _queued.Add(due);
            }
        }
    }

    /// <summary>执行已经排上队的回调（在锁外，回调里可以再动定时器）。</summary>
    public void RunQueuedCallbacks()
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            due = [.. _queued];
            _queued.Clear();
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueTicks { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("测试时钟只支持一次性定时器。");
            }

            lock (owner._lock)
            {
                // 已经排上队的回调不撤 —— 与真实定时器一样，排上队就拦不住了。
                owner._armed.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueTicks = owner._nowTicks + dueTime.Ticks;
                    owner._armed.Add(this);
                }
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                owner._armed.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
