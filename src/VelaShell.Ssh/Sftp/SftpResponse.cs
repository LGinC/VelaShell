// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §3、§4
//   行为规格:                    velashell-docs/zh/ssh/spec/06-sftp.md §5、§九

using System.Buffers;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Sftp;

/// <summary>一个已经收下来的 SFTP 应答。</summary>
/// <remarks>用完要 <see cref="Dispose"/> —— 载荷是从池里租的。</remarks>
internal sealed class SftpResponse : IDisposable
{
    private byte[]? _rented;
    private readonly int _length;

    internal SftpResponse(SftpMessageType type, uint requestId, byte[] rented, int length)
    {
        Type = type;
        RequestId = requestId;
        _rented = rented;
        _length = length;
    }

    /// <summary>应答类型。</summary>
    public SftpMessageType Type { get; }

    /// <summary>对应的请求编号。</summary>
    public uint RequestId { get; }

    /// <summary><b>request-id 之后</b>的内容。</summary>
    public ReadOnlySequence<byte> Payload =>
        _rented is null
            ? throw new ObjectDisposedException(nameof(SftpResponse))
            : new ReadOnlySequence<byte>(_rented, 0, _length);

    /// <summary>这条应答是不是一个状态码，是的话解出来。</summary>
    public bool TryGetStatus(out SftpStatusCode code, out string message)
    {
        if (Type != SftpMessageType.Status)
        {
            code = SftpStatusCode.Ok;
            message = "";
            return false;
        }

        (code, message) = SftpWire.ReadStatus(Payload);
        return true;
    }

    /// <summary>把应答对到期望的类型上：错误状态翻成 <see cref="SftpException"/>，别的类型是协议错误。</summary>
    /// <param name="path">出问题的路径，进异常。</param>
    /// <param name="operation">出问题的操作，进异常。</param>
    /// <param name="expected">
    /// 成功时应答该是什么类型：<see cref="SftpMessageType.Status"/> 表示只认 <c>OK</c>，
    /// 别的类型表示「错误状态之外，只认这一种」。
    /// </param>
    /// <remarks>
    /// <para>
    /// <c>EOF</c> 在这里也算错误 —— 走到这里的调用都期待一个数据应答或者 <c>OK</c>。
    /// 「读到末尾」「目录读完」这两处正常的 <c>EOF</c> 由读文件、读目录的代码自己先认出来，不经过这里。
    /// </para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/06 §三〕<b>类型对不上就是协议错误</b>，不按「不是错误状态就算成功」处理：
    /// 曾经 <c>WRITE</c> 收到任何非 STATUS 的应答都被记成已确认（<c>DurableLength</c> 失真，续传点跨过没写下的数据），
    /// <c>OPEN</c> 收到 DATA 会把数据当成句柄。
    /// </para>
    /// </remarks>
    /// <exception cref="SftpException">错误状态。</exception>
    /// <exception cref="SshProtocolException">应答的类型不是 <paramref name="expected"/>。</exception>
    public void ThrowIfError(string? path, SftpOperation operation, SftpMessageType expected)
    {
        if (TryGetStatus(out SftpStatusCode code, out string message) && code != SftpStatusCode.Ok)
        {
            throw new SftpException(code, message, path, operation);
        }
        ExpectType(path, operation, expected);
    }

    /// <summary>应答必须是 <paramref name="expected"/> 这种类型（错误状态已经由调用方认过）。</summary>
    /// <exception cref="SshProtocolException">不是。</exception>
    public void ExpectType(string? path, SftpOperation operation, SftpMessageType expected)
    {
        if (Type != expected)
        {
            throw new SshProtocolException(
                SshPhase.Open,
                $"SFTP {operation}{(path is null ? "" : $"（{PeerText.Sanitize(path)}）")} 期望 {expected} 应答，收到 {Type}（{(byte)Type}）。");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        byte[]? rented = Interlocked.Exchange(ref _rented, null);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
