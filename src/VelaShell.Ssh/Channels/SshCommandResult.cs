// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.5   exec
//   RFC 4254 §6.10  exit-status / exit-signal
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §5.2、§5.4

namespace VelaShell.Ssh.Channels;

/// <summary>一条命令跑完之后的全部结果：退出状态、标准输出、标准错误。</summary>
/// <param name="ExitStatus">进程是怎么结束的。</param>
/// <param name="StandardOutput">标准输出（按 UTF-8 解码）。</param>
/// <param name="StandardError">标准错误（按 UTF-8 解码）。</param>
public readonly record struct SshCommandResult(
    SshExitStatus ExitStatus,
    string StandardOutput,
    string StandardError)
{
    /// <summary>退出码；被信号杀死或连接中断时为 <see langword="null"/>。</summary>
    public int? ExitCode => ExitStatus.ExitCode;

    /// <summary>是否以退出码 0 正常结束。</summary>
    public bool IsSuccess => ExitStatus.IsSuccess;

    /// <summary>消息里最多带 stderr 的多少个字符（取末尾）。</summary>
    internal const int MaxStandardErrorInMessage = 1024;

    /// <summary>不是成功就抛，异常消息里带上 stderr 的末尾。</summary>
    /// <param name="commandLine">写进异常消息的命令行；<see langword="null"/> 时只说「远端命令」。</param>
    /// <exception cref="SshCommandFailedException">命令没有以退出码 0 结束。</exception>
    /// <remarks>
    /// <para>
    /// stderr 要带上 —— 「命令失败了」而不说它抱怨了什么，等于让调用方再跑一遍去看。
    /// </para>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/08 §一〕<b>只带清洗、截断过的末尾</b>：stderr 是对端的输出，可以有几 MB、
    /// 带着终端转义序列，而消息最后会被打到终端、界面或日志里。原文完整地留在 <see cref="SshCommandFailedException.Result"/>。
    /// </para>
    /// </remarks>
    public SshCommandResult EnsureSuccess(string? commandLine = null)
    {
        if (IsSuccess)
        {
            return this;
        }

        // 消息是纯文本，不带 Markdown 记号（曾经用反引号括命令行，到了界面上原样显示成两个反引号）。
        string what = commandLine is null ? "远端命令" : $"远端命令「{commandLine}」";
        string stderr = Diagnostics.PeerText.SanitizeTail(StandardError, MaxStandardErrorInMessage);

        throw new SshCommandFailedException(
            stderr.Length == 0 ? $"{what} {ExitStatus}。" : $"{what} {ExitStatus}：{stderr}", this);
    }
}
