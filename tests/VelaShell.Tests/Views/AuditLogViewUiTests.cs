using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 审计日志窗口(真控件):行渲染出来、异常事件挂上 <c>problem</c> 样式类、筛到没有时显示空状态。
/// </summary>
[TestClass]
[TestCategory("Security")]
public class AuditLogViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AuditLogViewUiTests).Assembly);

    [TestMethod]
    public void Rows_Render_AndProblemsStandOut()
    {
        OnUi(() =>
        {
            IAuditLogService audit = Substitute.For<IAuditLogService>();
            audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns(_ =>
                 [
                     new AuditEntry { Category = "connection", Action = "connect-failed", Detail = "root@10.0.0.1:22" },
                     new AuditEntry { Category = "connection", Action = "connect", Detail = "root@10.0.0.2:22" },
                 ]);
            var vm = new AuditLogViewModel(audit);
            vm.LoadAsync().GetAwaiter().GetResult();
            var window = new AuditLogView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single();
                Assert.IsTrue(list.IsVisible);
                TextBlock[] problems =
                [
                    .. list.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("problem"))
                ];
                Assert.AreEqual(vm.Rows[0].ActionLabel, problems.Single().Text, "只有连接失败那一行标红");

                vm.SearchText = "no-such-host";
                Dispatcher.UIThread.RunJobs();
                Assert.IsFalse(list.IsVisible);
                Assert.Contains(t => t.IsEffectivelyVisible && t.Text == Core.Resources.Strings.Get("AuditLog_Empty"), window.GetVisualDescendants().OfType<TextBlock>());
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 筛选条上的类别下拉、关键字框与刷新按钮同一个高度:28,与任务管理器、路由追踪的工具条输入框
    /// 以及 DESIGN.md §5.1 的描边按钮一致。
    /// </summary>
    /// <remarks>
    /// 原先下拉与输入框没写尺寸,吃的是 Fluent 的默认值(最小高 32、14 号字),比同一排 30 高的刷新按钮
    /// 还高一截,也比别的工具窗口的筛选框高。
    /// </remarks>
    [TestMethod]
    public void FilterBar_ControlsShareTheToolbarHeight()
    {
        OnUi(() =>
        {
            IAuditLogService audit = Substitute.For<IAuditLogService>();
            audit.QueryAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns(_ => [new AuditEntry { Category = "connection", Action = "connect", Detail = "root@10.0.0.2:22" }]);
            var vm = new AuditLogViewModel(audit);
            vm.LoadAsync().GetAwaiter().GetResult();
            var window = new AuditLogView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                ComboBox category = window.GetVisualDescendants().OfType<ComboBox>().Single();
                TextBox search = window.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.PlaceholderText == Core.Resources.Strings.Get("AuditLog_SearchPlaceholder"));
                Button refresh = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Content as string == Core.Resources.Strings.Get("Refresh"));

                Assert.AreEqual(28, category.Bounds.Height, 0.5, "类别下拉");
                Assert.AreEqual(28, search.Bounds.Height, 0.5, "关键字框");
                Assert.AreEqual(28, refresh.Bounds.Height, 0.5, "刷新按钮");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(
            () =>
            {
                body();
                return Task.CompletedTask;
            },
            CancellationToken.None
        ).GetAwaiter().GetResult();
}
