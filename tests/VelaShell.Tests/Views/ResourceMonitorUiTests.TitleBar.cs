using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls.Controls;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 标题栏:主机标识与暂停 / 继续跟主窗口的全局功能图标组一样收在标题栏里,
/// 副标题行只剩采样说明。
/// </summary>
public sealed partial class ResourceMonitorUiTests
{
    [TestMethod]
    public void TitleBar_CarriesTheHostBadgeAndThePauseToggle()
    {
        OnUi(() =>
        {
            UseChinese();
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = OpenWarm(WithGpu());
            try
            {
                Border titleBar = Named<Border>(window, "TitleBarStrip");
                Border badge = Named<Border>(window, "HostBadge");
                Button pause = Named<Button>(window, "PauseButton");
                Assert.Contains(titleBar, badge.GetVisualAncestors(), "主机标识要在标题栏里");
                Assert.Contains(titleBar, pause.GetVisualAncestors(), "暂停键要在标题栏里");
                Assert.AreEqual(window.FindResource("VelaTitleActionButtonTheme"), pause.Theme,
                    "暂停键与主窗口的全局功能图标同一个主题");
                Assert.IsGreaterThan(0, pause.Bounds.Width, "按钮被挤成零宽等于没有");
                Assert.Contains(t => t.Text == vm.HostName, badge.GetVisualDescendants().OfType<TextBlock>());
                Assert.IsLessThanOrEqualTo(titleBar.Bounds.Height, badge.Bounds.Height, "主机标识不能把 28 的标题栏撑高");

                Assert.AreSame(window.FindResource("Icon.pause"), VisibleIcon(pause).Data);

                pause.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.IsTrue(vm.IsPaused);
                Assert.AreSame(window.FindResource("Icon.play"), VisibleIcon(pause).Data, "暂停中换成「继续」");
                Assert.IsTrue(window.TryFindResource("VelaAccent", window.ActualThemeVariant, out object? accent));
                Assert.AreEqual(((ISolidColorBrush)accent!).Color, ((ISolidColorBrush)VisibleIcon(pause).Foreground!).Color);
            }
            finally
            {
                window.Close();
            }
        });

        static LucideIcon VisibleIcon(Button button) =>
            button.GetVisualDescendants().OfType<LucideIcon>().Single(i => i.IsVisible);
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
