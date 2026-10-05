// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.3(裁决独立计时);velashell-docs/zh/ssh/spec/09-dialing.md §2.4;
//           velashell-docs/zh/ssh/spec/08-failures.md §4

namespace VelaShell.Ssh.Session;

/// <summary>
/// 一把可以<b>停表</b>的连接计时器。
/// </summary>
/// <remarks>
/// <para>
/// 连接超时是为网络往返设计的：拨号、版本交换、密钥交换都应该在它之内完成。
/// 但密钥交换中间夹着一段<b>等人</b>的时间 —— 首次连接要弹窗问用户认不认这把主机密钥。
/// 那段时间算进连接超时的话，用户看了十秒指纹再点「信任」，这一轮已经被判超时，
/// 而「永久信任」的那次持久化也拿到一个已取消的令牌，根本没存下来。
/// </para>
/// <para>
/// 所以裁决期间停表（<see cref="Pause"/> / <see cref="Resume"/>），
/// 裁决自己另有 <c>HostKeyDecisionTimeout</c>。
/// </para>
/// <para>
/// <b>停表按实际用掉的时间结账，不看到点的回调来没来。</b>回调要等线程池轮到它才执行，线程池忙的时候会晚很久。
/// 曾经借 <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> 计时，停表时令牌还没取消就当作没到点：
/// 预算其实已经用完，计时器按剩 0 冻住，裁决照样把指纹拿去问用户，用户点完「信任」一续表就报超时。
/// 现在停表时用完了就当场判超时（调用方据此不再去问）；停着表时才跑到的回调不作数 ——
/// 账在停表时已经结过了。所以定时器是自己的，不是 <c>CancelAfter</c> 的：那个回调不知道有停表这回事。
/// </para>
/// <para>
/// <b>停表要一路往外传</b>：经跳板连接时，里面那一跳的整个建连都跑在外面那一跳的
/// 拨号计时之内；里面在等人时外面也得停，不然外层会在用户还在看指纹时判超时。
/// </para>
/// <para>线程安全：停表与续表可能来自不同线程（策略回调在哪个线程返回不由我们决定）。</para>
/// </remarks>
internal sealed class SshConnectDeadline : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _callerToken;
    private readonly SshConnectDeadline? _outer;
    private readonly TimeProvider _timeProvider;
    private readonly bool _limited;
    private readonly ITimer? _timer;
    private readonly Lock _lock = new();

    private TimeSpan _remaining;
    private long _runningSince;
    private int _pauseDepth;
    private bool _expired;
    private bool _disposed;

    /// <summary>建一把计时器并立刻开始走。</summary>
    /// <param name="budget">总时长；<see cref="Timeout.InfiniteTimeSpan"/> 表示不限时。</param>
    /// <param name="cancellationToken">调用方的取消（不停表，始终生效）。</param>
    /// <param name="outer">外层计时器（经跳板时）；停表会一并传给它。</param>
    /// <param name="timeProvider">时钟；默认系统时钟（测试用手动拨的那种）。</param>
    public SshConnectDeadline(
        TimeSpan budget,
        CancellationToken cancellationToken,
        SshConnectDeadline? outer = null,
        TimeProvider? timeProvider = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _callerToken = cancellationToken;
        _outer = outer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _remaining = budget;
        _runningSince = _timeProvider.GetTimestamp();
        _limited = budget != Timeout.InfiniteTimeSpan;

        // 「限不限时」不看 _timer：预算很短时，回调可能在下面这次赋值之前就跑了，那一次到点不能丢。
        if (_limited)
        {
            _timer = _timeProvider.CreateTimer(
                static state => ((SshConnectDeadline)state!).OnDue(), this, budget, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>计时器到点或调用方取消时被取消的令牌。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>是计时器到点了（而不是调用方取消的）。</summary>
    /// <remarks>
    /// 经跳板时，里面那一跳只拿得到外层的令牌：它被取消了，得靠这个分清是「超时」还是「用户不要了」——
    /// 前者要说清楚卡在哪一跳，后者原样当取消往外传。
    /// </remarks>
    public bool IsExpired => _cts.IsCancellationRequested && !_callerToken.IsCancellationRequested;

    /// <summary>停表。可以嵌套；每次 <see cref="Pause"/> 都要配一次 <see cref="Resume"/>。</summary>
    /// <remarks>
    /// 停表那一刻预算已经用完的，当场判超时 —— <see cref="Token"/> 随即取消。
    /// 调用方停表之后要再看一眼令牌，取消了就不该再去问人。
    /// </remarks>
    public void Pause()
    {
        bool expire = false;
        lock (_lock)
        {
            if (_pauseDepth++ == 0 && IsRunning)
            {
                _remaining -= _timeProvider.GetElapsedTime(_runningSince);
                if (_remaining <= TimeSpan.Zero)
                {
                    _remaining = TimeSpan.Zero;
                    _expired = expire = true;
                }
                else
                {
                    _timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }
        }

        // 取消要在锁外做：它会就地跑令牌上登记的回调（外面的套接字操作、里面那一跳链着的令牌）。
        if (expire)
        {
            Expire();
        }

        // 外层照样要停 —— 哪怕这里刚判了超时：续表时对外层也要配一次 Resume。
        _outer?.Pause();
    }

    /// <summary>续表：从停表时剩下的时间接着走。</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_pauseDepth > 0 && --_pauseDepth == 0 && IsRunning)
            {
                _runningSince = _timeProvider.GetTimestamp();
                _timer!.Change(_remaining, Timeout.InfiniteTimeSpan);
            }
        }

        _outer?.Resume();
    }

    /// <summary>还在计时：限了时、没到点、没被取消、建连也还没结束。只在锁内读。</summary>
    private bool IsRunning => _limited && !_expired && !_disposed && !_cts.IsCancellationRequested;

    private void OnDue()
    {
        lock (_lock)
        {
            // 停着表时才跑到的回调是过时的：它在停表之前就排上了队，而账在停表时已经结过 ——
            // 真到点了，Pause 当场就判了；没到，续表时会按剩下的重新排。
            if (_pauseDepth > 0 || !IsRunning)
            {
                return;
            }

            _expired = true;
        }

        Expire();
    }

    private void Expire()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 建连已经结束了 —— 没有谁还在等这个令牌。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer?.Dispose();
        _cts.Dispose();
    }
}
