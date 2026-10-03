using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// #555:多行快捷命令的换行处理,以及内置命令的覆盖层(删掉的不显示、改过的由同标识的自定义命令顶替)。
/// </summary>
[TestClass]
[TestCategory("QuickCommands")]
public class QuickCommandOverlayTests
{
    [TestMethod]
    [DataRow("a\r\nb", "a\nb")]
    [DataRow("a\rb", "a\nb")]
    [DataRow("  ls -la \n", "ls -la")]
    [DataRow("\r\n\r\ncd /srv\r\ngit pull\r\n\r\n", "cd /srv\ngit pull")]
    public void Normalize_UnifiesLineEndings_AndTrims(string input, string expected) =>
        Assert.AreEqual(expected, QuickCommandText.Normalize(input));

    [TestMethod]
    public void Normalize_Null_IsEmpty() => Assert.AreEqual(string.Empty, QuickCommandText.Normalize(null));

    /// <summary>末尾的换行不算多行:发送前本来就会去掉,它不会让任何一行提前执行。</summary>
    [TestMethod]
    [DataRow("ls -la", false)]
    [DataRow("ls -la\n", false)]
    [DataRow("ls -la\r\n\r\n", false)]
    [DataRow("", false)]
    [DataRow("a\nb", true)]
    [DataRow("a\r\nb", true)]
    [DataRow("a\rb", true)]
    public void IsMultiline_IgnoresTrailingLineBreaks(string text, bool expected) =>
        Assert.AreEqual(expected, QuickCommandText.IsMultiline(text));

    [TestMethod]
    [DataRow("cd /srv\ngit pull", "cd /srv")]
    [DataRow("cd /srv\r\ngit pull", "cd /srv")]
    [DataRow("single", "single")]
    [DataRow("", "")]
    public void FirstLine_StopsAtTheFirstLineBreak(string text, string expected) =>
        Assert.AreEqual(expected, QuickCommandText.FirstLine(text));

    [TestMethod]
    [DataRow("single", 1)]
    [DataRow("a\nb\nc", 3)]
    [DataRow("a\r\nb\r\nc\r\n", 3)]
    [DataRow("a\rb", 2)]
    [DataRow("a\n\nb", 3)]
    [DataRow("", 0)]
    public void LineCount_CountsEveryLineButNotTheTrailingBreak(string text, int expected) =>
        Assert.AreEqual(expected, QuickCommandText.LineCount(text));

    [TestMethod]
    public void VisibleBuiltIns_WithoutOverlay_IsTheWholeCatalog() =>
        Assert.AreSequenceEqual(
            [.. QuickCommandCatalog.BuiltIns.Select(command => command.Id)],
            [.. QuickCommandCatalog.VisibleBuiltIns(new QuickCommandData()).Select(command => command.Id)]
        );

    /// <summary>删掉的(隐藏清单)与改过的(同标识的自定义命令)都不再以原样出现,其余照旧。</summary>
    [TestMethod]
    public void VisibleBuiltIns_SkipsHiddenAndOverriddenCommands()
    {
        QuickCommand hidden = QuickCommandCatalog.BuiltIns[0];
        QuickCommand overridden = QuickCommandCatalog.BuiltIns[1];
        var data = new QuickCommandData
        {
            HiddenBuiltInIds = [hidden.Id, Guid.NewGuid()],
            Commands = [new() { Id = overridden.Id, Name = "edited", CommandText = "echo edited" }],
        };

        Guid[] visible = [.. QuickCommandCatalog.VisibleBuiltIns(data).Select(command => command.Id)];

        Assert.DoesNotContain(hidden.Id, visible);
        Assert.DoesNotContain(overridden.Id, visible);
        Assert.HasCount(QuickCommandCatalog.BuiltIns.Count - 2, visible);
    }

    [TestMethod]
    public void IsBuiltInId_KnowsTheCatalog()
    {
        Assert.IsTrue(QuickCommandCatalog.BuiltIns.All(command => QuickCommandCatalog.IsBuiltInId(command.Id)));
        Assert.IsFalse(QuickCommandCatalog.IsBuiltInId(Guid.NewGuid()));
    }

    /// <summary>复制分组时顺序表要复制一份,不能与静态目录或原对象共用同一个列表。</summary>
    [TestMethod]
    public void CloneGroup_CopiesTheCommandOrder()
    {
        var group = new QuickCommandGroup { Id = Guid.NewGuid(), Name = "Ops", CommandOrder = [Guid.NewGuid()] };

        QuickCommandGroup clone = QuickCommandGroupCatalog.Clone(group);
        clone.CommandOrder.Add(Guid.NewGuid());

        Assert.HasCount(1, group.CommandOrder);
        Assert.HasCount(2, clone.CommandOrder);
    }
}
