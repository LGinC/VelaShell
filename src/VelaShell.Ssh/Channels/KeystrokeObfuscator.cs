// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL / ssh_config(5)  ObscureKeystrokeTiming(只取行为描述)、ping@openssh.com
//   行为规格:                          velashell-docs/zh/ssh/spec/05-connection.md §7.4

using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;

namespace VelaShell.Ssh.Channels;

/// <summary>
/// 按键时序混淆：交互 shell 里每次按键一个报文，按键之间的时间间隔在网上看得见 —— 是公认的侧信道，能推测出输入了什么。
/// 这里把输入攒起来、按固定节拍发；没有输入的节拍上发等长的 PING 当掩护，一直发到最后一次按键之后的一段随机时间。
/// </summary>
/// <remarks>
/// <para>
/// 使用者写的是这里的 <see cref="Writer"/>；节拍循环每一拍最多发一个报文：攒下的输入（一个 <c>CHANNEL_DATA</c>）或者一个掩护 PING。
/// 一下子写进来很多（粘贴、传文件）的不用藏，直接发 —— 混淆的是「打字」的节奏。
/// </para>
/// <para>
/// 掩护要对端认 PING（<c>ping@openssh.com</c>）；不认时只攒批、不发掩护 —— 对端不认的报文不发。闲着（最后一次按键之后的掩护期也过了）就一个报文都不发。
/// </para>
/// </remarks>
internal sealed class KeystrokeObfuscator : IAsyncDisposable
{
    /// <summary>一次写进来超过这么多就不是在打字了：直接发，不等节拍。</summary>
    internal const int PasteThreshold = 256;

    /// <summary>最后一次按键之后，掩护再持续多久（这个区间里随机取，免得「掩护停了」本身泄漏最后一次按键的时刻）。</summary>
    internal static readonly TimeSpan ChaffMin = TimeSpan.FromMilliseconds(500);

    internal static readonly TimeSpan ChaffMax = TimeSpan.FromMilliseconds(1500);

    private readonly Pipe _input = new(new PipeOptions(useSynchronizationContext: false));
    private readonly SshChannel _channel;
    private readonly Func<CancellationToken, ValueTask<bool>> _sendChaff;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <param name="channel">shell 的通道。</param>
    /// <param name="interval">节拍。</param>
    /// <param name="sendChaff">发一个掩护报文；对端不认 PING 时返回 <see langword="false"/>（不发）。</param>
    /// <param name="time">时钟（测试换）。</param>
    public KeystrokeObfuscator(
        SshChannel channel, TimeSpan interval, Func<CancellationToken, ValueTask<bool>> sendChaff, TimeProvider? time = null)
    {
        _channel = channel;
        _interval = interval;
        _sendChaff = sendChaff;
        _time = time ?? TimeProvider.System;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>使用者写输入的地方（代替通道自己的 stdin）。</summary>
    public PipeWriter Writer => _input.Writer;

    /// <summary>发过的掩护报文个数（测试与诊断用）。</summary>
    public int ChaffSent => Volatile.Read(ref _chaffSent);

    private int _chaffSent;

    /// <summary>输入到此为止：攒下的发完、再发 EOF。</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        await _input.Writer.CompleteAsync().ConfigureAwait(false);
        await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = _input.Reader;
        try
        {
            while (true)
            {
                // 闲着：等第一下按键，一个报文都不发。
                ReadResult read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                bool haveRead = true;
                long chaffUntil = 0;
                while (true)
                {
                    bool completed = false;
                    if (haveRead)
                    {
                        // 攒下的一次发出去（一个 CHANNEL_DATA），之后的掩护期从这一下按键算起。
                        ReadOnlySequence<byte> buffer = read.Buffer;
                        if (!buffer.IsEmpty)
                        {
                            await ForwardAsync(buffer, cancellationToken).ConfigureAwait(false);
                            chaffUntil = _time.GetTimestamp() + RandomChaffTicks();
                        }
                        reader.AdvanceTo(buffer.End);
                        completed = read.IsCompleted;
                    }
                    else if (_time.GetTimestamp() < chaffUntil)
                    {
                        // 这一拍没有输入：发一个掩护。
                        if (await _sendChaff(cancellationToken).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref _chaffSent);
                        }
                    }
                    else
                    {
                        break;   // 掩护期过了：回去闲着
                    }

                    if (completed)
                    {
                        await _channel.SendEofAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await Task.Delay(_interval, _time, cancellationToken).ConfigureAwait(false);

                    // 一拍到了：有什么拿什么；粘贴来的一大段不等节拍，直接发。
                    haveRead = reader.TryRead(out read);
                    while (haveRead && read.Buffer.Length > PasteThreshold && !read.IsCompleted)
                    {
                        await ForwardAsync(read.Buffer, cancellationToken).ConfigureAwait(false);
                        reader.AdvanceTo(read.Buffer.End);
                        haveRead = reader.TryRead(out read);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 收工。
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ForwardAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
    {
        PipeWriter output = _channel.StandardInput;
        foreach (ReadOnlyMemory<byte> segment in buffer)
        {
            output.Write(segment.Span);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private long RandomChaffTicks()
    {
        long min = (long)(ChaffMin.TotalSeconds * _time.TimestampFrequency);
        long max = (long)(ChaffMax.TotalSeconds * _time.TimestampFrequency);
        return RandomNumberGenerator.GetInt32(0, int.MaxValue) % Math.Max(1, max - min) + min;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 收工路径不抛。
        }
        await _input.Writer.CompleteAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
