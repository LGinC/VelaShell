// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL        auth-agent-req@openssh.com / auth-agent@openssh.com
//   RFC 4252 §7             publickey 认证的签名输入
//   OpenSSH PROTOCOL.sshsig SSHSIG 签名输入
//   行为规格:               velashell-docs/zh/ssh/spec/07-forwarding.md §七

using VelaShell.Ssh.HostKeys;

namespace VelaShell.Ssh.Forwarding;

/// <summary>远端请求用某把密钥签名时，交给使用者定夺。</summary>
/// <param name="Key">远端想用哪把钥。</param>
/// <param name="Comment">这把钥在 agent 里的注释（通常是私钥文件路径）。</param>
/// <remarks>
/// <para>
/// 只有钥和注释的话，使用者分不出这是自己刚在远端敲的 <c>git pull</c>，还是那台机器上有人在拿这把钥登录别处
/// （velashell-docs/zh/ssh/spec/07 §7.2.1）。所以被签的数据认得出来时，下面几项会填上「签来做什么」。
/// </para>
/// <para>
/// ⚠️ 这几项都来自远端。能当真的程度各不相同，见各自的说明 ——
/// <see cref="UserName"/> 与 <see cref="DestinationHostKey"/> 是签名本身绑死的，远端编不出别的值；
/// 文本已经清掉了控制字符并截短，可以直接放进界面。
/// </para>
/// </remarks>
public readonly record struct AgentSignatureRequest(SshPublicKey Key, string Comment)
{
    /// <summary>
    /// 被签的是一次 SSH 公钥登录（RFC 4252 §7 的认证请求）时，要以哪个用户登录；不是登录时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 服务端会核对签名输入里的用户名与请求里的是否一致，所以这个签名只能拿去以这个用户登录 —— 远端写个别的名字骗不了人。
    /// </remarks>
    public string? UserName { get; init; }

    /// <summary>登录请求里的服务名（几乎总是 <c>ssh-connection</c>）；不是登录时为 <see langword="null"/>。</summary>
    public string? Service { get; init; }

    /// <summary>
    /// 这次登录的目的主机的主机密钥；核实不了时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>两种来源，都是签名本身绑死的：</para>
    /// <list type="bullet">
    ///   <item>登录方法是 <c>publickey-hostbound-v00@openssh.com</c>：主机密钥就写在被签的数据里，服务端会核对它是不是自己的。</item>
    ///   <item>普通的 <c>publickey</c>：远端那一跳先经这条 agent 通道转来过一条 <c>session-bind@openssh.com</c>，
    ///   我们<b>自己验过</b>它的签名（主机密钥对会话标识的签名），而会话标识与被签的登录请求对得上。</item>
    /// </list>
    /// <para>
    /// 为 <see langword="null"/> 的常见原因：远端的 ssh 太旧、不发 <c>session-bind</c>；或者远端<b>故意</b>不发。
    /// 界面上应当说「核实不了」，而不是什么都不说。
    /// </para>
    /// </remarks>
    public SshPublicKey? DestinationHostKey { get; init; }

    /// <summary>
    /// 被签的是一份 SSHSIG 签名（<c>ssh-keygen -Y sign</c>、git 的 SSH 提交签名）时，它的命名空间（如 <c>git</c>、<c>file</c>）；
    /// 否则为 <see langword="null"/>。
    /// </summary>
    public string? SignatureNamespace { get; init; }

    /// <summary>被签的是不是一次 SSH 登录。</summary>
    public bool IsUserAuthentication => UserName is not null;
}
