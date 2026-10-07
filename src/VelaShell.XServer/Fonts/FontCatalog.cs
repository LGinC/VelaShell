// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「OpenFont」「ListFonts」两节(名字不区分大小写,
//   '*' 匹配任意串、'?' 匹配单个字符)
//   X Logical Font Description Conventions(XLFD)—— 14 个字段的字体名

using System.Globalization;

namespace VelaShell.XServer.Fonts;

/// <summary>
/// 服务端认识的全部核心字体:内置的 misc-fixed(BDF),加上虚拟的 cursor 字体。
/// </summary>
/// <remarks>
/// <para>
/// 每份 BDF 以两种编码出现:<c>…-iso10646-1</c>(双字节,UTF-8 locale 下的 xterm 要这个)与
/// <c>…-iso8859-1</c>(单字节,Latin-1)。两种编码的字形是同一份 —— Latin-1 正好是 Unicode 的前 256 个码位。
/// </para>
/// <para>
/// 别名(<c>fixed</c>、<c>6x13</c>、<c>9x15bold</c>…)与 X.Org misc 字体目录里的 fonts.alias 同一习惯:
/// 大量老程序只认这几个短名字,缺了它们 xterm 连默认字体都开不出来。没有数据的字号(8x13、5x7…)与 XLFD 里要的字号
/// 退到高度最接近的内置字体(见 <see cref="NearestSize" />),不回 BadName。
/// </para>
/// <para>字体数据在第一次打开时才解析,并缓存;列名字只读 BDF 的 FONT 一行。</para>
/// </remarks>
internal sealed class FontCatalog
{
    /// <summary>虚拟 cursor 字体里的字形数(cursorfont 的 0–152,偶数是形状、奇数是它的掩码)。</summary>
    public const int CursorGlyphCount = 154;

    private static readonly string[] BuiltInFiles = ["6x13", "6x13B", "9x15", "9x15B", "10x20"];

    private static readonly Dictionary<string, (string File, bool TwoByte)> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fixed"] = ("6x13", false),
        ["variable"] = ("6x13B", false),
        ["6x13"] = ("6x13", false),
        ["6x13bold"] = ("6x13B", false),
        ["9x15"] = ("9x15", false),
        ["9x15bold"] = ("9x15B", false),
        ["10x20"] = ("10x20", false),
        // X.Org misc 字体目录 fonts.alias 里的其余短名字:没有那个字号的数据,退到高度最接近、粗细相同的内置字体 ——
        // 原先 BadName,xterm -fn 8x13、Xaw 默认的 5x7 / 6x10 之类直接打不开(数据要更多字号见 xs_plan F21)。
        ["5x7"] = ("6x13", false),
        ["5x8"] = ("6x13", false),
        ["6x9"] = ("6x13", false),
        ["6x10"] = ("6x13", false),
        ["6x12"] = ("6x13", false),
        ["7x13"] = ("6x13", false),
        ["7x13bold"] = ("6x13B", false),
        ["7x14"] = ("6x13", false),
        ["7x14bold"] = ("6x13B", false),
        ["8x13"] = ("6x13", false),
        ["8x13bold"] = ("6x13B", false),
        ["8x16"] = ("9x15", false),
        ["9x18"] = ("9x15", false),
        ["9x18bold"] = ("9x15B", false),
        ["12x24"] = ("10x20", false),
    };

    private readonly Dictionary<string, (string File, bool TwoByte)> _names = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, BdfFont> _parsed = [with(StringComparer.Ordinal)];
    private readonly Dictionary<(string File, bool TwoByte), XFont> _fonts = [];
    /// <summary>合成出来的字体(没有 BDF):cursor 与 nil2。</summary>
    private readonly Dictionary<string, XFont> _synthetic = [with(StringComparer.OrdinalIgnoreCase)];

    private static readonly string[] SyntheticNames = ["cursor", "nil2"];

    public FontCatalog()
    {
        foreach (string file in BuiltInFiles)
        {
            string xlfd = ReadXlfd(file).ToLowerInvariant();
            if (xlfd.Length == 0)
            {
                continue;
            }
            // BDF 里的名字是 ISO10646-1;另派生一个 ISO8859-1 的名字。
            _names[xlfd] = (file, true);
            _names[ReplaceCharset(xlfd, "iso8859-1")] = (file, false);
        }
        foreach ((string alias, (string File, bool TwoByte) target) in Aliases)
        {
            _names[alias] = target;
        }
    }

    /// <summary>全部可用名字(含别名与合成字体)。</summary>
    public IEnumerable<string> AllNames => _names.Keys.Concat(SyntheticNames);

    /// <summary>按模式列名字(ListFonts)。</summary>
    public List<string> Match(string pattern, int max)
    {
        List<string> result = [];
        foreach (string name in AllNames.Order(StringComparer.Ordinal))
        {
            if (result.Count >= max)
            {
                break;
            }
            if (WildcardMatch(pattern, name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    /// <summary>按名字打开(OpenFont):名字可以带通配符,取第一个匹配的;找不到返回 null(BadName)。</summary>
    public XFont? Open(string name)
    {
        string? resolved = _names.ContainsKey(name) || SyntheticNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? name
            : Match(name, 1).FirstOrDefault() ?? NearestSize(name);
        if (resolved is null)
        {
            return null;
        }
        if (SyntheticNames.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            string key = resolved.ToLowerInvariant();
            if (!_synthetic.TryGetValue(key, out XFont? synthetic))
            {
                synthetic = key == "cursor" ? BuildCursorFont() : BuildNilFont();
                _synthetic[key] = synthetic;
            }
            return synthetic;
        }
        (string file, bool twoByte) = _names[resolved];
        if (_fonts.TryGetValue((file, twoByte), out XFont? cached))
        {
            return cached;
        }
        BdfFont bdf = Load(file);
        string canonical = twoByte ? bdf.FontName.ToLowerInvariant() : ReplaceCharset(bdf.FontName.ToLowerInvariant(), "iso8859-1");
        XFont font = Build(bdf, canonical, twoByte);
        _fonts[(file, twoByte)] = font;
        return font;
    }

    /// <summary>
    /// 完整的 14 字段 XLFD 要了一个我们没有的字号(<c>-misc-fixed-medium-r-normal--14-*-*-*-*-*-iso8859-1</c>):
    /// foundry、family、weight、slant、charset 都对得上的里面,取像素高度最接近的(一样近取小的)。原先直接 BadName。
    /// 尺寸按 PIXEL_SIZE,没给时按 POINT_SIZE 与 RESOLUTION_Y(没给按 75 dpi)换算;两个都没给就不猜(通配本来就该匹配上)。
    /// setwidth、add-style、spacing、平均宽度不看 —— 只有 misc-fixed 一族,宁可给一个近似的也别让程序打不开字体。
    /// </summary>
    /// <summary>就近回退时必须对得上的 XLFD 字段:FOUNDRY、FAMILY_NAME、WEIGHT_NAME、SLANT、CHARSET_REGISTRY、CHARSET_ENCODING。</summary>
    private static readonly int[] NearestSizeFields = [1, 2, 3, 4, 13, 14];

    private string? NearestSize(string pattern)
    {
        string[] want = pattern.ToLowerInvariant().Split('-');
        if (want.Length != 15 || want[0].Length != 0)
        {
            return null;
        }
        int target;
        if (int.TryParse(want[7], NumberStyles.None, CultureInfo.InvariantCulture, out int pixels) && pixels > 0)
        {
            target = pixels;
        }
        else if (int.TryParse(want[8], NumberStyles.None, CultureInfo.InvariantCulture, out int decipoints) && decipoints > 0)
        {
            int dpi = int.TryParse(want[10], NumberStyles.None, CultureInfo.InvariantCulture, out int y) && y > 0 ? y : 75;
            target = (int)Math.Round(decipoints / 10.0 * dpi / 72.27);
        }
        else
        {
            return null;
        }
        string? best = null;
        int bestDistance = int.MaxValue, bestPixels = 0;
        foreach (string name in _names.Keys)
        {
            string[] have = name.Split('-');
            if (have.Length != 15 || !int.TryParse(have[7], NumberStyles.None, CultureInfo.InvariantCulture, out int size)
                || !NearestSizeFields.All(i => WildcardMatch(want[i], have[i])))
            {
                continue;
            }
            int distance = Math.Abs(size - target);
            if (distance < bestDistance || (distance == bestDistance && size < bestPixels))
            {
                (best, bestDistance, bestPixels) = (name, distance, size);
            }
        }
        return best;
    }

    /// <summary>X 的字体名通配:'*' 任意串,'?' 单个字符,不区分大小写。</summary>
    internal static bool WildcardMatch(string pattern, string name)
    {
        int p = 0, n = 0, starP = -1, starN = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starN = n;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                n = ++starN;
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

    private static string ReplaceCharset(string xlfd, string charset)
    {
        // XLFD 的最后两个字段是 CHARSET_REGISTRY-CHARSET_ENCODING。
        int last = xlfd.LastIndexOf('-');
        int second = last > 0 ? xlfd.LastIndexOf('-', last - 1) : -1;
        return second < 0 ? xlfd : xlfd[..second] + "-" + charset;
    }

    private static Stream OpenResource(string file) =>
        typeof(FontCatalog).Assembly.GetManifestResourceStream($"VelaShell.XServer.Fonts.{file}.bdf")
        ?? throw new InvalidOperationException($"内置字体资源缺失:{file}.bdf");

    private static string ReadXlfd(string file)
    {
        using StreamReader reader = new(OpenResource(file));
        return BdfParser.ReadFontName(reader);
    }

    private BdfFont Load(string file)
    {
        if (!_parsed.TryGetValue(file, out BdfFont? bdf))
        {
            using StreamReader reader = new(OpenResource(file));
            bdf = BdfParser.Parse(reader);
            _parsed[file] = bdf;
        }
        return bdf;
    }

    private static XFont Build(BdfFont bdf, string name, bool twoByte)
    {
        Dictionary<int, XGlyph> glyphs = twoByte
            ? bdf.Glyphs.Where(kv => kv.Key <= 0xFFFF).ToDictionary()
            : bdf.Glyphs.Where(kv => kv.Key <= 0xFF).ToDictionary();

        int minB1 = 0, maxB1 = 0, minC2 = 255, maxC2 = 0;
        if (twoByte)
        {
            minB1 = glyphs.Keys.Min(k => k >> 8);
            maxB1 = glyphs.Keys.Max(k => k >> 8);
        }
        foreach (int code in glyphs.Keys)
        {
            minC2 = Math.Min(minC2, code & 0xFF);
            maxC2 = Math.Max(maxC2, code & 0xFF);
        }

        XCharInfo[] infos = [.. glyphs.Values.Select(g => g.Info)];
        XCharInfo minBounds = new(
            infos.Min(i => i.LeftBearing), infos.Min(i => i.RightBearing), infos.Min(i => i.Width),
            infos.Min(i => i.Ascent), infos.Min(i => i.Descent));
        XCharInfo maxBounds = new(
            infos.Max(i => i.LeftBearing), infos.Max(i => i.RightBearing), infos.Max(i => i.Width),
            infos.Max(i => i.Ascent), infos.Max(i => i.Descent));

        List<XFontProperty> props = [new("FONT", 0, name)];
        foreach ((string key, string value) in bdf.Properties)
        {
            if (key.StartsWith('_') || key is "FONT")
            {
                continue;
            }
            string v = !twoByte && key == "CHARSET_REGISTRY" ? "ISO8859" : value;
            props.Add(int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                ? new XFontProperty(key, number)
                : new XFontProperty(key, 0, v));
        }

        int expected = (maxB1 - minB1 + 1) * (maxC2 - minC2 + 1);
        return new XFont
        {
            Name = name,
            IsTwoByte = twoByte,
            Ascent = (short)bdf.FontAscent,
            Descent = (short)bdf.FontDescent,
            DefaultChar = (ushort)bdf.DefaultChar,
            Glyphs = glyphs,
            Properties = props,
            MinByte1 = (byte)minB1,
            MaxByte1 = (byte)maxB1,
            MinChar2 = (byte)minC2,
            MaxChar2 = (byte)maxC2,
            MinBounds = minBounds,
            MaxBounds = maxBounds,
            AllCharsExist = glyphs.Count == expected,
        };
    }

    /// <summary>
    /// nil2:X.Org misc 字体目录里那个什么都不画的小字体。xterm 拿它做隐形指针,
    /// 缺了它 xterm 照样能跑,只是会收到一条 BadName。这里合成一个:256 个字符,全是空字形。
    /// </summary>
    private static XFont BuildNilFont()
    {
        XCharInfo info = new(0, 1, 1, 1, 1);
        Dictionary<int, XGlyph> glyphs = [];
        for (int i = 0; i < 256; i++)
        {
            glyphs[i] = new XGlyph(info, new byte[2]);
        }
        return new XFont
        {
            Name = "nil2",
            Ascent = 1,
            Descent = 1,
            Glyphs = glyphs,
            MinChar2 = 0,
            MaxChar2 = 255,
            MinBounds = info,
            MaxBounds = info,
            AllCharsExist = true,
            Properties = [new("FONT", 0, "nil2")],
        };
    }

    /// <summary>
    /// 虚拟 cursor 字体:只有度量,没有字形。光标形状按字形号交给宿主映射成系统光标(架构 §7)。
    /// </summary>
    private static XFont BuildCursorFont()
    {
        XCharInfo info = new(0, 16, 16, 16, 0);
        Dictionary<int, XGlyph> glyphs = [];
        for (int i = 0; i < CursorGlyphCount; i++)
        {
            glyphs[i] = new XGlyph(info, new byte[16 * 16]);
        }
        return new XFont
        {
            Name = "cursor",
            Ascent = 16,
            Descent = 0,
            Glyphs = glyphs,
            MinChar2 = 0,
            MaxChar2 = CursorGlyphCount - 1,
            MinBounds = info,
            MaxBounds = info,
            AllCharsExist = true,
            Properties = [new("FONT", 0, "cursor")],
        };
    }
}
