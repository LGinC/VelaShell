// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL.agent §2  restrict-destination-v00@openssh.com 的一跳
//   行为规格:                  velashell-docs/zh/ssh/spec/07-forwarding.md §7.3.2

using System.Text;

namespace VelaShell.Ssh.Keys;

/// <summary>目的地约束的一跳：放行「经 <see cref="Via"/>（<see langword="null"/> 即本机）、以 <see cref="UserName"/> 登到 <see cref="Destination"/>」这一段路。</summary>
/// <remarks>
/// <para>
/// 〔velashell-docs/zh/ssh/spec/07 §7.3.2〕一次签名要走的路，每一段都得有一跳放行：本机直接登 prod 要（本机 → prod）；
/// 先登 bastion、把 agent 转发过去再从 bastion 登 db，要（本机 → bastion）<b>和</b>（bastion → db）。
/// 转发主机上看得见这把钥，也要有一跳以它为起点。
/// </para>
/// <para>
/// 起点叫 <see cref="Via"/> 而不叫「来源」：一跳的意思是「<b>经</b>这台主机去用」。属性只读，校验在构造时做完；相等按内容比。
/// </para>
/// </remarks>
public sealed record SshAgentHop
{
    /// <summary>用户名的 UTF-8 字节上限。</summary>
    internal const int MaxUserNameBytes = 255;

    /// <summary>构造一跳。</summary>
    /// <param name="destination">这一段到达的主机。</param>
    /// <param name="userName">在终点上以哪个用户登录，可以带 <c>*</c> / <c>?</c> 通配；<see langword="null"/> 表示不限。</param>
    /// <param name="via">经哪台转发主机去用（钥经 agent 转发到了它上面）；<see langword="null"/> 表示从本机直接出发。</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">用户名给了却为空、UTF-8 超过 255 字节、含空白 / 控制字符 / <c>,</c> / <c>!</c>。</exception>
    /// <remarks>
    /// 用户名只在这把钥<b>登录</b>的那一段核对：钥只是经这一段的终点转发出去、拿去登别处时，不受这里的用户名影响。
    /// <c>,</c> 与 <c>!</c>（模式列表、取反）的语义没有对真 agent 核对过，不让调用方依赖说不清的东西。
    /// </remarks>
    public SshAgentHop(SshAgentHopHost destination, string? userName = null, SshAgentHopHost? via = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (userName is not null)
        {
            ValidateUserName(userName);
        }

        Destination = destination;
        UserName = userName;
        Via = via;
    }

    /// <summary>这一段到达的主机。</summary>
    public SshAgentHopHost Destination { get; }

    /// <summary>在终点上以哪个用户登录；<see langword="null"/> 表示不限。</summary>
    public string? UserName { get; }

    /// <summary>经哪台转发主机去用；<see langword="null"/> 表示从本机直接出发。</summary>
    public SshAgentHopHost? Via { get; }

    private static void ValidateUserName(string userName)
    {
        if (userName.Length == 0 || Encoding.UTF8.GetByteCount(userName) > MaxUserNameBytes)
        {
            throw new ArgumentException(
                $"用户名给了就要非空、UTF-8 不超过 {MaxUserNameBytes} 字节；不限用户就给 null。", nameof(userName));
        }
        if (userName.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or '!'))
        {
            throw new ArgumentException("用户名里不许有空白、控制字符、「,」与「!」（模式列表与取反没有核对过）。", nameof(userName));
        }
    }
}
