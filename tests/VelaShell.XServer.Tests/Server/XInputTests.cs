using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>XInputExtension:设备查询、XI2 事件选择与投递、XI2 抓取、原始事件。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class XInputTests
{
    private const byte GenericEvent = 35;

    private static async Task<byte> XiAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(15).U16(0).Bytes(Encoding.Latin1.GetBytes("XInputExtension")).Pad());
        Assert.AreEqual(1, q.Bytes[8]);
        XMessage v = await c.RequestAsync(q.Bytes[9], 47, b => b.U16(2).U16(2));
        Assert.AreEqual(2, v.U16(8));
        Assert.AreEqual(2, v.U16(10));
        return q.Bytes[9];
    }

    private static Task<ushort> SelectAsync(XTestClient c, byte xi, uint window, ushort device, uint mask) =>
        c.SendAsync(xi, 46, b => b.U32(window).U16(1).U16(0).U16(device).U16(1)
            .U8((byte)mask).U8((byte)(mask >> 8)).U8((byte)(mask >> 16)).U8((byte)(mask >> 24)));

    private static Task<XMessage> NextXiAsync(XTestClient c, byte xi, int evtype, int timeoutMs = 5000) =>
        c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == GenericEvent && m.Bytes[1] == xi && m.U16(8) == evtype, timeoutMs);

    private static async Task<uint> MapTopAsync(XTestClient c, RecordingHost host)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(10).I16(20).U16(60).U16(40).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return id;
    }

    [TestMethod]
    public async Task XIQueryDevice列出两个主设备与两个从设备()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        XMessage all = await c.RequestAsync(xi, 48, b => b.U16(0).U16(0));
        Assert.AreEqual(4, all.U16(8));
        Assert.AreEqual(2, all.U16(32), "第一个是主指针");
        Assert.AreEqual(1, all.U16(34), "use = MasterPointer");
        XMessage masters = await c.RequestAsync(xi, 48, b => b.U16(1).U16(0));
        Assert.AreEqual(2, masters.U16(8));
        XMessage bad = await c.RequestAsync(xi, 48, b => b.U16(42).U16(0));
        Assert.IsTrue(bad.IsError);
    }

    [TestMethod]
    public async Task XI2按键事件带主设备号与键码_核心事件照样给选了核心的客户端()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient core = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, top, 1, 1u << 2);                             // XIAllMasterDevices:KeyPress
        await core.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x1));      // 另一个客户端:核心 KeyPress
        await core.SyncAsync();
        await c.SyncAsync();                                                   // 两个客户端的请求都执行完了,再注入
        server.FocusTopLevel(host.Mapped[top]);
        server.InjectKey(38, true);

        XMessage e = await NextXiAsync(c, xi, 2);
        Assert.AreEqual(3, e.U16(10), "deviceid = 主键盘");
        Assert.AreEqual(38u, e.U32(16), "detail = 键码");
        Assert.AreEqual(top, e.U32(24), "event 窗口");
        Assert.AreEqual(5, e.U16(52), "sourceid = 从键盘");
        XMessage coreEvent = await core.NextEventAsync(2);
        Assert.AreEqual(38, coreEvent.Bytes[1]);
    }

    [TestMethod]
    public async Task XI2按下按钮形成隐式抓取_移出窗口仍收到移动()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, top, 1, (1u << 4) | (1u << 5) | (1u << 6));
        await c.SyncAsync();
        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: true);
        XMessage press = await NextXiAsync(c, xi, 4);
        Assert.AreEqual(1u, press.U32(16), "button 1");
        Assert.AreEqual(5 << 16, (int)press.U32(40), "event_x(FP1616)");

        server.InjectPointerMotion(host.Mapped[top], 200, 5);   // 出了 60 宽的窗口
        XMessage motion = await c.NextAsync(m => m.EventCode == GenericEvent && m.U16(8) == 6 && (int)m.U32(40) == 200 << 16);
        Assert.AreEqual(top, motion.U32(24), "隐式抓取期间事件仍发到按下的窗口");
        Assert.AreEqual(1, motion.Bytes[80] >> 1 & 1, "buttons 掩码里按钮 1 按着");
    }

    [TestMethod]
    public async Task XIGrabDevice抓住键盘后按键都发给抓取窗口()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        XMessage status = await c.RequestAsync(xi, 51, b => b.U32(top).U32(0).U32(0).U16(3).U8(1).U8(1).U8(0).U8(0).U16(1)
            .U8(1 << 2).U8(0).U8(0).U8(0));
        Assert.AreEqual(0, status.Bytes[8], "GrabSuccess");
        server.FocusTopLevel(null);   // 焦点不在它身上也照样收到
        server.InjectKey(24, true);
        XMessage e = await NextXiAsync(c, xi, 2);
        Assert.AreEqual(top, e.U32(24));
        await c.SendAsync(xi, 52, b => b.U32(0).U16(3).U16(0));
        await c.SyncAsync();
    }

    [TestMethod]
    public async Task 根窗口上选的原始移动报设备的绝对位置_Warp不发也不挪设备位置()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);   // 在根坐标 (10, 20)
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 17);   // XIAllDevices:RawMotion
        await c.SyncAsync();
        server.InjectPointerMotion(host.Mapped[top], 1, 1);
        await NextXiAsync(c, xi, 17);
        server.InjectPointerMotion(host.Mapped[top], 4, 6);
        XMessage raw = await NextXiAsync(c, xi, 17);
        Assert.AreEqual(1, raw.U16(22), "valuators_len");
        Assert.AreEqual(14, (int)raw.U32(36), "轴声明的是 Abs X:报设备的位置(整数部分)");
        Assert.AreEqual(26, (int)raw.U32(44), "Abs Y");

        // 程序 Warp 回窗口中心:服务端合成的,不是设备运动 —— 不发原始移动。
        await c.SendAsync(41, 0, b => b.U32(0).U32(top).I16(0).I16(0).U16(0).U16(0).I16(30).I16(20));   // WarpPointer
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => NextXiAsync(c, xi, 17, timeoutMs: 100), "原先 Warp 也发一条反向的增量");

        // 用户再挪 +2:原始值是设备的位置(没被 Warp 改过),客户端拿前后两次的差得到真实的 +2。
        server.InjectPointerMotion(host.Mapped[top], 6, 6);
        XMessage next = await NextXiAsync(c, xi, 17);
        Assert.AreEqual(16, (int)next.U32(36));
        Assert.AreEqual(26, (int)next.U32(44));
    }

    /// <summary>XIChangeHierarchy 的 AddMaster:一条 HIERARCHYCHANGE。</summary>
    private static Task<ushort> AddMasterAsync(XTestClient c, byte xi, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        int padded = (bytes.Length + 3) & ~3;
        return c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0)
            .U16(1).U16((ushort)((8 + padded) / 4)).U16((ushort)bytes.Length).U8(1).U8(1).Bytes(bytes).Pad());
    }

    [TestMethod]
    public async Task AddMaster新建一对主设备_发HierarchyChanged_QueryDevice列得出()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 11);   // XIAllDevices:HierarchyChanged
        await AddMasterAsync(c, xi, "second");

        XMessage changed = await NextXiAsync(c, xi, 11);
        Assert.AreEqual(1u, changed.U32(16) & 1, "flags 含 MasterAdded");
        Assert.AreEqual(6, changed.U16(20), "num_info:原来四个 + 新的一对");

        XMessage all = await c.RequestAsync(xi, 48, b => b.U16(0).U16(0));
        Assert.AreEqual(6, all.U16(8));
        string names = Encoding.Latin1.GetString(all.Bytes);
        Assert.Contains("second pointer", names);
        Assert.Contains("second keyboard", names);
    }

    [TestMethod]
    public async Task AttachSlave之后事件以新主设备报_DetachSlave之后只报从设备且没有核心事件()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await AddMasterAsync(c, xi, "second");   // 主指针 6、主键盘 7
        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(3).U16(2).U16(4).U16(6));   // AttachSlave 4 → 6
        await SelectAsync(c, xi, top, 0, 1u << 6);                                                 // XIAllDevices:Motion
        await c.SyncAsync();

        server.InjectPointerMotion(host.Mapped[top], 3, 3);
        XMessage attached = await NextXiAsync(c, xi, 6);
        Assert.AreEqual(6, attached.U16(10), "deviceid = 新的主指针");

        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(4).U16(2).U16(4).U16(0));    // DetachSlave 4
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x40));                             // 同时选核心 PointerMotion
        await c.SyncAsync();
        server.InjectPointerMotion(host.Mapped[top], 5, 5);
        XMessage floating = await NextXiAsync(c, xi, 6);
        Assert.AreEqual(4, floating.U16(10), "浮动:deviceid = 从设备本身");
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(6, timeoutMs: 150), "浮动的从设备不产生核心事件");
    }

    [TestMethod]
    public async Task RemoveMaster让从设备浮动_虚拟核心设备不能删除()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        await AddMasterAsync(c, xi, "second");
        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(3).U16(2).U16(5).U16(7));    // 从键盘挂到 7
        await c.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(2).U16(3).U16(6).U8(1).U8(0).U16(0).U16(0));   // RemoveMaster 6,Float
        XMessage keyboard = await c.RequestAsync(xi, 48, b => b.U16(5).U16(0));
        Assert.AreEqual(5, keyboard.U16(34), "use = FloatingSlave");
        Assert.AreEqual(0, keyboard.U16(36), "attachment = 0");

        XMessage error = await c.RequestAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(2).U16(3).U16(2).U8(1).U8(0).U16(0).U16(0));
        Assert.IsTrue(error.IsError, "删虚拟核心指针:BadDevice");
    }

    [TestMethod]
    public async Task AddMaster分完设备ID回BadAlloc_错误里带已生效的条数_之前的改动照样生效()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 11);   // HierarchyChanged

        // 一条请求里 255 个 AddMaster:设备 ID 是 CARD8(XI 1.x),6–255 只容得下 125 对。
        XMessage error = await c.RequestAsync(xi, 43, b =>
        {
            b.U8(255).U8(0).U8(0).U8(0);
            for (int i = 0; i < 255; i++)
            {
                b.U16(1).U16(3).U16(1).U8(1).U8(1).Bytes("m"u8.ToArray()).Pad();
            }
        });
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(11, error.Bytes[1], "BadAlloc");
        Assert.AreEqual(125u, error.U32(4), "bad value = 已经生效的条数");

        XMessage changed = await NextXiAsync(c, xi, 11);
        Assert.AreEqual(254, changed.U16(20), "num_info:原来四个 + 125 对");
        XMessage all = await c.RequestAsync(xi, 48, b => b.U16(0).U16(0));
        Assert.AreEqual(254, all.U16(8));
    }

    [TestMethod]
    public async Task XI2按钮事件的buttons是事件之前的按钮状态()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint top = await MapTopAsync(c, host);
        await SelectAsync(c, xi, top, 0, (1u << 4) | (1u << 5));   // ButtonPress | ButtonRelease
        await c.SyncAsync();

        server.InjectPointerButton(host.Mapped[top], 3, 3, 1, pressed: true);
        XMessage press = await NextXiAsync(c, xi, 4);
        Assert.AreEqual(0, press.Bytes[80] & 0x02, "按下:按钮 1 在事件之前还没按着");
        server.InjectPointerButton(host.Mapped[top], 3, 3, 1, pressed: false);
        XMessage release = await NextXiAsync(c, xi, 5);
        Assert.AreEqual(0x02, release.Bytes[80] & 0x02, "松开:事件之前还按着");
    }

    /// <summary>XIPassiveGrabDevice(在根窗口上抓按钮或按键,掩码 1 个单位为 0)。</summary>
    private static Task<XMessage> PassiveGrabAsync(XTestClient c, byte xi, uint detail, byte grabType, params uint[] modifiers) =>
        c.RequestAsync(xi, 54, b =>
        {
            b.U32(0).U32(c.RootWindow).U32(0).U32(detail).U16(2).U16((ushort)modifiers.Length).U16(1)
                .U8(grabType).U8(1).U8(1).U8(0).U16(0).U32(0);
            foreach (uint m in modifiers)
            {
                b.U32(m);
            }
        });

    [TestMethod]
    public async Task XI2被动抓取与别的客户端冲突的修饰组合回报为AlreadyGrabbed()
    {
        await using X11Server server = new();
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(a);
        await XiAsync(b);
        XMessage first = await PassiveGrabAsync(a, xi, 1, 0, 0);
        Assert.AreEqual(0, first.U16(8), "a 的抓取都成");

        XMessage second = await PassiveGrabAsync(b, xi, 1, 0, 0, 4);
        Assert.AreEqual(1, second.U16(8), "b 的 0 号组合与 a 冲突");
        Assert.AreEqual(0u, second.U32(32));
        Assert.AreEqual(1, second.Bytes[36], "AlreadyGrabbed");

        // AnyButton(0)与 AnyModifier 跟谁都冲突。
        XMessage any = await PassiveGrabAsync(b, xi, 0, 0, 0x80000000);
        Assert.AreEqual(1, any.U16(8));
    }

    [TestMethod]
    public async Task XI2被动抓取的detail与修饰位按规范校验()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        XMessage keycode = await PassiveGrabAsync(c, xi, 5, 1, 0);
        Assert.IsTrue(keycode.IsError, "键码 5 不合法");
        Assert.AreEqual(2, keycode.Detail, "BadValue");
        XMessage huge = await PassiveGrabAsync(c, xi, 70000, 0, 0);
        Assert.AreEqual(2, huge.Detail, "detail 超过 255");
        XMessage modifier = await PassiveGrabAsync(c, xi, 1, 0, 0x100);
        Assert.AreEqual(2, modifier.Detail, "核心修饰位之外的位");
    }

    /// <summary>
    /// 核心被动抓取:AnyModifier 的抓取与别人的具体组合冲突(原先只比完全相同的组合);Ungrab 掉 Shift 那一个组合,其余组合照样有效
    /// (原先 Ungrab 不拆分);核心的 UngrabKey(AnyKey, AnyModifier)不动同一客户端的 XI2 被动抓取(原先一起删);修饰与事件掩码校验。
    /// </summary>
    [TestMethod]
    public async Task 核心被动抓取按组合查冲突_Ungrab拆分AnyModifier抓取_核心Ungrab不动XI2的被动抓取()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(a);
        uint top = await MapTopAsync(a, host);
        await b.SendAsync(2, 0, w => w.U32(top).U32(0x800).U32(0x1));            // B 在顶层上选 KeyPress
        await a.SendAsync(33, 0, w => w.U32(top).U16(0x8000).U8(38).U8(1).U8(1).Pad());   // A:GrabKey(a, AnyModifier)
        await a.SyncAsync();

        XMessage conflict = await b.RequestAsync(33, 0, w => w.U32(top).U16(0x1).U8(38).U8(1).U8(1).Pad());
        Assert.AreEqual(10, conflict.Detail, "Shift+a 落在 A 的 AnyModifier 里:BadAccess");

        await a.SendAsync(34, 38, w => w.U32(top).U16(0x1).Pad());   // A:UngrabKey(a, Shift)
        await a.SyncAsync();
        server.FocusTopLevel(host.Mapped[top]);
        await b.SyncAsync();
        server.InjectKey(XKeycodes.ShiftLeft, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: false);
        server.InjectKey(XKeycodes.ShiftLeft, pressed: false);
        XMessage shifted = await b.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 2 && m.Detail == XKeycodes.A);
        Assert.AreEqual(1, shifted.U16(28) & 0xFF, "Shift+a 不再被抓,照常给 B");
        server.InjectKey(XKeycodes.A, pressed: true);
        server.InjectKey(XKeycodes.A, pressed: false);
        await a.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == 2 && m.Detail == XKeycodes.A);   // 不带修饰的 a 仍被 A 抓

        // A 的 XI2 被动抓取(s,不带修饰);核心 UngrabKey(AnyKey, AnyModifier)只删核心的抓取。
        XMessage xiGrab = await a.RequestAsync(xi, 54, w => w.U32(0).U32(top).U32(0).U32(XKeycodes.S).U16(3).U16(1).U16(1)
            .U8(1).U8(1).U8(1).U8(0).U16(0).U8(1 << 2).U8(0).U8(0).U8(0).U32(0));
        Assert.AreEqual(0, xiGrab.U16(8));
        await a.SendAsync(34, 0, w => w.U32(top).U16(0x8000).Pad());
        await a.SyncAsync();
        server.InjectKey(XKeycodes.S, pressed: true);
        XMessage xiPress = await NextXiAsync(a, xi, 2);
        Assert.AreEqual(XKeycodes.S, xiPress.U32(16), "XI2 的被动抓取还在");
        server.InjectKey(XKeycodes.S, pressed: false);

        XMessage badModifiers = await a.RequestAsync(33, 0, w => w.U32(top).U16(0x100).U8(40).U8(1).U8(1).Pad());
        Assert.AreEqual(2, badModifiers.Detail, "修饰组合里有核心修饰位之外的位:BadValue");
        XMessage badMask = await a.RequestAsync(28, 0, w => w.U32(top).U16(0x1).U8(1).U8(1).U32(0).U32(0).U8(1).U8(0).U16(0));
        Assert.AreEqual(2, badMask.Detail, "SETofPOINTEREVENT 之外的位:BadValue");
    }

    [TestMethod]
    public async Task 一个客户端在一个窗口上的XI2被动抓取有上限()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint[] all = [.. Enumerable.Range(0, 256).Select(m => (uint)m)];
        XMessage? error = null;
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        for (uint button = 1; button <= 20 && error is null; button++)
        {
            XMessage m = await PassiveGrabAsync(c, xi, button, 0, all);
            error = m.IsError ? m : null;
        }
        Assert.IsNotNull(error, "16 × 256 = 4096 个之后应当回 BadAlloc");
        Assert.AreEqual(11, error.Detail);
        Assert.IsLessThan(5_000, watch.ElapsedMilliseconds);
    }

    private static Task<ushort> XiChangePropertyAsync(XTestClient c, byte xi, byte mode, byte format, uint property, uint type, byte[] values) =>
        c.SendAsync(xi, 57, b => b.U16(2).U8(mode).U8(format).U32(property).U32(type).U32((uint)(values.Length / (format / 8))).Bytes(values).Pad());

    [TestMethod]
    public async Task XI设备属性按核心属性的规则校验_按本机序存放_变化发XI_PropertyEvent()
    {
        await using X11Server server = new();
        await using XTestClient big = await XTestClient.ConnectAsync(server, bigEndian: true);
        await using XTestClient little = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(big);
        await XiAsync(little);
        const uint property = 1, integer = 19;   // PRIMARY 当属性名用;INTEGER

        // little 在根窗口上选 XI_PropertyEvent(第 12 位)。
        await SelectAsync(little, xi, little.RootWindow, 0, 1u << 12);
        await little.SyncAsync();

        // 大端客户端写一个 32 位值,小端客户端读到的是同一个数。
        await XiChangePropertyAsync(big, xi, 0, 32, property, integer, [0x11, 0x22, 0x33, 0x44]);
        XMessage created = await NextXiAsync(little, xi, 12);
        Assert.AreEqual(property, created.U32(16));
        Assert.AreEqual(1, created.Bytes[20], "PropertyCreated");
        XMessage read = await little.RequestAsync(xi, 59, b => b.U16(2).U8(0).U8(0).U32(property).U32(0).U32(0).U32(1));
        Assert.AreEqual(0x11223344u, read.U32(32));

        // Append 的类型不一样:BadMatch。
        await XiChangePropertyAsync(little, xi, 2, 8, property, integer, [1]);
        XMessage mismatch = await little.NextAsync(m => m.IsError);
        Assert.AreEqual(8, mismatch.Detail, "BadMatch");

        // 声称的个数比带的数据多:BadLength(原先截断了事)。
        XMessage length = await little.RequestAsync(xi, 57, b => b.U16(2).U8(0).U8(8).U32(property).U32(integer).U32(100).U32(0));
        Assert.AreEqual(16, length.Detail, "BadLength");

        await little.SendAsync(xi, 58, b => b.U16(2).U16(0).U32(property));   // XIDeleteProperty
        XMessage deleted = await NextXiAsync(little, xi, 12);
        Assert.AreEqual(0, deleted.Bytes[20], "PropertyDeleted");
    }
    [TestMethod]
    public async Task XISelectEvents按设备分开存_分两次选不互相覆盖_一次请求给两个设备各选各的()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(c);
        uint window = await MapTopAsync(c, host);
        XTopLevelWindow handle = host.Mapped[window];

        // 先给 XIAllMasterDevices 选 RawMotion,再给 XIAllDevices 选 HierarchyChanged(原先后一次把前一次的主设备掩码盖掉)。
        await SelectAsync(c, xi, c.RootWindow, 1, 1u << 17);
        await SelectAsync(c, xi, c.RootWindow, 0, 1u << 11);
        await c.SyncAsync();
        server.InjectPointerMotion(handle, 5, 5);
        await NextXiAsync(c, xi, 17);

        XMessage selected = await c.RequestAsync(xi, 60, b => b.U32(c.RootWindow));   // XIGetSelectedEvents
        Assert.AreEqual(2, selected.U16(8), "两份:XIAllDevices 与 XIAllMasterDevices");
        Assert.AreEqual(0, selected.U16(32));
        Assert.AreEqual(1u << 11, selected.U32(36));
        Assert.AreEqual(1, selected.U16(44));
        Assert.AreEqual(1u << 17, selected.U32(48));

        // 一次请求:主指针(2)选 Motion,主键盘(3)选 KeyPress —— 原先后一个把前一个盖掉,指针事件全丢。
        await c.SendAsync(xi, 46, b => b.U32(window).U16(2).U16(0)
            .U16(2).U16(1).U32(1u << 6)
            .U16(3).U16(1).U32(1u << 2));
        await c.SyncAsync();
        server.InjectPointerMotion(handle, 9, 9);
        XMessage motion = await NextXiAsync(c, xi, 6);
        Assert.AreEqual(2, motion.U16(10), "deviceid = 主指针");
        server.FocusTopLevel(handle);
        server.InjectKey(38, pressed: true);
        await NextXiAsync(c, xi, 2);
    }
    [TestMethod]
    public async Task BreakGrabs解除抓取与冻结_放开GrabServer_把浮动的从设备挂回去()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server);
        await using XTestClient b = await XTestClient.ConnectAsync(server);
        byte xi = await XiAsync(a);

        async Task<uint> MapAtAsync(XTestClient c, short x)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, w => w.U32(id).U32(c.RootWindow).I16(x).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0x800).U32(0x4));   // ButtonPress
            await c.SendAsync(8, 0, w => w.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint t = await MapAtAsync(a, 0), u = await MapAtAsync(b, 300);

        // 卡住的样子:A 同步抓着指针(设备冻结)、抓着服务器,还把从指针浮动了;之后 A 再也不说话(远端进程被 SIGSTOP)。
        await a.SendAsync(xi, 43, w => w.U8(1).U8(0).U8(0).U8(0).U16(4).U16(2).U16(4).U16(0));   // DetachSlave 4
        await a.RequestAsync(26, 0, w => w.U32(t).U16(0x4).U8(0).U8(1).U32(0).U32(0).U32(0));    // GrabPointer,pointer_mode = Synchronous
        await a.SendAsync(36, 0);                                                                   // GrabServer
        await a.SyncAsync();
        ushort pending = await b.SendAsync(43, 0);                                                  // B 的请求被 GrabServer 挂住

        server.BreakGrabs();
        XMessage reply = await b.NextAsync(m => m.IsReply && m.Sequence == pending);
        Assert.IsTrue(reply.IsReply, "GrabServer 放开了");
        XMessage slave = await b.RequestAsync(xi, 48, w => w.U16(4).U16(0));                       // XIQueryDevice 4
        Assert.AreEqual(2, slave.U16(36), "从指针挂回虚拟核心指针");

        server.InjectPointerButton(host.Mapped[u], 5, 5, 1, pressed: true);
        XMessage press = await b.NextEventAsync(4);
        Assert.AreEqual(u, press.U32(12), "抓取与冻结都解除了,按下照常报给 B 的窗口");
    }
    [TestMethod]
    public async Task RestrictForwardedClients_SSH转发来的连接看不到XTEST_收不到原始按键_不能改设备层级()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { RestrictForwardedClients = true }, host);
        await using XTestClient local = await XTestClient.ConnectAsync(server);
        await using XTestClient forwarded = await XTestClient.ConnectAsync(server, label: "joe@remote:22");   // 经 ServeAuthenticatedAsync

        static async Task<bool> HasXTestAsync(XTestClient c) =>
            (await c.RequestAsync(98, 0, b => b.U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("XTEST")).Pad())).Bytes[8] == 1;
        Assert.IsTrue(await HasXTestAsync(local));
        Assert.IsFalse(await HasXTestAsync(forwarded), "伪造的输入与真实键盘无从区分");

        byte xi = await XiAsync(forwarded);
        byte xiLocal = await XiAsync(local);
        await SelectAsync(forwarded, xi, forwarded.RootWindow, 0, 1u << 13);   // RawKeyPress
        await SelectAsync(local, xiLocal, local.RootWindow, 0, 1u << 13);
        await forwarded.SyncAsync();
        await local.SyncAsync();
        server.InjectKey(38, pressed: true);
        await NextXiAsync(local, xiLocal, 13);
        await Assert.ThrowsAsync<OperationCanceledException>(() => NextXiAsync(forwarded, xi, 13, timeoutMs: 200), "不抢焦点就能记下所有按键");

        await forwarded.SendAsync(xi, 43, b => b.U8(1).U8(0).U8(0).U8(0).U16(4).U16(2).U16(4).U16(0));   // DetachSlave 4
        Assert.AreEqual(10, (await forwarded.NextAsync(m => m.IsError)).Detail, "BadAccess");
    }
}
