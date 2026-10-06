// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §5.1

using System.Net;

namespace VelaShell.Ssh.Forwarding;

/// <summary>一个转发器此刻的吞吐（应用字节 / 秒，本机视角）。</summary>
/// <param name="SentPerSecond">本机送进隧道的速率。</param>
/// <param name="ReceivedPerSecond">从隧道收回本机的速率。</param>
/// <remarks>取最近三个整秒的平均（不含正在走的这一秒）：比瞬时值稳，面板上不会一跳一跳。</remarks>
public readonly record struct ForwardThroughput(double SentPerSecond, double ReceivedPerSecond);

/// <summary>转发器上一条正在搬的连接（快照）。</summary>
/// <param name="Id">这条连接在本转发器内的序号（与连接事件里的一致）。</param>
/// <param name="Source">来源（本地转发是本机客户端；远程转发是服务端那头的 originator；可能为 <see langword="null"/>）。</param>
/// <param name="Target">目标的名字。</param>
/// <param name="StartedAt">开始搬运的时刻。</param>
/// <param name="BytesSent">到目前为止本机送进隧道的应用字节数。</param>
/// <param name="BytesReceived">到目前为止从隧道收回的应用字节数。</param>
public sealed record ForwardConnectionInfo(
    long Id, EndPoint? Source, string Target, DateTimeOffset StartedAt, long BytesSent, long BytesReceived);

/// <summary>按整秒分桶的滑动窗口：记字节，报最近三个整秒的平均速率。</summary>
internal sealed class ThroughputMeter(TimeProvider time)
{
    private const int WindowSeconds = 3;

    // 多留一个桶给「正在走的这一秒」。
    private readonly long[] _seconds = new long[WindowSeconds + 1];
    private readonly long[] _bytes = new long[WindowSeconds + 1];
    private readonly Lock _gate = new();

    private long NowSecond => (long)time.GetElapsedTime(0).TotalSeconds;

    public void Record(int bytes)
    {
        long second = NowSecond;
        int slot = (int)(second % _bytes.Length);
        lock (_gate)
        {
            if (_seconds[slot] != second)
            {
                _seconds[slot] = second;
                _bytes[slot] = 0;
            }
            _bytes[slot] += bytes;
        }
    }

    public double PerSecond
    {
        get
        {
            long current = NowSecond;
            long sum = 0;
            lock (_gate)
            {
                for (int i = 0; i < _bytes.Length; i++)
                {
                    if (_seconds[i] >= current - WindowSeconds && _seconds[i] < current)
                    {
                        sum += _bytes[i];
                    }
                }
            }
            return sum / (double)WindowSeconds;
        }
    }
}

/// <summary>令牌桶限速：一秒的额度可以一次用掉（突发），之后按速率补；不够时欠着、按欠额等。</summary>
/// <remarks>
/// 一个转发器的全部连接共用一个（每个方向一个）：「这条隧道最多占多少带宽」是转发器的事，不是单条连接的 ——
/// 否则开十条连接就是十倍。在搬运循环里、写到另一头之前等：等的时候不再读，背压自然传回发送方。
/// </remarks>
internal sealed class ByteRateLimiter
{
    private readonly long _bytesPerSecond;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private double _available;
    private long _last;

    public ByteRateLimiter(long bytesPerSecond, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerSecond, 1);
        _bytesPerSecond = bytesPerSecond;
        _time = time;
        _available = bytesPerSecond;
        _last = time.GetTimestamp();
    }

    /// <summary>记下要搬 <paramref name="bytes"/> 字节，额度不够就等到够为止。</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        TimeSpan delay = Reserve(bytes);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>记账并算出要等多久（测试直接看它）。</summary>
    internal TimeSpan Reserve(int bytes)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            double elapsed = _time.GetElapsedTime(_last, now).TotalSeconds;
            _last = now;
            _available = Math.Min(_bytesPerSecond, _available + (elapsed * _bytesPerSecond)) - bytes;
            return _available >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_available / _bytesPerSecond);
        }
    }
}
