using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>与宿主的剪贴板互通:宿主 → X(服务端当属主)与 X → 宿主(服务端当请求方,含 INCR)。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class ClipboardTests
{
    private const byte SelectionRequest = 30, SelectionNotify = 31, PropertyNotify = 28, SelectionClear = 29;

    private static async Task<uint> InternAsync(XTestClient c, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        XMessage m = await c.RequestAsync(16, 0, b => b.U16((ushort)bytes.Length).U16(0).Bytes(bytes).Pad());
        return m.U32(8);
    }

    private static async Task<uint> CreateWindowAsync(XTestClient c)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0));
        return id;
    }

    private static Task<ushort> ChangePropertyAsync(XTestClient c, uint window, uint property, uint type, byte[] data) =>
        c.SendAsync(18, 0, b => b.U32(window).U32(property).U32(type).U8(8).U8(0).U8(0).U8(0).U32((uint)data.Length).Bytes(data).Pad());

    private static Task<ushort> SendSelectionNotifyAsync(XTestClient c, XMessage request, uint property) =>
        c.SendAsync(25, 0, b => b.U32(request.U32(12)).U32(0)
            .U8(SelectionNotify).U8(0).U16(0).U32(request.U32(4)).U32(request.U32(12)).U32(request.U32(16))
            .U32(request.U32(20)).U32(property).U32(0).U32(0));

    [TestMethod]
    public async Task 宿主的文本X客户端能以UTF8与TARGETS取到()
    {
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false });   // 这里只看传输;跟着焦点走见下面单独的用例
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint targets = await InternAsync(c, "TARGETS");
        uint prop = await InternAsync(c, "MY_PROP");
        uint window = await CreateWindowAsync(c);

        server.SetClipboardText("héllo 世界");
        XMessage owner;
        do
        {
            owner = await c.RequestAsync(23, 0, b => b.U32(clipboard));
        }
        while (owner.U32(8) == 0);

        await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(utf8).U32(prop).U32(0));
        XMessage notify = await c.NextEventAsync(SelectionNotify);
        Assert.AreEqual(prop, notify.U32(20));
        XMessage value = await c.RequestAsync(20, 1, b => b.U32(window).U32(prop).U32(0).U32(0).U32(1000));
        Assert.AreEqual(utf8, value.U32(8));
        Assert.AreEqual("héllo 世界", Encoding.UTF8.GetString(value.Bytes, 32, (int)value.U32(16)));

        await c.SendAsync(24, 0, b => b.U32(window).U32(clipboard).U32(targets).U32(prop).U32(0));
        await c.NextEventAsync(SelectionNotify);
        XMessage list = await c.RequestAsync(20, 1, b => b.U32(window).U32(prop).U32(0).U32(0).U32(1000));
        uint[] atoms = [.. Enumerable.Range(0, (int)list.U32(16)).Select(i => list.U32(32 + (i * 4)))];
        CollectionAssert.Contains(atoms, utf8);
        CollectionAssert.Contains(atoms, 31u, "STRING");
    }

    [TestMethod]
    public async Task X客户端复制的文本交给宿主_写回来不抢选区()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint window = await CreateWindowAsync(c);

        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));   // SetSelectionOwner
        XMessage request = await c.NextEventAsync(SelectionRequest);
        Assert.AreEqual(window, request.U32(8), "owner");
        Assert.AreEqual(utf8, request.U32(20), "先要 UTF8_STRING");

        uint property = request.U32(24);
        await ChangePropertyAsync(c, request.U32(12), property, utf8, Encoding.UTF8.GetBytes("来自 X"));
        await SendSelectionNotifyAsync(c, request, property);
        await host.WaitForAsync(() => host.Clipboard == "来自 X");

        server.SetClipboardText("来自 X");   // 宿主把同一段文本写回来
        XMessage owner = await c.RequestAsync(23, 0, b => b.U32(clipboard));
        Assert.AreEqual(window, owner.U32(8), "不应抢走 X 客户端的选区");

        server.SetClipboardText("宿主的新文本");
        XMessage clear = await c.NextEventAsync(SelectionClear);
        Assert.AreEqual(window, clear.U32(8));
    }

    [TestMethod]
    public async Task X端复制之后属主退出_服务端替宿主接管_别的X程序照样粘贴得到()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        XTestClient copier = await XTestClient.ConnectAsync(server);
        await using XTestClient paster = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(copier, "CLIPBOARD");
        uint utf8 = await InternAsync(copier, "UTF8_STRING");
        uint window = await CreateWindowAsync(copier);

        await copier.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        XMessage request = await copier.NextEventAsync(SelectionRequest);
        uint property = request.U32(24);
        await ChangePropertyAsync(copier, request.U32(12), property, utf8, Encoding.UTF8.GetBytes("复制的文本"));
        await SendSelectionNotifyAsync(copier, request, property);
        await host.WaitForAsync(() => host.Clipboard == "复制的文本");

        await copier.DisposeAsync();   // 复制的程序退出
        uint requestor = await CreateWindowAsync(paster);
        uint prop = await InternAsync(paster, "PASTED");
        for (int i = 0; i < 50; i++)
        {
            XMessage owner = await paster.RequestAsync(23, 0, b => b.U32(clipboard));
            if (owner.U32(8) != 0)
            {
                break;
            }
            await Task.Delay(20);
        }
        await paster.SendAsync(24, 0, b => b.U32(requestor).U32(clipboard).U32(utf8).U32(prop).U32(0));   // ConvertSelection
        XMessage notify = await paster.NextEventAsync(SelectionNotify);
        Assert.AreEqual(prop, notify.U32(20), "有人回应了");
        XMessage value = await paster.RequestAsync(20, 1, b => b.U32(requestor).U32(prop).U32(0).U32(0).U32(1000));
        Assert.AreEqual("复制的文本", Encoding.UTF8.GetString(value.Bytes, 32, (int)value.U32(16)));
    }

    [TestMethod]
    public async Task 大文本走INCR分块()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { ClipboardFollowsFocus = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        uint incr = await InternAsync(c, "INCR");
        uint window = await CreateWindowAsync(c);

        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        XMessage request = await c.NextEventAsync(SelectionRequest);
        uint requestor = request.U32(12), property = request.U32(24);

        await c.SendAsync(2, 0, b => b.U32(requestor).U32(0x800).U32(0x400000));   // PropertyChangeMask
        await c.SendAsync(18, 0, b => b.U32(requestor).U32(property).U32(incr).U8(32).U8(0).U8(0).U8(0).U32(1).U32(9));
        await SendSelectionNotifyAsync(c, request, property);

        string[] chunks = ["abc", "defg", "hi", ""];
        foreach (string chunk in chunks)
        {
            XMessage deleted = await c.NextAsync(m => !m.IsReply && !m.IsError && m.EventCode == PropertyNotify && m.Bytes[16] == 1);
            Assert.AreEqual(property, deleted.U32(8));
            await ChangePropertyAsync(c, requestor, property, utf8, Encoding.UTF8.GetBytes(chunk));
        }
        await host.WaitForAsync(() => host.Clipboard == "abcdefghi");
    }

    [TestMethod]
    public async Task 服务端当XSETTINGS管理器发布DPI_且管理器选区转换不出剪贴板内容()
    {
        await using X11Server server = new(new X11ServerOptions { Dpi = 144 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint selection = await InternAsync(c, "_XSETTINGS_S0");
        uint settings = await InternAsync(c, "_XSETTINGS_SETTINGS");
        XMessage owner = await c.RequestAsync(23, 0, b => b.U32(selection));
        uint manager = owner.U32(8);
        Assert.AreNotEqual(0u, manager);

        XMessage prop = await c.RequestAsync(20, 0, b => b.U32(manager).U32(settings).U32(0).U32(0).U32(1000));
        Assert.AreEqual(settings, prop.U32(8), "类型也是 _XSETTINGS_SETTINGS");
        byte[] data = prop.Bytes[32..(32 + (int)prop.U32(16))];
        string text = Encoding.ASCII.GetString(data);
        int at = text.IndexOf("Xft/DPI", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, at);
        // 名字(7 字节补到 8)之后是 last-change-serial,再之后才是值。
        Assert.AreEqual(144 * 1024, BitConverter.ToInt32(data, at + 8 + 4));

        server.SetClipboardText("secret");
        uint window = await CreateWindowAsync(c);
        uint utf8 = await InternAsync(c, "UTF8_STRING");
        await c.SendAsync(24, 0, b => b.U32(window).U32(selection).U32(utf8).U32(utf8).U32(0));
        XMessage notify = await c.NextEventAsync(SelectionNotify);
        Assert.AreEqual(0u, notify.U32(20), "管理器选区不给剪贴板内容");
    }

    [TestMethod]
    public async Task 换DPI只替换RESOURCE_MANAGER里的Xft几项_用户xrdb进去的资源留着()
    {
        await using X11Server server = new(new X11ServerOptions { Dpi = 96 });
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint resources = await InternAsync(c, "RESOURCE_MANAGER");
        // xrdb -merge 的结果:用户的资源,连同一条带续行的与一条旧的 Xft.dpi。
        byte[] text = Encoding.Latin1.GetBytes("XTerm*background:\tblack\nEmacs.font:\tfixed\\\n-misc\nXft.dpi:\t120\n! 注释: 留着\n*faceName:\tMonospace\n");
        await c.SendAsync(18, 0, b => b.U32(c.RootWindow).U32(resources).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)text.Length).Bytes(text).Pad());
        await c.SyncAsync();

        server.SetDisplayScale(144);
        await c.SyncAsync();
        XMessage value = await c.RequestAsync(20, 0, b => b.U32(c.RootWindow).U32(resources).U32(0).U32(0).U32(100000));
        string merged = Encoding.Latin1.GetString(value.Bytes, 32, (int)value.U32(16));
        StringAssert.Contains(merged, "XTerm*background:\tblack\n", "原先换一次 DPI 整份覆盖,用户的资源全丢");
        StringAssert.Contains(merged, "Emacs.font:\tfixed\\\n-misc\n", "续行跟着它那一条走");
        StringAssert.Contains(merged, "*faceName:\tMonospace\n");
        StringAssert.Contains(merged, "Xft.dpi:\t144\n");
        Assert.DoesNotContain("Xft.dpi:\t120", merged, "服务端管的那几项换成新值,不留重复的旧值");
        Assert.AreEqual(1, merged.Split("Xft.antialias:").Length - 1);
    }

    [TestMethod]
    public async Task 关掉互通后不取也不占()
    {
        using RecordingHost host = new();
        await using X11Server server = new(new X11ServerOptions { SyncClipboard = false }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint clipboard = await InternAsync(c, "CLIPBOARD");
        uint window = await CreateWindowAsync(c);

        server.SetClipboardText("x");
        await c.SendAsync(22, 0, b => b.U32(window).U32(clipboard).U32(0));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(SelectionRequest, timeoutMs: 200));
        Assert.IsNull(host.Clipboard);
    }
    [TestMethod]
    public async Task 剪贴板跟着键盘焦点走_别的会话读不到宿主的文本_后台会话的复制不进系统剪贴板()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server, label: "joe@a:22");
        await using XTestClient xclip = await XTestClient.ConnectAsync(server, label: "joe@a:22");   // 同一个 SSH 会话里的 xclip
        await using XTestClient b = await XTestClient.ConnectAsync(server, label: "joe@b:22");
        uint clipboard = await InternAsync(a, "CLIPBOARD");
        uint utf8 = await InternAsync(a, "UTF8_STRING");

        async Task<uint> MapAsync(XTestClient c)
        {
            uint id = c.NewId();
            await c.SendAsync(1, 0, w => w.U32(id).U32(c.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
            await c.SendAsync(8, 0, w => w.U32(id));
            await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
            return id;
        }
        uint windowA = await MapAsync(a);
        uint windowB = await MapAsync(b);
        server.FocusTopLevel(host.Mapped[windowA]);   // 用户在用会话 A 的程序

        server.SetClipboardText("本机复制的密码");
        while ((await a.RequestAsync(23, 0, w => w.U32(clipboard))).U32(8) == 0)
        {
        }

        async Task<uint> ConvertAsync(XTestClient c)
        {
            uint requestor = await CreateWindowAsync(c);
            uint property = await InternAsync(c, "PASTED");
            await c.SendAsync(24, 0, w => w.U32(requestor).U32(clipboard).U32(utf8).U32(property).U32(0));
            return (await c.NextEventAsync(SelectionNotify)).U32(20);
        }
        Assert.AreNotEqual(0u, await ConvertAsync(xclip), "同一个会话(连接名相同)读得到");
        Assert.AreEqual(0u, await ConvertAsync(b), "别的会话读不到:原先点一下任意 X 窗口,所有会话都读得到");

        // 后台会话 B 抢 CLIPBOARD:不去取它的内容,系统剪贴板不被改写。
        await b.SendAsync(22, 0, w => w.U32(windowB).U32(clipboard).U32(0));
        await Assert.ThrowsAsync<OperationCanceledException>(() => b.NextEventAsync(SelectionRequest, timeoutMs: 200));
        Assert.IsNull(host.Clipboard);

        // 焦点所在的会话 A 复制:照常交给宿主。
        await a.SendAsync(22, 0, w => w.U32(windowA).U32(clipboard).U32(0));
        XMessage request = await a.NextEventAsync(SelectionRequest);
        await ChangePropertyAsync(a, request.U32(12), request.U32(24), utf8, Encoding.UTF8.GetBytes("从 A 复制"));
        await SendSelectionNotifyAsync(a, request, request.U32(24));
        await host.WaitForAsync(() => host.Clipboard == "从 A 复制");
    }
    [TestMethod]
    public async Task 宿主接管剪贴板的XFIXES通知只发给焦点所在的会话_别的会话不知道何时有新内容()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient a = await XTestClient.ConnectAsync(server, label: "joe@a:22");
        await using XTestClient b = await XTestClient.ConnectAsync(server, label: "joe@b:22");
        uint clipboard = await InternAsync(a, "CLIPBOARD");
        XMessage xfixes = await b.RequestAsync(98, 0, w => w.U16(6).U16(0).Bytes(Encoding.Latin1.GetBytes("XFIXES")).Pad());
        byte major = xfixes.Bytes[9], firstEvent = xfixes.Bytes[10];
        await a.SendAsync(major, 2, w => w.U32(a.RootWindow).U32(clipboard).U32(0x1));   // SelectSelectionInput:SetSelectionOwner
        await b.SendAsync(major, 2, w => w.U32(b.RootWindow).U32(clipboard).U32(0x1));
        await a.SyncAsync();
        await b.SyncAsync();

        uint windowA = a.NewId();
        await a.SendAsync(1, 0, w => w.U32(windowA).U32(a.RootWindow).I16(0).I16(0).U16(40).U16(30).U16(0).U16(1).U32(0).U32(0));
        await a.SendAsync(8, 0, w => w.U32(windowA));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(windowA));
        server.FocusTopLevel(host.Mapped[windowA]);

        server.SetClipboardText("本机复制的第一段");
        await a.NextEventAsync(firstEvent);
        server.SetClipboardText("本机复制的第二段");   // 服务端本来就是属主:GetSelectionOwner 看不出变化
        XMessage second = await a.NextEventAsync(firstEvent);
        Assert.AreEqual(clipboard, second.U32(12), "焦点所在的会话照常收到");
        await b.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => b.NextEventAsync(firstEvent, timeoutMs: 200),
            "别的会话收不到:原先它据此精确得知宿主剪贴板何时有了新内容");
    }

    [TestMethod]
    public async Task SetSelectionOwner的时间戳规则与当前有没有属主无关_未来的时间戳锁不住选区()
    {
        await using X11Server server = new();
        await using XTestClient attacker = await XTestClient.ConnectAsync(server);
        await using XTestClient victim = await XTestClient.ConnectAsync(server);
        uint selection = await InternAsync(attacker, "CLIPBOARD");
        uint evil = await CreateWindowAsync(attacker);
        uint good = await CreateWindowAsync(victim);
        async Task<uint> OwnerAsync() => (await victim.RequestAsync(23, 0, b => b.U32(selection))).U32(8);

        // 先放弃(没有属主了),再带一个远在未来的时间戳去占:原先「没属主时不查」,未来时间被接受。
        await attacker.SendAsync(22, 0, b => b.U32(0).U32(selection).U32(0));
        await attacker.SendAsync(22, 0, b => b.U32(evil).U32(selection).U32(0x7FFFFFF0));
        await attacker.SyncAsync();
        Assert.AreEqual(0u, await OwnerAsync(), "晚于服务端当前时间:无效");

        // 正常程序带真实时间去占:照常成功(原先之后所有人都被当成「早于当前属主」静默忽略)。
        await Task.Delay(20);
        await victim.SendAsync(22, 0, b => b.U32(good).U32(selection).U32(0));
        Assert.AreEqual(good, await OwnerAsync());

        // 属主放弃之后,最后一次换属主的时间还在:带着更早时间戳的请求照样无效。
        await victim.SendAsync(22, 0, b => b.U32(0).U32(selection).U32(0));
        await attacker.SendAsync(22, 0, b => b.U32(evil).U32(selection).U32(1));
        await attacker.SyncAsync();
        Assert.AreEqual(0u, await OwnerAsync(), "早于最后一次换属主的时间:无效");
    }
}
