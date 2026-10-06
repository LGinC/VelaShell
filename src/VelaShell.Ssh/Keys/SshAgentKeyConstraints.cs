// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七(加钥见 §7.3)

namespace VelaShell.Ssh.Keys;

/// <summary>往 agent 里加钥时附带的约束（<c>ssh-add -t</c> / <c>ssh-add -c</c>）。</summary>
/// <remarks>
/// 并非每个 agent 都支持约束 —— 不支持的会整条请求拒绝，而不是忽略约束。
/// </remarks>
public sealed record SshAgentKeyConstraints
{
    /// <summary>多久之后由 agent 自己删掉这把钥；<see langword="null"/> 表示不限。</summary>
    /// <remarks>按整秒发送，不足一秒的向上取整（不把有效期截短）。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 不为正，或超过 <see cref="uint.MaxValue"/> 秒（报文里是 uint32）。曾经 0 与负数被静默钳成 1 秒 —— 加进去的钥一秒后就没了。
    /// </exception>
    public TimeSpan? Lifetime
    {
        get;
        init => field = value is null || (value > TimeSpan.Zero && value <= MaxLifetime)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Lifetime), value, "有效期必须为正、且不超过 uint32 能表示的秒数；不限就给 null。");
    }

    /// <summary>有效期的上限：<see cref="uint.MaxValue"/> 秒。</summary>
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(uint.MaxValue);

    /// <summary>每次用这把钥签名时，由 agent 向使用者确认。</summary>
    public bool IsConfirmationRequired { get; init; }

    internal bool IsEmpty => Lifetime is null && !IsConfirmationRequired;
}
