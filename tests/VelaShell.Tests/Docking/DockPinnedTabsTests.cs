using VelaShell.Docking.Model;

namespace VelaShell.Tests.Docking;

/// <summary>
/// 固定标签(#521)的模型语义:固定的永远排在组的最前面,批量关闭绕开它们,
/// 拖动与跨组移动都守住"固定在前"这条顺序。
/// </summary>
[TestClass]
[TestCategory("Docking")]
public sealed class DockPinnedTabsTests
{
    private sealed class TestDocument(string title) : DockDocument
    {
        public override string ToString() => title;
    }

    /// <summary>主组里依次放入 a..e(激活在最后一个)。</summary>
    private static (DockWorkspace Workspace, TestDocument[] Docs) Scene(int count = 5)
    {
        var workspace = new DockWorkspace();
        TestDocument[] docs = [.. Enumerable.Range(0, count).Select(i => new TestDocument(((char)('a' + i)).ToString()))];
        foreach (TestDocument doc in docs)
        {
            workspace.AddDocument(doc);
        }
        return (workspace, docs);
    }

    private static string Order(DockGroup group) =>
        string.Concat(group.Documents.Select(d => d.IsPinned ? d.ToString()!.ToUpperInvariant() : d.ToString()));

    [TestMethod]
    public void Pin_MovesToEndOfPinnedBlock_UnpinMovesToStartOfTheRest()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene();
        DockGroup group = workspace.PrimaryGroup;

        workspace.SetPinned(d[3], pinned: true);
        Assert.AreEqual("Dabce", Order(group), "固定 = 挪到固定区末尾(此时固定区是空的,就是最前面)");

        workspace.SetPinned(d[1], pinned: true);
        Assert.AreEqual("DBace", Order(group), "第二个固定的排在第一个后面,而不是抢到最前");

        workspace.SetPinned(d[3], pinned: false);
        Assert.AreEqual("Bdace", Order(group), "取消固定 = 挪到普通区开头,离原位最近的合法位置");
        Assert.AreEqual(1, group.PinnedCount);
    }

    [TestMethod]
    public void SetPinned_ToTheSameState_IsANoOp()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene();
        workspace.SetPinned(d[2], pinned: false);
        Assert.AreEqual("abcde", Order(workspace.PrimaryGroup));

        workspace.SetPinned(d[2], pinned: true);
        workspace.SetPinned(d[2], pinned: true);
        Assert.AreEqual("Cabde", Order(workspace.PrimaryGroup));
    }

    [TestMethod]
    public void CloseFamily_LeavesPinnedTabsAlone()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene();
        workspace.SetPinned(d[0], pinned: true); // A b c d e

        workspace.CloseOtherDocuments(d[2]);
        Assert.AreEqual("Ac", Order(workspace.PrimaryGroup), "「关闭其他」:固定的与自己都留下");

        (workspace, d) = Scene();
        workspace.SetPinned(d[0], pinned: true);
        workspace.CloseAllDocuments(d[2]);
        Assert.AreEqual("A", Order(workspace.PrimaryGroup), "「关闭所有」:只剩固定的");

        (workspace, d) = Scene();
        workspace.SetPinned(d[0], pinned: true);
        workspace.CloseLeftDocuments(d[3]);
        Assert.AreEqual("Ade", Order(workspace.PrimaryGroup), "「关闭左侧」:左边的固定标签不动");

        (workspace, d) = Scene();
        workspace.SetPinned(d[0], pinned: true);
        workspace.SetPinned(d[1], pinned: true); // A B c d e
        workspace.CloseRightDocuments(d[0]);
        Assert.AreEqual("AB", Order(workspace.PrimaryGroup), "「关闭右侧」:右边的固定标签不动");
    }

    [TestMethod]
    public void CloseAll_OnAPinnedTab_KeepsItToo()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene(3);
        workspace.SetPinned(d[1], pinned: true);

        workspace.CloseAllDocuments(d[1]);

        Assert.AreEqual("B", Order(workspace.PrimaryGroup));
    }

    [TestMethod]
    public void ExplicitClose_StillClosesAPinnedTab()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene(3);
        workspace.SetPinned(d[1], pinned: true);

        workspace.RequestClose(d[1]);

        Assert.AreEqual("ac", Order(workspace.PrimaryGroup), "冲着固定标签本人的关闭(右键「关闭」、Ctrl+W)照常生效");
    }

    [TestMethod]
    public void MoveDocument_KeepsEachKindInItsOwnBlock()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene();
        workspace.SetPinned(d[0], pinned: true);
        workspace.SetPinned(d[1], pinned: true); // A B c d e

        workspace.MoveDocument(d[4], 0);
        Assert.AreEqual("ABecd", Order(workspace.PrimaryGroup), "普通标签拖不进固定区,停在它后面第一位");

        workspace.MoveDocument(d[0], 4);
        Assert.AreEqual("BAecd", Order(workspace.PrimaryGroup), "固定标签拖不出固定区,停在它的末尾");
    }

    [TestMethod]
    public void DockToAnotherGroup_KeepsPinnedState_AndLandsInTheRightBlock()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene(4);
        workspace.SplitDocument(d[3], DockOrientation.Horizontal);
        DockGroup right = workspace.FindGroup(d[3])!;
        workspace.DockTo(d[2], right, DockPosition.Center); // 右组:d c
        workspace.SetPinned(d[2], pinned: true);             // 右组:C d
        workspace.SetPinned(d[0], pinned: true);             // 左组:A b

        workspace.DockTo(d[0], right, DockPosition.Center, index: 2);
        Assert.AreEqual("CAd", Order(right), "固定标签落进目标组时收进固定区,身份跟着走");

        workspace.DockTo(d[1], right, DockPosition.Center, index: 0);
        Assert.AreEqual("CAbd", Order(right), "普通标签不能插到固定标签前面");
    }

    [TestMethod]
    public void ClampInsertIndex_UsesThePreRemovalIndexForBothCases()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene(4);
        workspace.SetPinned(d[0], pinned: true);
        workspace.SetPinned(d[1], pinned: true); // A B c d
        DockGroup group = workspace.PrimaryGroup;

        Assert.AreEqual(2, DockWorkspace.ClampInsertIndex(group, d[0], 4), "同组固定标签:最远插到固定区之后那一格(摘出自己后就是固定区末尾)");
        Assert.AreEqual(2, DockWorkspace.ClampInsertIndex(group, d[3], 0), "同组普通标签:最近插到固定区之后");
        Assert.AreEqual(4, DockWorkspace.ClampInsertIndex(group, d[3], 9));

        var outsider = new TestDocument("x");
        Assert.AreEqual(2, DockWorkspace.ClampInsertIndex(group, outsider, 1), "跨组来的普通标签");
    }

    [TestMethod]
    public void ReplaceDocument_HandsThePinOverToTheReplacement()
    {
        (DockWorkspace workspace, TestDocument[] d) = Scene(3);
        workspace.SetPinned(d[2], pinned: true); // C a b
        var connected = new TestDocument("z");

        workspace.ReplaceDocument(d[2], connected);

        Assert.IsTrue(connected.IsPinned, "「连接中」时固定了,连上之后的真标签照样是固定的");
        Assert.AreEqual("Zab", Order(workspace.PrimaryGroup));
    }

    [TestMethod]
    public void MultiRowTabs_RaisesPropertyChanged()
    {
        var workspace = new DockWorkspace();
        List<string?> changed = [];
        workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        workspace.MultiRowTabs = true;
        workspace.MultiRowTabs = true;

        Assert.AreSequenceEqual([nameof(DockWorkspace.MultiRowTabs)], changed.ToArray(), "只在值真的变了时通知一次");
    }
}
