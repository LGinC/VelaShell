using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using VelaShell.XServer;

namespace VelaShell.Services.XServer;

/// <summary>
/// 非矩形 X 窗口(SHAPE 扩展:xeyes、不规则的弹层)的原生窗口只在形状里面接收鼠标:形状以外本来就画成全透明,
/// 原先却照样接住点击,用户点不到它下面的窗口。Windows 上用窗口区域(SetWindowRgn)把命中范围裁成形状;
/// 别的平台没有对应的做法,什么也不做。
/// </summary>
internal static partial class WindowRegion
{
    /// <summary>
    /// 把窗口的命中范围裁成 <paramref name="shape" />(原生窗口坐标、物理像素);null = 恢复整个矩形。
    /// 只对无装饰的窗口用:有系统边框时窗口区域还得把标题栏、边框一起算进去。
    /// </summary>
    public static void Apply(Window window, IReadOnlyList<XRect>? shape)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle)
        {
            return;
        }
        try
        {
            nint region = 0;
            if (shape is not null)
            {
                region = CreateRectRgn(0, 0, 0, 0);
                foreach (XRect r in shape)
                {
                    nint part = CreateRectRgn(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
                    _ = CombineRgn(region, region, part, RegionOr);
                    _ = DeleteObject(part);
                }
            }
            // 设成功之后区域归系统所有,不再释放;失败时自己释放。
            if (SetWindowRgn(handle.Handle, region, redraw: true) == 0 && region != 0)
            {
                _ = DeleteObject(region);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private const int RegionOr = 2;   // RGN_OR

    [SupportedOSPlatform("windows")]
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [SupportedOSPlatform("windows")]
    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [SupportedOSPlatform("windows")]
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
