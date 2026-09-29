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
                Assert.IsTrue(window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.IsEffectivelyVisible && t.Text == Core.Resources.Strings.Get("AuditLog_Empty")));
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
