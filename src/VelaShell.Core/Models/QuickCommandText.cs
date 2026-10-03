namespace VelaShell.Core.Models;

/// <summary>快捷命令正文的换行处理:保存时统一换行符,显示与发送时判断是不是多行命令。</summary>
public static class QuickCommandText
{
    /// <summary>
    /// 保存前整理正文:<c>\r\n</c> 与单独的 <c>\r</c> 一律换成 <c>\n</c>,再去掉首尾空白。
    /// </summary>
    /// <remarks>
    /// Windows 上多行输入框按回车插入的是 <c>\r\n</c>;不统一的话,同一条命令在不同平台上
    /// 存出来的正文不一样,经云同步来回一趟就成了「改过」。
    /// </remarks>
    public static string Normalize(string? text) =>
        (text ?? string.Empty).ReplaceLineEndings("\n").Trim();

    /// <summary>
    /// 去掉末尾换行后仍含换行 —— 发出去时中间的每个换行都等于按一次回车。
    /// </summary>
    public static bool IsMultiline(string? text) =>
        !string.IsNullOrEmpty(text) && text.AsSpan().TrimEnd("\r\n").IndexOfAny('\r', '\n') >= 0;

    /// <summary>正文的第一行(侧栏一行放不下整条多行命令,只显示这一行)。</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        int end = text.AsSpan().IndexOfAny('\r', '\n');
        return end < 0 ? text : text[..end];
    }

    /// <summary>正文的行数(末尾的换行不算一行)。</summary>
    public static int LineCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        ReadOnlySpan<char> span = text.AsSpan().TrimEnd("\r\n");
        int lines = 1;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '\n' || (span[i] == '\r' && (i + 1 >= span.Length || span[i + 1] != '\n')))
            {
                lines++;
            }
        }
        return lines;
    }
}
