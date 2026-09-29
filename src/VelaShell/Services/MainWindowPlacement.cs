using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>
/// 主窗口「记住上次」的位置、尺寸与最大化(设置 → 常规 → 启动时窗口状态,#529)。
/// 关闭时经 <see cref="SaveTo" /> 写进 <see cref="AppearanceOptions" /> 的几个槽位,
/// 启动时由 <see cref="TryRestorePosition" /> 摆回去。
/// </summary>
/// <remarks>
/// 关闭那一刻窗口常常不在普通态:最大化时 <c>Position</c> 是最大化后的左上角,
/// 最小化时 Win32 报的是 (-32000, -32000),那时的宽高也不是用户调过的尺寸。所以普通态的位置与尺寸要平时记着。
/// <para>
/// 平时记只在 Windows 上做:Win32 的 WM_MOVE 与 WM_SIZE 在同一次 SetWindowPos 里同步发完,
/// 投递出去的快照回调运行时窗口状态已是最终值,最大化 / 最小化途中的坐标不会被当成普通态记下。
/// X11 的窗口状态经属性变更事件另行通知,macOS 进出全屏带过渡,两处都保证不了这一点,
/// 所以只在普通态下关闭时读当下的值,最大化 / 最小化时关闭沿用上次记下的。
/// </para>
/// </remarks>
internal sealed class MainWindowPlacement
{
    /// <summary>标题栏至少要露出这么宽(逻辑像素),才算抓得住、拖得回来。</summary>
    private const double GripWidth = 120;

    /// <summary>标题栏从窗口顶边往下至少要露出这么高(逻辑像素)。</summary>
    private const double GripHeight = 32;

    private readonly Window _window;

    /// <summary>这次运行里最近一次普通态的位置(物理像素)与尺寸(逻辑像素);还没处于过普通态时为 null。</summary>
    private (PixelPoint Position, double Width, double Height)? _normal;

    /// <summary>最小化之前的状态:从最小化关闭时,据它判断下次是否最大化打开。</summary>
    private WindowState _restoreState;

    private bool _snapshotPending;

    /// <summary>开始跟踪窗口。在窗口构造函数里、启动状态套用之前创建。</summary>
    /// <param name="window">主窗口。</param>
    /// <param name="trackWhileOpen">是否平时记普通态;默认只在 Windows 上记(原因见类注释),显式传值只为测试。</param>
    public MainWindowPlacement(Window window, bool? trackWhileOpen = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _restoreState = window.WindowState;
        bool track = trackWhileOpen ?? OperatingSystem.IsWindows();
        if (track)
        {
            window.PositionChanged += (_, _) => ScheduleSnapshot();
        }
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                if (window.WindowState != WindowState.Minimized)
                {
                    _restoreState = window.WindowState;
                }
                if (track)
                {
                    ScheduleSnapshot();
                }
            }
            else if (track && e.Property == TopLevel.ClientSizeProperty)
            {
                ScheduleSnapshot();
            }
        };
    }

    /// <summary>把位置、尺寸与最大化写进设置的槽位;这次运行里从没处于过普通态时,位置与尺寸保留原值。</summary>
    /// <param name="appearance">要回写的外观设置。</param>
    public void SaveTo(AppearanceOptions appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        WindowState state = _window.WindowState == WindowState.Minimized ? _restoreState : _window.WindowState;
        appearance.LastWindowMaximized = state == WindowState.Maximized;
        if (_window.WindowState == WindowState.Normal)
        {
            // 普通态关闭:当下的值就是答案,不等那个可能还没跑的快照。
            Snapshot();
        }
        if (_normal is var (position, width, height))
        {
            appearance.LastWindowX = position.X;
            appearance.LastWindowY = position.Y;
            appearance.LastWindowWidth = width;
            appearance.LastWindowHeight = height;
        }
    }

    /// <summary>
    /// 把窗口摆到记下的位置,在 Show 之前、尺寸定好之后调用。位置已不在任何一块屏幕上
    /// (拔掉了副屏、换了分辨率或排列),或拿不到屏幕信息时不动它,窗口照 XAML 里的 CenterScreen 居中打开 ——
    /// 否则窗口开在屏幕外,看不见也拖不回来。
    /// </summary>
    /// <param name="window">主窗口。</param>
    /// <param name="position">记下的左上角(物理像素)。</param>
    /// <returns>是否采用了记下的位置。</returns>
    public static bool TryRestorePosition(Window window, PixelPoint position)
    {
        ArgumentNullException.ThrowIfNull(window);
        IReadOnlyList<Screen> screens;
        try
        {
            screens = window.Screens.All;
        }
        catch
        {
            // 屏幕信息在窗口显示前不一定可用(无头 / 远程桌面):核实不了就居中。
            return false;
        }
        double width = double.IsNaN(window.Width) ? window.MinWidth : window.Width;
        foreach (Screen screen in screens)
        {
            double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            if (IsReachable(position, width * scaling, screen.WorkingArea, scaling))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = position;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 左上角在 <paramref name="position" />、宽 <paramref name="width" /> 物理像素的窗口,标题栏在这块工作区里是否抓得住:
    /// 顶边落在工作区里、下面还留得出一截标题栏,横向与工作区重叠得够宽。标题栏在窗口最上面,
    /// 所以顶边高出工作区(被屏幕上沿切掉)或贴着底边都不算。
    /// </summary>
    /// <param name="position">窗口左上角(物理像素)。</param>
    /// <param name="width">窗口宽度(物理像素)。</param>
    /// <param name="workingArea">屏幕工作区(物理像素)。</param>
    /// <param name="scaling">这块屏幕的缩放,把逻辑像素的门槛换成物理像素。</param>
    internal static bool IsReachable(PixelPoint position, double width, PixelRect workingArea, double scaling)
    {
        if (position.Y < workingArea.Y || position.Y + (GripHeight * scaling) > workingArea.Bottom)
        {
            return false;
        }
        double overlap = Math.Min(position.X + width, workingArea.Right) - Math.Max(position.X, workingArea.X);
        return overlap >= GripWidth * scaling;
    }

    private void ScheduleSnapshot()
    {
        if (_snapshotPending)
        {
            return;
        }
        _snapshotPending = true;
        // 投递出去再读:最大化 / 最小化那一次的移动事件到达时,窗口状态可能还没改过来。
        Dispatcher.UIThread.Post(Snapshot, DispatcherPriority.Background);
    }

    private void Snapshot()
    {
        _snapshotPending = false;
        if (_window.WindowState == WindowState.Normal)
        {
            _normal = (_window.Position, _window.Width, _window.Height);
        }
    }
}
