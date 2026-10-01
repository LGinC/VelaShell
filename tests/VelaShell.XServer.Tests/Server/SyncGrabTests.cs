using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>同步抓取:冻结期间设备事件排队,AllowEvents 放行 / 单步 / 重放。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class SyncGrabTests
{
    private const byte KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6;
    private const byte Synchronous = 0, Asynchronous = 1;

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host, uint eventMask = 0)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0)
            .U32(0x800).U32(eventMask));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    private static async Task<List<XMessage>> DrainAsync(XTestClient c, params byte[] codes)
    {
        await c.SyncAsync();
        List<XMessage> events = [];
        while (true)
        {
            try
            {
                events.Add(await c.NextAsync(m => !m.IsReply && !m.IsError && codes.Contains(m.EventCode), timeoutMs: 80));
            }
            catch (OperationCanceledException)
            {
                return events;
            }
        }
    }

    [TestMethod]
    public async Task 同步GrabPointer冻住指针_AsyncPointer放行后按顺序送达()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        server.InjectPointerMotion(host.Mapped[top], 1, 1);
        await DrainAsync(c, MotionNotify);

        // GrabPointer:owner-events False,事件掩码 PointerMotion,指针同步、键盘异步。
        XMessage grab = await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        Assert.AreEqual(0, grab.Bytes[1], "GrabSuccess");
        server.InjectPointerMotion(host.Mapped[top], 10, 10);
        server.InjectPointerMotion(host.Mapped[top], 20, 20);
        Assert.IsEmpty(await DrainAsync(c, MotionNotify), "冻着:移动排队,一条都不发");

        await c.SendAsync(35, 0, b => b.U32(0));   // AllowEvents AsyncPointer
        List<XMessage> moves = await DrainAsync(c, MotionNotify);
        Assert.HasCount(2, moves, "放行后两条按原顺序到");
        Assert.AreEqual(10, moves[0].I16(24), "event-x");
        Assert.AreEqual(20, moves[1].I16(24));
    }

    [TestMethod]
    public async Task 同步GrabButton激活后冻结_ReplayPointer把按下重放给下面的窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(a, host);
        uint child = a.NewId();
        await a.SendAsync(1, 0, w => w.U32(child).U32(top).I16(10).I16(10).U16(50).U16(40).U16(0).U16(1).U32(0).U32(0));
        await a.SendAsync(8, 0, w => w.U32(child));
        await a.SyncAsync();
        await b.SendAsync(2, 0, w => w.U32(child).U32(0x800).U32(0x4 | 0x8));   // B 在子窗口上选 ButtonPress | ButtonRelease
        await b.SyncAsync();

        // A 在顶层上登记按钮 1 的同步被动抓取(任意修饰)。
        await a.SendAsync(28, 0, w => w.U32(top).U16(0x4 | 0x8).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U8(1).U8(0).U16(0x8000));
        await a.SyncAsync();

        server.InjectPointerButton(host.Mapped[top], 20, 20, 1, pressed: true);
        server.InjectPointerButton(host.Mapped[top], 20, 20, 1, pressed: false);
        List<XMessage> grabbed = await DrainAsync(a, ButtonPress, ButtonRelease);
        Assert.HasCount(1, grabbed, "A 只拿到按下;松开在冻结的队列里");
        Assert.AreEqual(ButtonPress, grabbed[0].EventCode);
        Assert.IsEmpty(await DrainAsync(b, ButtonPress, ButtonRelease), "被动抓取截走了按下");

        await a.SendAsync(35, 2, w => w.U32(0));   // AllowEvents ReplayPointer
        List<XMessage> replayed = await DrainAsync(b, ButtonPress, ButtonRelease);
        Assert.HasCount(2, replayed, "按下重放给 B,排着的松开随后也到 B");
        Assert.AreEqual(ButtonPress, replayed[0].EventCode);
        Assert.AreEqual(child, replayed[0].U32(12), "event 窗口是子窗口");
        Assert.AreEqual(ButtonRelease, replayed[1].EventCode);
        Assert.IsEmpty(await DrainAsync(a, ButtonPress, ButtonRelease), "A 的抓取已经解除");
    }

    [TestMethod]
    public async Task 同步GrabKeyboard_SyncKeyboard每次放行一个按键事件()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);

        XMessage grab = await c.RequestAsync(31, 0, b => b.U32(top).U32(0).U8(Asynchronous).U8(Synchronous).U16(0));
        Assert.AreEqual(0, grab.Bytes[1], "GrabSuccess");
        server.InjectKey(38, pressed: true);
        server.InjectKey(38, pressed: false);
        server.InjectKey(39, pressed: true);
        Assert.IsEmpty(await DrainAsync(c, KeyPress, KeyRelease), "键盘冻着");

        await c.SendAsync(35, 4, b => b.U32(0));   // SyncKeyboard
        List<XMessage> one = await DrainAsync(c, KeyPress, KeyRelease);
        Assert.HasCount(1, one, "只放行到下一个按键事件为止");
        Assert.AreEqual(38, one[0].Bytes[1]);

        await c.SendAsync(35, 3, b => b.U32(0));   // AsyncKeyboard
        List<XMessage> rest = await DrainAsync(c, KeyPress, KeyRelease);
        Assert.HasCount(2, rest);
        Assert.AreEqual(KeyRelease, rest[0].EventCode);
        Assert.AreEqual(39, rest[1].Bytes[1]);
    }

    [TestMethod]
    public async Task UngrabPointer解冻_排着的事件照常送达()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host, eventMask: 0x40);
        server.InjectPointerMotion(host.Mapped[top], 1, 1);
        await DrainAsync(c, MotionNotify);

        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        server.InjectPointerMotion(host.Mapped[top], 30, 30);
        Assert.IsEmpty(await DrainAsync(c, MotionNotify));
        await c.SendAsync(27, 0, b => b.U32(0));   // UngrabPointer
        Assert.HasCount(1, await DrainAsync(c, MotionNotify), "抓取解除,冻结随之解除,事件按普通选择送达");
    }

    private static Task<XMessage> GrabPointerAsync(XTestClient c, uint window, uint confineTo = 0) =>
        c.RequestAsync(26, 0, b => b.U32(window).U16(0).U8(Asynchronous).U8(Asynchronous).U32(confineTo).U32(0).U32(0));

    private static Task<XMessage> GrabKeyboardAsync(XTestClient c, uint window) =>
        c.RequestAsync(31, 0, b => b.U32(window).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));

    [TestMethod]
    public async Task 抓取窗口变得不可见时抓取自动解除_confine_to不可见时回GrabNotViewable()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(a, host);
        uint child = a.NewId();
        await a.SendAsync(1, 0, x => x.U32(child).U32(top).I16(10).I16(10).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0));
        await a.SendAsync(8, 0, x => x.U32(child));

        Assert.AreEqual(0, (await GrabPointerAsync(a, child)).Detail);
        Assert.AreEqual(0, (await GrabKeyboardAsync(a, child)).Detail);
        Assert.AreEqual(1, (await GrabPointerAsync(b, b.RootWindow)).Detail, "AlreadyGrabbed");
        Assert.AreEqual(1, (await GrabKeyboardAsync(b, b.RootWindow)).Detail, "AlreadyGrabbed");

        await a.SendAsync(10, 0, x => x.U32(top));   // UnmapWindow:子窗口随之不可见
        await a.SyncAsync();
        Assert.AreEqual(0, (await GrabPointerAsync(b, b.RootWindow)).Detail, "指针抓取随之解除");
        Assert.AreEqual(0, (await GrabKeyboardAsync(b, b.RootWindow)).Detail, "键盘抓取随之解除");

        await b.SendAsync(27, 0, x => x.U32(0));   // UngrabPointer
        Assert.AreEqual(3, (await GrabPointerAsync(b, b.RootWindow, confineTo: child)).Detail, "confine-to 不可见:GrabNotViewable");
    }

    /// <summary>收齐这段时间的 FocusIn / FocusOut / KeymapNotify,写成「in/out 窗口名 detail mode」与「keymap」。</summary>
    private static async Task<List<string>> FocusTraceAsync(XTestClient c, Dictionary<uint, string> names)
    {
        string[] details = ["Ancestor", "Virtual", "Inferior", "Nonlinear", "NonlinearVirtual", "Pointer", "PointerRoot", "None"];
        List<string> trace = [];
        foreach (XMessage m in await DrainAsync(c, 9, 10, 11))
        {
            trace.Add(m.EventCode == 11
                ? "keymap"
                : $"{(m.EventCode == 9 ? "in" : "out")} {names[m.U32(4)]} {details[m.Detail]}{(m.Bytes[8] == 0 ? "" : " mode" + m.Bytes[8])}");
        }
        return trace;
    }

    private static void AssertTrace(string[] expected, List<string> actual) =>
        Assert.AreSequenceEqual(expected, actual, string.Join(" | ", actual));

    [TestMethod]
    public async Task 焦点事件按上下级关系给detail_中间的窗口发虚拟事件_FocusIn之后跟KeymapNotify()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        const uint focusChange = 0x200000, keymapState = 0x4000;
        uint top = await MapTopAsync(c, host, focusChange);
        uint other = await MapTopAsync(c, host, focusChange);
        uint child = c.NewId(), grandchild = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(child).U32(top).I16(0).I16(0).U16(50).U16(50).U16(0).U16(1).U32(0).U32(0x800).U32(focusChange));
        await c.SendAsync(1, 0, b => b.U32(grandchild).U32(child).I16(0).I16(0).U16(20).U16(20).U16(0).U16(1).U32(0).U32(0x800).U32(focusChange | keymapState));
        await c.SendAsync(8, 0, b => b.U32(child));
        await c.SendAsync(8, 0, b => b.U32(grandchild));
        Dictionary<uint, string> names = new() { [top] = "T", [other] = "O", [child] = "C", [grandchild] = "G" };
        await DrainAsync(c, 9, 10, 11);

        Task SetFocusAsync(uint window) => c.SendAsync(42, 2, b => b.U32(window).U32(0));   // SetInputFocus,revert-to Parent
        // PointerRoot → G。指针在 (0, 0),落在最后映射的 O 里:旧焦点是 PointerRoot 时,从指针所在的窗口往上(连根)先发 Pointer;
        // 然后 G 的根往下到 G 之前是 NonlinearVirtual,G 本身 Nonlinear。
        await SetFocusAsync(grandchild);
        AssertTrace(["out O Pointer", "in T NonlinearVirtual", "in C NonlinearVirtual", "in G Nonlinear", "keymap"], await FocusTraceAsync(c, names));

        await SetFocusAsync(child);        // G 是 C 的下级
        AssertTrace(["out G Ancestor", "in C Inferior"], await FocusTraceAsync(c, names));

        await SetFocusAsync(grandchild);   // G 是 C 的下级,反过来
        AssertTrace(["out C Inferior", "in G Ancestor", "keymap"], await FocusTraceAsync(c, names));

        await SetFocusAsync(other);        // 共同祖先是根:两边的中间窗口都是 NonlinearVirtual
        AssertTrace(["out G Nonlinear", "out C NonlinearVirtual", "out T NonlinearVirtual", "in O Nonlinear"], await FocusTraceAsync(c, names));

        // 键盘抓取激活 / 解除:就像焦点从 O 移到抓取窗口 C、再移回来,mode 是 Grab(1)/ Ungrab(2)。
        await c.RequestAsync(31, 0, b => b.U32(child).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));
        AssertTrace(["out O Nonlinear mode1", "in T NonlinearVirtual mode1", "in C Nonlinear mode1"], await FocusTraceAsync(c, names));
        await c.SendAsync(32, 0, b => b.U32(0));   // UngrabKeyboard
        AssertTrace(["out C Nonlinear mode2", "out T NonlinearVirtual mode2", "in O Nonlinear mode2"], await FocusTraceAsync(c, names));
    }

    [TestMethod]
    public async Task 两个设备都冻着时排着的事件放行后按原来的先后_排队有上限()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow handle = host.Mapped[top];
        await c.RequestAsync(31, 0, b => b.U32(top).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));                        // GrabKeyboard
        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Synchronous).U32(0).U32(0).U32(0));        // GrabPointer:两个都冻上

        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectPointerMotion(handle, 10, 10);
        server.InjectKey(XKeycodes.A, pressed: false);
        Assert.IsEmpty(await DrainAsync(c, KeyPress, KeyRelease, MotionNotify), "冻着:一条都不发");

        await c.SendAsync(35, 6, b => b.U32(0));   // AllowEvents AsyncBoth
        List<XMessage> events = await DrainAsync(c, KeyPress, KeyRelease, MotionNotify);
        Assert.AreSequenceEqual([KeyPress, MotionNotify, KeyRelease], events.Select(e => e.EventCode).ToArray(), "按键、移动、松开:按到达的先后,不是两个设备各走各的");

        // 再冻上,宿主不停地移动:排着的事件有上限。
        await c.RequestAsync(26, 0, b => b.U32(top).U16(0x40).U8(Synchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        for (int i = 0; i < X11Server.MaxFrozenInput + 500; i++)
        {
            server.InjectPointerMotion(handle, i % 90, 5);
        }
        Assert.AreEqual(X11Server.MaxFrozenInput, await server.InvokeAsync(() => server.FrozenInputCount));
    }

    [TestMethod]
    public async Task 抓取期间Enter与Leave只报给抓取方_激活与解除时发Grab与Ungrab模式的crossing()
    {
        const byte EnterNotify = 7, LeaveNotify = 8;
        const uint enterLeave = 0x10 | 0x20;
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);

        async Task<uint> MapAtAsync(short x)
        {
            uint id = a.NewId();
            await a.SendAsync(1, 0, w => w.U32(id).U32(a.RootWindow).I16(x).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0x800).U32(enterLeave));
            await a.SendAsync(8, 0, w => w.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint t = await MapAtAsync(0), u = await MapAtAsync(300);
        await b.SendAsync(2, 0, w => w.U32(u).U32(0x800).U32(enterLeave));   // B 也在 U 上选了 Enter / Leave
        server.InjectPointerMotion(host.Mapped[t], 5, 5);
        await DrainAsync(a, EnterNotify, LeaveNotify);
        await DrainAsync(b, EnterNotify, LeaveNotify);

        // A 在 U 上抓指针(owner-events False,抓取掩码 Enter | Leave):就像指针从 T 瞬移到 U,mode = Grab;只报抓取窗口 U 上的。
        await a.RequestAsync(26, 0, w => w.U32(u).U16((ushort)enterLeave).U8(Asynchronous).U8(Asynchronous).U32(0).U32(0).U32(0));
        List<XMessage> grabbed = await DrainAsync(a, EnterNotify, LeaveNotify);
        Assert.HasCount(1, grabbed, "只有抓取窗口上的那一个");
        Assert.AreEqual(EnterNotify, grabbed[0].EventCode);
        Assert.AreEqual(u, grabbed[0].U32(12));
        Assert.AreEqual(1, grabbed[0].Bytes[30], "mode = Grab");

        server.InjectPointerMotion(host.Mapped[u], 5, 5);   // 真的挪进 U:照常的 crossing,但只报给抓取方
        Assert.AreEqual(EnterNotify, (await DrainAsync(a, EnterNotify, LeaveNotify)).Single().EventCode);
        Assert.IsEmpty(await DrainAsync(b, EnterNotify, LeaveNotify), "抓取期间别的客户端收不到");

        server.InjectPointerMotion(host.Mapped[t], 5, 5);
        await DrainAsync(a, EnterNotify, LeaveNotify);
        await a.SendAsync(27, 0, w => w.U32(0));             // UngrabPointer:就像指针从 U 瞬移回 T,mode = Ungrab,照常报给所有人
        List<XMessage> released = await DrainAsync(b, EnterNotify, LeaveNotify);
        Assert.HasCount(1, released);
        Assert.AreEqual(LeaveNotify, released[0].EventCode);
        Assert.AreEqual(2, released[0].Bytes[30], "mode = Ungrab");
    }

    [TestMethod]
    public async Task 键盘被抓着且owner_events为True时按键照常按焦点报告_不是按指针所在的窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        async Task<uint> MapAtAsync(short x)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(x).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0x800).U32(0x1));   // KeyPress
            await c.SendAsync(8, 0, b => b.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint focused = await MapAtAsync(0), pointed = await MapAtAsync(300);
        server.FocusTopLevel(host.Mapped[focused]);
        server.InjectPointerMotion(host.Mapped[pointed], 5, 5);   // 指针在另一个窗口里
        await c.RequestAsync(31, 1, b => b.U32(c.RootWindow).U32(0).U8(Asynchronous).U8(Asynchronous).U16(0));   // GrabKeyboard,owner-events True

        server.InjectKey(XKeycodes.A, pressed: true);
        XMessage key = await c.NextEventAsync(KeyPress);
        Assert.AreEqual(focused, key.U32(12), "event = 焦点窗口");
    }
}
