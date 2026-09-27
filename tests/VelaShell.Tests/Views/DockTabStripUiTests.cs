using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls.Controls;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;

namespace VelaShell.Tests.Views;

/// <summary>
/// 标签条的排版(#521):一排放不下先收窄最宽的标签、多排换行、固定标签的外观与手势、
/// 右键菜单里的「多行显示标签页」,以及激活的标签自动滚进可视区。
/// </summary>
/// <remarks>
/// 用插件文档做标签:它是最省事的一种"真标签"(有 DataTemplate、走 DockTabItemBase),
/// 标题栏的三栏布局与右键菜单五种标签是同一套写法。
/// </remarks>
[TestClass]
[TestCategory("DockPaneUi")]
public sealed class DockTabStripUiTests
{
    private const string LongTitle = "prod-database-primary.eu-west-1.internal.example.com";

    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DockTabStripUiTests).Assembly);

    [TestMethod]
    public void ShrinkCap_TrimsOnlyTheWidest_AndNeverGoesBelowTheFloor()
    {
        Assert.AreEqual(175d, DockTabPanel.ShrinkCap([50, 300, 300, 100], budget: 500, minWidth: 60),
                        "50 + 100 原样,两个 300 各削到 175,正好 500");
        Assert.AreEqual(60d, DockTabPanel.ShrinkCap([50, 300, 300, 100], budget: 200, minWidth: 60),
                        "预算连下限都给不起:按下限算,剩下的交给滚动");
        Assert.AreEqual(133d, DockTabPanel.ShrinkCap([200, 200, 200], budget: 400, minWidth: 60),
                        "取整到整像素,免得小数累加后超出视口");
    }

    [TestMethod]
    public void CrowdedSingleRow_ShrinksTheLongTabs_InsteadOfOverflowing()
    {
        _session.Dispatch(() =>
        {
            (Window window, DockGroupControl group, DockWorkspace workspace, PluginDocument[] docs) =
                Scene(3000, ["FTP", LongTitle + " 1", LongTitle + " 2", LongTitle + " 3"]);
            double[] natural = [.. docs.Select(doc => Container(group, doc).Bounds.Width)];
            Assert.IsGreaterThan(natural[0] * 2, natural[1], "长标题的标签得明显比短的宽,否则这条测的是空气");

            window.Width = natural.Sum() * 0.75;
            Pump(window);

            ScrollViewer strip = Strip(group);
            Assert.IsLessThanOrEqualTo(strip.Viewport.Width + 0.5, strip.Extent.Width,
                                       "收窄之后一排放得下,不该再溢出滚动");
            Assert.AreEqual(natural[0], Container(group, docs[0]).Bounds.Width, 0.5,
                            "短标签没有理由陪着长标签一起变窄");
            double shrunk = Container(group, docs[1]).Bounds.Width;
            Assert.IsLessThan(natural[1], shrunk);
            Assert.AreEqual(shrunk, Container(group, docs[2]).Bounds.Width, 0.5, "长标签削到同一宽度");
            Assert.AreEqual(shrunk, Container(group, docs[3]).Bounds.Width, 0.5);
            TextBlock title = Title(group, docs[1]);
            Assert.IsTrue(title.TextLayout.TextLines.Any(line => line.HasCollapsed), "被削的是标题,出省略号");
            Assert.IsTrue(CloseButton(group, docs[1]).IsEffectivelyVisible, "关闭钮不能被挤掉");
            SaveOptionalFrame(window, "dock-tabs-shrunk.png");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void MultiRow_WrapsIntoRows_AndGrowsTheStrip_ThenBackToOneRow()
    {
        _session.Dispatch(() =>
        {
            string[] titles = [.. Enumerable.Range(1, 8).Select(i => $"server-{i}.example.com")];
            (Window window, DockGroupControl group, DockWorkspace workspace, PluginDocument[] docs) = Scene(640, titles);
            ScrollViewer strip = Strip(group);
            Assert.IsGreaterThan(strip.Viewport.Width + 0.5, strip.Extent.Width, "单排时得真的放不下");
            double singleRowHeight = strip.Bounds.Height;

            workspace.MultiRowTabs = true;
            Pump(window);

            double[] rows = [.. docs.Select(doc => Container(group, doc).TranslatePoint(default, strip)!.Value.Y)
                                    .Distinct()];
            Assert.IsGreaterThan(1, rows.Length, "放不下就换行");
            Assert.IsLessThanOrEqualTo(strip.Viewport.Width + 0.5, strip.Extent.Width, "多排不再有横向溢出");
            Assert.AreEqual(rows.Length * singleRowHeight, strip.Bounds.Height, 0.5, "标签条随排数长高,每排与单排同高");
            Assert.IsFalse(OverflowControls(group).IsVisible, "没有溢出,滚动三连钮也就不出现");
            foreach (PluginDocument doc in docs)
            {
                Assert.AreEqual(singleRowHeight, Container(group, doc).Bounds.Height, 0.5);
            }

            // 强调线跟着激活标签换到它那一排。
            workspace.ActivateDocument(docs[^1]);
            Pump(window);
            Border indicator = group.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ActiveTabIndicator");
            Point lastOrigin = Container(group, docs[^1]).TranslatePoint(default, strip)!.Value;
            // 读基值:强调线换位走 180ms 过渡,headless 里时钟不走,读到的会是过渡起点。
            var target = (ITransform)indicator.GetBaseValue(Visual.RenderTransformProperty).Value!;
            Assert.AreEqual(lastOrigin.Y, target.Value.M32, 0.5);
            SaveOptionalFrame(window, "dock-tabs-multirow.png");

            workspace.MultiRowTabs = false;
            Pump(window);
            Assert.HasCount(1, docs.Select(doc => Container(group, doc).TranslatePoint(default, strip)!.Value.Y).Distinct());
            Assert.AreEqual(singleRowHeight, strip.Bounds.Height, 0.5);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void MultiRow_DoesNotApplyToSideTabStrips()
    {
        _session.Dispatch(() =>
        {
            (Window window, DockGroupControl group, DockWorkspace workspace, PluginDocument[] docs) =
                Scene(640, ["alpha", "beta", "gamma"]);
            workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Left;
            workspace.MultiRowTabs = true;
            Pump(window);

            double[] xs = [.. docs.Select(doc => Container(group, doc).TranslatePoint(default, Strip(group))!.Value.X).Distinct()];
            Assert.HasCount(1, xs, "停在侧边的标签条本来就是一个标签一行,多排开关不改变它");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void PinnedTab_ShowsAPin_IgnoresMiddleClick_AndThePinUnpins()
    {
        _session.Dispatch(() =>
        {
            (Window window, DockGroupControl group, DockWorkspace workspace, PluginDocument[] docs) =
                Scene(800, ["alpha", "beta"]);
            List<DockDocument> closed = [];
            workspace.DocumentClosed += closed.Add;

            workspace.SetPinned(docs[1], pinned: true);
            Pump(window);

            Assert.AreSame(docs[1], workspace.PrimaryGroup.Documents[0], "固定的排到最前面");
            Assert.IsFalse(CloseButton(group, docs[1]).IsEffectivelyVisible, "固定标签上没有 ×");
            Button pin = UnpinButton(group, docs[1]);
            Assert.IsTrue(pin.IsEffectivelyVisible, "× 的位置换成一枚图钉");
            Assert.AreSame(Application.Current!.FindResource("Icon.pin"),
                           pin.GetVisualDescendants().OfType<LucideIcon>().Single().Data);
            SaveOptionalFrame(window, "dock-tabs-pinned.png");

            Control container = Container(group, docs[1]);
            Point spot = container.TranslatePoint(new Point(6, container.Bounds.Height / 2), window)!.Value;
            window.MouseDown(spot, MouseButton.Middle);
            window.MouseUp(spot, MouseButton.Middle);
            Pump(window);
            Assert.IsEmpty(closed, "中键关不掉固定标签");

            ClickCenter(window, pin);
            Pump(window);
            Assert.IsFalse(docs[1].IsPinned, "点图钉 = 取消固定");
            Assert.IsTrue(CloseButton(group, docs[1]).IsEffectivelyVisible, "× 回来了");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void RightClickMenu_MultiRowItem_ShowsAndTogglesTheWorkspaceSwitch()
    {
        _session.Dispatch(() =>
        {
            (Window window, DockGroupControl group, DockWorkspace workspace, PluginDocument[] docs) =
                Scene(800, ["alpha", "beta"]);

            MenuItem item = OpenMenuItem(window, group, docs[0]);
            Assert.IsFalse(item.IsChecked);
            Assert.IsTrue(item.IsEnabled);
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Container(group, docs[0]).GetVisualDescendants().OfType<DockTabItemBase>().Single().ContextMenu!.Close();
            Pump(window);
            Assert.IsTrue(workspace.MultiRowTabs, "菜单项改的是整个工作区的开关");

            item = OpenMenuItem(window, group, docs[1]);
            Assert.IsTrue(item.IsChecked, "另一个标签的菜单打开时读的是同一个开关");
            Container(group, docs[1]).GetVisualDescendants().OfType<DockTabItemBase>().Single().ContextMenu!.Close();

            workspace.PrimaryGroup.TabsPosition = DockTabsPosition.Right;
            Pump(window);
            item = OpenMenuItem(window, group, docs[1]);
            Assert.IsFalse(item.IsEnabled, "侧边标签条上这一项不起作用,置灰");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ActivatingAnOverflowedTab_ScrollsItIntoView()
    {
        _session.Dispatch(() =>
        {
            string[] titles = [.. Enumerable.Range(1, 12).Select(i => $"host-{i:00}")];
            (Window window, DockGroupControl group, DockWorkspace workspace, PluginDocument[] docs) = Scene(480, titles);
            ScrollViewer strip = Strip(group);
            workspace.ActivateDocument(docs[0]);
            Pump(window);
            Assert.AreEqual(0d, strip.Offset.X, 0.5);
            Assert.IsGreaterThan(strip.Viewport.Width + 0.5, strip.Extent.Width, "收到下限仍放不下,才轮得到滚动");

            // 从「所有标签页」下拉里挑最后一个 = 激活它。
            workspace.ActivateDocument(docs[^1]);
            Pump(window);

            Control last = Container(group, docs[^1]);
            double left = last.TranslatePoint(default, strip)!.Value.X;
            Assert.IsGreaterThan(0d, strip.Offset.X, "激活溢出区里的标签,标签条得滚过去");
            Assert.IsGreaterThanOrEqualTo(-0.5, left);
            Assert.IsLessThanOrEqualTo(strip.Viewport.Width + 0.5, left + last.Bounds.Width, "整个标签都在可视区里");

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ---- 脚手架 ----

    private static (Window Window, DockGroupControl Group, DockWorkspace Workspace, PluginDocument[] Docs) Scene(
        double width, string[] titles)
    {
        var workspace = new DockWorkspace();
        PluginDocument[] docs = [.. titles.Select((title, i) => new PluginDocument($"doc-{i}", title, "acme.demo", new Border()))];
        foreach (PluginDocument doc in docs)
        {
            workspace.AddDocument(doc);
        }
        workspace.ActivateDocument(docs[0]);
        var dock = new DockWorkspaceControl { Workspace = workspace };
        var window = new Window { Width = width, Height = 300, Content = dock };
        window.Show();
        Pump(window);
        return (window, dock.GetVisualDescendants().OfType<DockGroupControl>().Single(), workspace, docs);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static ScrollViewer Strip(DockGroupControl group) =>
        group.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.Name == "TabScroll");

    private static StackPanel OverflowControls(DockGroupControl group) =>
        group.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "OverflowControls");

    private static Control Container(DockGroupControl group, DockDocument document) =>
        group.GetVisualDescendants().OfType<ItemsControl>().Single(items => items.Name == "TabsHost")
             .ContainerFromItem(document)
        ?? throw new AssertFailedException("标签容器没有实体化。");

    private static TextBlock Title(DockGroupControl group, DockDocument document) =>
        Container(group, document).GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == document.Title);

    /// <summary>标签右端两枚按钮里的 ×(没有提示文字的那一枚)。</summary>
    private static Button CloseButton(DockGroupControl group, DockDocument document) =>
        EndButtons(group, document).Single(button => ToolTip.GetTip(button) is null);

    /// <summary>标签右端两枚按钮里的图钉(带「取消固定」提示的那一枚)。</summary>
    private static Button UnpinButton(DockGroupControl group, DockDocument document) =>
        EndButtons(group, document).Single(button => ToolTip.GetTip(button) is not null);

    private static IEnumerable<Button> EndButtons(DockGroupControl group, DockDocument document) =>
        Container(group, document).GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("tab-end"));

    /// <summary>在标签上真点一下右键(走 ContextRequested,与用户操作同一条路),返回菜单里的「多行显示标签页」。</summary>
    private static MenuItem OpenMenuItem(Window window, DockGroupControl group, DockDocument document)
    {
        Control container = Container(group, document);
        Point spot = container.TranslatePoint(new Point(6, container.Bounds.Height / 2), window)!.Value;
        window.MouseDown(spot, MouseButton.Right);
        window.MouseUp(spot, MouseButton.Right);
        Pump(window);
        ContextMenu menu = container.GetVisualDescendants().OfType<DockTabItemBase>().Single().ContextMenu!;
        Assert.IsTrue(menu.IsOpen, "右键应当打开标签的菜单");
        return menu.Items.OfType<MenuItem>().Single(item => item.Name == DockTabItemBase.MultiRowTabsMenuItemName);
    }

    private static void ClickCenter(Window window, Control control)
    {
        Point spot = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new AssertFailedException("控件不在可视树上。");
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
    }

    /// <summary>设了 VELASHELL_VISUAL_QA_DIR 就存一帧,供人眼复核样式(与 DockPaneUiTests 同款)。</summary>
    private static void SaveOptionalFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }
}
