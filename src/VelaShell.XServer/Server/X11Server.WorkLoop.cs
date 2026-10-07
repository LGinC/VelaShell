// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 1 节「Protocol Formats」(请求按序执行)、
//   「GrabServer」(独占期间不处理其他连接的请求)
//   架构:velashell-docs/zh/xserver/design/architecture.md §5(线程模型)

using System.Diagnostics;
using System.Threading.Channels;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>执行线程一次持锁最多跑这么久,然后放锁让宿主读像素(宿主的 UI 线程在 ReadPixels 里等这把锁)。</summary>
    private static readonly long LockBudgetTicks = Stopwatch.Frequency / 250;   // 4 毫秒

    /// <summary>GrabServer 期间暂存的别的客户端的工作项。</summary>
    private readonly List<WorkItem> _deferred = [];

    private XClient? _serverGrabber;

    /// <summary>一项工作:客户端的一条请求(<see cref="Request" />),或者一段要在执行线程上跑的代码。</summary>
    /// <summary>执行线程上的一项工作:一段代码,或者一条请求(<see cref="Request" /> 是池里租来的缓冲,前 <see cref="RequestLength" /> 字节是请求)。</summary>
    private readonly record struct WorkItem(XClient? Client, Action? Action, byte[]? Request = null, int RequestLength = 0);

    /// <summary>把一件事排进执行线程。可以在任意线程上调。</summary>
    internal void Post(XClient? client, Action action) => _work.Writer.TryWrite(new WorkItem(client, action));

    /// <summary>把客户端的一条请求排进执行线程(不为每条请求分配闭包)。</summary>
    private void PostRequest(XClient client, byte[] request, int length) => _work.Writer.TryWrite(new WorkItem(client, null, request, length));

    /// <summary>排进执行线程并等它做完(连接建立等少数需要结果的地方用)。</summary>
    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _work.Writer.TryWrite(new WorkItem(null, () =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));
        if (!queued)
        {
            tcs.TrySetCanceled();   // 执行循环已经收工
        }
        return tcs.Task;
    }

    /// <summary>持锁期间产生的诊断日志:放锁之后再交给宿主(宿主的日志往往同步写文件,持锁写就是让 UI 线程陪着等磁盘)。</summary>
    private readonly List<string> _pendingLog = [];

    /// <summary>此刻持着像素锁的执行线程(没人持有为 0)。</summary>
    private int _lockThread;

    /// <summary>
    /// 客户端能成批触发的日志(协议错误、连接进出、字体没找到)每秒最多记这么多条(全部客户端合计);
    /// 再多只计数,下一次能记时补一行「没记的有几条」。
    /// </summary>
    private const int FrequentLogsPerSecond = 50;

    private long _frequentLogSecond;
    private int _frequentLogsThisSecond;
    private int _frequentLogsSuppressed;

    /// <summary>
    /// 诊断日志的唯一出口(<see cref="X11ServerOptions.Log" />)。执行线程持锁时先攒着,放锁之后按原顺序交出去;
    /// 连接的读写线程上直接交。
    /// </summary>
    internal void Log(string message)
    {
        if (_options.Log is not { } log)
        {
            return;
        }
        if (_lockThread == Environment.CurrentManagedThreadId)
        {
            _pendingLog.Add(message);
            return;
        }
        log(message);
    }

    /// <summary>放锁之后:把持锁期间攒下的日志交给宿主。</summary>
    private void FlushLog()
    {
        if (_pendingLog.Count == 0 || _options.Log is not { } log)
        {
            return;
        }
        string[] lines = [.. _pendingLog];
        _pendingLog.Clear();
        foreach (string line in lines)
        {
            try
            {
                log(line);
            }
            catch (Exception)
            {
                // 宿主的日志出错不能拖垮执行线程。
            }
        }
    }

    /// <summary>
    /// 这一条客户端能成批触发的日志要不要记:每秒最多 <see cref="FrequentLogsPerSecond" /> 条 —— 一个客户端每秒能打出
    /// 几十万条错误请求、连上又断开几千次,条条都记,日志文件一晚上就是几个 GB。只在执行线程上调。
    /// </summary>
    private bool ShouldLogFrequent()
    {
        if (_options.Log is null)
        {
            return false;
        }
        long second = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        if (second != _frequentLogSecond)
        {
            if (_frequentLogsSuppressed > 0)
            {
                Log($"{_frequentLogsSuppressed} more log lines were not written (limit {FrequentLogsPerSecond} per second)");
            }
            _frequentLogSecond = second;
            _frequentLogsThisSecond = 0;
            _frequentLogsSuppressed = 0;
        }
        if (_frequentLogsThisSecond >= FrequentLogsPerSecond)
        {
            _frequentLogsSuppressed++;
            return false;
        }
        _frequentLogsThisSecond++;
        return true;
    }

    /// <summary>
    /// 放回来的暂存请求(GrabServer 结束、SYNC 的 Await 等到了、XTEST 的延迟到了):执行循环先取它,再取通道。
    /// 原先就地一口气执行完 —— 最坏 255 个客户端 × 1024 条,全程持锁,绕过每批 4 毫秒的预算与对宿主的让行。
    /// 先于通道取保证了顺序:同一个客户端还在通道里的请求都比暂存的这些来得晚。
    /// </summary>
    private readonly Queue<WorkItem> _ready = new();

    /// <summary>把暂存的请求按原顺序放回执行循环(见 <see cref="_ready" />)。</summary>
    private void Requeue(List<WorkItem> items)
    {
        foreach (WorkItem item in items)
        {
            _ready.Enqueue(item);
        }
    }

    private bool TryTakeItem(ChannelReader<WorkItem> reader, out WorkItem item) =>
        _ready.TryDequeue(out item) || reader.TryRead(out item);

    private async Task RunLoopAsync()
    {
        ChannelReader<WorkItem> reader = _work.Reader;
        try
        {
            while (_ready.Count != 0 || await reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                // lock 不公平:刚放锁就再拿,等着读像素的宿主线程可能一直抢不到。宿主在等就先让它读完。
                _pixelGate.YieldToHost();
                lock (_pixelGate.Lock)
                {
                    _lockThread = Environment.CurrentManagedThreadId;
                    try
                    {
                        long deadline = Stopwatch.GetTimestamp() + LockBudgetTicks;
                        while (TryTakeItem(reader, out WorkItem item))
                        {
                            RunItem(item);
                            if (Stopwatch.GetTimestamp() >= deadline || _pixelGate.HostWaiting)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        _lockThread = 0;
                    }
                }
                // 宿主回调与日志一律在放锁之后调:回调里同步等 UI 线程、而 UI 线程正在 ReadPixels 里等这把锁,就是死锁。
                FlushLog();
                FlushDamage();
                _host.Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // 收工。
        }
    }

    private void RunItem(WorkItem item)
    {
        // SYNC 的 Await 期间,这个客户端之后的请求暂存,条件成立时放回。
        if (_syncWaits.Count != 0 && DeferIfWaiting(item))
        {
            return;
        }
        // XTEST 的 FakeInput 带了延迟:到点之前这个客户端之后的请求暂存。
        if (_fakeInputDelays.Count != 0 && DeferIfFakeInputPending(item))
        {
            return;
        }
        // GrabServer 期间,别人的请求原样暂存,Ungrab 后按原顺序放回(协议「GrabServer」)。
        if (_serverGrabber is { } grabber && item.Client is { } client && !ReferenceEquals(client, grabber) && !client.Closed)
        {
            _deferred.Add(item);
            return;
        }
        long started = Stopwatch.GetTimestamp();
        // 每项工作一份预算(X-1):扣光时请求回 Alloc,而不是持着像素锁跑上几分钟、让宿主界面陪着冻住。
        WorkBudget.Begin(RequestWorkBudget);
        try
        {
            if (item.Request is { } request)
            {
                ExecuteRequest(item.Client!, request, item.RequestLength);
            }
            else
            {
                item.Action!();
            }
        }
        catch (Exception ex)
        {
            Log($"work item failed: {ex}");
        }
        finally
        {
            WorkBudget.End();
        }
        long elapsed = Stopwatch.GetTimestamp() - started;
        if (elapsed >= SlowItemTicks && ShouldLogFrequent())
        {
            // 预算之内的单项也可能慢(合法但昂贵的请求):点名客户端,宿主日志里才找得到是谁让界面卡了一下。
            string what = item.Request is { } r
                ? $"{item.Client} opcode {r[0]}{(r[0] >= XOpcode.FirstExtension ? $".{r[1]}" : "")}"
                : item.Client is { } owner ? $"{owner} (internal)" : "host or timer";
            Log($"slow work item: {what} held the pixel lock for {elapsed * 1000 / Stopwatch.Frequency} ms");
        }
    }

    /// <summary>一项工作持锁超过这么久就记一行日志(见 <see cref="RunItem" />)。</summary>
    private static readonly long SlowItemTicks = Stopwatch.Frequency / 4;   // 250 毫秒

    /// <summary>每项工作的工作量预算(<see cref="WorkBudget" />);测试可以调小。</summary>
    internal long RequestWorkBudget { get; set; } = WorkBudget.DefaultUnits;

    /// <summary>GrabServer 结束(或持有者断开):把暂存的请求按原顺序重新排进去。</summary>
    private void ReleaseServerGrab()
    {
        _serverGrabber = null;
        Requeue(_deferred);
        _deferred.Clear();
    }
}
