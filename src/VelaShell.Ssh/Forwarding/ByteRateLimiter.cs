// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §5.1

namespace VelaShell.Ssh.Forwarding;

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
