using System.Text;
using System.Text.RegularExpressions;

namespace VelaShell.Tests.Design;

/// <summary>
/// 守门:headless UI 测试里两种「看着在测、其实没测 / 会卡死整套」的 <c>Dispatch</c> 写法(AGENTS.md「几条会让你踩坑的硬约束」)。
/// </summary>
/// <remarks>
/// <para>
/// <b>一、无返回值的 async lambda。</b><c>HeadlessUnitTestSession</c> 没有 <c>Func&lt;Task&gt;</c> 重载,
/// <c>Dispatch(async () =&gt; { … })</c> 绑到的是 <c>Dispatch&lt;Task&gt;(Func&lt;Task&gt;)</c>:外层任务在第一个
/// <c>await</c> 处就完成了,之后的断言异常没人接 —— 往用例体末尾插一条 <c>Assert.Fail</c> 照样通过。
/// 2026-07 起有 11 处这样写(plan.md §70),直到 §139 才改完;AGENTS.md 早就写着这条约束,但没拦住。
/// 改法:lambda 末尾 <c>return true;</c>,或用 <c>TestSupport/HeadlessUi.cs</c> 的 <c>RunOnUiAsync</c>。
/// </para>
/// <para>
/// <b>二、在 async 用例里 <c>await …Dispatch(…)</c>。</b>会话在 UI 线程上完成任务,<c>await</c> 的续体就地跑在
/// UI 线程上,MSTest 接着在那条线程上跑下一条;下一条若是 <c>GetAwaiter().GetResult()</c> 写法,就在 UI 线程上
/// 等 UI 线程,整套卡死(plan.md §139)。改用 <c>RunOnUiAsync</c>。
/// </para>
/// <para>
/// 扫描前先把注释与字符串字面量抹成空白(行号不变):说明文字里引用这两种写法是正当的,
/// 字符串里的大括号也不该算进 lambda 的配对。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed partial class HeadlessDispatchUsageTests
{
    [GeneratedRegex(@"\.Dispatch\(\s*async\b")]
    private static partial Regex AsyncDispatch { get; }

    [GeneratedRegex(@"\bawait\s+[\w.]+\.Dispatch\(")]
    private static partial Regex AwaitedDispatch { get; }

    [TestMethod]
    public void NoTestDispatchesAnAsyncLambdaWithoutAResult_OrAwaitsDispatch()
    {
        string testsRoot = Path.Combine(RepoRoot(), "tests");
        List<string> offenders = [];
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(testsRoot, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }
            scanned++;
            offenders.AddRange(Scan(File.ReadAllText(file)).Select(finding => $"{relative}:{finding}"));
        }

        Assert.IsGreaterThan(100, scanned, $"只扫到 {scanned} 个测试源文件 —— 多半是仓库根目录没找对,守门就失效了。");
        Assert.IsEmpty(offenders,
            "以下 Dispatch 写法会让断言失效或把整套测试卡死。async 用例体要么在 lambda 末尾 return true;,"
            + "要么改用 TestSupport/HeadlessUi.cs 的 RunOnUiAsync:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>扫描器本身的正反样例:它要是什么都扫不出来,上面那条就是另一条「怎么改都绿」。</summary>
    [TestMethod]
    [DataRow("_session.Dispatch(async () => { await Task.Yield(); Assert.Fail(); }, ct).GetAwaiter().GetResult();", true)]
    [DataRow("_session.Dispatch(async () => { await Task.Yield(); return true; }, ct).GetAwaiter().GetResult();", false)]
    // 只有内层 lambda 有 return,外层用例体照样没有返回值。
    [DataRow("_session.Dispatch(async () => { Func<int> f = () => { return 1; }; await Task.Yield(); }, ct);", true)]
    // 字符串里的大括号与 return 不算。
    [DataRow("_session.Dispatch(async () => { string s = \"} return x;\"; await Task.Yield(); }, ct);", true)]
    [DataRow("_session.Dispatch(async () => { string s = $\"{a}}\"; await Task.Yield(); return true; }, ct);", false)]
    // 注释里引用这两种写法是说明,不是代码。
    [DataRow("/// 千万别写 _session.Dispatch(async () => { … }) 或 await _session.Dispatch(…)\nint x = 1;", false)]
    [DataRow("// await _session.Dispatch(() => 1, ct);\nint x = 1;", false)]
    [DataRow("public async Task T() { await _session.Dispatch(() => { }, ct); }", true)]
    [DataRow("public Task T() => _session.RunOnUiAsync(async () => { await Task.Yield(); });", false)]
    public void Scanner_FlagsExactlyTheBadShapes(string source, bool flagged) =>
        Assert.AreEqual(flagged, Scan(source).Count > 0, source);

    /// <summary>扫一份源码,返回「行号  说明」形式的问题清单。</summary>
    private static List<string> Scan(string source)
    {
        string code = BlankCommentsAndStrings(source);
        List<string> findings = [];
        foreach (Match match in AsyncDispatch.Matches(code))
        {
            int arrow = code.IndexOf("=>", match.Index, StringComparison.Ordinal);
            int open = arrow < 0 ? -1 : code.IndexOf('{', arrow);
            if (open < 0 || !HasTopLevelReturn(code, open))
            {
                findings.Add($"{LineOf(code, match.Index)}  Dispatch(async …) 的用例体没有返回值");
            }
        }
        foreach (Match match in AwaitedDispatch.Matches(code))
        {
            findings.Add($"{LineOf(code, match.Index)}  在 async 用例里 await …Dispatch(…)");
        }
        return findings;
    }

    /// <summary>从 lambda 体的左括号起找配对的右括号,看中间有没有顶层(深度 1)的 return。</summary>
    private static bool HasTopLevelReturn(string code, int open)
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    if (--depth == 0)
                    {
                        return false;
                    }
                    break;
                case 'r' when depth == 1
                              && string.CompareOrdinal(code, i, "return", 0, 6) == 0
                              && (i == 0 || !IsIdentifierChar(code[i - 1]))
                              && i + 6 < code.Length && !IsIdentifierChar(code[i + 6]):
                    // `return;`(无值)对 async lambda 不改变它的类型,不算。
                    int next = i + 6;
                    while (next < code.Length && char.IsWhiteSpace(code[next]))
                    {
                        next++;
                    }
                    if (next < code.Length && code[next] != ';')
                    {
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// 把注释、字符串与字符字面量的内容换成空格(换行保留,行号不变)。
    /// 插值字符串的洞里也是代码,但这里不需要它 —— 整串抹掉,省得为大括号配对操心。
    /// </summary>
    private static string BlankCommentsAndStrings(string source)
    {
        StringBuilder result = new(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                int end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                Blank(result, source, i, end);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                Blank(result, source, i, end);
                i = end;
            }
            else if (c == '"' && string.CompareOrdinal(source, i, "\"\"\"", 0, 3) == 0)
            {
                int end = source.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 3;
                Blank(result, source, i, end);
                i = end;
            }
            else if (c == '"')
            {
                bool verbatim = i > 0 && (source[i - 1] == '@' || (source[i - 1] == '$' && i > 1 && source[i - 2] == '@'));
                bool interpolated = i > 0 && (source[i - 1] == '$' || (source[i - 1] == '@' && i > 1 && source[i - 2] == '$'));
                int end = EndOfString(source, i, verbatim, interpolated);
                Blank(result, source, i, end);
                i = end;
            }
            else if (c == '\'')
            {
                int end = i + 1;
                while (end < source.Length && source[end] != '\'')
                {
                    end += source[end] == '\\' ? 2 : 1;
                }
                end = Math.Min(source.Length, end + 1);
                Blank(result, source, i, end);
                i = end;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }
        return result.ToString();
    }

    /// <summary>字符串的结束位置(不含右引号之后)。插值洞里的嵌套字符串按深度跳过。</summary>
    private static int EndOfString(string source, int quote, bool verbatim, bool interpolated)
    {
        int holeDepth = 0;
        int i = quote + 1;
        while (i < source.Length)
        {
            char c = source[i];
            if (holeDepth > 0)
            {
                if (c == '"')
                {
                    i = EndOfString(source, i, false, false);
                    continue;
                }
                if (c == '{')
                {
                    holeDepth++;
                }
                else if (c == '}')
                {
                    holeDepth--;
                }
                i++;
                continue;
            }
            if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }
            if (interpolated && c == '{')
            {
                if (i + 1 < source.Length && source[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }
                holeDepth = 1;
                i++;
                continue;
            }
            if (interpolated && c == '}' && i + 1 < source.Length && source[i + 1] == '}')
            {
                i += 2;
                continue;
            }
            if (c == '"')
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return source.Length;
    }

    private static void Blank(StringBuilder result, string source, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            result.Append(source[i] is '\n' or '\r' ? source[i] : ' ');
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }
}
