using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
/// 资源管理器的会话行悬停显示备注(#549):提示挂在整行上,没写备注的那一行不挂。
/// </summary>
[TestClass]
[TestCategory("SessionTree")]
public class SessionTreeNotesUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionTreeNotesUiTests).Assembly);

    [TestMethod]
    public void HoveringASessionRow_ShowsItsNotes()
    {
        _session.Dispatch(async () =>
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
            repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>
            {
                new() { Name = "bastion", Host = "10.0.0.1", Notes = "跳板机\n值班:运维二组" },
                new() { Name = "plain", Host = "10.0.0.2" },
            }));
            var viewModel = new SessionTreeViewModel(repository);
            await viewModel.LoadCommand.Execute().FirstAsync();

            var view = new SessionTreeView { DataContext = viewModel };
            var window = new Window { Width = 260, Height = 400, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            try
            {
                var rows = view.GetVisualDescendants()
                    .OfType<Border>()
                    .Where(border => border.Classes.Contains("session") && border.IsVisible)
                    .ToDictionary(border => ((SessionTreeNodeViewModel)border.DataContext!).Name);

                Border bastion = rows["bastion"];
                Border plain = rows["plain"];
                Assert.AreEqual("跳板机\n值班:运维二组", ToolTip.GetTip(bastion));
                Assert.IsNull(ToolTip.GetTip(plain), "没写备注的行不该弹一块空提示");

                // 真的悬停上去:提示挂在整行上,指着行里任何一处都弹(这里不等默认的 400ms)。
                ToolTip.SetShowDelay(bastion, 0);
                ToolTip.SetShowDelay(plain, 0);
                window.MouseMove(Center(bastion, window));
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(ToolTip.GetIsOpen(bastion), "悬停在有备注的行上要弹出备注");

                // 移到列表下方的空白处(提示框按指针定位,紧挨着的下一行可能正好压在它底下)。
                window.MouseMove(new Point(window.Bounds.Width / 2, window.Bounds.Height - 10));
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(ToolTip.GetIsOpen(bastion), "移开之后提示要收起");

                window.MouseMove(Center(plain, window));
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(ToolTip.GetIsOpen(plain), "没写备注的行悬停上去什么也不弹");
            }
            finally
            {
                window.Close();
            }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static Point Center(Visual row, Visual window) =>
        row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window) ?? default;
}
