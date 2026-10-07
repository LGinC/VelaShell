using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Services.XServer;
using VelaShell.Ssh.Transport;
using VelaShell.Tests.TestSupport;
using VelaShell.Views.XServer;
using VelaShell.XServer;

namespace VelaShell.Tests.XServer;

/// <summary>
/// 内置 X 服务端的 Avalonia 宿主:X 客户端映射一个窗口 → 出现一个原生窗口,尺寸、标题、像素都对;
/// 点关闭按钮 → 请客户端关(没声明 WM_DELETE_WINDOW 就断开它)→ 原生窗口随之收掉。
/// 客户端是手拼的最小 X 协议(小端),经内存双工流直接接进 <see cref="X11Server.ServeAsync" />。
/// </summary>
[TestClass]
[TestCategory("XServerHostUi")]
public sealed class AvaloniaXServerHostUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaXServerHostUiTests).Assembly);

    [TestMethod]
    public void InputMap_TranslatesPhysicalKeysButtonsAndCursors()
    {
        Assert.AreEqual(XKeycodes.A, XInputMap.Keycode(PhysicalKey.A));
        Assert.AreEqual(XKeycodes.Return, XInputMap.Keycode(PhysicalKey.Enter));
        Assert.AreEqual(XKeycodes.ControlLeft, XInputMap.Keycode(PhysicalKey.ControlLeft));
        Assert.AreEqual(XKeycodes.Up, XInputMap.Keycode(PhysicalKey.ArrowUp));
        Assert.AreEqual(XKeycodes.KeypadEnter, XInputMap.Keycode(PhysicalKey.NumPadEnter));
        Assert.AreEqual(0, XInputMap.Keycode(PhysicalKey.None));
        Assert.AreEqual(3, XInputMap.Button(MouseButton.Right));
        Assert.AreEqual(8, XInputMap.Button(MouseButton.XButton1));
        Assert.AreEqual(StandardCursorType.Ibeam, XInputMap.Cursor(XCursorShape.Text));
        Assert.AreEqual(StandardCursorType.None, XInputMap.Cursor(XCursorShape.Hidden));
        Assert.AreEqual(StandardCursorType.Arrow, XInputMap.Cursor(XCursorShape.Arrow));
        Assert.AreEqual(StandardCursorType.BottomRightCorner, XInputMap.Cursor(XCursorShape.ResizeSouthEast));
    }

    /// <summary>日文 JIS / 巴西 ABNT2 / 韩文键盘的键、F13–F24、多媒体键都有 X 键码;Ro、Yen 按系统布局推出的键值并进键位表。</summary>
    [TestMethod]
    public void InputMap_CoversInternationalFunctionAndMediaKeys_AndExtrasJoinTheKeymap()
    {
        Assert.AreEqual(XKeycodes.IntlRo, XInputMap.Keycode(PhysicalKey.IntlRo));
        Assert.AreEqual(XKeycodes.IntlYen, XInputMap.Keycode(PhysicalKey.IntlYen));
        Assert.AreEqual(XKeycodes.Henkan, XInputMap.Keycode(PhysicalKey.Convert));
        Assert.AreEqual(XKeycodes.F13, XInputMap.Keycode(PhysicalKey.F13));
        Assert.AreEqual(XKeycodes.F24, XInputMap.Keycode(PhysicalKey.F24));
        Assert.AreEqual(XKeycodes.AudioMute, XInputMap.Keycode(PhysicalKey.AudioVolumeMute));

        HostKeymapResult us = HostKeymap.FromBundled("us")!;
        Assert.IsEmpty(us.Extras, "手选布局:随程序带的表里没有 Ro / Yen,服务端沿用起步的 JIS 键值");
        // ABNT2:Ro 是 / ?;Yen 这台键盘没有(第一层 0 跳过)。
        HostKeymapResult abnt2 = HostKeymap.WithExtras(us, [('/', '?', 0, 0), (0, 0, 0, 0)]);
        Assert.HasCount(1, abnt2.Extras);
        Assert.AreEqual(XKeycodes.IntlRo, abnt2.Extras[0].Keycode);
        Assert.AreSequenceEqual([(uint)'/', '?'], abnt2.Extras[0].Columns);
        Assert.IsFalse(abnt2.SameAs(us), "多了额外的键就不是同一个结果");
        Assert.AreEqual(2, abnt2.ToXKeymap().KeysymsPerKeycode);
    }

    /// <summary>显示器多于服务端的上限(16 台)时只交主显示器与排在前面的几台 —— 原先整个列表交过去,服务端抛的异常落在 UI 线程上。</summary>
    [TestMethod]
    public void 显示器超过上限时只交主显示器与排在前面的几台()
    {
        (int Id, bool Primary)[] screens = [.. Enumerable.Range(0, 20).Select(i => (i, i == 18))];
        IReadOnlyList<(int Id, bool Primary)> limited = AvaloniaXServerHost.LimitScreens(screens, s => s.Primary);
        Assert.HasCount(X11Server.MaxMonitors, limited);
        Assert.AreEqual(18, limited[0].Id, "主显示器留着");
        Assert.AreEqual(14, limited[^1].Id, "其余按先后取够");
        Assert.HasCount(3, AvaloniaXServerHost.LimitScreens(screens[..3], s => s.Primary), "没超过就原样交");
    }

    [TestMethod]
    public void HostKeymap_MapsCharactersAndDeadKeysToKeysyms()
    {
        Assert.AreEqual('a', HostKeymap.Keysym('a'));
        Assert.AreEqual(0xe4u, HostKeymap.Keysym('ä'), "Latin-1 字符就是它自己");
        Assert.AreEqual(0x0100_20ACu, HostKeymap.Keysym('€'), "其余用 Unicode 键值");
        Assert.AreEqual(0u, HostKeymap.Keysym('\u0001'), "控制字符按「打不出字符」算");
        Assert.AreEqual(0xfe52u, HostKeymap.DeadKeysym('^'), "dead_circumflex");
        Assert.AreEqual(0xfe52u, HostKeymap.DeadKeysym('ˆ'), "macOS 的死键给 U+02C6");
        Assert.AreEqual(0xfe53u, HostKeymap.DeadKeysym('˜'), "macOS 的死键给 U+02DC");
        Assert.AreEqual(0xfe51u, HostKeymap.DeadKeysym('´'), "dead_acute");
        Assert.AreEqual('@', HostKeymap.DeadKeysym('@'), "不认识的死键按普通字符给");
    }

    /// <summary>四层排成核心列:没有第三、四层时每键两列;有时六列(XKB §17:组 1 第 1、2 级,组 2 照抄,组 1 第 3、4 级),缺的层照抄。</summary>
    [TestMethod]
    public void HostKeymap_AssemblesCoreColumns()
    {
        byte[] keycodes = [.. HostKeymap.Keycodes()];
        Assert.HasCount(HostKeymap.LastKeycode - HostKeymap.FirstKeycode + 2, keycodes);
        Assert.AreEqual(XKeycodes.IntlBackslash, keycodes[^1]);

        (uint, uint, uint, uint)[] plain = [.. keycodes.Select(k => k == XKeycodes.Q ? ('q', 'Q', 0u, 0u) : (1u, 0u, 0u, 0u))];
        HostKeymapResult two = HostKeymap.Assemble(plain);
        Assert.AreEqual(2, two.PerKeycode);
        Assert.IsFalse(two.HasAltGr);
        Assert.AreEqual('Q', two.Main[((XKeycodes.Q - HostKeymap.FirstKeycode) * 2) + 1]);
        Assert.AreEqual(1u, two.Main[1], "第二层缺的照抄第一层");

        plain[XKeycodes.Q - HostKeymap.FirstKeycode] = ('q', 'Q', '@', 0u);
        HostKeymapResult six = HostKeymap.Assemble(plain);
        Assert.IsTrue(six.HasAltGr);
        Assert.AreSequenceEqual(['q', 'Q', 'q', 'Q', '@', '@'], six.Main.AsSpan((XKeycodes.Q - HostKeymap.FirstKeycode) * 6, 6).ToArray(), "第四层缺的照抄第三层");
        Assert.IsTrue(six.SameAs(HostKeymap.Assemble(plain)));
        Assert.IsFalse(six.SameAs(two));
    }

    /// <summary>设置里手选的布局用随程序带的表:德语 y / z 互换、AltGr 层(Q 上的 @、E 上的 €);美式没有 AltGr 层;表外的名字给 null。</summary>
    [TestMethod]
    public void HostKeymap_BuildsChosenLayoutsFromTheBundledTable()
    {
        HostKeymapResult de = HostKeymap.FromBundled("de")!;
        Assert.IsTrue(de.HasAltGr);
        static uint At(HostKeymapResult k, byte keycode, int column) => k.Main[((keycode - HostKeymap.FirstKeycode) * k.PerKeycode) + column];
        Assert.AreEqual('z', At(de, XKeycodes.Y, 0), "德语 QWERTZ:Y 的位置打 z");
        Assert.AreEqual('y', At(de, XKeycodes.Z, 0));
        Assert.AreEqual('@', At(de, XKeycodes.Q, 4), "AltGr+Q");
        Assert.AreEqual(0x20acu, At(de, XKeycodes.E, 4), "AltGr+E = EuroSign");
        Assert.AreEqual(0xff08u, At(de, XKeycodes.BackSpace, 0), "固定键不取表里的值");

        HostKeymapResult us = HostKeymap.FromBundled("us")!;
        Assert.IsFalse(us.HasAltGr, "美式布局没有 Level3 键");
        Assert.AreEqual('!', At(us, XKeycodes.D1, 1));

        Assert.IsNull(HostKeymap.FromBundled("xx"));

        Assert.AreEqual("de", de.Layout, "手选的布局名跟着键位表走");
        var keymap = de.ToXKeymap();
        Assert.AreEqual("de", keymap.Layout);
        Assert.IsTrue(keymap.AltGr, "有 AltGr 层:右 Alt 当 AltGr");
        Assert.AreEqual(6, keymap.KeysymsPerKeycode);
    }

    /// <summary>跟随系统时没有布局名:取随程序带的表里第一、二层最像的那个;认不出来按 us。</summary>
    [TestMethod]
    public void HostKeymap_GuessesTheLayoutNameWhenFollowingTheSystem()
    {
        byte[] keycodes = [.. HostKeymap.Keycodes()];
        List<(uint, uint, uint, uint)> Levels(string layout)
        {
            uint[] t = BundledKeymaps.Layouts[layout];
            return [.. keycodes.Select((k, i) => HostKeymap.Fixed(k) is { } f ? (f.Item1, f.Item2, 0u, 0u) : (t[i * 4], t[i * 4 + 1], t[i * 4 + 2], t[i * 4 + 3]))];
        }
        Assert.AreEqual("de", HostKeymap.Assemble(Levels("de")).Layout);
        Assert.AreEqual("fr", HostKeymap.Assemble(Levels("fr")).Layout);
        Assert.AreEqual("us", HostKeymap.Assemble(Levels("us")).Layout);
        Assert.AreEqual("us", HostKeymap.Assemble([.. keycodes.Select(_ => ((uint)'a', (uint)'A', 0u, 0u))]).Layout, "认不出来");
    }

    /// <summary>设置页下拉里的每个布局(「自动」除外)在随程序带的表里都有 —— 选了却没有数据就会退回系统布局,用户看不出来。</summary>
    [TestMethod]
    public void BundledKeymaps_CoverEveryLayoutOfferedInSettings()
    {
        string[] offered = [.. VelaShell.ViewModels.SettingsViewModel.BuildKeyboardLayouts().Select(c => c.Value).Where(v => v.Length != 0)];
        Assert.IsNotEmpty(offered);
        string[] missing = [.. offered.Where(v => !BundledKeymaps.Layouts.ContainsKey(v))];
        Assert.IsEmpty(missing, string.Join(", ", missing));
        Assert.IsTrue(BundledKeymaps.Layouts.Values.All(t => t.Length == HostKeymap.Keycodes().Count() * 4));
    }

    /// <summary>macOS 的虚拟键码表:要推导的键里,除了与布局无关的那几个,每个都有且互不相同。</summary>
    [TestMethod]
    public void MacKeymap_VirtualKeyTableCoversEveryCharacterKey()
    {
        int[] codes = [.. HostKeymap.Keycodes().Where(k => HostKeymap.Fixed(k) is null).Select(MacKeymap.VirtualKeyFor)];
        Assert.IsTrue(codes.All(c => c >= 0), "每个打字符的键都有 kVK_* 对应");
        Assert.HasCount(codes.Length, codes.Distinct());
        Assert.AreEqual(0x00, MacKeymap.VirtualKeyFor(XKeycodes.A), "kVK_ANSI_A");
        Assert.AreEqual(0x0A, MacKeymap.VirtualKeyFor(XKeycodes.IntlBackslash), "kVK_ISO_Section");
    }

    [TestMethod]
    public void LinuxKeymap_ParsesDisplayNumber()
    {
        Assert.AreEqual(0, LinuxKeymap.DisplayNumber(":0"));
        Assert.AreEqual(12, LinuxKeymap.DisplayNumber("localhost:12.0"));
        Assert.AreEqual(1, LinuxKeymap.DisplayNumber("unix:1"));
        Assert.AreEqual(-1, LinuxKeymap.DisplayNumber("wayland-0"));
        Assert.AreEqual(-1, LinuxKeymap.DisplayNumber(":x"));
    }

    /// <summary>
    /// 按当前系统的布局真的算一次(Windows / 有 X 显示的 Linux):不打字符的键(退格、Tab、回车、Ctrl、Shift)
    /// 与布局无关,永远是标准键值;字母键在任何布局下都打得出字符。macOS 的 TIS 接口只能在主线程上调
    /// (macOS 14 起在别的线程上调会断言失败、整个进程退出),测试线程不是主线程,所以 macOS 上不在这里真取。
    /// </summary>
    [TestMethod]
    public void HostKeymap_BuildsFromTheCurrentSystemLayout()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS 的 TIS 接口只能在主线程上调,宿主在 UI 线程上调用");
            return;
        }
        HostKeymapResult? keymap = OperatingSystem.IsWindows() ? WindowsKeymap.Build(WindowsKeymap.CurrentLayout())
            : OperatingSystem.IsLinux() ? LinuxKeymap.Build(ownDisplay: -1)
            : null;
        if (keymap is null)
        {
            Assert.IsTrue(OperatingSystem.IsLinux(), "Windows 上总能按当前布局算出键位表");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                Assert.Inconclusive("没有 $DISPLAY:沿用服务端的 US 键位表");
                return;
            }
            // 桌面标配的库在最小化的容器 / CI 镜像里可能没有:那是环境不全,不是实现的错。库都在还取不到才是真失败。
            string[] missing = [.. new[] { "libxcb.so.1", "libxkbcommon.so.0", "libxkbcommon-x11.so.0" }
                .Where(name => !NativeLibrary.TryLoad(name, out _))];
            if (missing.Length > 0)
            {
                Assert.Inconclusive($"缺少 {string.Join("、", missing)}:沿用服务端的 US 键位表");
                return;
            }
            Assert.Fail("有 $DISPLAY、库也都在,却没按桌面布局算出键位表");
        }
        int per = keymap.PerKeycode;
        Assert.IsTrue(per is 2 or 6, "无 AltGr 两列;有 AltGr 按 XKB §17 的核心列序六列");
        uint At(byte keycode, int column) => keymap.Main[((keycode - HostKeymap.FirstKeycode) * per) + column];
        Assert.AreEqual(0xff08u, At(XKeycodes.BackSpace, 0));
        Assert.AreEqual(0xfe20u, At(XKeycodes.Tab, 1), "Shift+Tab = ISO_Left_Tab");
        Assert.AreEqual(0xff0du, At(XKeycodes.Return, 0));
        Assert.AreEqual(0xffe1u, At(XKeycodes.ShiftLeft, 0));
        Assert.AreNotEqual(0u, At(XKeycodes.A, 0), "字母键在任何布局下都打得出字符");
        Assert.AreNotEqual(0u, At(XKeycodes.D1, 1), "数字行的 Shift 层也有字符");
        Assert.HasCount(per, keymap.IntlBackslash);
        if (per == 6)
        {
            Assert.AreEqual(At(XKeycodes.A, 0), At(XKeycodes.A, 2), "组 2 照抄组 1");
        }
    }

    [TestMethod]
    public async Task MappedWindow_BecomesNativeWindow_AndCloseButtonDisconnectsClient() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);

        (uint idBase, uint root) = await HandshakeAsync(client);
        uint window = idBase | 1, gc = idBase | 2;
        // CreateWindow 60×40 于 (100,50),背景白;WM_NAME = "xtest";映射;用 GC 填一块红。
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(100).I16(50).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0xFFFFFF));
        byte[] title = Encoding.ASCII.GetBytes("xtest");
        await SendAsync(client, 18, 0, w => w.U32(window).U32(39).U32(31).U8(8).Zero(3).U32((uint)title.Length).Bytes(title).Pad());
        await SendAsync(client, 8, 0, w => w.U32(window));
        await SendAsync(client, 55, 0, w => w.U32(gc).U32(window).U32(0x4).U32(0xFF0000));
        await SendAsync(client, 70, 0, w => w.U32(window).U32(gc).I16(0).I16(0).U16(10).U16(10));

        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        await WaitForAsync(() => native.Title == "xtest" ? native : null);
        Assert.AreEqual(new Size(60, 40), native.ClientSize, "headless 缩放为 1:物理像素 = DIP");
        Assert.AreEqual(window, native.Handle.Id);

        // 像素:左上角是填的红,右下是背景白。
        await WaitForAsync(() => Pixel(native, 5, 5) == 0xFF0000 ? native : null);
        Assert.AreEqual(0xFFFFFFu, Pixel(native, 50, 30));

        // 关闭按钮:客户端没声明 WM_DELETE_WINDOW → 服务端断开它 → 窗口随之收掉。
        native.Close();
        await WaitForAsync(() => host.Windows.Count == 0 ? native : null);
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        host.Detach();
    });

    /// <summary>
    /// 原生窗口的尺寸变了而不是我们按服务端几何设的(Linux 上 Avalonia 的 X11 后端给的原因是 Unspecified 而不是 User):照样回报给服务端。
    /// </summary>
    [TestMethod]
    public async Task NativeResizeWithoutUserReason_IsReportedToTheServer() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        _ = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        await WaitForAsync(() => native.ClientSize.Width == 60 ? native : null);

        native.Width = 90;   // 不经服务端改了原生窗口的尺寸(相当于用户在 Linux 上拖了边框)
        await WaitForAsync(() => native.Handle.Snapshot.Width == 90 ? native : null);
        host.Detach();
    });

    /// <summary>
    /// 映射时照客户端的提示摆:映射前就设好的 <c>_NET_WM_STATE</c> 最大化、<c>WM_HINTS</c> 的 initial_state = Iconic(<c>xterm -iconic</c>)、
    /// 用户指定在 (0, 0) 的位置(USPosition,<c>xterm -geometry +0+0</c>)—— 原先都按普通窗口显示、(0, 0) 一律挪到屏幕中央。
    /// </summary>
    [TestMethod]
    public async Task MapHonoursInitialStatesAndUserPosition() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte[]> replies = new();
        (uint idBase, uint root) = await HandshakeAsync(client, replies);
        uint maximized = idBase | 1, iconic = idBase | 2, pinned = idBase | 3;
        foreach (uint id in (uint[])[maximized, iconic, pinned])
        {
            await SendAsync(client, 1, 24, w => w.U32(id).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        }
        uint netWmState = await InternAsync(client, replies, "_NET_WM_STATE");
        uint vert = await InternAsync(client, replies, "_NET_WM_STATE_MAXIMIZED_VERT");
        uint horz = await InternAsync(client, replies, "_NET_WM_STATE_MAXIMIZED_HORZ");
        await SendAsync(client, 18, 0, w => w.U32(maximized).U32(netWmState).U32(4).U8(32).Zero(3).U32(2).U32(vert).U32(horz));
        await SendAsync(client, 18, 0, w => w.U32(iconic).U32(35).U32(35).U8(32).Zero(3).U32(9)              // WM_HINTS:StateHint,IconicState
            .U32(2).U32(0).U32(3).U32(0).U32(0).U32(0).U32(0).U32(0).U32(0));
        await SendAsync(client, 18, 0, w => w.U32(pinned).U32(40).U32(41).U8(32).Zero(3).U32(18).U32(1).Zero(17 * 4));   // USPosition
        foreach (uint id in (uint[])[maximized, iconic, pinned])
        {
            await SendAsync(client, 8, 0, w => w.U32(id));
        }

        XNativeWindow max = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == maximized));
        XNativeWindow min = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == iconic));
        XNativeWindow pin = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == pinned));
        Assert.AreEqual(Avalonia.Controls.WindowState.Maximized, max.WindowState, "映射前设好的最大化");
        Assert.AreEqual(Avalonia.Controls.WindowState.Minimized, min.WindowState, "initial_state = IconicState");
        Assert.IsFalse(min.ShowActivated, "一映射就最小化的窗口不抢前台");
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual((0, 0), (pin.Handle.Snapshot.X, pin.Handle.Snapshot.Y), "USPosition 的 (0, 0) 不被挪到屏幕中央");

        XNativeWindow[] all = [.. host.Windows];
        host.Detach();
        await WaitForAsync(() => all.All(w => !w.IsVisible) ? all : null);
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>InputOnly 的顶层(GTK 的 GtkInvisible):看不见,不开原生窗口 —— 原先宿主多出一个黑色的窗口。</summary>
    [TestMethod]
    public async Task InputOnlyTopLevel_GetsNoNativeWindow() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint invisible = idBase | 1, visible = idBase | 2;
        await SendAsync(client, 1, 0, w => w.U32(invisible).U32(root).I16(-100).I16(-100).U16(10).U16(10).U16(0).U16(2).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(invisible));
        await SendAsync(client, 1, 24, w => w.U32(visible).U32(root).I16(10).I16(10).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(visible));
        await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == visible));   // 之前映射的 InputOnly 那个也处理过了
        Assert.IsFalse(host.Windows.Any(w => w.Handle.Id == invisible));

        XNativeWindow[] all = [.. host.Windows];
        host.Detach();
        await WaitForAsync(() => all.All(w => !w.IsVisible) ? all : null);
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>连接建立;之后收到的回复按到达顺序放进 <paramref name="replies" />(事件与错误读掉不留)。</summary>
    private static async Task<(uint IdBase, uint Root)> HandshakeAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte[]> replies)
    {
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head);
        Assert.AreEqual(1, head[0], "连接建立成功");
        byte[] rest = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)) * 4];
        await stream.ReadExactlyAsync(rest);
        byte[] reply = [.. head, .. rest];
        int vendor = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(24));
        uint root = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(40 + ((vendor + 3) & ~3) + (reply[29] * 8)));
        _ = ReadRepliesAsync();
        return (BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(12)), root);

        async Task ReadRepliesAsync()
        {
            try
            {
                while (true)
                {
                    byte[] message = new byte[32];
                    await stream.ReadExactlyAsync(message);
                    if (message[0] == 1 || (message[0] & 0x7F) == 35)
                    {
                        byte[] extra = new byte[BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4)) * 4];
                        await stream.ReadExactlyAsync(extra);
                        if (message[0] == 1)
                        {
                            replies.Enqueue([.. message, .. extra]);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
            {
            }
        }
    }

    /// <summary>InternAtom,等它的回复(用 <see cref="HandshakeAsync(Stream, System.Collections.Concurrent.ConcurrentQueue{byte[]})" /> 建立的连接)。</summary>
    private static async Task<uint> InternAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte[]> replies, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        await SendAsync(stream, 16, 0, w => w.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        byte[] reply = await WaitForAsync(() => replies.TryDequeue(out byte[]? r) ? r : null);
        return BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(8));
    }

    /// <summary>
    /// owner 级联关闭(Avalonia 关 owner 时先问它拥有的窗口,有一个不肯 owner 就关不掉):
    /// 父窗口在 X 里取消映射、对话框还映射着 → 父窗口收掉,对话框不带 owner 重新显示;停服时一个都不留;
    /// 弹层的关闭不转给客户端(没有 WM_DELETE_WINDOW 的弹层原先一关就断开了整个程序)。
    /// </summary>
    [TestMethod]
    public async Task OwnerCascade_ReshowsStillMappedDialogs_LeavesNoGhosts_AndPopupCloseKeepsTheClient() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint parent = idBase | 1, dialog = idBase | 2, popup = idBase | 3;
        await SendAsync(client, 1, 24, w => w.U32(parent).U32(root).I16(10).I16(10).U16(80).U16(60).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 1, 24, w => w.U32(dialog).U32(root).I16(20).I16(20).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 18, 0, w => w.U32(dialog).U32(68).U32(33).U8(32).Zero(3).U32(1).U32(parent));   // WM_TRANSIENT_FOR
        await SendAsync(client, 8, 0, w => w.U32(parent));
        await SendAsync(client, 8, 0, w => w.U32(dialog));
        XNativeWindow parentNative = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == parent));
        XNativeWindow dialogNative = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == dialog));
        Assert.AreSame(parentNative, dialogNative.Owner, "对话框压在父窗口之上");

        // 父窗口取消映射、对话框还在:父窗口收掉,对话框重新显示(原先对话框拦下关闭,父窗口成了关不掉的空壳)。
        await SendAsync(client, 10, 0, w => w.U32(parent));
        await WaitForAsync(() => !parentNative.IsVisible ? parentNative : null);
        XNativeWindow reshown = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == dialog && w.IsVisible));
        Assert.IsNull(reshown.Owner);

        // 弹层(override-redirect):原生窗口被关(Alt+F4)不转给客户端,客户端不被断开。
        await SendAsync(client, 1, 24, w => w.U32(popup).U32(root).I16(30).I16(30).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0x200).U32(1));
        await SendAsync(client, 8, 0, w => w.U32(popup));
        XNativeWindow popupNative = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == popup));
        popupNative.Close();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(serve.IsCompleted, "客户端没有被断开");
        Assert.IsTrue(popupNative.IsVisible);

        // 停服:一个原生窗口都不留。
        XNativeWindow[] all = [.. host.Windows];
        host.Detach();
        await WaitForAsync(() => all.All(w => !w.IsVisible) ? all : null);
    });

    /// <summary>
    /// 按钮按着的时候窗口失活(Alt+Tab、别的窗口抢走)或失去捕获:之后的松开不会再送到这个窗口,X 那边要替它松开 ——
    /// 否则那个按钮一直按着、自动抓取也一直不解除。之后真的松开时不再补一次。
    /// headless 平台不发 Deactivated / PointerCaptureLost,这里直接调那两个处理器都调的 <see cref="XNativeWindow.ReleaseHeldButtons" />。
    /// </summary>
    [TestMethod]
    public async Task ReleaseHeldButtons_ReleasesInX_AndTheLaterMouseUpIsNotRepeated() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte> events = new();
        (uint idBase, uint root) = await HandshakeAsync(client, events);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x4 | 0x8));                                                  // ButtonPress | ButtonRelease
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        native.Activate();

        native.MouseDown(new Point(5, 5), MouseButton.Left);
        await WaitForAsync(() => events.Contains((byte)4) ? native : null);           // ButtonPress

        native.ReleaseHeldButtons();
        await WaitForAsync(() => events.Count(e => e == 5) == 1 ? native : null);       // ButtonRelease
        native.MouseUp(new Point(5, 5), MouseButton.Left);                               // 之后真的松开:不再补一个
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.ContainsSingle(e => e == 5, events, "松开只有一次");

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 按住一个键:同一个键再来 KeyDown(系统的自动重复,中间没有 KeyUp)时告诉服务端这是重复 —— 核心客户端看到
    /// 「按下、松开、按下、松开」(X 的自动重复),而不是「按下、按下、松开」;修饰键的重复由服务端丢掉。
    /// </summary>
    [TestMethod]
    public async Task 按住键时重复的KeyDown按自动重复转交_修饰键不重复() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        System.Collections.Concurrent.ConcurrentQueue<byte> events = new();
        (uint idBase, uint root) = await HandshakeAsync(client, events);
        uint window = idBase | 1;
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x1 | 0x2));                                                // KeyPress | KeyRelease
        await SendAsync(client, 8, 0, w => w.U32(window));
        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        native.Activate();
        server.FocusTopLevel(native.Handle);
        byte[] Keys() => [.. events.Where(e => e is 2 or 3)];

        native.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        native.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);                   // 自动重复
        native.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
        await WaitForAsync(() => Keys().Length >= 4 ? native : null);
        native.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);
        native.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.Shift);         // Windows 上按住 Shift 一直有 KeyDown
        native.KeyReleaseQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.None);
        await WaitForAsync(() => Keys().Length >= 6 ? native : null);
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        CollectionAssert.AreEqual(new byte[] { 2, 3, 2, 3, 2, 3 }, Keys());

        native.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// macOS 上 Command 组合键收不到 KeyUp:Command 松开时把按着它时按下的键一并松开 —— 否则 X 那边以为 C 一直按着。
    /// 在别的系统上打开这个处理来测(macOS 才默认打开)。
    /// </summary>
    [TestMethod]
    public async Task Command组合键收不到KeyUp时_Command松开把它们一并松开() => await _session.RunOnUiAsync(async () =>
    {
        bool before = XNativeWindow.CommandKeyUpMayBeLost;
        XNativeWindow.CommandKeyUpMayBeLost = true;
        try
        {
            AvaloniaXServerHost host = new();
            await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
            await host.AttachAsync(server, CancellationToken.None);
            (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
            Task serve = server.ServeAsync(serverSide, isLocal: true);
            System.Collections.Concurrent.ConcurrentQueue<byte> events = new();
            (uint idBase, uint root) = await HandshakeAsync(client, events);
            uint window = idBase | 1;
            await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
                .U32(0x800).U32(0x1 | 0x2));
            await SendAsync(client, 8, 0, w => w.U32(window));
            XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
            native.Activate();
            server.FocusTopLevel(native.Handle);
            byte[] Keys() => [.. events.Where(e => e is 2 or 3)];

            native.KeyPressQwerty(PhysicalKey.MetaLeft, RawInputModifiers.Meta);
            native.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Meta);       // Cmd+C:AppKit 不发它的 KeyUp
            native.KeyReleaseQwerty(PhysicalKey.MetaLeft, RawInputModifiers.None);
            await WaitForAsync(() => Keys().Length >= 4 ? native : null);
            CollectionAssert.AreEqual(new byte[] { 2, 2, 3, 3 }, Keys(), "Command 与 C 都松开了");

            native.CloseByHost();
            host.Detach();
            client.Dispose();
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            XNativeWindow.CommandKeyUpMayBeLost = before;
        }
    });

    /// <summary>
    /// override-redirect 的弹出层只在用户正在用 X 窗口时才系统级置顶:用户在本机窗口里时映射上来的(远端程序画的假凭据框)不盖住本机程序,
    /// 用户回到某个 X 窗口时照常置顶(菜单要在最上面)。
    /// </summary>
    [TestMethod]
    public async Task OverrideRedirectPopup_IsTopmostOnlyWhileAnXWindowIsActive() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);
        (uint idBase, uint root) = await HandshakeAsync(client);
        uint normal = idBase | 1, popup = idBase | 2;
        await SendAsync(client, 1, 24, w => w.U32(popup).U32(root).I16(10).I16(10).U16(30).U16(20).U16(0).U16(1).U32(0)
            .U32(0x200).U32(1));                                                                       // override-redirect
        await SendAsync(client, 8, 0, w => w.U32(popup));
        XNativeWindow menu = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == popup));
        Assert.IsFalse(menu.Topmost, "没有 X 窗口是活动的:原先一直系统级置顶,盖住所有本机程序");

        // 用户回到 X 窗口(映射一个普通窗口,它随之成为活动窗口)。
        await SendAsync(client, 1, 24, w => w.U32(normal).U32(root).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await SendAsync(client, 8, 0, w => w.U32(normal));
        XNativeWindow main = await WaitForAsync(() => host.Windows.FirstOrDefault(w => w.Handle.Id == normal));
        main.Activate();
        await WaitForAsync(() => menu.Topmost ? menu : null);

        main.CloseByHost();
        menu.CloseByHost();
        host.Detach();
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
    });

    /// <summary>
    /// 原生窗口按 256 × 256 切块、只取损伤矩形:跨块的窗口、后来只改了右下角一小块、客户端改了尺寸(缓冲变大、块数变多)之后,
    /// 各块的像素都对,没改到的地方保持原样。
    /// </summary>
    [TestMethod]
    public async Task TiledSurface_CopiesOnlyDamage_AndFollowsResize() => await _session.RunOnUiAsync(async () =>
    {
        AvaloniaXServerHost host = new();
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = "" }, host);
        await host.AttachAsync(server, CancellationToken.None);
        (InMemoryDuplexStream serverSide, InMemoryDuplexStream client) = InMemoryTransport.CreatePair();
        Task serve = server.ServeAsync(serverSide, isLocal: true);

        (uint idBase, uint root) = await HandshakeAsync(client);
        uint window = idBase | 1, red = idBase | 2, green = idBase | 3, blue = idBase | 4;
        // 300×270:横竖各跨两块。背景白,左上角填红。
        await SendAsync(client, 1, 24, w => w.U32(window).U32(root).I16(40).I16(40).U16(300).U16(270).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0xFFFFFF));
        await SendAsync(client, 8, 0, w => w.U32(window));
        await SendAsync(client, 55, 0, w => w.U32(red).U32(window).U32(0x4).U32(0xFF0000));
        await SendAsync(client, 55, 0, w => w.U32(green).U32(window).U32(0x4).U32(0x00FF00));
        await SendAsync(client, 55, 0, w => w.U32(blue).U32(window).U32(0x4).U32(0x0000FF));
        await SendAsync(client, 70, 0, w => w.U32(window).U32(red).I16(0).I16(0).U16(10).U16(10));

        XNativeWindow native = await WaitForAsync(() => host.Windows.FirstOrDefault());
        await WaitForAsync(() => Pixel(native, 5, 5) == 0xFF0000 ? native : null);
        Assert.AreEqual(0xFFFFFFu, Pixel(native, 290, 260), "右下那块(第二行第二列)是背景");

        // 只改右下角一小块:它所在的块取到了,左上角的块保持原样。
        await SendAsync(client, 70, 0, w => w.U32(window).U32(green).I16(280).I16(260).U16(10).U16(10));
        await WaitForAsync(() => Pixel(native, 285, 265) == 0x00FF00 ? native : null);
        Assert.AreEqual(0xFF0000u, Pixel(native, 5, 5));
        Assert.AreEqual(0xFFFFFFu, Pixel(native, 150, 150));

        // 客户端把窗口改到 520×300(横向三块):缓冲变了,整窗重取;新露出来的地方画了蓝色。
        // (默认 bit-gravity 是 Forget:改尺寸时服务端按背景重画整窗,原来的红、绿都没了 —— 原生窗口要跟服务端的缓冲一致。)
        await SendAsync(client, 12, 0, w => w.U32(window).U16(0x4 | 0x8).U16(0).U32(520).U32(300));
        await SendAsync(client, 70, 0, w => w.U32(window).U32(blue).I16(510).I16(290).U16(10).U16(10));
        await WaitForAsync(() => native.ClientSize == new Size(520, 300) && Pixel(native, 515, 295) == 0x0000FF ? native : null);
        foreach ((int x, int y) in new[] { (5, 5), (285, 265), (400, 10), (10, 290), (515, 295) })
        {
            Assert.AreEqual(ServerPixel(native, x, y), Pixel(native, x, y), $"({x},{y}) 与服务端的缓冲一致");
        }

        await SendAsync(client, 4, 0, w => w.U32(window));   // DestroyWindow
        await WaitForAsync(() => host.Windows.Count == 0 ? native : null);
        client.Dispose();
        await serve.WaitAsync(TimeSpan.FromSeconds(5));
        host.Detach();
    });

    /// <summary>服务端缓冲里的像素(低 24 位)。</summary>
    private static uint ServerPixel(XNativeWindow window, int x, int y)
    {
        uint[] pixels = new uint[window.Handle.Snapshot.Width * window.Handle.Snapshot.Height];
        (int w, _) = window.Handle.CopyPixels(pixels);
        return pixels[(y * w) + x] & 0xFFFFFF;
    }

    private static uint Pixel(XNativeWindow window, int x, int y)
    {
        Dispatcher.UIThread.RunJobs();
        if (window.CaptureRenderedFrame() is not { } frame)
        {
            return 0;
        }
        using (frame)
        {
            uint[] pixel = new uint[1];
            var pin = GCHandle.Alloc(pixel, GCHandleType.Pinned);
            try
            {
                frame.CopyPixels(new PixelRect(x, y, 1, 1), pin.AddrOfPinnedObject(), 4, 4);
            }
            finally
            {
                pin.Free();
            }
            uint value = pixel[0];
            // 截图的像素格式随渲染后端:BGRA 小端读出来是 0xAARRGGBB,RGBA 要把 R、B 对调。
            if (frame.Format == Avalonia.Platform.PixelFormat.Rgba8888)
            {
                value = (value & 0xFF00FF00) | ((value & 0xFF) << 16) | ((value >> 16) & 0xFF);
            }
            return value & 0xFFFFFF;
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        for (int i = 0; i < 250; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } value)
            {
                return value;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("等不到预期的状态");
    }

    // ------------------------------------------------------------------ 最小的 X 客户端(小端)

    private static async Task<(uint IdBase, uint Root)> HandshakeAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte>? events = null)
    {
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head);
        Assert.AreEqual(1, head[0], "连接建立成功");
        byte[] rest = new byte[BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)) * 4];
        await stream.ReadExactlyAsync(rest);
        byte[] reply = [.. head, .. rest];
        uint idBase = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(12));
        int vendor = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(24));
        int formats = reply[29];
        uint root = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(40 + ((vendor + 3) & ~3) + (formats * 8)));
        _ = events is null ? ReadAndDiscardAsync(stream) : ReadEventsAsync(stream, events);   // 不看的就读掉,只要别把管道堵住
        return (idBase, root);
    }

    /// <summary>记下收到的事件码(回复与错误跳过)。</summary>
    private static async Task ReadEventsAsync(Stream stream, System.Collections.Concurrent.ConcurrentQueue<byte> events)
    {
        byte[] head = new byte[32];
        try
        {
            while (true)
            {
                await stream.ReadExactlyAsync(head);
                if (head[0] == 1 || (head[0] & 0x7F) == 35)
                {
                    await stream.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) * 4]);
                }
                if (head[0] > 1)
                {
                    events.Enqueue((byte)(head[0] & 0x7F));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
        {
        }
    }

    private static async Task ReadAndDiscardAsync(Stream stream)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task SendAsync(Stream stream, byte opcode, byte data, Action<Body> body)
    {
        Body b = new();
        body(b);
        byte[] payload = b.ToArray();
        byte[] request = new byte[4 + payload.Length];
        request[0] = opcode;
        request[1] = data;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)(request.Length / 4));
        payload.CopyTo(request, 4);
        await stream.WriteAsync(request);
        await stream.FlushAsync();
    }

    private sealed class Body
    {
        private readonly List<byte> _bytes = [];

        public Body U8(byte v) { _bytes.Add(v); return this; }

        public Body U16(ushort v) { _bytes.Add((byte)v); _bytes.Add((byte)(v >> 8)); return this; }

        public Body I16(short v) => U16(unchecked((ushort)v));

        public Body U32(uint v) => U16((ushort)v).U16((ushort)(v >> 16));

        public Body Zero(int n) { _bytes.AddRange(new byte[n]); return this; }

        public Body Bytes(byte[] data) { _bytes.AddRange(data); return this; }

        public Body Pad() { while (_bytes.Count % 4 != 0) { _bytes.Add(0); } return this; }

        public byte[] ToArray() => [.. _bytes];
    }
}
