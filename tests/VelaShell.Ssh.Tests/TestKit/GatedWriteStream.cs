// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>写可以被卡住的流：用来模拟「对端不读我们发的东西」。</summary>
internal sealed class GatedWriteStream(Stream inner) : Stream
{
    private volatile TaskCompletionSource? _gate;
    private int _waiting;

    public int WritesWaiting => Volatile.Read(ref _waiting);

    public void Block() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Unblock() => Interlocked.Exchange(ref _gate, null)?.TrySetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_gate is { } gate)
        {
            Interlocked.Increment(ref _waiting);
            await gate.Task.WaitAsync(cancellationToken);
        }
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Unblock();
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Unblock();
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
