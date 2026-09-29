using System.Text;
using System.Text.RegularExpressions;

namespace VelaShell.Core.Models;

/// <summary>快捷命令里的一个变量占位:名字与(可选的)默认值。</summary>
/// <param name="Name">变量名,同名的占位共用一个值。</param>
/// <param name="DefaultValue">默认值;没写 <c>=</c> 时为空串。</param>
public sealed record QuickCommandVariable(string Name, string DefaultValue);

/// <summary>
/// 快捷命令的变量占位:<c>{{名字}}</c> 或 <c>{{名字=默认值}}</c>,执行前逐个询问,再替换进命令正文。
/// </summary>
/// <remarks>
/// <para>
/// 运维命令里本来就有大量双花括号 —— <c>docker inspect -f '{{.State.Status}}'</c>、
/// <c>kubectl -o go-template='{{range .items}}…{{end}}'</c>、Ansible 的 <c>{{ inventory_hostname }}</c>。
/// 这些必须原样发出去,所以占位的写法收得很窄:
/// </para>
/// <list type="bullet">
/// <item>名字只能是字母 / 下划线开头、由字母数字下划线连字符组成,<b>花括号里不许有空白</b>
/// —— <c>{{.Names}}</c>、<c>{{json .}}</c>、<c>{{ var }}</c> 都不算占位;</item>
/// <item>Go 模板的无参动作 <c>end</c> / <c>else</c> / <c>break</c> / <c>continue</c> 不算占位;</item>
/// <item>默认值里不许有花括号与换行。</item>
/// </list>
/// <para>
/// 占位写在命令正文里,不改 <see cref="QuickCommand" /> 的结构,Gist 同步与导入导出照旧。
/// </para>
/// </remarks>
public sealed partial class QuickCommandTemplate
{
    private static readonly HashSet<string> GoTemplateKeywords = new(StringComparer.Ordinal)
    {
        "end", "else", "break", "continue"
    };

    private QuickCommandTemplate(string text, IReadOnlyList<QuickCommandVariable> variables)
    {
        Text = text;
        Variables = variables;
    }

    /// <summary>命令原文(含占位)。</summary>
    public string Text { get; }

    /// <summary>按首次出现顺序排列的变量,同名只出现一次。</summary>
    public IReadOnlyList<QuickCommandVariable> Variables { get; }

    /// <summary>是否含有至少一个变量占位。</summary>
    public bool HasVariables => Variables.Count > 0;

    /// <summary>解析命令正文里的变量占位。</summary>
    public static QuickCommandTemplate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var variables = new List<QuickCommandVariable>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in PlaceholderRegex().Matches(text))
        {
            if (!IsPlaceholder(match))
            {
                continue;
            }
            string name = match.Groups["name"].Value;
            string defaultValue = match.Groups["default"].Value;
            if (indexByName.TryGetValue(name, out int index))
            {
                // 同名写了多处、只有后面那处带默认值:用第一个非空的默认值。
                if (variables[index].DefaultValue.Length == 0 && defaultValue.Length > 0)
                {
                    variables[index] = variables[index] with { DefaultValue = defaultValue };
                }
                continue;
            }
            indexByName[name] = variables.Count;
            variables.Add(new(name, defaultValue));
        }
        return new(text, variables);
    }

    /// <summary>
    /// 把变量替换成给定的值;没给的用默认值。值里的换行换成空格 ——
    /// 快捷命令只发正文不带回车,值里夹一个换行就等于替用户按了回车、命令提前执行。
    /// </summary>
    public string Render(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!HasVariables)
        {
            return Text;
        }
        var defaults = Variables.ToDictionary(v => v.Name, v => v.DefaultValue, StringComparer.Ordinal);
        return PlaceholderRegex().Replace(Text, match =>
        {
            if (!IsPlaceholder(match))
            {
                return match.Value;
            }
            string name = match.Groups["name"].Value;
            string value = values.TryGetValue(name, out string? given) ? given : defaults[name];
            return FlattenLineBreaks(value);
        });
    }

    private static bool IsPlaceholder(Match match) => !GoTemplateKeywords.Contains(match.Groups["name"].Value);

    private static string FlattenLineBreaks(string value)
    {
        if (value.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            return value;
        }
        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
            {
                continue;
            }
            builder.Append(c is '\r' or '\n' ? ' ' : c);
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"\{\{(?<name>[\p{L}_][\p{L}\p{N}_-]*)(?:=(?<default>[^{}\r\n]*))?\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
