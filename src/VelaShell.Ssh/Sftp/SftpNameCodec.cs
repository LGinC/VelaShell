// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6   文件名是 string,没有规定编码
//   RFC 3629                          UTF-8
//   行为规格: velashell-docs/zh/ssh/spec/06-sftp.md §4.7

using System.Buffers;
using System.Text;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Sftp;

/// <summary>文件名与路径在线上的字节与 .NET 字符串之间怎么换（见 <see cref="SftpOptions.FileNameEncoding"/>）。</summary>
/// <remarks>
/// <para>
/// SFTP v3 没规定文件名的编码（v4 起才定为 UTF-8）。GBK、Shift-JIS、Latin-1 的老服务器、老 NAS、嵌入式设备很常见：
/// 名字里的字节不是合法的 UTF-8。曾经一律按 UTF-8 宽容解码 —— 解不开的字节变成 U+FFFD，再按 UTF-8 编回去已经是另一串字节，
/// 这些文件<b>打不开、删不掉、改不了名</b>，链接项被误报成断链。
/// </para>
/// <para>
/// 〔决策〕默认（UTF-8）<b>无损往返</b>：合法的 UTF-8 照常解；解不开的每个字节 <c>b</c>（只可能是 0x80–0xFF）单独变成一个
/// 孤立的低代理 <c>U+DC00 + b</c>；编码时把孤立的 <c>U+DC80</c>–<c>U+DCFF</c> 还原成那个字节。合法的 UTF-8 永远解不出孤立代理，
/// 所以这个映射不会和真实的名字撞上。界面上它们显示成替换字符，但拿这个字符串回去开、删、改名，到服务端的还是原来那串字节。
/// </para>
/// <para>
/// 选了别的编码（<c>GBK</c> 等）时按那个编码解与编；那种编码下非法的字节不保证往返 —— 选了它，就是认定服务端用的是它。
/// </para>
/// </remarks>
internal sealed class SftpNameCodec
{
    private const char EscapeBase = '\uDC00';
    private const char FirstEscape = '\uDC80';
    private const char LastEscape = '\uDCFF';

    /// <summary>UTF-8，解不开的字节无损转义。</summary>
    public static SftpNameCodec Utf8 { get; } = new(null);

    private readonly Encoding? _legacy;

    private SftpNameCodec(Encoding? legacy) => _legacy = legacy;

    /// <summary>按选项取编解码器；<see langword="null"/> 或 UTF-8 都是 <see cref="Utf8"/>。</summary>
    public static SftpNameCodec For(Encoding? encoding) =>
        encoding is null || encoding.CodePage == Encoding.UTF8.CodePage ? Utf8 : new SftpNameCodec(encoding);

    /// <summary>线上的字节 → 字符串。</summary>
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        if (_legacy is not null)
        {
            return _legacy.GetString(bytes);
        }

        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        StringBuilder builder = new(bytes.Length);
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out Rune rune, out int consumed) == OperationStatus.Done)
            {
                builder.Append(rune);
                bytes = bytes[consumed..];
            }
            else
            {
                // 每个解不开的字节单独转义，下一个字节重新开始解 —— 这样合法的部分不会被一起吞掉。
                builder.Append((char)(EscapeBase + bytes[0]));
                bytes = bytes[1..];
            }
        }
        return builder.ToString();
    }

    /// <summary>字符串 → 线上的字节。</summary>
    public byte[] Encode(string text)
    {
        if (_legacy is not null)
        {
            return _legacy.GetBytes(text);
        }

        if (!ContainsEscape(text))
        {
            return Encoding.UTF8.GetBytes(text);
        }

        ArrayBufferWriter<byte> output = new(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                Write(output, new Rune(c, text[i + 1]));
                i++;
            }
            else if (c is >= FirstEscape and <= LastEscape)
            {
                output.Write([(byte)(c - EscapeBase)]);
            }
            else
            {
                // 别的孤立代理照 UTF8Encoding 的老规矩换成 U+FFFD。
                Write(output, Rune.TryCreate(c, out Rune rune) ? rune : Rune.ReplacementChar);
            }
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>读一个名字字段。</summary>
    public string Read(ref SshDataReader reader, int maxLength)
    {
        ReadOnlySequence<byte> field = reader.ReadString(maxLength);
        return field.IsSingleSegment ? Decode(field.FirstSpan) : Decode(field.ToArray());
    }

    /// <summary>写一个名字字段。</summary>
    public void Write(ref SshDataWriter writer, string text) => writer.WriteString(Encode(text));

    /// <summary>有没有一个不在代理对里的 <c>U+DC80</c>–<c>U+DCFF</c>。</summary>
    private static bool ContainsEscape(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (c is >= FirstEscape and <= LastEscape)
            {
                return true;
            }
        }
        return false;
    }

    private static void Write(ArrayBufferWriter<byte> output, Rune rune)
    {
        Span<byte> buffer = output.GetSpan(4);
        int written = rune.EncodeToUtf8(buffer);
        output.Advance(written);
    }
}
