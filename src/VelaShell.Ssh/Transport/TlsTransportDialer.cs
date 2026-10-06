// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 8446   TLS 1.3
//   RFC 6066 §3  SNI
//   行为规格:  velashell-docs/zh/ssh/spec/09-dialing.md §4.4

using System.Net.Security;
using System.Security.Authentication;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Transport;

/// <summary>在到这一跳的连接上套一层 TLS。</summary>
/// <param name="Options">SNI 与证书校验。</param>
/// <param name="Inner">怎么到达这一跳。</param>
/// <remarks>
/// TLS 不是单独的一跳：它就套在 <see cref="Inner"/> 连到的那个端点上，跳信息记的也是 <see cref="Inner"/> 的种类。
/// 握手交给 BCL 的 <see cref="SslStream"/>（协议版本由操作系统挑），本库只管参数与失败的归类。
/// </remarks>
internal sealed record TlsTransportDialer(SshTlsOptions Options, ISshTransportDialer Inner) : ISshTransportDialer, ISshDialKindSource
{
    /// <inheritdoc />
    public SshDialKind Kind => DialHops.KindOf(Inner);

    /// <inheritdoc />
    public async ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // 到不了这一跳：内层的失败原样往外走（它已经带着自己的跳信息）。
        Stream stream = await Inner.DialAsync(target, cancellationToken).ConfigureAwait(false);
        long startedAt = Environment.TickCount64;
        HandshakeTolerantStream carrier = new(stream);
        SslStream tls = new(carrier, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = Options.ServerName ?? target.EndPoint.Host,
                    RemoteCertificateValidationCallback = Options.RemoteCertificateValidation,
                },
                cancellationToken).ConfigureAwait(false);
            carrier.HandshakeDone = true;
            return tls;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && target.Deadline is { IsExpired: true })
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw Failure(SshFailureReason.Timeout, target, startedAt,
                $"{target.EndPoint} 接下了连接，却一直没有完成 TLS 握手。", ex);
        }
        catch (AuthenticationException ex)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw Failure(SshFailureReason.TlsFailed, target, startedAt,
                $"与 {target.EndPoint} 的 TLS 握手失败（服务端证书不可信或名字对不上，或者对端说的不是 TLS）：{ex.Message}", ex);
        }
        catch (IOException ex)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw Failure(SshFailureReason.TlsFailed, target, startedAt,
                $"{target.EndPoint} 在 TLS 握手中途断开了连接（对端多半说的不是 TLS）：{ex.Message}", ex);
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 只在握手期间容忍同步写的载体流：<see cref="SslStream"/> 握手失败时用<b>同步</b> <c>Write</c> 发告警，
    /// 而内存流、跳板的通道流（<c>SshChannelStream</c>）只支持异步写 —— 那一下抛的 <see cref="NotSupportedException"/>
    /// 会把真正的握手失败原因盖掉。告警只是礼节（连接随后就拆），发不出去就算了；握手之后同步写照常交给内层。
    /// </summary>
    private sealed class HandshakeTolerantStream(Stream inner) : Stream
    {
        /// <summary>握手完成之后不再吞同步写的失败。</summary>
        public bool HandshakeDone { get; set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                inner.Write(buffer);
            }
            catch (NotSupportedException) when (!HandshakeDone)
            {
                // 握手失败时的告警：发不出去就算了。
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
            try
            {
                inner.Flush();
            }
            catch (NotSupportedException) when (!HandshakeDone)
            {
            }
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private SshConnectException Failure(
        SshFailureReason reason, SshDialTarget target, long startedAt, string message, Exception inner) =>
        new(reason, SshPhase.Dialing, message, inner)
        {
            Hops = [DialHops.Hop(Kind, target.EndPoint, succeeded: false, startedAt, "TLS 握手失败")],
        };
}
