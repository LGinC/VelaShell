// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/05-connection.md §3.2

namespace VelaShell.Ssh.Session;

/// <summary>
/// 排在发送泵前面、还没封装上线的字节数，以及排队等额度的发送方。
/// </summary>
/// <remarks>
/// <para>
/// 额度不够的发送方<b>按到达顺序排队</b>，额度空出来时从队头一个一个放行，放到额度又用完为止。
/// 放行的那一刻就替它记上账 —— 后面的人看到的是已经扣过的额度，不会一窝蜂地都以为还有空。
/// </para>
/// <para>
/// 〔CH-P3〕曾经是一个「有变化了」的广播：每刷出一轮就叫醒全部等待者，它们一起去看额度、一起通过，
/// 积压一下子冲过上限好几个报文，下一轮又一起睡回去（惊群）；先等的也不一定先走。
/// </para>
/// <para>
/// 只有数据面的发送方在这里等。接收循环的应答、本端的窗口回补、保活探测<b>不等</b>（<see cref="Charge"/>）——
/// 解除背压要靠接收循环，让它等就是让它等它自己。它们的字节照样记账，数据面的发送方看得到。
/// </para>
/// </remarks>
/// <param name="limit">积压到这么多字节时，新来的发送方开始排队。</param>
internal sealed class SendBudget(long limit)
{
    private readonly Lock _lock = new();
    private readonly Queue<Waiter> _waiters = new();
    private long _pending;
    private int _queued;
    private Exception? _closed;

    /// <summary>当前记着的字节数。</summary>
    public long Pending => Interlocked.Read(ref _pending);

    /// <summary>还在排队的发送方数（取消了、还没被清出队列的也算）。</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>等额度：额度够、而且没人排在前面就当场记账返回；否则排到队尾。</summary>
    /// <exception cref="OperationCanceledException">排队期间被取消（没有记账）。</exception>
    /// <remarks>连接已经收尾时抛的是收尾的原因（见 <see cref="Close"/>）。</remarks>
    public ValueTask ReserveAsync(int bytes, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (_closed is { } closed)
            {
                return ValueTask.FromException(closed);
            }

            // 队头上取消了的先清掉：它们不再等，却会挡住下面这条「没人排在前面」的快路。
            while (_waiters.TryPeek(out Waiter? head) && head.Task.IsCompleted)
            {
                _waiters.Dequeue();
                Interlocked.Decrement(ref _queued);
            }

            // ⚠️ **先亮出「有人要排」，再看额度。**Refund 在锁外，顺序反过来：先还额度，再看有没有人排。
            //    两边各自「先写后读」，至少有一边看得见对方 —— 要么这里看到还回来的额度走快路，
            //    要么 Refund 看到有人要排、进锁放行。反过来写的话，这里刚看完「额度满了」、还没进队，
            //    Refund 就还完额度、看到「没人排」走了，这一位在队里一直等到下一次还额度 —— 也许永远不来。
            Interlocked.Increment(ref _queued);
            if (_waiters.Count == 0 && Pending < limit)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Add(ref _pending, bytes);
                return ValueTask.CompletedTask;
            }

            waiter = new Waiter(bytes);
            _waiters.Enqueue(waiter);
        }

        return WaitAsync(waiter, cancellationToken);
    }

    private async ValueTask WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        try
        {
            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                // 取消先到：它留在队里，轮到时被跳过（放行处 TrySetResult 失败、不记账）。
                if (waiter.TrySetCanceled(cancellationToken))
                {
                    throw;
                }
            }

            // 放行与取消撞在一起、放行先到：账已经记上了，照常往下走（或者是收尾，抛收尾的原因）。
            await waiter.Task.ConfigureAwait(false);
        }
    }

    /// <summary>不等额度、直接记账：接收循环的应答、窗口回补、保活探测。</summary>
    public void Charge(int bytes) => Interlocked.Add(ref _pending, bytes);

    /// <summary>还回额度（帧上线了、或者没发出去）；有人在排队就按顺序放行。</summary>
    public void Refund(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _pending, -bytes);
        if (Volatile.Read(ref _queued) > 0)
        {
            Grant();
        }
    }

    /// <summary>从队头起放行，放到额度用完为止。</summary>
    private void Grant()
    {
        lock (_lock)
        {
            while (Pending < limit && _waiters.TryDequeue(out Waiter? next))
            {
                Interlocked.Decrement(ref _queued);

                // 先记账再放行：放行之后它的续体随时会跑，那时账上必须已经有它。
                Interlocked.Add(ref _pending, next.Bytes);
                if (!next.TrySetResult())
                {
                    Interlocked.Add(ref _pending, -next.Bytes);   // 已经取消了
                }
            }
        }
    }

    /// <summary>连接收尾：排着队的一律拿到 <paramref name="reason"/>，之后再来的也是；账清零。</summary>
    public void Close(Exception reason)
    {
        lock (_lock)
        {
            _closed ??= reason;
            while (_waiters.TryDequeue(out Waiter? waiter))
            {
                waiter.TrySetException(reason);
            }
            Volatile.Write(ref _queued, 0);
            Interlocked.Exchange(ref _pending, 0);
        }
    }

    private sealed class Waiter(int bytes) : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public int Bytes { get; } = bytes;
    }
}
