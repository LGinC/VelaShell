using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>连接层的健壮性:主动断开真的断开、超大请求不会拖垮服务端。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RobustnessTests
{
    [TestMethod]
    public async Task KillClient之后被杀的客户端连接立即结束()
    {
        await using X11Server server = new();
        await using XTestClient killer = await XTestClient.ConnectAsync(server);
        await using XTestClient victim = await XTestClient.ConnectAsync(server);
        uint window = victim.NewId();
        await victim.SendAsync(1, 0, b => b.U32(window).U32(victim.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await victim.SyncAsync();

        await killer.SendAsync(113, 0, b => b.U32(window));   // KillClient
        await killer.SyncAsync();

        // 被杀的一方什么也不发 —— 以前服务端的读端会一直挂在它的连接上;现在 ServeAsync 立即结束(TCP / Unix 套接字随之关闭)。
        await victim.ServerTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task 超大尺寸的PutImage与CopyArea回错误而不是耗尽内存()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint pixmap = c.NewId();
        await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(16).U16(16));
        uint gc = c.NewId();
        await c.SendAsync(55, 0, b => b.U32(gc).U32(pixmap).U32(0));

        // PutImage 声称 65535×65535,只带 4 字节数据。
        XMessage put = await c.RequestAsync(72, 2, b => b.U32(pixmap).U32(gc).U16(65535).U16(65535).I16(0).I16(0).U8(0).U8(24).U16(0).U32(0));
        Assert.IsTrue(put.IsError, "数据不够:BadLength");

        // CopyArea 65535×65535:只拷与源、目标相交的部分,不分配请求尺寸的缓冲。
        await c.SendAsync(62, 0, b => b.U32(pixmap).U32(pixmap).U32(gc).I16(0).I16(0).I16(1).I16(1).U16(65535).U16(65535));
        await c.SyncAsync();   // 服务端还活着、还在回应
    }

    private const byte BadAlloc = 11;

    private static (byte, byte, Action<XTestClient.Body>?) CreateWindow(uint id, uint parent) =>
        (1, 0, b => b.U32(id).U32(parent).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));

    [TestMethod]
    public async Task 窗口嵌套到上限之后再建或挪进去回BadAlloc_整条链照常映射与销毁()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        List<uint> chain = [];
        for (int level = 1; level <= X11Server.MaxWindowDepth; level++)
        {
            uint id = c.NewId();
            chain.Add(id);
        }
        await c.SendManyAsync(chain.Select((id, i) => CreateWindow(id, i == 0 ? c.RootWindow : chain[i - 1])));
        await c.SendManyAsync(chain.Select(id => ((byte)8, (byte)0, (Action<XTestClient.Body>?)(b => b.U32(id)))));   // MapWindow
        await c.SyncAsync();

        (byte op, byte data, Action<XTestClient.Body>? body) = CreateWindow(c.NewId(), chain[^1]);
        XMessage tooDeep = await c.RequestAsync(op, data, body);
        Assert.IsTrue(tooDeep.IsError);
        Assert.AreEqual(BadAlloc, tooDeep.Detail);

        // 一棵两层的小树挪到倒数第二层下面:整棵落下去就是第 257 层。
        uint small = c.NewId(), leaf = c.NewId();
        await c.SendManyAsync([CreateWindow(small, c.RootWindow), CreateWindow(leaf, small)]);
        XMessage reparent = await c.RequestAsync(7, 0, b => b.U32(small).U32(chain[^2]).I16(0).I16(0));
        Assert.IsTrue(reparent.IsError);
        Assert.AreEqual(BadAlloc, reparent.Detail);

        await c.SendAsync(4, 0, b => b.U32(chain[0]));   // DestroyWindow:整条链从最深处开始销毁
        XMessage gone = await c.RequestAsync(14, 0, b => b.U32(chain[^1]));   // GetGeometry
        Assert.IsTrue(gone.IsError, "最深的那个窗口随链首一起销毁了");
    }

    [TestMethod]
    public async Task 一个客户端的窗口数到上限之后回BadAlloc_销毁一个就能再建()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = c.NewId();
        List<uint> children = [.. Enumerable.Range(0, X11Server.MaxWindowsPerClient - 1).Select(_ => c.NewId())];
        await c.SendManyAsync([CreateWindow(top, c.RootWindow), .. children.Select(id => CreateWindow(id, top))]);
        await c.SyncAsync();

        (byte op, byte data, Action<XTestClient.Body>? body) = CreateWindow(c.NewId(), top);
        XMessage full = await c.RequestAsync(op, data, body);
        Assert.IsTrue(full.IsError);
        Assert.AreEqual(BadAlloc, full.Detail);

        await c.SendAsync(4, 0, b => b.U32(children[0]));
        uint again = c.NewId();
        (op, data, body) = CreateWindow(again, top);
        await c.SendAsync(op, data, body);
        XMessage geometry = await c.RequestAsync(14, 0, b => b.U32(again));
        Assert.IsTrue(geometry.IsReply, "腾出一个名额之后照常建窗口");
    }
}
