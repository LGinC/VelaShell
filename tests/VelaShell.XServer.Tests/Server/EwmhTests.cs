using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>窗口管理器的角色:EWMH 根属性、窗口提示解析、经根窗口 ClientMessage 提出的请求。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class EwmhTests
{
    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<uint> CreateTopAsync(XTestClient c)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(100).U16(80).U16(0).U16(1).U32(0).U32(0));
        return id;
    }

    private static Task<ushort> SetCard32Async(XTestClient c, uint window, uint property, uint type, params uint[] values) =>
        c.SendAsync(18, 0, b =>
        {
            b.U32(window).U32(property).U32(type).U8(32).U8(0).U8(0).U8(0).U32((uint)values.Length);
            foreach (uint v in values)
            {
                b.U32(v);
            }
        });

    private static Task<ushort> RootClientMessageAsync(XTestClient c, uint window, uint type, params uint[] data) =>
        c.SendAsync(25, 0, b =>
        {
            b.U32(c.RootWindow).U32(0x180000)   // SubstructureNotify | SubstructureRedirect
                .U8(33).U8(32).U16(0).U32(window).U32(type);
            for (int i = 0; i < 5; i++)
            {
                b.U32(i < data.Length ? data[i] : 0);
            }
        });

    [TestMethod]
    public async Task 根窗口有检查窗口与EWMH支持列表()
    {
        await using X11Server server = new(new X11ServerOptions { ScreenWidth = 1280, ScreenHeight = 720 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint check = await InternAsync(c, "_NET_SUPPORTING_WM_CHECK");
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(check).U32(0).U32(0).U32(1));
        uint checkWindow = p.U32(32);
        XMessage self = await c.RequestAsync(20, 0, b => b.U32(checkWindow).U32(check).U32(0).U32(0).U32(1));
        Assert.AreEqual(checkWindow, self.U32(32), "检查窗口上的属性指向它自己");

        uint supported = await InternAsync(c, "_NET_SUPPORTED");
        uint moveresize = await InternAsync(c, "_NET_WM_MOVERESIZE");
        XMessage list = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(supported).U32(0).U32(0).U32(100));
        uint[] atoms = [.. Enumerable.Range(0, (int)list.U32(16)).Select(i => list.U32(32 + (i * 4)))];
        CollectionAssert.Contains(atoms, moveresize);

        uint workarea = await InternAsync(c, "_NET_WORKAREA");
        XMessage area = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(workarea).U32(0).U32(0).U32(4));
        Assert.AreEqual(1280u, area.U32(40));
    }

    [TestMethod]
    public async Task 映射后有WM_STATE与客户端列表_提示解析进窗口快照()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        uint motif = await InternAsync(c, "_MOTIF_WM_HINTS");
        uint type = await InternAsync(c, "_NET_WM_WINDOW_TYPE");
        uint dialog = await InternAsync(c, "_NET_WM_WINDOW_TYPE_DIALOG");
        await SetCard32Async(c, top, motif, motif, 2, 0, 0, 0, 0);                       // 不要装饰
        await SetCard32Async(c, top, type, 4, dialog);                                   // ATOM
        await SetCard32Async(c, top, 40, 41, 16 | 32, 0, 0, 0, 0, 200, 150, 800, 600, 0, 0, 0, 0, 0, 0, 0, 0, 0);   // WM_NORMAL_HINTS
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        XTopLevelWindow handle = host.Mapped[top];
        Assert.IsFalse(handle.Snapshot.Decorated);
        Assert.AreEqual(XWindowType.Dialog, handle.Snapshot.WindowType);
        Assert.AreEqual(200, handle.Snapshot.MinWidth);
        Assert.AreEqual(600, handle.Snapshot.MaxHeight);

        uint wmState = await InternAsync(c, "WM_STATE");
        XMessage state = await c.RequestAsync(20, 0, b => b.U32(top).U32(wmState).U32(0).U32(0).U32(2));
        Assert.AreEqual(1u, state.U32(32), "WM_STATE = Normal");
        uint clients = await InternAsync(c, "_NET_CLIENT_LIST");
        XMessage list = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(clients).U32(0).U32(0).U32(10));
        Assert.AreEqual(top, list.U32(32));
    }

    [TestMethod]
    public async Task NET_WM_STATE请求交给宿主_宿主设状态后写回属性()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        uint netWmState = await InternAsync(c, "_NET_WM_STATE");
        uint fullscreen = await InternAsync(c, "_NET_WM_STATE_FULLSCREEN");

        await RootClientMessageAsync(c, top, netWmState, 1, fullscreen, 0, 1);   // add
        await host.WaitForAsync(() => host.Requests.OfType<XStateChangeRequest>().Any());
        XStateChangeRequest request = host.Requests.OfType<XStateChangeRequest>().First();
        Assert.AreEqual(XWindowStates.Fullscreen, request.Add);

        server.SetTopLevelStates(host.Mapped[top], XWindowStates.Fullscreen);
        await c.SyncAsync();
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(top).U32(netWmState).U32(0).U32(0).U32(10));
        uint[] atoms = [.. Enumerable.Range(0, (int)p.U32(16)).Select(i => p.U32(32 + (i * 4)))];
        CollectionAssert.Contains(atoms, fullscreen);
        await host.WaitForAsync(() => (host.Mapped[top].Snapshot.States & XWindowStates.Fullscreen) != 0);
    }

    [TestMethod]
    public async Task NET_WM_MOVERESIZE交给宿主且释放按着的按钮()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x4 | 0x8 | 0x40));   // ButtonPress | ButtonRelease | PointerMotion
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        server.InjectPointerButton(host.Mapped[top], 10, 10, 1, pressed: true);
        await c.NextEventAsync(4);

        uint moveresize = await InternAsync(c, "_NET_WM_MOVERESIZE");
        await RootClientMessageAsync(c, top, moveresize, 10, 10, 8, 1, 1);   // Move,按钮 1
        await host.WaitForAsync(() => host.Requests.OfType<XMoveResizeRequest>().Any());
        XMoveResizeRequest request = host.Requests.OfType<XMoveResizeRequest>().First();
        Assert.AreEqual(XMoveResizeDirection.Move, request.Direction);
        Assert.AreEqual(1, request.Button);

        // 指针被窗口管理器接管:按钮 1 不再算按着(QueryPointer 的 mask 里没有 Button1)。
        XMessage pointer = await c.RequestAsync(38, 0, b => b.U32(top));
        Assert.AreEqual(0, pointer.U16(24) & 0x100);
    }

    [TestMethod]
    public async Task NET_ACTIVE_WINDOW只有用户操作引起的才标成UserInitiated_CurrentTime与过期的时间戳不算()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint active = await InternAsync(c, "_NET_ACTIVE_WINDOW");
        uint top = await CreateTopAsync(c);
        await c.SendAsync(2, 0, b => b.U32(top).U32(0x800).U32(0x4));   // ButtonPress
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));

        async Task<XActivateRequest> ActivateAsync(uint source, uint time)
        {
            int before = host.Requests.OfType<XActivateRequest>().Count();
            await RootClientMessageAsync(c, top, active, source, time, 0);
            await host.WaitForAsync(() => host.Requests.OfType<XActivateRequest>().Count() > before);
            return host.Requests.OfType<XActivateRequest>().Last();
        }

        Assert.IsFalse((await ActivateAsync(1, 0)).UserInitiated, "CurrentTime 说明不了是用户引起的:原先一律激活");
        Assert.IsTrue((await ActivateAsync(2, 0)).UserInitiated, "分页器 / 任务栏直接代表用户");

        await Task.Delay(20);
        server.InjectPointerButton(host.Mapped[top], 5, 5, 1, pressed: true);   // 用户在程序里点了一下
        uint pressed = (await c.NextEventAsync(4)).U32(4);
        XActivateRequest byClick = await ActivateAsync(1, pressed);
        Assert.IsTrue(byClick.UserInitiated, "时间戳就是那次点击的:是它引起的");
        Assert.AreEqual(1, byClick.Source);
        Assert.AreEqual(pressed, byClick.Timestamp);
        Assert.IsFalse((await ActivateAsync(1, pressed - 10)).UserInitiated, "早于用户最近一次操作:过期的时间戳");
    }

    [TestMethod]
    public async Task 焦点给了顶层就更新活动窗口与FOCUSED状态()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = await CreateTopAsync(c);
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        server.FocusTopLevel(host.Mapped[top]);
        await c.SyncAsync();
        uint active = await InternAsync(c, "_NET_ACTIVE_WINDOW");
        XMessage p = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(active).U32(0).U32(0).U32(1));
        Assert.AreEqual(top, p.U32(32));
        await host.WaitForAsync(() => (host.Mapped[top].Snapshot.States & XWindowStates.Focused) != 0);
    }

    [TestMethod]
    public async Task FocusTopLevel按ICCCM的输入模型_不动override_redirect与不收输入的窗口_WM_TAKE_FOCUS发给声明了它的客户端()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint protocols = await InternAsync(c, "WM_PROTOCOLS"), takeFocus = await InternAsync(c, "WM_TAKE_FOCUS");
        const uint wmHints = 35, atom = 4;

        async Task<(uint Window, XTopLevelWindow Handle)> MapAsync(bool overrideRedirect, bool? input, bool takesFocus)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0)
                .U32(0x200).U32(overrideRedirect ? 1u : 0));
            if (input is { } value)
            {
                await SetCard32Async(c, id, wmHints, wmHints, 1, value ? 1u : 0, 0, 0, 0, 0, 0, 0, 0);   // flags = InputHint
            }
            if (takesFocus)
            {
                await SetCard32Async(c, id, protocols, atom, takeFocus);
            }
            await c.SendAsync(8, 0, b => b.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return (id, host.Mapped[id]);
        }

        async Task<uint> FocusAfterAsync(XTopLevelWindow handle)
        {
            server.FocusTopLevel(handle);
            return (await c.RequestAsync(43, 0)).U32(8);   // GetInputFocus
        }

        (_, XTopLevelWindow popup) = await MapAsync(overrideRedirect: true, input: null, takesFocus: false);
        Assert.AreEqual(1u, await FocusAfterAsync(popup), "override-redirect:焦点不动(还是 PointerRoot)");

        (_, XTopLevelWindow noInput) = await MapAsync(overrideRedirect: false, input: false, takesFocus: false);
        Assert.AreEqual(1u, await FocusAfterAsync(noInput), "No Input:焦点不动");

        (uint globallyActive, XTopLevelWindow global) = await MapAsync(overrideRedirect: false, input: false, takesFocus: true);
        Assert.AreEqual(1u, await FocusAfterAsync(global), "Globally Active:窗口管理器不设焦点");
        XMessage message = await c.NextEventAsync(33);
        Assert.AreEqual(globallyActive, message.U32(4));
        Assert.AreEqual(takeFocus, message.U32(12), "data[0] = WM_TAKE_FOCUS");
        Assert.AreNotEqual(0u, message.U32(16), "data[1] 是有效的时间戳,不是 CurrentTime");

        (uint locallyActive, XTopLevelWindow local) = await MapAsync(overrideRedirect: false, input: true, takesFocus: true);
        Assert.AreEqual(locallyActive, await FocusAfterAsync(local), "Locally Active:设焦点");
        Assert.AreEqual(locallyActive, (await c.NextEventAsync(33)).U32(4), "同时发 WM_TAKE_FOCUS");
    }
    [TestMethod]
    public async Task SetInputFocus按时间戳_宿主换了焦点之后迟到的旧请求不生效_客户端挪焦点到别的顶层时请宿主激活()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint protocols = await InternAsync(c, "WM_PROTOCOLS"), takeFocus = await InternAsync(c, "WM_TAKE_FOCUS");
        const uint wmHints = 35, atom = 4;

        async Task<(uint Window, XTopLevelWindow Handle)> MapAsync(bool takesFocus)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
            await SetCard32Async(c, id, wmHints, wmHints, 1, 1, 0, 0, 0, 0, 0, 0, 0);   // input = True
            if (takesFocus)
            {
                await SetCard32Async(c, id, protocols, atom, takeFocus);
            }
            await c.SendAsync(8, 0, b => b.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return (id, host.Mapped[id]);
        }

        async Task<uint> FocusAsync() => (await c.RequestAsync(43, 0)).U32(8);   // GetInputFocus

        (uint a, XTopLevelWindow handleA) = await MapAsync(takesFocus: true);   // Locally Active(GTK3 这类)
        (uint b, XTopLevelWindow handleB) = await MapAsync(takesFocus: false);
        server.FocusTopLevel(handleA);   // 用户点了 A:服务端给 A 发 WM_TAKE_FOCUS
        uint stamp = (await c.NextEventAsync(33)).U32(16);
        await Task.Delay(20);
        server.FocusTopLevel(handleB);   // 紧接着点了 B
        Assert.AreEqual(b, await FocusAsync());

        // A 对 WM_TAKE_FOCUS 的回应经 SSH 才到:时间戳早于宿主换焦点的时间,不生效。
        await c.SendAsync(42, 1, x => x.U32(a).U32(stamp));
        Assert.AreEqual(b, await FocusAsync(), "原先不看时间戳,焦点被拉回 A,而宿主上亮着的是 B");
        Assert.IsFalse(host.Requests.OfType<XFocusRequest>().Any());

        await c.SendAsync(42, 3, x => x.U32(a).U32(0));   // revert-to 只有 None / PointerRoot / Parent
        Assert.AreEqual(2, (await c.NextAsync(m => m.IsError)).Detail, "BadValue");

        // 客户端用 CurrentTime 把焦点挪到另一个顶层:生效,并请宿主激活它的原生窗口。
        await c.SendAsync(42, 1, x => x.U32(a).U32(0));
        Assert.AreEqual(a, await FocusAsync());
        await host.WaitForAsync(() => host.Requests.OfType<XFocusRequest>().Any(r => r.Window.Id == a));
    }
}
