using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>XFIXES 扩展:区域对象、选区属主追踪、光标隐藏与窗口形状区域。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class XFixesTests
{
    private const uint Primary = 1;   // 预定义原子 PRIMARY

    private static async Task<byte> XFixesMajorAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(6).U16(0).Bytes(Encoding.Latin1.GetBytes("XFIXES")).Pad());
        Assert.AreEqual(1, q.Bytes[8], "XFIXES 应当存在");
        Assert.AreEqual(65, q.Bytes[10], "first-event");
        Assert.AreEqual(128, q.Bytes[11], "first-error");
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

    [TestMethod]
    public async Task QueryVersion最高报5点0()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        XMessage v = await c.RequestAsync(major, 0, b => b.U32(6).U32(0));
        Assert.AreEqual(5u, v.U32(8));
        Assert.AreEqual(0u, v.U32(12));
        XMessage old = await c.RequestAsync(major, 0, b => b.U32(2).U32(0));
        Assert.AreEqual(2u, old.U32(8), "客户端要的更低就回它要的");
    }

    [TestMethod]
    public async Task 区域的并与取回以及销毁后报BadRegion()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);

        uint a = c.NewId(), b = c.NewId();
        await c.SendAsync(major, 5, x => x.U32(a).I16(0).I16(0).U16(10).U16(10));
        await c.SendAsync(major, 5, x => x.U32(b).I16(20).I16(0).U16(5).U16(5));
        await c.SendAsync(major, 13, x => x.U32(a).U32(b).U32(a));   // a = a ∪ b

        XMessage fetch = await c.RequestAsync(major, 19, x => x.U32(a));
        Assert.IsTrue(fetch.IsReply);
        Assert.AreEqual(0, fetch.I16(8), "extents x");
        Assert.AreEqual(25, fetch.U16(12), "extents 宽");
        Assert.AreEqual(10, fetch.U16(14), "extents 高");
        int count = (fetch.Bytes.Length - 32) / 8;
        int area = 0;
        for (int i = 0; i < count; i++)
        {
            area += fetch.U16(32 + (i * 8) + 4) * fetch.U16(32 + (i * 8) + 6);
        }
        Assert.AreEqual(100 + 25, area);

        await c.SendAsync(major, 10, x => x.U32(a));
        XMessage error = await c.RequestAsync(major, 19, x => x.U32(a));
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(128, error.Bytes[1], "BadRegion = first-error + 0");
    }

    [TestMethod]
    public async Task 选区属主变更与属主断开都发SelectionNotify()
    {
        await using X11Server server = new();
        await using XTestClient watcher = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(watcher);
        await watcher.SendAsync(major, 2, b => b.U32(watcher.RootWindow).U32(Primary).U32(0x7));
        await watcher.SyncAsync();

        XTestClient owner = await XTestClient.ConnectAsync(server);
        uint window = owner.NewId();
        await owner.SendAsync(1, 0, b => b.U32(window).U32(owner.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(1).U32(0).U32(0));
        await owner.SendAsync(22, 0, b => b.U32(window).U32(Primary).U32(0));   // SetSelectionOwner
        await owner.SyncAsync();

        XMessage set = await watcher.NextEventAsync(65);
        Assert.AreEqual(0, set.Bytes[1], "subtype = SetSelectionOwner");
        Assert.AreEqual(watcher.RootWindow, set.U32(4));
        Assert.AreEqual(window, set.U32(8), "owner");
        Assert.AreEqual(Primary, set.U32(12));

        await owner.DisposeAsync();
        XMessage closed = await watcher.NextEventAsync(65);
        Assert.AreEqual(2, closed.Bytes[1], "subtype = SelectionClientClose");
        Assert.AreEqual(0u, closed.U32(8));
    }

    [TestMethod]
    public async Task 选区监听每个客户端有上限_超出回BadAlloc_撤掉之后又能登记_窗口销毁时登记跟着清掉()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        int limit = X11Server.MaxSelectionInputsPerClient;
        uint[] windows = [.. Enumerable.Range(0, limit + 1).Select(_ => c.NewId())];
        await c.SendManyAsync(windows.Select<uint, (byte, byte, Action<XTestClient.Body>?)>(w =>
            (1, 0, b => b.U32(w).U32(c.RootWindow).I16(0).I16(0).U16(1).U16(1).U16(0).U16(2).U32(0).U32(0))));
        ushort last = await c.SendManyAsync(windows.Select<uint, (byte, byte, Action<XTestClient.Body>?)>(w =>
            (major, 2, b => b.U32(w).U32(Primary).U32(0x7))));
        await c.SyncAsync();
        XMessage error = await c.NextAsync(m => m.IsError, timeoutMs: 1000);
        Assert.AreEqual(11, error.Detail, "BadAlloc:原先没有上限,换属主时还要整表扫一遍");
        Assert.AreEqual(last, error.Sequence, "前面的都登记上了,只有超出的那一条失败");

        // 改已有登记的掩码不算新登记;撤掉一条(掩码 0)、或销毁一个登记过的窗口之后,又能登记新的。
        await c.SendAsync(major, 2, b => b.U32(windows[0]).U32(Primary).U32(0x1));
        await c.SendAsync(major, 2, b => b.U32(windows[1]).U32(Primary).U32(0));
        await c.SendAsync(4, 0, b => b.U32(windows[2]));   // DestroyWindow
        await c.SendAsync(major, 2, b => b.U32(windows[limit]).U32(Primary).U32(0x7));
        await c.SendAsync(major, 2, b => b.U32(c.RootWindow).U32(Primary).U32(0x7));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextAsync(m => m.IsError, timeoutMs: 200));

        // 登记照常生效:换属主时登记了的窗口都收到,撤掉的与销毁了的收不到。
        await c.SendAsync(22, 0, b => b.U32(windows[3]).U32(Primary).U32(0));
        await c.SyncAsync();
        HashSet<uint> notified = [];
        while (notified.Count < limit)
        {
            XMessage notify = await c.NextEventAsync(65);
            Assert.AreEqual(0, notify.Bytes[1], "subtype = SetSelectionOwner");
            notified.Add(notify.U32(4));
        }
        Assert.DoesNotContain(windows[1], notified);
        Assert.DoesNotContain(windows[2], notified);
        Assert.Contains(c.RootWindow, notified);
    }

    [TestMethod]
    public async Task HideCursor让宿主隐藏光标_ShowCursor恢复()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        uint top = await MapTopAsync(c, host);
        server.InjectPointerMotion(host.Mapped[top], 5, 5);

        await c.SendAsync(major, 29, b => b.U32(top));
        await host.WaitForAsync(() => host.Log.Contains("cursor Hidden"));

        await c.SendAsync(major, 30, b => b.U32(top));
        await host.WaitForAsync(() => host.Log.LastOrDefault(e => e.StartsWith("cursor", StringComparison.Ordinal)) == "cursor Arrow");

        XMessage error = await c.RequestAsync(major, 30, b => b.U32(top));
        Assert.IsTrue(error.IsError, "没隐藏过就 ShowCursor 是 BadMatch");
        Assert.AreEqual(8, error.Bytes[1]);
    }

    [TestMethod]
    public async Task SetWindowShapeRegion设出宿主可见的顶层形状()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        uint top = await MapTopAsync(c, host);
        XTopLevelWindow handle = host.Mapped[top];

        uint region = c.NewId();
        await c.SendAsync(major, 5, b => b.U32(region).I16(0).I16(0).U16(20).U16(10));
        await c.SendAsync(major, 21, b => b.U32(top).U8(0).U8(0).U8(0).U8(0).I16(5).I16(5).U32(region));
        await host.WaitForAsync(() => handle.Snapshot.Shape is not null);
        Assert.AreEqual(200, handle.Snapshot.Shape!.Sum(r => r.Width * r.Height));
        Assert.AreEqual(5, handle.Snapshot.Shape!.Min(r => r.X), "偏移生效");

        await c.SendAsync(major, 21, b => b.U32(top).U8(0).U8(0).U8(0).U8(0).I16(0).I16(0).U32(0));
        await host.WaitForAsync(() => handle.Snapshot.Shape is null);
    }

    [TestMethod]
    public async Task DestroyPointerBarrier只认屏障_拿根窗口或别人的窗口来删回BadBarrier()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient other = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        uint theirs = other.NewId();
        await other.SendAsync(1, 0, b => b.U32(theirs).U32(other.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await other.SyncAsync();

        foreach (uint victim in (uint[])[c.RootWindow, theirs])
        {
            XMessage error = await c.RequestAsync(major, 32, b => b.U32(victim));
            Assert.IsTrue(error.IsError);
            Assert.AreEqual(129, error.Bytes[1], "BadBarrier = first-error + 1");
            XMessage geometry = await c.RequestAsync(14, 0, b => b.U32(victim));   // GetGeometry:窗口还在
            Assert.IsTrue(geometry.IsReply);
        }

        uint barrier = c.NewId();
        XMessage slanted = await c.RequestAsync(major, 31, b => b.U32(barrier).U32(c.RootWindow).I16(0).I16(0).I16(10).I16(10)
            .U32(0).U16(0).U16(0));
        Assert.IsTrue(slanted.IsError, "不与坐标轴平行 → BadValue");
        Assert.AreEqual(2, slanted.Bytes[1]);

        await c.SendAsync(major, 31, b => b.U32(barrier).U32(c.RootWindow).I16(0).I16(0).I16(0).I16(100).U32(0).U16(0).U16(0));
        await c.SendAsync(major, 32, b => b.U32(barrier));
        XMessage twice = await c.RequestAsync(major, 32, b => b.U32(barrier));
        Assert.AreEqual(129, twice.Bytes[1], "删过一次就不在了");
    }

    [TestMethod]
    public async Task 棋盘格位图建区域回BadAlloc()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        uint bitmap = c.NewId(), gc = c.NewId();
        await c.SendAsync(53, 1, b => b.U32(bitmap).U32(c.RootWindow).U16(256).U16(256));   // CreatePixmap 深度 1
        await c.SendAsync(55, 0, b => b.U32(gc).U32(bitmap).U32(0));
        // 256×256 的棋盘格:每行 128 段,一共 32768 段。ZPixmap 深度 1,每行 32 字节,低位在前。
        byte[] image = new byte[32 * 256];
        for (int y = 0; y < 256; y++)
        {
            Array.Fill(image, y % 2 == 0 ? (byte)0x55 : (byte)0xAA, y * 32, 32);
        }
        await c.SendAsync(72, 2, b => b.U32(bitmap).U32(gc).U16(256).U16(256).I16(0).I16(0).U8(0).U8(1).U16(0).Bytes(image));

        uint region = c.NewId();
        XMessage error = await c.RequestAsync(major, 6, b => b.U32(region).U32(bitmap));   // CreateRegionFromBitmap
        Assert.IsTrue(error.IsError);
        Assert.AreEqual(11, error.Detail, "BadAlloc");
    }

    [TestMethod]
    public async Task SetCursorName把名字建成原子_受与InternAtom同一套上限()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await XFixesMajorAsync(c);
        await c.RequestAsync(major, 0, b => b.U32(5).U32(0));
        uint cursor = c.NewId();
        uint pixmap = c.NewId();
        await c.SendAsync(53, 1, b => b.U32(pixmap).U32(c.RootWindow).U16(1).U16(1));
        await c.SendAsync(93, 0, b => b.U32(cursor).U32(pixmap).U32(0).U16(0).U16(0).U16(0).U16(0xFFFF).U16(0xFFFF).U16(0xFFFF).U16(0).U16(0));

        // 名字建成原子:GetCursorName 回的原子就是 InternAtom 查得到的那个。
        byte[] text = Encoding.Latin1.GetBytes("text");
        await c.SendAsync(major, 23, b => b.U32(cursor).U16((ushort)text.Length).U16(0).Bytes(text).Pad());
        XMessage named = await c.RequestAsync(major, 24, b => b.U32(cursor));
        XMessage interned = await c.RequestAsync(16, 1, b => b.U16((ushort)text.Length).U16(0).Bytes(text).Pad());   // only-if-exists
        Assert.AreNotEqual(0u, named.U32(8));
        Assert.AreEqual(interned.U32(8), named.U32(8));

        // 每次一个 6 万多字节的新名字:原子名合计 16 MB 的上限对这条路同样有效(原先 GetCursorName 不设限地建原子)。
        await c.SendManyAsync(Enumerable.Range(0, 300).Select<int, (byte, byte, Action<XTestClient.Body>?)>(i =>
        {
            byte[] name = new byte[65000];
            BitConverter.TryWriteBytes(name, i);
            name[4] = (byte)'x';
            return (major, 23, b => b.U32(cursor).U16((ushort)name.Length).U16(0).Bytes(name).Pad());
        }));
        await c.SyncAsync();
        XMessage error = await c.NextAsync(m => m.IsError, timeoutMs: 1000);
        Assert.AreEqual(11, error.Detail, "BadAlloc");
    }
}
