// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-miller-ssh-agent  SSH Agent Protocol
//   OpenSSH PROTOCOL.agent  实现口径
//   行为规格:               velashell-docs/zh/ssh/design/architecture.md §8 第 5 项;velashell-docs/zh/ssh/spec/07-forwarding.md §七(加钥见 §7.3、目的地约束见 §7.3.2)

namespace VelaShell.Ssh.Keys;

/// <summary>往 agent 里加钥时附带的约束（<c>ssh-add -t</c> / <c>ssh-add -c</c> / <c>ssh-add -h</c>）。</summary>
/// <remarks>
/// 并非每个 agent 都支持约束 —— 不支持的会整条请求拒绝，而不是忽略约束；本库被拒就是被拒，从不去掉约束重试。
/// 相等按内容比（<see cref="AllowedHops"/> 逐项、按顺序）。
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

    /// <summary>
    /// 目的地约束（<c>ssh-add -h</c>）：只许走这几跳。<see langword="null"/>（默认）表示不加目的地约束。
    /// </summary>
    /// <exception cref="ArgumentException">给了却是空表、超过 64 条，或者有 <see langword="null"/> 元素。</exception>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/07 §7.3.2〕限定由 agent 执行：它靠会话声明（§7.4）知道每一次签名是为哪条路径上的哪台主机签的。
    /// 经转发使用要逐跳写全（本机 → 转发主机、转发主机 → 目的主机）；带这种约束的钥只能用于用户认证，不能拿来签 git 提交。
    /// </para>
    /// <para>
    /// <b>空表不许当「不限」</b>：它要么是「哪儿也不许去」（加进去就是废的），要么是使用者把勾全去掉了。不限就给 <see langword="null"/>。
    /// 条目原样保留、不去重。只给了它也发 <c>25</c> —— 约束接在 <c>17</c> 后面会被 agent 静默丢掉，钥就成了哪儿都能登。
    /// </para>
    /// </remarks>
    public IReadOnlyList<SshAgentHop>? AllowedHops
    {
        get;
        init
        {
            if (value is not null && (value.Count is 0 or > MaxHops || value.Any(hop => hop is null)))
            {
                throw new ArgumentException(
                    $"目的地约束要有 1–{MaxHops} 跳、不许有 null；不限目的地就给 null（空表不当「不限」）。", nameof(AllowedHops));
            }
            field = value is null ? null : Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>目的地约束最多多少跳。</summary>
    internal const int MaxHops = 64;

    internal bool IsEmpty => Lifetime is null && !IsConfirmationRequired && AllowedHops is null;

    /// <summary>按内容比较；<see cref="AllowedHops"/> 逐项、按顺序。</summary>
    public bool Equals(SshAgentKeyConstraints? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Lifetime == other.Lifetime
            && IsConfirmationRequired == other.IsConfirmationRequired
            && (AllowedHops is null
                ? other.AllowedHops is null
                : other.AllowedHops is not null && AllowedHops.SequenceEqual(other.AllowedHops)));

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Lifetime);
        hash.Add(IsConfirmationRequired);
        hash.Add(AllowedHops?.Count ?? -1);
        foreach (SshAgentHop hop in AllowedHops ?? [])
        {
            hash.Add(hop);
        }
        return hash.ToHashCode();
    }
}
