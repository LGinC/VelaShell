using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Controls.Controls;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 对话框标题栏的规格(DESIGN.md §4.2):与回放中心等独立窗口同一个样子 —— 标题靠左、13px,
/// 标题前一个 15px 线条图标,27×27 关闭键贴住右上角,悬停的红底被卡片的圆角裁掉。
/// </summary>
internal static class DialogTitleBarAssert
{
    public static void FollowsSpec(Window window)
    {
        Border bar = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("window-titlebar"));
        Assert.AreEqual(28, bar.Bounds.Height, 0.5, "标题栏统一 28 高");

        Button close = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("caption-close"));
        Assert.IsEmpty(bar.GetVisualDescendants().OfType<PathIcon>(), "标题栏不用实心的 PathIcon");
        LucideIcon[] leading = [.. bar.GetVisualDescendants().OfType<LucideIcon>().Where(icon => !close.IsVisualAncestorOf(icon))];
        Assert.HasCount(1, leading, "标题前有一个线条图标(关闭键的 × 不算)");
        Assert.AreEqual(15, leading[0].Bounds.Width, 0.01, "标题前的图标 15px");

        TextBlock title = bar.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
        Assert.IsTrue(window.TryFindResource("VelaFontSize13", out object? size));
        Assert.AreEqual((double)size!, title.FontSize, "标题字号 13");

        Assert.AreEqual(27, close.Bounds.Width, 0.01, "关闭键 27×27");
        Assert.AreEqual(27, close.Bounds.Height, 0.01);
        Point topRight = close.TranslatePoint(new Point(close.Bounds.Width, 0), bar)!.Value;
        Assert.AreEqual(bar.Bounds.Width, topRight.X, 0.5, "关闭键贴住标题栏右边");
        Assert.AreEqual(0, topRight.Y, 0.5, "关闭键贴住标题栏上边");
    }

    /// <summary>
    /// 悬停关闭键时红底铺满 27×27 的方块,到了卡片的圆角处要被裁掉。卡片没写 <c>ClipToBounds</c> 时,
    /// 方块的右上角伸出圆角外一截,悬在透明的窗口上。
    /// 按 Windows 的浮起卡片装外框再量:其它平台的卡片是直角,量不出这件事。
    /// </summary>
    public static void CloseHoverStaysInsideTheRoundedCorner(Window window)
    {
        WindowChrome.Apply(window, WindowChromeKind.Dialog, ChromePlatform.Windows);
        Dispatcher.UIThread.RunJobs();
        Button close = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("caption-close"));
        Border card = close.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("window-card"));
        Assert.IsGreaterThan(0, card.CornerRadius.TopRight, "前置:Windows 的浮起卡片是圆角的");

        window.MouseMove(close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value);
        Dispatcher.UIThread.RunJobs();
        using WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new AssertFailedException("无头渲染没有出帧");

        Assert.IsTrue(IsRed(Pixel(frame, close.TranslatePoint(new Point(3, close.Bounds.Height / 2), window)!.Value)),
            "前置:悬停后关闭键是红底");
        // 卡片描边内侧、右上角的第一个像素:落在关闭键的方块里,却在圆角外面。
        Assert.IsFalse(IsRed(Pixel(frame, card.TranslatePoint(new Point(card.Bounds.Width - 2, 1), window)!.Value)),
            "关闭键的红底伸出了卡片的圆角:卡片要写 ClipToBounds=\"True\"");
    }

    private static bool IsRed((byte R, byte G, byte B, byte A) pixel) =>
        pixel.A > 128 && pixel.R > pixel.G + 60 && pixel.R > pixel.B + 60;

    private static (byte R, byte G, byte B, byte A) Pixel(WriteableBitmap frame, Point at)
    {
        uint[] pixel = new uint[1];
        var pin = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect((int)at.X, (int)at.Y, 1, 1), pin.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            pin.Free();
        }
        uint value = pixel[0];
        (byte first, byte second, byte third, byte alpha) = ((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
        // BGRA 小端读出来第一个字节是 B;RGBA 是 R。
        return frame.Format == PixelFormat.Rgba8888 ? (first, second, third, alpha) : (third, second, first, alpha);
    }
}
