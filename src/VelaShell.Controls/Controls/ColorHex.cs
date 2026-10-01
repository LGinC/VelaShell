using System.Globalization;
using Avalonia.Media;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 设置里存的十六进制色值与 <see cref="Color" /> 之间的互转。
/// </summary>
/// <remarks>
/// 宿主里各处色值的口径并不完全一样:终端四色只认 <c>#RRGGBB</c>,强调色还认 <c>#RGB</c>,
/// 标签颜色走 <see cref="Color.TryParse(string, out Color)" />。取色器**写出去**一律是
/// <c>#RRGGBB</c>(大写) —— 三处都认;**读进来**放宽一些,手改过的配置也能回显。
/// </remarks>
public static class ColorHex
{
    /// <summary>
    /// 解析 <c>#RGB</c> / <c>#RRGGBB</c> / <c>#AARRGGBB</c>(井号可省,首尾空白忽略)。
    /// </summary>
    /// <param name="text">色值文本。</param>
    /// <param name="color">解析出的颜色;失败时为 default。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string digits = text.Trim();
        if (digits.StartsWith('#'))
        {
            digits = digits[1..];
        }
        // 只认纯十六进制:Color.TryParse 还认颜色名(red、Transparent…),
        // 而取色器的输入框里敲一个 "red" 应该当作没填对,而不是悄悄变成一个颜色。
        if (digits.Length is not (3 or 6 or 8) || !digits.All(Uri.IsHexDigit))
        {
            return false;
        }
        return Color.TryParse("#" + digits, out color);
    }

    /// <summary>格式化为 <c>#RRGGBB</c>(大写,丢掉 alpha)。</summary>
    /// <param name="color">颜色。</param>
    /// <returns>六位十六进制色值。</returns>
    public static string Format(Color color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
}
