// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §2.1（建连期间的归类）

using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace VelaShell.Ssh.Diagnostics;

/// <summary>
/// 调用方回调（主机密钥策略、主机密钥类型偏好、横幅处理器）自己抛的异常：建连路上裹一层，到 <c>ConnectAsync</c> 出口原样还原。
/// </summary>
/// <remarks>
/// 建连期间流上的 <see cref="IOException"/> 归成「对端断开」，而且判为可重试（spec/08 §2.1）。
/// 回调里的 IO 错 —— 宿主的信任库出错、写不了它自己的文件 —— 不是对端断开，重试也不会好。
/// 曾经它们一起被那道按类型归类的 catch 改写成了 <c>ClosedByPeer</c>；裹这一层，就是为了让那几道 catch 认不出它。
/// 本库的异常（<see cref="SshException"/>）与取消不裹：它们本来就带着该有的口径。
/// </remarks>
internal sealed class SshCallbackFaultException : Exception
{
    private readonly ExceptionDispatchInfo _original;

    private SshCallbackFaultException(Exception original)
        : base(original.Message, original) => _original = ExceptionDispatchInfo.Capture(original);

    /// <summary>调一个调用方的回调；它自己抛的异常（取消与本库的异常除外）裹一层再往外抛。</summary>
    public static async ValueTask<T> InvokeAsync<T>(Func<ValueTask<T>> callback)
    {
        try
        {
            return await callback().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SshException))
        {
            throw new SshCallbackFaultException(ex);
        }
    }

    /// <inheritdoc cref="InvokeAsync{T}(Func{ValueTask{T}})"/>
    public static async ValueTask InvokeAsync(Func<ValueTask> callback)
    {
        try
        {
            await callback().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SshException))
        {
            throw new SshCallbackFaultException(ex);
        }
    }

    /// <summary>原样抛出回调当初抛的那个异常（保留它自己的调用栈）。</summary>
    [DoesNotReturn]
    public void ThrowOriginal() => _original.Throw();
}
