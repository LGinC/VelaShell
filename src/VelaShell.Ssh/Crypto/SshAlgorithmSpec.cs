// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  Ciphers / KexAlgorithms / MACs / HostKeyAlgorithms 的 + - ^ 写法(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7.2

namespace VelaShell.Ssh.Crypto;

/// <summary>一条算法清单写法哪里不成立。</summary>
public enum SshAlgorithmSpecProblem
{
    /// <summary>没写任何名字（只有前缀，或者只有分隔符）。</summary>
    Empty,

    /// <summary>不认识的名字（拼错了，或者根本不是这一类的）。</summary>
    Unknown,

    /// <summary>认得、但本库没实现（CBC、3des、group1……写进去也谈不成）。</summary>
    Unimplemented,

    /// <summary>删完一个不剩。</summary>
    NothingLeft,
}

/// <summary>算法清单写法不成立（<see cref="SshAlgorithmSpec.Apply"/>）。</summary>
public sealed class SshAlgorithmSpecException : ArgumentException
{
    /// <summary>创建一个异常。</summary>
    public SshAlgorithmSpecException(SshAlgorithmCategory category, SshAlgorithmSpecProblem problem, string? name)
        : base(problem switch
        {
            SshAlgorithmSpecProblem.Empty => $"{category} 的清单里没有任何名字。",
            SshAlgorithmSpecProblem.Unimplemented => $"{category} 里的 {name} 本库没有实现（写进去也谈不成）。",
            SshAlgorithmSpecProblem.NothingLeft => $"{category} 的清单删完一个不剩。",
            _ => $"{category} 里不认识 {name}。",
        })
    {
        Category = category;
        Problem = problem;
        Name = name;
    }

    /// <summary>哪一类。</summary>
    public SshAlgorithmCategory Category { get; }

    /// <summary>哪里不成立。</summary>
    public SshAlgorithmSpecProblem Problem { get; }

    /// <summary>出问题的那个名字（<see cref="SshAlgorithmSpecProblem.Unknown"/> / <see cref="SshAlgorithmSpecProblem.Unimplemented"/> 时）。</summary>
    public string? Name { get; }
}

/// <summary>
/// OpenSSH <c>ssh_config</c> 的算法清单写法：<c>+a,b</c> 追加到默认之后、<c>-a,b</c> 从默认里删掉（可带 <c>*</c> / <c>?</c> 通配）、
/// <c>^a,b</c> 提到最前，不带前缀则整个替换。
/// </summary>
/// <remarks>
/// <para>
/// 「能写哪些名字」以 <see cref="SshAlgorithmCatalog"/> 为准：实现了的照常收（含默认不开的老算法 —— <c>+ssh-rsa</c> 正是这么用的），
/// 常见却没实现的报 <see cref="SshAlgorithmSpecProblem.Unimplemented"/>，都不是的报 <see cref="SshAlgorithmSpecProblem.Unknown"/>。
/// 不带通配的删除项也要是认得的名字：拼错了的 <c>-chacha20-poly1305</c> 什么都删不掉，用户却以为已经关了。
/// </para>
/// <para>宿主的连接配置（自定义算法清单）与 <c>ssh_config</c> 的导入用的是这同一份解析。</para>
/// </remarks>
public static class SshAlgorithmSpec
{
    /// <summary>按写法把一条清单作用到 <paramref name="defaults"/> 上。</summary>
    /// <param name="category">类别（决定认哪些名字）。</param>
    /// <param name="spec">写法；空白 = 原样用 <paramref name="defaults"/>。</param>
    /// <param name="defaults">「默认」是哪一份。</param>
    /// <returns>得到的清单。</returns>
    /// <exception cref="SshAlgorithmSpecException">写法不成立。</exception>
    public static IReadOnlyList<string> Apply(SshAlgorithmCategory category, string? spec, IReadOnlyList<string> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (string.IsNullOrWhiteSpace(spec))
        {
            return defaults;
        }

        string text = spec.Trim();
        char op = text[0] is '+' or '-' or '^' ? text[0] : '\0';
        string[] names = (op == '\0' ? text : text[1..])
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            throw new SshAlgorithmSpecException(category, SshAlgorithmSpecProblem.Empty, null);
        }

        IReadOnlyList<string> available = SshAlgorithmCatalog.Implemented(category);
        foreach (string name in names)
        {
            if (op == '-' && IsPattern(name))
            {
                continue;
            }
            if (!available.Contains(name, StringComparer.Ordinal))
            {
                throw new SshAlgorithmSpecException(
                    category,
                    SshAlgorithmCatalog.KnownUnimplemented(category).Contains(name, StringComparer.Ordinal)
                        ? SshAlgorithmSpecProblem.Unimplemented
                        : SshAlgorithmSpecProblem.Unknown,
                    name);
            }
        }

        string[] distinct = [.. names.Distinct(StringComparer.Ordinal)];
        string[] result = op switch
        {
            '+' => [.. defaults, .. distinct.Where(n => !defaults.Contains(n, StringComparer.Ordinal))],
            '^' => [.. distinct, .. defaults.Where(a => !distinct.Contains(a, StringComparer.Ordinal))],
            '-' => [.. defaults.Where(a => !distinct.Any(p => Matches(p, a)))],
            _ => distinct,
        };
        return result.Length > 0 ? result : throw new SshAlgorithmSpecException(category, SshAlgorithmSpecProblem.NothingLeft, null);
    }

    /// <summary>把一条写法作用到 <paramref name="set"/> 的对应清单上（加密与 MAC 两个方向一起）。</summary>
    /// <param name="set">原来的清单（它的这一类就是「默认」）。</param>
    /// <param name="category">类别；<see cref="SshAlgorithmCategory.Compression"/> 不在此列（由开关管）。</param>
    /// <param name="spec">写法。</param>
    /// <exception cref="SshAlgorithmSpecException">写法不成立。</exception>
    /// <exception cref="ArgumentOutOfRangeException">类别是压缩。</exception>
    public static SshAlgorithmSet ApplyTo(SshAlgorithmSet set, SshAlgorithmCategory category, string? spec)
    {
        ArgumentNullException.ThrowIfNull(set);
        switch (category)
        {
            case SshAlgorithmCategory.KeyExchange:
                return set with { KeyExchange = Apply(category, spec, set.KeyExchange) };
            case SshAlgorithmCategory.HostKey:
                return set with { HostKey = Apply(category, spec, set.HostKey) };
            case SshAlgorithmCategory.Encryption:
                IReadOnlyList<string> ciphers = Apply(category, spec, set.EncryptionClientToServer);
                return set with { EncryptionClientToServer = ciphers, EncryptionServerToClient = ciphers };
            case SshAlgorithmCategory.Mac:
                IReadOnlyList<string> macs = Apply(category, spec, set.MacClientToServer);
                return set with { MacClientToServer = macs, MacServerToClient = macs };
            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, "压缩不用清单写法，由开关管。");
        }
    }

    private static bool IsPattern(string name) => name.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary><c>*</c> 配任意多个字符、<c>?</c> 配一个（大小写敏感，与协议一致）。</summary>
    private static bool Matches(string pattern, string value)
    {
        if (!IsPattern(pattern))
        {
            return string.Equals(pattern, value, StringComparison.Ordinal);
        }

        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v]))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }
}
