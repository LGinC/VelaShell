// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §5.2  none
//   RFC 4252 §7    publickey
//   RFC 4252 §8    password
//   RFC 4256       keyboard-interactive
//   行为规格:      velashell-docs/zh/ssh/spec/04-authentication.md §2、§4、§5、§6

using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Auth;

/// <summary>密码认证。</summary>
public sealed class PasswordCredential : SshCredential
{
    private readonly Func<CancellationToken, ValueTask<string>> _provider;

    /// <summary>用一个固定密码构造。</summary>
    public PasswordCredential(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        _provider = _ => ValueTask.FromResult(password);
    }

    /// <summary>
    /// 用一个**按需取值**的回调构造。
    /// </summary>
    /// <remarks>
    /// 推荐这个重载：密码可以留在保险库 / 系统密钥链里，只在真正要用时才解出来，
    /// 而不是从配置加载那一刻起就躺在托管堆上。
    /// </remarks>
    public PasswordCredential(Func<CancellationToken, ValueTask<string>> passwordProvider) => _provider = passwordProvider ?? throw new ArgumentNullException(nameof(passwordProvider));

    /// <inheritdoc />
    public override string MethodName => SshProtocolNames.AuthPassword;

    /// <inheritdoc />
    public override string Label => "password";

    /// <summary>
    /// <c>keyboard-interactive</c> 只有一条不回显提示时，是否用这个密码自动作答。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认 <see langword="true"/>：很多服务器同时开放 <c>password</c> 与
    /// <c>keyboard-interactive</c>，而后者的唯一提示就是「Password:」。
    /// 这是 OpenSSH 客户端的实际行为，也是用户期望的。
    /// </para>
    /// <para>
    /// <b>真正的 2FA 场景要关掉它</b>：那里第一条提示可能就是动态码，
    /// 自动填密码只会白白消耗一次尝试（velashell-docs/zh/ssh/spec/04 §6.5）。
    /// </para>
    /// </remarks>
    public bool AlsoAnswerKeyboardInteractive { get; init; } = true;

    /// <summary>服务端连续不接受新密码时，最多问几次（<see cref="NewPasswordProvider"/> 最多被调用的次数）。</summary>
    public const int MaxNewPasswordAttempts = 3;

    /// <summary>
    /// 服务端要求先改密码（密码过期、管理员要求更换）时取新密码的回调；<see langword="null"/>（默认）就不改。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/04 §5.1〕收到 <c>SSH_MSG_USERAUTH_PASSWD_CHANGEREQ</c> 时调用；回调给出新密码，
    /// 库带着旧密码（这条凭据的密码）与新密码发改密码请求。改成了就接着认证（服务端通常直接放行），
    /// 服务端嫌新密码不好会再问一次（<see cref="SshPasswordChangeRequest.Attempt"/> 递增），最多 <see cref="MaxNewPasswordAttempts"/> 次。
    /// </para>
    /// <para>
    /// <b>回调返回 <see langword="null"/></b>：这次不改 —— 记为这条凭据的一次失败，方法都试完时报
    /// <see cref="Diagnostics.SshFailureReason.PasswordExpired"/>。<b>抛 <see cref="OperationCanceledException"/></b>：
    /// 用户不连了，与键盘交互的回调同一个口径（以 <c>Aborted</c> 结束）。
    /// </para>
    /// <para>
    /// <b>协议里没有「再输一次」这一步</b>：输错了的新密码会直接成为账户的密码。界面要让用户输两遍、自己比对。
    /// 改成之后，使用者自己保存着的旧密码就过时了 —— 要不要更新、怎么更新是使用者的事。
    /// </para>
    /// </remarks>
    public Func<SshPasswordChangeRequest, CancellationToken, ValueTask<string?>>? NewPasswordProvider { get; init; }

    /// <summary>取出密码。</summary>
    public ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken = default) =>
        _provider(cancellationToken);
}
