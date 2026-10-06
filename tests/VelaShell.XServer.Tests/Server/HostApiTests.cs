using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>宿主接口:运行中改的配置(DPI / 缩放、键位表)、选项校验、窗口快照与变化、光标、响铃、句柄的归属。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class HostApiTests
{
    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<string> RootStringAsync(XTestClient c, string property)
    {
        uint atom = await InternAsync(c, property);
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(atom).U32(0).U32(0).U32(1000));
        return Encoding.Latin1.GetString(p.Bytes, 32, (int)p.U32(16));
    }

    private static async Task<byte> MajorAsync(XTestClient c, string extension)
    {
        byte[] name = Encoding.Latin1.GetBytes(extension);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        Assert.AreEqual(1, q.Bytes[8], $"{extension} 应当存在");
        return q.Bytes[9];
    }

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(60).U16(40).U16(0).U16(1).U32(0)
            .U32(0x2).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    private static Task<ushort> SetTitleAsync(XTestClient c, uint window, string title)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(title);
        return c.SendAsync(18, 0, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)bytes.Length).Bytes(bytes).Pad());
    }

    [TestMethod]
    public async Task SetDisplayScale更新Xft_dpi与XSETTINGS的缩放()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        Assert.Contains("Xft.dpi:\t96", await RootStringAsync(c, "RESOURCE_MANAGER"));
        await c.SendAsync(2, 0, b => b.U32(c.RootWindow).U32(0x800).U32(0x400000));   // PropertyChange
        await c.SyncAsync();

        server.SetDisplayScale(192, 2);
        XMessage notify = await c.NextEventAsync(28);
        Assert.AreEqual(c.RootWindow, notify.U32(4));
        Assert.Contains("Xft.dpi:\t192", await RootStringAsync(c, "RESOURCE_MANAGER"));

        uint selection = await InternAsync(c, "_XSETTINGS_S0");
        uint settings = await InternAsync(c, "_XSETTINGS_SETTINGS");
        uint manager = (await c.RequestAsync(23, 0, b => b.U32(selection))).U32(8);
        XMessage prop = await c.RequestAsync(20, 0, b => b.U32(manager).U32(settings).U32(0).U32(0).U32(1000));
        byte[] data = prop.Bytes[32..(32 + (int)prop.U32(16))];
        int at = Encoding.ASCII.GetString(data).IndexOf("Gdk/WindowScalingFactor", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, at);
        Assert.AreEqual(2, BitConverter.ToInt32(data, at + 24 + 4), "名字 23 字节补到 24,再跳过 last-change-serial");
    }

    [TestMethod]
    public async Task SetKeymap一次换掉键值与布局名_客户端只收到一轮MappingNotify()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SyncAsync();
        // 德语布局的一角:键码 29 是 z / Z(QWERTZ)。
        server.SetKeymap(new XKeymap("de").Map(29, 'z', 'Z'));
        XMessage mapping = await c.NextEventAsync(34);
        Assert.AreEqual(1, mapping.Bytes[4], "request = Keyboard");
        Assert.AreEqual(29, mapping.Bytes[5], "从列出的最小键码起");

        XMessage keys = await c.RequestAsync(101, 0, b => b.U8(29).U8(1).U16(0));   // GetKeyboardMapping
        Assert.AreEqual('z', keys.U32(32));
        Assert.Contains("\0de\0", await RootStringAsync(c, "_XKB_RULES_NAMES"), "布局名跟着键位表一起到");
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(34, timeoutMs: 300),
            "修饰键表没变:不该再有第二轮 MappingNotify");
    }

    [TestMethod]
    public async Task SetKeymap的AltGr把右Alt挪进Mod5()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        server.SetKeymap(new XKeymap("de", 6) { AltGr = true }.Map(24, 'q', 'Q', 'q', 'Q', '@', '@'));
        await c.NextEventAsync(34);
        XMessage modifiers = await c.RequestAsync(119, 0);   // GetModifierMapping
        int per = modifiers.Bytes[1];
        Assert.AreEqual(XKeycodes.AltRight, modifiers.Bytes[32 + (7 * per)], "Mod5 里有右 Alt");
        XMessage keys = await c.RequestAsync(101, 0, b => b.U8(XKeycodes.AltRight).U8(1).U16(0));
        Assert.AreEqual(0xfe03u, keys.U32(32), "ISO_Level3_Shift");
    }

    [TestMethod]
    public void 选项不合法时构造就抛异常()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { Dpi = 0 }));
        Assert.AreEqual("options", error.ParamName);
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { ScreenWidth = 40_000 }));
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { DisplayNumber = -1 }));
        Assert.Throws<ArgumentException>(() => new X11Server(new X11ServerOptions { Monitors = [new XMonitor(0, 0, 0, 10)] }));
    }

    [TestMethod]
    public async Task Display按实际监听的传输给出_没监听时为null()
    {
        await using X11Server server = new(new X11ServerOptions { DisplayNumber = 97, ListenTcp = false, UnixSocketPath = "" });
        Assert.AreEqual(97, server.DisplayNumber);
        Assert.IsNull(server.Display, "还没开始监听");
        await server.StartAsync();
        Assert.IsNull(server.Display, "TCP 与 Unix 套接字都关了:只能经 ServeAsync 喂流");
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }

    [TestMethod]
    public async Task 宿主方法当场校验参数_别的服务端的窗口不收()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using X11Server other = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XTopLevelWindow window = host.Mapped[await MapTopAsync(c, host)];

        Assert.Throws<ArgumentOutOfRangeException>(() => server.ResizeTopLevel(window, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => server.InjectPointerButton(window, 0, 0, 0, pressed: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => server.SetTopLevelFrameExtents(window, new XFrameExtents(-1, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => other.MoveTopLevel(window, 0, 0));
    }

    [TestMethod]
    public async Task 快照整份替换_变化按组报告_没变不报()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];
        XTopLevelSnapshot before = window.Snapshot;
        Assert.IsTrue(before.IsMapped);
        Assert.AreEqual(60, before.Width);

        await SetTitleAsync(c, top, "one");
        await host.WaitForAsync(() => window.Snapshot.Title == "one");
        Assert.AreEqual(XTopLevelChanges.Title, host.LastChanges);
        Assert.AreEqual("", before.Title, "旧快照不变");

        await SetTitleAsync(c, top, "one");   // 值没变:不该报
        await c.SendAsync(12, 0, b => b.U32(top).U16(0xC).U16(0).U32(80).U32(50));   // ConfigureWindow 宽高
        await host.WaitForAsync(() => window.Snapshot.Width == 80);
        Assert.AreEqual(XTopLevelChanges.Geometry, host.LastChanges);
        Assert.ContainsSingle(e => e.Contains(" Title ", StringComparison.Ordinal), host.Log, "同样的标题再设一遍不算变化");
    }

    [TestMethod]
    public async Task 交给宿主的标题去掉控制字符与双向排版控制符_过长的截断()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow window = host.Mapped[top];

        await SetTitleAsync(c, top, "a\u001Bb\u0007c\u0085d");
        await host.WaitForAsync(() => window.Snapshot.Title == "abcd");

        // _NET_WM_NAME(UTF-8):RLO 能把任务栏里的标题倒着显示。
        uint netWmName = await InternAsync(c, "_NET_WM_NAME"), utf8String = await InternAsync(c, "UTF8_STRING");
        byte[] spoof = Encoding.UTF8.GetBytes("invoice‮txt.exe");
        await c.SendAsync(18, 0, b => b.U32(top).U32(netWmName).U32(utf8String).U8(8).U8(0).U8(0).U8(0).U32((uint)spoof.Length).Bytes(spoof).Pad());
        await host.WaitForAsync(() => window.Snapshot.Title == "invoicetxt.exe");

        byte[] huge = Encoding.UTF8.GetBytes(new string('x', 20000));
        await c.SendAsync(18, 0, b => b.U32(top).U32(netWmName).U32(utf8String).U8(8).U8(0).U8(0).U8(0).U32((uint)huge.Length).Bytes(huge).Pad());
        await host.WaitForAsync(() => window.Snapshot.Title.Length == 4096);
    }

    [TestMethod]
    public async Task 位图光标连图像交给宿主_XFIXES起的名字推出形状()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);

        uint pixmap = c.NewId(), cursor = c.NewId();
        await c.SendAsync(53, 1, b => b.U32(pixmap).U32(top).U16(16).U16(16));   // CreatePixmap,深度 1,全 0
        await c.SendAsync(93, 0, b => b.U32(cursor).U32(pixmap).U32(0)              // CreateCursor,没有 mask
            .U16(0xFFFF).U16(0).U16(0).U16(0).U16(0).U16(0xFFFF).U16(3).U16(4));
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x4000).U32(cursor));           // ChangeWindowAttributes:cursor
        await c.SyncAsync();
        server.InjectPointerMotion(host.Mapped[top], 5, 5);
        await host.WaitForAsync(() => host.Cursor?.Image is not null);
        XCursorImage image = host.Cursor!.Image!;
        Assert.AreEqual((16, 16, 3, 4), (image.Width, image.Height, image.HotspotX, image.HotspotY));
        Assert.AreEqual(0xFF0000FFu, image.Pixels[0], "source 为 0 的像素用背景色(蓝),不透明");
        Assert.AreEqual(XCursorShape.Arrow, host.Cursor.Shape, "没起名字:形状推不出来");

        byte xfixes = await MajorAsync(c, "XFIXES");
        byte[] name = Encoding.Latin1.GetBytes("text");
        await c.SendAsync(xfixes, 23, b => b.U32(cursor).U16((ushort)name.Length).U16(0).Bytes(name).Pad());   // SetCursorName
        await host.WaitForAsync(() => host.Cursor?.Shape == XCursorShape.Text);
        Assert.AreSame(image, host.Cursor!.Image, "图像不变");
    }

    /// <summary>建一个选了 ButtonPress 的顶层并映射。</summary>
    private static async Task<uint> MapClickableAsync(XTestClient c, RecordingHost host, short x, short y)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(x).I16(y).U16(100).U16(100).U16(0).U16(1).U32(0)
            .U32(0x800).U32(0x4 | 0x8));   // CWEventMask:ButtonPress | ButtonRelease
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    [TestMethod]
    public async Task 两个顶层重叠时_指针按宿主给的那个原生窗口命中_激活时X里也抬上来()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint a = await MapClickableAsync(c, host, 0, 0);
        uint b = await MapClickableAsync(c, host, 50, 50);   // 后建的 B 在 X 的堆叠里压在 A 上面

        // 用户在 A 的原生窗口里、两窗重叠的地方点了一下:原先按 X 的堆叠落到看不见的 B 上。
        server.InjectPointerButton(host.Mapped[a], 60, 60, 1, pressed: true);
        XMessage press = await c.NextEventAsync(4);
        Assert.AreEqual(a, press.U32(12), "ButtonPress 的事件窗口是 A");
        server.InjectPointerButton(host.Mapped[a], 60, 60, 1, pressed: false);
        await c.NextEventAsync(5);

        // 宿主激活 A:X 里 A 也抬到 B 上面。
        server.FocusTopLevel(host.Mapped[a]);
        await c.SyncAsync();
        XMessage tree = await c.RequestAsync(15, 0, w => w.U32(c.RootWindow));
        List<uint> children = [.. Enumerable.Range(0, tree.U16(16)).Select(i => tree.U32(32 + (4 * i)))];
        Assert.IsGreaterThan(children.IndexOf(b), children.IndexOf(a), "A 在 B 之上");
    }

    [TestMethod]
    public async Task 最小化的顶层不再接住指针_客户端抬高顶层时请宿主照办()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint a = await MapClickableAsync(c, host, 0, 0);
        uint b = await MapClickableAsync(c, host, 50, 50);

        // 指针在 A 的原生窗口里,但落在 A 之外、B 之内的地方(拖动时捕获着指针):全局找,B 最小化了就不算。
        server.InjectPointerMotion(host.Mapped[a], 120, 120);
        await c.SyncAsync();
        Assert.AreEqual(b, (await c.RequestAsync(38, 0, w => w.U32(c.RootWindow))).U32(12), "QueryPointer 的 child 是 B");
        server.SetTopLevelStates(host.Mapped[b], XWindowStates.Hidden);
        server.InjectPointerMotion(host.Mapped[a], 121, 121);
        await c.SyncAsync();
        Assert.AreEqual(0u, (await c.RequestAsync(38, 0, w => w.U32(c.RootWindow))).U32(12), "最小化的 B 不再接住指针");

        // XRaiseWindow(ConfigureWindow stack-mode = Above,没给 sibling)对顶层:宿主收到 XRaiseRequest。
        await c.SendAsync(12, 0, w => w.U32(a).U16(0x40).U16(0).U32(0));
        await host.WaitForAsync(() => host.Requests.Any(r => r is XRaiseRequest raise && raise.Window.Id == a));
    }

    [TestMethod]
    public async Task 最小化之后客户端MapWindow还原_请宿主去掉Hidden()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        server.SetTopLevelStates(host.Mapped[top], XWindowStates.Hidden);   // 用户在宿主里最小化了它
        await c.SyncAsync();

        await c.SendAsync(8, 0, b => b.U32(top));   // Tk 的 wm deiconify:MapWindow
        await host.WaitForAsync(() => host.Requests.Any(r => r is XStateChangeRequest { Remove: XWindowStates.Hidden } s && s.Window.Id == top));
    }

    [TestMethod]
    public async Task 同一批里的窗口变化合并_映射了又取消的抵消()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);

        // 3000 次改标题:原先每次一个 TopLevelChanged。
        await c.SendManyAsync(Enumerable.Range(0, 3000).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
        {
            byte[] title = Encoding.Latin1.GetBytes($"t{i}");
            return (18, 0, b => b.U32(top).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)title.Length).Bytes(title).Pad());
        }));
        await host.WaitForAsync(() => host.Mapped[top].Snapshot.Title == "t2999");
        await c.SyncAsync();
        int changes = host.Log.Count(e => e.StartsWith($"changed {top:x}", StringComparison.Ordinal));
        Assert.IsLessThan(300, changes, $"3000 次改标题交给宿主 {changes} 次");

        // 2000 对映射 / 取消映射:同一批里成对抵消,宿主几乎不必建、关原生窗口。
        uint other = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(other).U32(c.RootWindow).I16(0).I16(0).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0));
        await c.SendManyAsync(Enumerable.Range(0, 4000).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
            (i % 2 == 0 ? (byte)8 : (byte)10, 0, b => b.U32(other))));
        await c.SyncAsync();
        await Task.Delay(50);
        int maps = host.Log.Count(e => e.StartsWith($"mapped {other:x}", StringComparison.Ordinal));
        Assert.IsLessThan(200, maps, $"2000 次映射交给宿主 {maps} 次");
        Assert.IsFalse(host.Mapped.ContainsKey(other), "最后是取消映射");
    }

    [TestMethod]
    public async Task 成批的响铃合并成一次_之后按时间节流()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await c.SendManyAsync(Enumerable.Range(0, 5000).Select<int, (byte, byte, Action<XTestClient.Body>?)>(_ => (104, 0, null)));
        await c.SyncAsync();
        await Task.Delay(50);
        int delivered = host.Log.Count(e => e.StartsWith("bell", StringComparison.Ordinal));
        Assert.IsTrue(delivered is >= 1 and <= 3, $"5000 次响铃交给宿主 {delivered} 次");
    }

    [TestMethod]
    public async Task 响铃按协议从基准音量换算()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        int bells = 0;
        foreach (byte percent in (byte[])[0, 100, unchecked((byte)(sbyte)-50)])   // 0 → 基准 50;100 → 100;−50 → 50 − 25
        {
            await c.SendAsync(104, percent);
            await host.WaitForAsync(() => host.Log.Count(e => e.StartsWith("bell", StringComparison.Ordinal)) == bells + 1);
            bells++;
            await Task.Delay(150);   // 两次响铃之间至少隔 100 毫秒(节流)
        }
        Assert.AreSequenceEqual(["bell 50", "bell 100", "bell 25"], host.Log.Where(e => e.StartsWith("bell", StringComparison.Ordinal)).ToArray());

        XMessage error = await c.RequestAsync(104, 101);
        Assert.IsTrue(error.IsError, "−100…100 以外是 BadValue");
        Assert.AreEqual(2, error.Bytes[1]);
    }
}
