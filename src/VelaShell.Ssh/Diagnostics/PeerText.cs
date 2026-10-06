// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Text;

namespace VelaShell.Ssh.Diagnostics;

/// <summary>把对端给的文本放进消息之前先清一遍。</summary>
/// <remarks>
/// <para>
/// 版本标识串、<c>DISCONNECT</c> 的描述、拒绝开通道的理由、SFTP 的状态消息、代理的应答 ——
/// 这些都来自对端，很多时候来自一个<b>还没被认证</b>的对端。异常消息最后会被打到终端或界面上，
/// 原样拼进去就是一个转义序列注入面（<c>ESC ] 52</c> 改剪贴板、清屏伪造提示、<c>CR</c> 盖掉前半行）。
/// </para>
/// <para>
/// 只清<b>进消息</b>的那一份：原话照样放在专门的属性里（如 <c>PeerDescription</c>、<c>ServerMessage</c>），
/// 那些属性的文档都写明了是不可信输入。
/// </para>
/// <para>
/// 公开出来，是给要把那些原话自己拼进界面文案的使用者用的 —— 同一套规则，不必各写一份。
/// </para>
/// </remarks>
public static class PeerText
{
    /// <summary>默认最多留多少个字符。</summary>
    public const int DefaultMaxLength = 256;

    /// <summary>控制字符、<c>DEL</c>、C1 控制码与双向文本控制符换成 <c>?</c>，超长的截断。</summary>
    /// <remarks>可打印的 Unicode 原样保留 —— 服务端的中文提示是正常的。</remarks>
    public static string Sanitize(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        bool truncated = text.Length > maxLength;
        ReadOnlySpan<char> source = truncated ? text.AsSpan(0, maxLength) : text;

        StringBuilder builder = new(source.Length + 1);
        foreach (char c in source)
        {
            builder.Append(IsUnsafe(c) ? '?' : c);
        }

        if (truncated)
        {
            builder.Append('…');
        }
        return builder.ToString();
    }

    /// <summary>
    /// 多行的对端文本（stderr 一类）进消息：每个字符照 <see cref="Sanitize"/> 清，换行收成一个「 ⏎ 」接成一行；
    /// 超长时留<b>末尾</b>，前面加省略号。
    /// </summary>
    /// <remarks>
    /// 留末尾是因为出错的那一句通常在最后（编译器、包管理器先打一屏进度，最后才说失败在哪）。
    /// 换行不原样留：消息常常进日志，一个换行就能在日志里伪造一行别的记录。
    /// </remarks>
    public static string SanitizeTail(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        ReadOnlySpan<char> source = text.AsSpan().Trim();
        bool truncated = source.Length > maxLength;
        if (truncated)
        {
            int start = source.Length - maxLength;
            if (char.IsLowSurrogate(source[start]))
            {
                start++;   // 别从一个代理对的中间切开
            }
            source = source[start..];
        }

        StringBuilder builder = new(source.Length + 8);
        if (truncated)
        {
            builder.Append('…');
        }

        int contentStart = builder.Length;
        bool pendingBreak = false;
        foreach (char c in source)
        {
            if (c is '\n' or '\r')
            {
                pendingBreak = builder.Length > contentStart;
                continue;
            }

            if (pendingBreak)
            {
                builder.Append(" ⏎ ");
                pendingBreak = false;
            }
            builder.Append(IsUnsafe(c) ? '?' : c);
        }
        return builder.ToString();
    }

    private static bool IsUnsafe(char c) =>
        c is < ' '                                   // C0：ESC、CR、LF、BEL …
        or >= '\u007F' and <= '\u009F'       // DEL 与 C1（\u009B 在一些终端上就是 CSI）
        or >= '‪' and <= '‮'       // 双向嵌入 / 覆盖：日志里伪造显示顺序
        or >= '⁦' and <= '⁩';      // 双向隔离
}
