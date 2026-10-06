// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §4.6

namespace VelaShell.Ssh.Forwarding;

/// <summary>
/// 远程动态转发（<c>-R [bind:]port</c> 不给目标）的放行名单：远端经它能让本机连哪些 <c>主机:端口</c>
/// —— 即 ssh_config(5) 的 <c>PermitRemoteOpen</c>。
/// </summary>
/// <remarks>
/// <para>
/// 〔决策 velashell-docs/zh/ssh/spec/07 §4.6〕<b>没有默认值。</b>远程动态转发等于把本机变成远端的 SOCKS 代理，
/// 本机能到的内网它就能到 —— 放哪些出去必须由调用方明说。要全放，显式给 <see cref="Any"/>。
/// </para>
/// <para>
/// 每条规则是 <c>主机:端口</c>：主机可带 <c>*</c> / <c>?</c> 通配（不分大小写），IPv6 字面量写在方括号里；
/// 端口是数字或 <c>*</c>。<b>按远端在 SOCKS 握手里给的名字比，不先解析</b>：规则写 IP，远端给域名就对不上（反之亦然）——
/// 宁可错拒，不让一个解析到内网地址的域名绕过名单。
/// </para>
/// </remarks>
public sealed class RemoteOpenPolicy
{
    private readonly (string Host, int? Port)[] _rules;
    private readonly bool _any;

    private RemoteOpenPolicy(bool any, (string Host, int? Port)[] rules)
    {
        _any = any;
        _rules = rules;
    }

    /// <summary>什么都放（OpenSSH 的 <c>PermitRemoteOpen any</c>）。本机能到的地方远端都能到。</summary>
    public static RemoteOpenPolicy Any { get; } = new(any: true, []);

    /// <summary>什么都不放（<c>PermitRemoteOpen none</c>）。</summary>
    public static RemoteOpenPolicy None { get; } = new(any: false, []);

    /// <summary>只放名单上的。</summary>
    /// <param name="rules">规则，形如 <c>localhost:8080</c>、<c>*.corp.example:443</c>、<c>10.0.0.?:*</c>、<c>[::1]:22</c>。</param>
    /// <exception cref="ArgumentException">规则写法不对（没有端口、端口不在 1–65535、主机为空）。</exception>
    public static RemoteOpenPolicy Allow(params IEnumerable<string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new RemoteOpenPolicy(any: false, [.. rules.Select(ParseRule)]);
    }

    /// <summary>放不放行这个目标。</summary>
    /// <param name="host">远端给的主机（域名或 IP 字面量，原样）。</param>
    /// <param name="port">远端给的端口。</param>
    public bool Permits(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _any || _rules.Any(rule => (rule.Port is null || rule.Port == port) && Matches(rule.Host, host));
    }

    /// <summary>给人看的写法（与 ssh_config 的一致）。</summary>
    public override string ToString() =>
        _any ? "any" : _rules.Length == 0 ? "none"
        : string.Join(' ', _rules.Select(r => $"{(r.Host.Contains(':') ? $"[{r.Host}]" : r.Host)}:{(r.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "*")}"));

    private static (string Host, int? Port) ParseRule(string rule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        string text = rule.Trim();

        string host;
        string port;
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != ':')
            {
                throw new ArgumentException($"放行规则「{rule}」写法不对：IPv6 要写成 [地址]:端口。", nameof(rule));
            }
            host = text[1..close];
            port = text[(close + 2)..];
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon <= 0)
            {
                throw new ArgumentException($"放行规则「{rule}」写法不对：要写成 主机:端口（端口可以是 *）。", nameof(rule));
            }
            host = text[..colon];
            port = text[(colon + 1)..];
        }

        if (host.Length == 0)
        {
            throw new ArgumentException($"放行规则「{rule}」没有主机。", nameof(rule));
        }
        if (port == "*")
        {
            return (host, null);
        }
        return int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number)
            && number is >= 1 and <= 65535
            ? (host, number)
            : throw new ArgumentException($"放行规则「{rule}」的端口要是 1–65535 或 *。", nameof(rule));
    }

    /// <summary><c>*</c> 配任意多个字符、<c>?</c> 配一个，不分大小写。</summary>
    private static bool Matches(string pattern, string value)
    {
        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
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
