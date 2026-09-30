using System.Diagnostics;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>连接层的健壮性:主动断开真的断开、连接建立有时限、超大请求不会拖垮服务端。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RobustnessTests
{
    [TestMethod]
    public async Task 连接建立报文迟迟不发完_到了时限就断开()
    {
        await using X11Server server = new() { SetupTimeout = TimeSpan.FromMilliseconds(300) };
        (Stream serverSide, Stream clientSide) = DuplexPair.Create();
        Task serving = server.ServeAsync(serverSide, isLocal: true);
        await clientSide.WriteAsync(new byte[] { (byte)'l', 0, 11, 0 });   // 12 字节的头只发了 4 字节,然后不动了
        await clientSide.FlushAsync();
        // 这条连接的服务任务结束(TCP / Unix 套接字随之由接受循环关掉);没有时限的话它一直挂着。
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        // 按时发完的照常连上。
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        Assert.IsTrue((await c.RequestAsync(43, 0)).IsReply, "GetInputFocus");
    }

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
        // 从外往里逐个映射、再从里往外逐个取消映射:每次只该重画那个窗口(或父窗口)的子树。原先从顶层走起,
        // 每个窗口的可见区域都要沿祖先算一遍 —— 映射一个 d 层深的窗口是 O(d²),整条链 O(n³),256 层要好几秒。
        var elapsed = Stopwatch.StartNew();
        await c.SendManyAsync(chain.Select<uint, (byte, byte, Action<XTestClient.Body>?)>(id => (8, 0, b => b.U32(id))));   // MapWindow
        await c.SyncAsync();
        await c.SendManyAsync(Enumerable.Reverse(chain).Select<uint, (byte, byte, Action<XTestClient.Body>?)>(id => (10, 0, b => b.U32(id))));   // UnmapWindow
        await c.SendManyAsync(chain.Select<uint, (byte, byte, Action<XTestClient.Body>?)>(id => (8, 0, b => b.U32(id))));
        await c.SyncAsync();
        Assert.IsLessThan(3000, elapsed.ElapsedMilliseconds, $"整条链映射、取消映射、再映射用了 {elapsed.ElapsedMilliseconds} ms");

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

    [TestMethod]
    public async Task 客户端连满之后新连接收到建立失败_走了一个就能再连_资源ID顶上三位恒为0()
    {
        await using X11Server server = new();
        List<XTestClient> clients = [];
        try
        {
            for (int i = 0; i < X11Server.MaxClients; i++)
            {
                XTestClient c = await XTestClient.ConnectAsync(server);
                Assert.AreEqual(1, c.SetupReply[0], $"第 {i + 1} 个连接应当成功");
                Assert.AreEqual(0u, c.ResourceBase & 0xE0000000, "资源 ID 的顶上三位恒为 0(协议第 8 节)");
                clients.Add(c);
            }

            await using (XTestClient refused = await XTestClient.ConnectAsync(server))
            {
                Assert.AreEqual(0, refused.SetupReply[0], "编号用完:连接建立失败,而不是卡住");
            }

            await clients[0].DisposeAsync();
            await clients[0].ServerTask.WaitAsync(TimeSpan.FromSeconds(3));
            clients.RemoveAt(0);
            XTestClient again = await XTestClient.ConnectAsync(server);
            clients.Add(again);
            Assert.AreEqual(1, again.SetupReply[0], "走了一个就空出一个编号");
        }
        finally
        {
            foreach (XTestClient c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task 协议错误日志在放掉像素锁之后才交给宿主_刷屏时每秒只记五十条()
    {
        using RecordingHost host = new();
        XTopLevelWindow? handle = null;
        int logged = 0, loggedUnderLock = 0;
        System.Collections.Concurrent.ConcurrentQueue<string> lines = new();
        await using X11Server server = new(new X11ServerOptions
        {
            Log = line =>
            {
                lines.Enqueue(line);
                if (!line.Contains("BadWindow", StringComparison.Ordinal) || handle is not { } window)
                {
                    return;
                }
                Interlocked.Increment(ref logged);
                // 别的线程去读像素:日志要是在持锁时调的,这里就拿不到锁(宿主的 UI 线程就是这么被拖住的)。
                // 抓到一次就够了,之后不再试(否则每条都要等满 1 秒)。
                if (Volatile.Read(ref loggedUnderLock) == 0 && !Task.Run(() => window.CopyPixels(new uint[16 * 16])).Wait(TimeSpan.FromSeconds(1)))
                {
                    Interlocked.Increment(ref loggedUnderLock);
                }
            },
        }, host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint top = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(top).U32(c.RootWindow).I16(0).I16(0).U16(16).U16(16).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        handle = host.Mapped[top];

        // 500 条 MapWindow(不存在的窗口):每条一个 BadWindow。
        await c.SendManyAsync(Enumerable.Range(0, 500).Select<int, (byte, byte, Action<XTestClient.Body>?)>(_ => (8, 0, b => b.U32(0x7FFFFF))));
        await c.SyncAsync();
        Assert.AreEqual(0, loggedUnderLock, "没有一条日志是持着像素锁交出去的");
        Assert.IsLessThanOrEqualTo(100, logged, $"刷屏的错误每秒最多记 50 条,实际 {logged}");

        await Task.Delay(1100);
        await c.RequestAsync(8, 0, b => b.U32(0x7FFFFF));
        Assert.IsTrue(lines.Any(l => l.Contains("more log lines were not written", StringComparison.Ordinal)), "补一行没记的有几条");
    }

    [TestMethod]
    public async Task 像素图超过像素上限回BadAlloc_顶层缓冲只保留上限之内的一块()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);

        // 32767² 的像素图:一块就是 4 GB。
        XMessage pixmap = await c.RequestAsync(53, 24, b => b.U32(c.NewId()).U32(c.RootWindow).U16(32767).U16(32767));
        Assert.IsTrue(pixmap.IsError);
        Assert.AreEqual(BadAlloc, pixmap.Detail);

        // 32767² 的顶层窗口照样建、照样映射(协议允许),缓冲只保留上限之内的一块。
        uint top = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(top).U32(c.RootWindow).I16(0).I16(0).U16(32767).U16(32767).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(top));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(top));
        (int w, int h) = (0, 0);
        Assert.IsTrue(host.Mapped[top].ReadPixels((_, width, height) => (w, h) = (width, height)));
        Assert.AreEqual(32767, w);
        Assert.IsLessThanOrEqualTo(VelaShell.XServer.Drawing.PixelBuffer.MaxPixels, (long)w * h, $"缓冲 {w}×{h}");

        // 把一个小顶层配置成 32767²:同样削到上限之内。
        uint small = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(small).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));
        await c.SendAsync(8, 0, b => b.U32(small));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(small));
        await c.SendAsync(12, 0, b => b.U32(small).U16(0x0C).U16(0).U32(32767).U32(32767));   // ConfigureWindow 宽、高
        await c.SyncAsync();
        Assert.IsTrue(host.Mapped[small].ReadPixels((_, width, height) => (w, h) = (width, height)));
        Assert.IsLessThanOrEqualTo(VelaShell.XServer.Drawing.PixelBuffer.MaxPixels, (long)w * h, $"缓冲 {w}×{h}");
    }

    [TestMethod]
    public async Task InternAtom的名字合计超上限回BadAlloc_已有的原子照常取得到()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        int max = (int)(X11Server.MaxAtomNameBytes / 65535) + 2;
        XMessage? refused = null;
        for (int i = 0; i < max && refused is null; i++)
        {
            byte[] name = new byte[65535];
            Array.Fill(name, (byte)'a');
            BitConverter.GetBytes(i).CopyTo(name, 0);
            XMessage reply = await c.RequestAsync(16, 0, b => b.U16(65535).U16(0).Bytes(name));
            refused = reply.IsError ? reply : null;
        }
        Assert.IsNotNull(refused, "原子名合计超过上限之后应当拒绝");
        Assert.AreEqual(BadAlloc, refused.Detail);

        XMessage wmName = await c.RequestAsync(16, 0, b => b.U16(7).U16(0).Bytes("WM_NAME"u8.ToArray()));
        Assert.AreEqual(39u, wmName.U32(8), "已经有的原子照常返回(预定义的 WM_NAME)");
    }

    [TestMethod]
    public async Task ChangeProperty追加到超过属性上限回BadAlloc_原来的值不变()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte bigRequests = (await c.RequestAsync(98, 0, b => b.U16(12).U16(0).Bytes("BIG-REQUESTS"u8.ToArray()))).Bytes[9];
        await c.RequestAsync(bigRequests, 0);
        uint window = c.NewId();
        await c.SendAsync(1, 0, b => b.U32(window).U32(c.RootWindow).I16(0).I16(0).U16(10).U16(10).U16(0).U16(1).U32(0).U32(0));

        byte[] chunk = new byte[15 * 1024 * 1024];
        await c.SendAsync(18, 0, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)chunk.Length).Bytes(chunk), bigRequest: true);
        await c.SendAsync(18, 2, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)chunk.Length).Bytes(chunk), bigRequest: true);
        ushort third = await c.SendAsync(18, 2, b => b.U32(window).U32(39).U32(31).U8(8).U8(0).U8(0).U8(0).U32((uint)chunk.Length).Bytes(chunk), bigRequest: true);
        XMessage error = await c.NextAsync(m => m.IsError && m.Sequence == third);
        Assert.AreEqual(BadAlloc, error.Detail, "45 MB 超过了 32 MB 的上限");

        XMessage property = await c.RequestAsync(20, 0, b => b.U32(window).U32(39).U32(0).U32(0).U32(0));   // GetProperty,长度 0:只看 bytes-after
        Assert.AreEqual((uint)(2 * chunk.Length), property.U32(12), "前两次追加照常生效");
    }

    [TestMethod]
    public async Task CloseDownMode为Retain时断开后资源留着_KillClient与AllTemporary销毁它们()
    {
        await using X11Server server = new();
        await using XTestClient observer = await XTestClient.ConnectAsync(server);

        async Task<uint> LeaveBehindAsync(byte mode)
        {
            XTestClient c = await XTestClient.ConnectAsync(server);
            uint pixmap = c.NewId();
            await c.SendAsync(53, 24, b => b.U32(pixmap).U32(c.RootWindow).U16(8).U16(8));
            await c.SendAsync(112, mode, _ => { });   // SetCloseDownMode
            await c.SyncAsync();
            Task serving = c.ServerTask;
            await c.DisposeAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(3));
            return pixmap;
        }
        async Task<bool> ExistsAsync(uint id) => (await observer.RequestAsync(14, 0, b => b.U32(id))).IsReply;   // GetGeometry

        uint permanent = await LeaveBehindAsync(1), temporary = await LeaveBehindAsync(2), destroyed = await LeaveBehindAsync(0);
        Assert.IsTrue(await ExistsAsync(permanent), "RetainPermanent:断开后资源还在");
        Assert.IsTrue(await ExistsAsync(temporary), "RetainTemporary:断开后资源还在");
        Assert.IsFalse(await ExistsAsync(destroyed), "Destroy:照常销毁");

        await observer.SendAsync(113, 0, b => b.U32(0));   // KillClient(AllTemporary)
        Assert.IsFalse(await ExistsAsync(temporary), "AllTemporary 销毁 RetainTemporary 留下的");
        Assert.IsTrue(await ExistsAsync(permanent), "RetainPermanent 的不受影响");

        await observer.SendAsync(113, 0, b => b.U32(permanent));   // KillClient(那个资源):销毁它的客户端留下的全部资源
        Assert.IsFalse(await ExistsAsync(permanent));
    }
}
