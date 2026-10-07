// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   ICCCM 2.0 —— §2.7.1「Text Properties」(STRING 是 ISO Latin-1、COMPOUND_TEXT、UTF8_STRING,TEXT 由属主挑一种)
//   Compound Text Encoding(X Consortium,Version 1.1.xf86.1)—— 初始状态 GL = ASCII、GR = ISO 8859-1 右半;
//   ESC ( F / ESC ) F / ESC - F / ESC $ ( F / ESC $ ) F 指定字符集;ESC % G … ESC % @ 之间是 UTF-8;
//   ESC % / d M L 引出的扩展段长 (M − 128) × 128 + (L − 128) 字节;CSI 1 ] / CSI 2 ] / CSI ] 是方向
//
//   属性里的字符串按类型解码(标题、机器名、剪贴板)。COMPOUND_TEXT 只认 ASCII、Latin-1 与 UTF-8 段:
//   别的字符集(中日韩的多字节字符集、其它 96 字符集)与扩展段没有码表,每个字符换成 U+FFFD。

using System.Text;

namespace VelaShell.XServer.Protocol;

/// <summary>属性里的字符串的编码(看属性的类型)。</summary>
internal enum XTextEncoding
{
    /// <summary>STRING:ISO Latin-1(其它不认识的类型也按它)。</summary>
    Latin1,

    /// <summary>UTF8_STRING。</summary>
    Utf8,

    /// <summary>COMPOUND_TEXT。</summary>
    CompoundText,
}

/// <summary>X 的文本编码:STRING / UTF8_STRING / COMPOUND_TEXT 的解码与 COMPOUND_TEXT 的编码。</summary>
internal static class XText
{
    private const byte Esc = 0x1B, Csi = 0x9B;

    public static string Decode(ReadOnlySpan<byte> data, XTextEncoding encoding) => encoding switch
    {
        XTextEncoding.Utf8 => Encoding.UTF8.GetString(data),
        XTextEncoding.CompoundText => DecodeCompoundText(data),
        _ => XWire.Latin1.GetString(data),
    };

    /// <summary>字符集:GL / GR 各指定一个,每个字符占几个字节,认不认识。</summary>
    private readonly record struct CharSet(bool Known, int BytesPerChar);

    private static readonly CharSet Ascii = new(true, 1), Latin1Right = new(true, 1);

    public static string DecodeCompoundText(ReadOnlySpan<byte> data)
    {
        StringBuilder text = new(data.Length);
        CharSet gl = Ascii, gr = Latin1Right;
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i];
            if (b == Esc)
            {
                i = ReadEscape(data, i, text, ref gl, ref gr);
                continue;
            }
            if (b == Csi)
            {
                // 方向:CSI 1 ] / CSI 2 ] / CSI ](参数字节 0x30–0x3F、中间字节 0x20–0x2F,终结字节 0x40–0x7E)。
                i++;
                while (i < data.Length && data[i] is >= 0x20 and <= 0x3F)
                {
                    i++;
                }
                i++;
                continue;
            }
            if (b is 0x09 or 0x0A or 0x20)   // HT、NL;空格不随 GL 的字符集变
            {
                text.Append((char)b);
                i++;
                continue;
            }
            CharSet set = b switch
            {
                >= 0x20 and <= 0x7E => gl,
                >= 0xA0 => gr,
                _ => default,
            };
            if (set.BytesPerChar == 0)
            {
                i++;   // 其余控制字符:不该出现,跳过
                continue;
            }
            if (set.Known)
            {
                text.Append((char)b);   // ASCII 与 Latin-1 右半:字节就是码位
            }
            else
            {
                text.Append('\uFFFD');
            }
            i += set.BytesPerChar;
        }
        return text.ToString();
    }

    /// <summary>读一个 ESC 序列,改 GL / GR 或吞掉一段 UTF-8 / 扩展段;返回序列之后的位置。</summary>
    private static int ReadEscape(ReadOnlySpan<byte> data, int start, StringBuilder text, ref CharSet gl, ref CharSet gr)
    {
        int i = start + 1;
        int intermediates = i;
        while (i < data.Length && data[i] is >= 0x20 and <= 0x2F)
        {
            i++;
        }
        if (i >= data.Length)
        {
            return data.Length;
        }
        ReadOnlySpan<byte> mid = data[intermediates..i];
        byte final = data[i++];
        switch (mid)
        {
            case [(byte)'(']:
                gl = final is (byte)'B' or (byte)'J' ? Ascii : new CharSet(false, 1);
                break;
            case [(byte)')']:
                gr = new CharSet(false, 1);
                break;
            case [(byte)'-']:
                gr = final == (byte)'A' ? Latin1Right : new CharSet(false, 1);
                break;
            case [(byte)'$', (byte)'(']:
                gl = new CharSet(false, MultiByteWidth(final));
                break;
            case [(byte)'$', (byte)')']:
                gr = new CharSet(false, MultiByteWidth(final));
                break;
            case [(byte)'%'] when final == (byte)'G':
                {
                    // UTF-8 段,到 ESC % @ 为止。
                    int end = data[i..].IndexOf([Esc, (byte)'%', (byte)'@']);
                    int stop = end < 0 ? data.Length : i + end;
                    text.Append(Encoding.UTF8.GetString(data[i..stop]));
                    return end < 0 ? data.Length : stop + 3;
                }
            case [(byte)'%', (byte)'/'] when final is >= (byte)'0' and <= (byte)'4' && i + 2 <= data.Length:
                {
                    // 扩展段:M、L 两个字节给出后面(编码名、STX、数据)的长度。没有码表:整段换成一个 U+FFFD。
                    int length = ((data[i] & 0x7F) * 128) + (data[i + 1] & 0x7F);
                    text.Append('\uFFFD');
                    return Math.Min(data.Length, i + 2 + length);
                }
        }
        return i;
    }

    /// <summary>94^N 多字节字符集的每字符字节数:终结字节 0x40–0x5F 是 2、0x60–0x6F 是 3、0x70–0x7E 是 4。</summary>
    private static int MultiByteWidth(byte final) => final switch
    {
        <= 0x5F => 2,
        <= 0x6F => 3,
        _ => 4,
    };

    /// <summary>
    /// 编成 COMPOUND_TEXT:ASCII 与 Latin-1 原样(初始状态就是它们),别的字符放进 UTF-8 段(ESC % G … ESC % @)。
    /// 换行之外的控制字符去掉(COMPOUND_TEXT 只许 HT 与 NL)。
    /// </summary>
    public static byte[] EncodeCompoundText(string text)
    {
        List<byte> bytes = new(text.Length + 8);
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch <= 0xFF)
            {
                if (ch is '\t' or '\n' || ch is >= ' ' and <= '~' || ch >= 0xA0)
                {
                    bytes.Add((byte)ch);
                }
                i++;
                continue;
            }
            int start = i;
            while (i < text.Length && text[i] > 0xFF)
            {
                i++;
            }
            bytes.AddRange([Esc, (byte)'%', (byte)'G']);
            bytes.AddRange(Encoding.UTF8.GetBytes(text[start..i]));
            bytes.AddRange([Esc, (byte)'%', (byte)'@']);
        }
        return [.. bytes];
    }
}
