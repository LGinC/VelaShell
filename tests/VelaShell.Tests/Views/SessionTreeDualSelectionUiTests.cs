using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 资源管理器 Ctrl 双选的界面一半:真的按住 Ctrl 点下去,列表不会按它自己的单选规则把选择改回去;
/// 右键双选里的行弹的是双选专用菜单,右键别的行弹回原菜单。
/// </summary>
/// <remarks>
/// 视图模型侧的规则由 <c>SessionTreeDualSelectionTests</c> 覆盖。这里守的是接线:
/// 列表控件本身是单选的,Ctrl 单击若漏到它手里,会被当成"切换这一项的选中",双选当场就散了 ——
/// 这种事只有走一遍真实的指针输入才看得出来。
/// </remarks>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeDualSelectionUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionTreeDualSelectionUiTests).Assembly);

    [TestMethod]
    public void CtrlClick_PairsTwoRows_AndTheirRightClickOpensTheDualMenu()
    {
        _session.Dispatch(async () =>
        {
            (SessionTreeViewModel viewModel, SessionTreeView view, Window window) = await ShowTreeAsync();
            Border alpha = Row(view, "alpha");
            Border beta = Row(view, "beta");
            Border gamma = Row(view, "gamma");
            ContextMenu? ownMenu = beta.ContextMenu;

            Click(window, alpha, MouseButton.Left, RawInputModifiers.None);
            Click(window, beta, MouseButton.Left, RawInputModifiers.Control);

            Assert.HasCount(2, viewModel.DualSelection, "Ctrl 单击第二行必须组成双选,而不是被列表改成单选。");
            Assert.AreEqual("alpha", viewModel.DualSelection[0].Name);
            Assert.AreEqual("beta", viewModel.DualSelection[1].Name);
            Assert.IsTrue(alpha.Classes.Contains("dualmarked"));
            Assert.IsTrue(beta.Classes.Contains("dualmarked"));

            Click(window, beta, MouseButton.Right, RawInputModifiers.None);
            Assert.HasCount(2, viewModel.DualSelection, "右键双选里的行不能把双选打散。");
            Assert.IsNotNull(beta.ContextMenu);
            Assert.AreNotSame(ownMenu, beta.ContextMenu, "右键双选里的行要弹双选专用菜单。");
            MenuItem open = beta.ContextMenu.Items.OfType<MenuItem>().First();
            beta.ContextMenu.Open(beta);
            Dispatcher.UIThread.RunJobs();
            Assert.AreSame(viewModel.OpenDualSftpCommand, open.Command, "双选菜单的第一项必须绑到 OpenDualSftpCommand。");
            beta.ContextMenu.Close();

            // 右键双选之外的行:双选结束,那一行弹它自己的原菜单;双选里那行也换回原菜单。
            Click(window, gamma, MouseButton.Right, RawInputModifiers.None);
            Assert.IsEmpty(viewModel.DualSelection);
            Assert.DoesNotContain(item => ReferenceEquals(item.Command, viewModel.OpenDualSftpCommand), gamma.ContextMenu!.Items.OfType<MenuItem>());
            Click(window, beta, MouseButton.Right, RawInputModifiers.None);
            Assert.AreSame(ownMenu, beta.ContextMenu);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void PlainClick_EndsTheDualSelection()
    {
        _session.Dispatch(async () =>
        {
            (SessionTreeViewModel viewModel, SessionTreeView view, Window window) = await ShowTreeAsync();
            Click(window, Row(view, "alpha"), MouseButton.Left, RawInputModifiers.None);
            Click(window, Row(view, "beta"), MouseButton.Left, RawInputModifiers.Control);
            Assert.HasCount(2, viewModel.DualSelection);

            Click(window, Row(view, "beta"), MouseButton.Left, RawInputModifiers.None);

            Assert.IsEmpty(viewModel.DualSelection);
            Assert.AreEqual("beta", viewModel.SelectedNode?.Name);
            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task<(SessionTreeViewModel, SessionTreeView, Window)> ShowTreeAsync()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>
        {
            new() { Id = Guid.NewGuid(), Name = "alpha", Host = "a.example.com", Username = "u", ConnectionType = ConnectionType.SSH },
            new() { Id = Guid.NewGuid(), Name = "beta", Host = "b.example.com", Username = "u", ConnectionType = ConnectionType.SFTP },
            new() { Id = Guid.NewGuid(), Name = "gamma", Host = "c.example.com", Username = "u", ConnectionType = ConnectionType.FTP },
        }));
        var viewModel = new SessionTreeViewModel(repository);
        await viewModel.LoadCommand.Execute().FirstAsync();
        var view = new SessionTreeView { DataContext = viewModel };
        var window = new Window { Width = 260, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (viewModel, view, window);
    }

    private static Border Row(SessionTreeView view, string name) =>
        view.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Classes.Contains("session")
                              && border.DataContext is SessionTreeNodeViewModel { IsGroup: false } node
                              && node.Name == name);

    private static void Click(Window window, Border row, MouseButton button, RawInputModifiers modifiers)
    {
        Point at = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window)
                   ?? throw new InvalidOperationException("row is not in the window");
        window.MouseDown(at, button, modifiers);
        window.MouseUp(at, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }
}
