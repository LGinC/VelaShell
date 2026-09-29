using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using VelaShell.Controls.Controls;

namespace VelaShell.Tests.Views;

/// <summary>
/// 28 高对话框标题栏的规格(DESIGN.md §4.2):Lucide 线条图标、13px 标题、27×27 关闭键贴住右上角。
/// </summary>
internal static class DialogTitleBarAssert
{
    public static void FollowsSpec(Window window)
    {
        Border bar = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("window-titlebar"));
        Assert.AreEqual(28, bar.Bounds.Height, 0.5, "标题栏统一 28 高");

        Assert.IsFalse(bar.GetVisualDescendants().OfType<PathIcon>().Any(), "标题栏用 Lucide 线条图标,不用实心的 PathIcon");
        Assert.IsTrue(bar.GetVisualDescendants().OfType<LucideIcon>().Any());

        TextBlock title = bar.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
        Assert.IsTrue(window.TryFindResource("VelaFontSize13", out object? size));
        Assert.AreEqual((double)size!, title.FontSize, "标题字号 13");

        Button close = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("caption-close"));
        Assert.AreEqual(27, close.Bounds.Width, 0.01, "关闭键 27×27");
        Assert.AreEqual(27, close.Bounds.Height, 0.01);
        Point topRight = close.TranslatePoint(new Point(close.Bounds.Width, 0), bar)!.Value;
        Assert.AreEqual(bar.Bounds.Width, topRight.X, 0.5, "关闭键贴住标题栏右边");
        Assert.AreEqual(0, topRight.Y, 0.5, "关闭键贴住标题栏上边");
    }
}
