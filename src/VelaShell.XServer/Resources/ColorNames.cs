// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「AllocNamedColor」「LookupColor」(名字不区分大小写;
//   颜色数据库由服务端提供)
//   Xlib - C Language X Interface 第 6.4 节「Color Strings」(#RGB 系列与 rgb:r/g/b 的写法)
//   颜色值:X.Org 的 rgb.txt(数据,随库分发,见 Resources/Data/README.md;与 CSS 冲突处是 X11 的值:
//   gray = 190、green = 0,255,0、maroon = 176,48,96、purple = 160,32,240)

using System.Globalization;

namespace VelaShell.XServer.Resources;

/// <summary>颜色名 → 16 位 RGB。</summary>
internal static class ColorNames
{
    private static readonly Dictionary<string, (byte R, byte G, byte B)> Table = Build();

    /// <summary>解析颜色名或数值写法;认不出来返回 null(BadName)。</summary>
    public static (ushort R, ushort G, ushort B)? Lookup(string spec)
    {
        string s = spec.Trim();
        if (s.StartsWith('#'))
        {
            return ParseHash(s[1..]);
        }
        if (s.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRgb(s[4..]);
        }
        string key = s.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (Table.TryGetValue(key, out (byte R, byte G, byte B) rgb))
        {
            return (Expand(rgb.R), Expand(rgb.G), Expand(rgb.B));
        }
        // grayN / greyN:N 从 0 到 100,按百分比取灰度。
        foreach (string prefix in (string[])["gray", "grey"])
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                && n is >= 0 and <= 100)
            {
                byte v = (byte)Math.Round(n * 255 / 100.0);
                return (Expand(v), Expand(v), Expand(v));
            }
        }
        return null;
    }

    private static ushort Expand(byte v) => (ushort)((v << 8) | v);

    /// <summary>#RGB / #RRGGBB / #RRRGGGBBB / #RRRRGGGGBBBB:每个分量取高位。</summary>
    private static (ushort, ushort, ushort)? ParseHash(string hex)
    {
        if (hex.Length is not (3 or 6 or 9 or 12) || !hex.All(Uri.IsHexDigit))
        {
            return null;
        }
        int n = hex.Length / 3;
        ushort Part(int i) => (ushort)(Convert.ToUInt32(hex.Substring(i * n, n), 16) << (16 - (4 * n)));
        return (Part(0), Part(1), Part(2));
    }

    /// <summary>rgb:r/g/b,每个分量 1–4 位十六进制,按位数缩放到 16 位。</summary>
    private static (ushort, ushort, ushort)? ParseRgb(string text)
    {
        string[] parts = text.Split('/');
        if (parts.Length != 3 || parts.Any(p => p.Length is < 1 or > 4 || !p.All(Uri.IsHexDigit)))
        {
            return null;
        }
        static ushort Scale(string p)
        {
            uint max = (1u << (4 * p.Length)) - 1;
            return (ushort)(Convert.ToUInt32(p, 16) * 0xFFFF / max);
        }
        return (Scale(parts[0]), Scale(parts[1]), Scale(parts[2]));
    }

    /// <summary>
    /// 颜色名表:X.Org 的 <c>rgb.txt</c>(约 750 项,含 red1–red4 这类编号变体与带空格的写法),作为数据随库分发
    /// (来源与许可见 <c>Resources/Data/README.md</c> 与 <c>NOTICE.md</c>)。键去掉空格、转小写 —— 查找时同样处理,
    /// 「dark slate gray」「DarkSlateGray」查到同一项。原先只内置了一百来个常用名,xterm 的 ANSI 调色板(red3、blue2……)、
    /// Emacs 的默认高亮(VioletRed4、Blue1……)都回 BadName。
    /// </summary>
    private static Dictionary<string, (byte, byte, byte)> Build()
    {
        Dictionary<string, (byte, byte, byte)> table = [with(StringComparer.Ordinal)];
        using Stream? stream = typeof(ColorNames).Assembly.GetManifestResourceStream("VelaShell.XServer.Resources.rgb.txt");
        if (stream is null)
        {
            return table;
        }
        using StreamReader reader = new(stream, System.Text.Encoding.Latin1);
        while (reader.ReadLine() is { } line)
        {
            // 「R G B<空白>名字」;! 开头的是注释。
            string[] parts = line.Split([' ', '\t'], 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || line.StartsWith('!')
                || !byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte r)
                || !byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte g)
                || !byte.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out byte b))
            {
                continue;
            }
            table.TryAdd(parts[3].Replace(" ", "", StringComparison.Ordinal).Trim().ToLowerInvariant(), (r, g, b));
        }
        return table;
    }
}
