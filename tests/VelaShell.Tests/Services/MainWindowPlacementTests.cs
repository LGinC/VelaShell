using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 主窗口「记住上次」的位置(#529):关闭时记下什么、启动时摆不摆回去。
/// </summary>
/// <remarks>
/// 无头窗口不会自己改状态、也不会像 Win32 那样在最大化时挪位置,
/// 这里按 Win32 的顺序手动排事件:先移动、再改状态,中间不让调度器跑。
/// </remarks>
[TestClass]
[TestCategory("WindowPlacement")]
public sealed class MainWindowPlacementTests
{
    private static HeadlessUnitTestSession _session = null!;

    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MainWindowPlacementTests).Assembly);

    [TestMethod]
    public void IsReachable_InsideTheWorkingArea() =>
        Assert.IsTrue(MainWindowPlacement.IsReachable(new(200, 100), 1440, Primary, 1));

    [TestMethod]
    public void IsReachable_OnAMonitorThatIsGone_IsRejected() =>
        // 上次开在右边的副屏上,这次副屏拔了。
        Assert.IsFalse(MainWindowPlacement.IsReachable(new(2100, 100), 1440, Primary, 1));

    [TestMethod]
    public void IsReachable_TitleBarAboveTheTopEdge_IsRejected() =>
        Assert.IsFalse(MainWindowPlacement.IsReachable(new(200, -1), 1440, Primary, 1));

    [TestMethod]
    public void IsReachable_TitleBarTooCloseToTheBottomEdge_IsRejected()
    {
        Assert.IsFalse(MainWindowPlacement.IsReachable(new(200, 1020), 1440, Primary, 1));
        Assert.IsTrue(MainWindowPlacement.IsReachable(new(200, 1008), 1440, Primary, 1));
    }

    [TestMethod]
    public void IsReachable_PartlyOffTheSide_NeedsEnoughOfTheTitleBar()
    {
        // 窗口大半拖出了左边,右端还露出 200px:抓得住。
        Assert.IsTrue(MainWindowPlacement.IsReachable(new(-1240, 100), 1440, Primary, 1));
        // 只露出 100px:不够。
        Assert.IsFalse(MainWindowPlacement.IsReachable(new(-1340, 100), 1440, Primary, 1));
        Assert.IsFalse(MainWindowPlacement.IsReachable(new(1820, 100), 1440, Primary, 1));
    }

    [TestMethod]
    public void IsReachable_SecondaryMonitorWithNegativeCoordinates() =>
        // 副屏在主屏左上方,坐标整个是负的。
        Assert.IsTrue(MainWindowPlacement.IsReachable(new(-2400, -900), 1440, new(-2560, -1080, 2560, 1040), 1));

    [TestMethod]
    public void IsReachable_ThresholdsScaleWithTheScreen()
    {
        // 露出 200 物理像素:100% 缩放下够 120,200% 缩放下不够 240。
        Assert.IsTrue(MainWindowPlacement.IsReachable(new(1720, 100), 2880, Primary, 1));
        Assert.IsFalse(MainWindowPlacement.IsReachable(new(1720, 100), 2880, Primary, 2));
    }

    [TestMethod]
    public void TryRestorePosition_OnScreen_PlacesTheWindowManually()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 1000, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            PixelRect area = window.Screens.All[0].WorkingArea;
            var target = new PixelPoint(area.X + 40, area.Y + 30);

            Assert.IsTrue(MainWindowPlacement.TryRestorePosition(window, target));
            Assert.AreEqual(WindowStartupLocation.Manual, window.WindowStartupLocation);
            Assert.AreEqual(target, window.Position);
        });
    }

    [TestMethod]
    public void TryRestorePosition_OffEveryScreen_KeepsCentering()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 1000, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            PixelPoint before = window.Position;
            PixelRect area = window.Screens.All.Select(s => s.WorkingArea).Aggregate((a, b) => a.Union(b));

            Assert.IsFalse(MainWindowPlacement.TryRestorePosition(window, new(area.Right + 100, area.Y + 30)));
            Assert.AreEqual(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
            Assert.AreEqual(before, window.Position);
        });
    }

    [TestMethod]
    public void SaveTo_ClosedInNormalState_WritesTheCurrentBounds()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 1000, Height = 700 };
            var placement = new MainWindowPlacement(window, trackWhileOpen: false);
            window.Position = new(300, 200);
            window.Width = 1200;

            var appearance = new AppearanceOptions { LastWindowMaximized = true };
            placement.SaveTo(appearance);

            Assert.AreEqual(300, appearance.LastWindowX);
            Assert.AreEqual(200, appearance.LastWindowY);
            Assert.AreEqual(1200, appearance.LastWindowWidth);
            Assert.AreEqual(700, appearance.LastWindowHeight);
            Assert.IsFalse(appearance.LastWindowMaximized);
        });
    }

    [TestMethod]
    public void SaveTo_MovedThenMaximized_KeepsTheNormalPositionOfThisSession()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 1000, Height = 700 };
            var placement = new MainWindowPlacement(window, trackWhileOpen: true);
            // 挪到另一块屏幕上。
            window.Position = new(2200, 150);
            Dispatcher.UIThread.RunJobs();
            // 在那里最大化:Win32 先报移动到最大化后的左上角,状态随后才变。
            window.Position = new(1912, -8);
            window.Width = 1936;
            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();

            var appearance = new AppearanceOptions { LastWindowX = 10, LastWindowY = 20 };
            placement.SaveTo(appearance);

            Assert.AreEqual(2200, appearance.LastWindowX, "最大化后的左上角不能当成普通态的位置");
            Assert.AreEqual(150, appearance.LastWindowY);
            Assert.AreEqual(1000, appearance.LastWindowWidth);
            Assert.IsTrue(appearance.LastWindowMaximized);
        });
    }

    [TestMethod]
    public void SaveTo_ClosedWhileMinimized_RemembersWhatItWasBefore()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 1000, Height = 700 };
            var placement = new MainWindowPlacement(window, trackWhileOpen: true);
            window.Position = new(300, 200);
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            // 从任务栏右键关闭一个最小化的窗口:Win32 此时报的坐标是 (-32000, -32000)。
            window.Position = new(-32000, -32000);
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();

            var appearance = new AppearanceOptions();
            placement.SaveTo(appearance);

            Assert.IsTrue(appearance.LastWindowMaximized, "最小化之前是最大化的,下次仍最大化打开");
            Assert.AreEqual(300, appearance.LastWindowX);
            Assert.AreEqual(200, appearance.LastWindowY);
        });
    }

    [TestMethod]
    public void SaveTo_NeverInNormalStateThisSession_KeepsWhatWasSaved()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 1000, Height = 700 };
            var placement = new MainWindowPlacement(window, trackWhileOpen: true);
            // 上次最大化关闭,这次最大化打开、最大化关闭。
            window.WindowState = WindowState.Maximized;
            window.Position = new(-8, -8);
            Dispatcher.UIThread.RunJobs();

            var appearance = new AppearanceOptions { LastWindowX = 40, LastWindowY = 50, LastWindowWidth = 900, LastWindowHeight = 600 };
            placement.SaveTo(appearance);

            Assert.AreEqual(40, appearance.LastWindowX);
            Assert.AreEqual(50, appearance.LastWindowY);
            Assert.AreEqual(900, appearance.LastWindowWidth);
            Assert.AreEqual(600, appearance.LastWindowHeight);
            Assert.IsTrue(appearance.LastWindowMaximized);
        });
    }

    [TestMethod]
    public void SaveTo_WithoutTracking_MaximizedCloseKeepsWhatWasSaved()
    {
        OnUi(() =>
        {
            var window = new Window { Width = 1000, Height = 700 };
            var placement = new MainWindowPlacement(window, trackWhileOpen: false);
            window.Position = new(300, 200);
            Dispatcher.UIThread.RunJobs();
            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();

            var appearance = new AppearanceOptions { LastWindowX = 40, LastWindowY = 50 };
            placement.SaveTo(appearance);

            Assert.AreEqual(40, appearance.LastWindowX, "不跟踪的平台上,最大化时关闭沿用上次记下的");
            Assert.AreEqual(50, appearance.LastWindowY);
            Assert.IsTrue(appearance.LastWindowMaximized);
        });
    }

    private static void OnUi(Action action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
