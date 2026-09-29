using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Localization;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 连接诊断的标题栏:导出报告与重新检测跟主窗口的全局功能图标一样收在标题栏里,
/// 关闭键是 27×27 的方块,目标行只剩诊断目标。
/// </summary>
[TestClass]
[TestCategory("DiagnosticsUI")]
public sealed class ConnectionDiagnosticsViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ConnectionDiagnosticsViewUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void TheActions_LiveInTheTitleBar()
    {
        OnUi(() =>
        {
            // 先显示再挂 DataContext:打开时会自动跑一轮诊断,这里只看布局。
            var view = new ConnectionDiagnosticsView();
            view.Show();
            view.DataContext = new ConnectionDiagnosticsViewModel(
                new SessionProfile { Name = "web", Host = "10.0.0.1", Username = "root" },
                Substitute.For<IConnectionDiagnosticsService>());
            Dispatcher.UIThread.RunJobs();
            view.UpdateLayout();
            try
            {
                Border titleBar = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("window-titlebar"));
                StackPanel actions = view.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "TitleActions");
                Assert.IsTrue(actions.GetVisualAncestors().Contains(titleBar));

                Button[] buttons = [.. actions.Children.OfType<Button>()];
                CollectionAssert.AreEqual(
                    new[] { Strings.Get("Diag_ExportReport"), Strings.Get("Diag_Rerun") },
                    buttons.Select(AutomationProperties.GetName).ToArray());
                object theme = view.FindResource("VelaTitleActionButtonTheme")!;
                Assert.IsTrue(buttons.All(b => ReferenceEquals(b.Theme, theme)), "与主窗口的全局功能图标同一个主题");
                Assert.IsFalse(buttons[0].IsEnabled, "还没有报告时不能导出");
                Assert.IsTrue(buttons[1].IsEnabled);

                Button close = titleBar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("caption-close"));
                Assert.AreEqual(27, close.Bounds.Width, 0.01, "关闭键与主窗口的窗口键同规格");
                Assert.AreEqual(27, close.Bounds.Height, 0.01);

                // 目标行里只剩诊断目标,不再有按钮
                string target = ((ConnectionDiagnosticsViewModel)view.DataContext!).TargetSummary;
                TextBlock targetText = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == target);
                Border targetRow = targetText.GetVisualAncestors().OfType<Border>().First();
                Assert.AreSame(targetText, targetRow.Child, "目标行里只有那一行字");
            }
            finally
            {
                view.Close();
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
