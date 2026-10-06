// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §5.1

namespace VelaShell.Ssh.Forwarding;

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
