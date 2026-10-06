// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §8    password,SSH_MSG_USERAUTH_PASSWD_CHANGEREQ
//   行为规格:      velashell-docs/zh/ssh/spec/04-authentication.md §5.1

namespace VelaShell.Ssh.Auth;

/// <summary>服务端要求先改密码（<c>SSH_MSG_USERAUTH_PASSWD_CHANGEREQ</c>），交给 <see cref="PasswordCredential.NewPasswordProvider"/> 的那一问。</summary>
public sealed class SshPasswordChangeRequest
{
    /// <summary>服务端给的提示，例如「密码已过期」「新密码太简单」；没给时是空串（不是 <see langword="null"/>）。</summary>
    /// <remarks>
    /// 来自<b>尚未认证</b>的对端，原样交出：摆到界面上之前要自己去掉控制字符、限长（可以用 <see cref="Diagnostics.PeerText.Sanitize"/>）。
    /// 与 <see cref="SshKeyboardChallenge"/> 的文字同一个口径。
    /// </remarks>
    public required string Prompt { get; init; }

    /// <summary>这是第几次要新密码，从 1 起。</summary>
    /// <remarks>
    /// 大于 1 表示上一次给的新密码服务端不接受（太简单、与旧密码太像……），<see cref="Prompt"/> 通常说了为什么。
    /// 最多问 <see cref="PasswordCredential.MaxNewPasswordAttempts"/> 次。
    /// </remarks>
    public required int Attempt { get; init; }
}
