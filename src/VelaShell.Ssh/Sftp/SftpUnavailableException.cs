// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §3.3、§6.2、§九

using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Sftp;

/// <summary>SFTP 子系统起不来。</summary>
public sealed class SftpUnavailableException : SshException
{
    /// <summary>创建一个 SFTP 不可用异常。</summary>
    public SftpUnavailableException(string message, Exception? innerException = null)
        : base(SshFailureReason.Unsupported, SshPhase.Open, message, innerException)
    {
    }

    /// <summary>带原因码的版本（如握手超时是 <see cref="SshFailureReason.Timeout"/>）。</summary>
    internal SftpUnavailableException(SshFailureReason reason, string message, Exception? innerException = null)
        : base(reason, SshPhase.Open, message, innerException)
    {
    }
}
