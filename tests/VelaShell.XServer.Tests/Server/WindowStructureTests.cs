using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>结构请求:DestroySubwindows、CirculateWindow、ReparentWindow、ConfigureWindow 的事件、次序与改道。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class WindowStructureTests
{
    private const uint SubstructureNotifyMask = 0x80000, SubstructureRedirectMask = 0x100000;
    private const byte DestroyNotify = 17, CirculateNotify = 26, CirculateRequest = 27;

    /// <summary>在 <paramref name="parent" /> 下建一个子窗口(InputOutput,背景色 <paramref name="background" />)。</summary>
    private static async Task<uint> CreateChildAsync(XTestClient c, uint parent, short x, short y, ushort width, ushort height,
        uint background = 0xFFFFFF, uint eventMask = 0)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(parent).I16(x).I16(y).U16(width).U16(height).U16(0).U16(1).U32(0)
            .U32(0x802).U32(background).U32(eventMask));
        return id;
    }

    private static Task<ushort> SelectInputAsync(XTestClient c, uint window, uint mask) =>
        c.SendAsync(2, 0, b => b.U32(window).U32(0x800).U32(mask));

    private static async Task<uint[]> QueryChildrenAsync(XTestClient c, uint window)
    {
        XMessage tree = await c.RequestAsync(15, 0, b => b.U32(window));
        return [.. Enumerable.Range(0, tree.U16(16)).Select(i => tree.U32(32 + (i * 4)))];
    }

    [TestMethod]
    public async Task CirculateWindow按遮挡挑窗口_有改道者时发CirculateRequest_压下去让出来的兄弟重画()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        await using XTestClient wm = await XTestClient.ConnectAsync(server);
        uint frame = await CreateChildAsync(c, c.RootWindow, 0, 0, 100, 100, background: 0xFFFFFF);
        await c.SendAsync(8, 0, b => b.U32(frame));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(frame));
        // 从下到上:alone(谁也不挡)、red、blue(挡住 red 的右半边)。
        uint alone = await CreateChildAsync(c, frame, 0, 70, 20, 20, background: 0x00FF00);
        uint red = await CreateChildAsync(c, frame, 0, 0, 40, 40, background: 0xFF0000);
        uint blue = await CreateChildAsync(c, frame, 20, 0, 40, 40, background: 0x0000FF);
        await c.SendAsync(9, 0, b => b.U32(frame));   // MapSubwindows
        await c.SyncAsync();
        XTopLevelWindow handle = host.Mapped[frame];
        uint Pixel(int x, int y)
        {
            (uint[] pixels, int width, _) = RecordingHost.Snapshot(handle);
            return pixels[(y * width) + x] & 0xFFFFFF;
        }
        Assert.AreEqual(0x0000FFu, Pixel(30, 10), "重叠处是上面的 blue");

        // 别的客户端在 frame 上选了 SubstructureRedirect:只发 CirculateRequest,次序不动。挑的是被挡着的最低的 red,不是最低的 alone。
        await SelectInputAsync(wm, frame, SubstructureRedirectMask);
        await wm.SyncAsync();
        await c.SendAsync(13, 0, b => b.U32(frame));   // CirculateWindow RaiseLowest
        XMessage request = await wm.NextEventAsync(CirculateRequest);
        Assert.AreEqual(frame, request.U32(4), "parent");
        Assert.AreEqual(red, request.U32(8), "被挡着的最低的那个(原先总挪最低的 alone)");
        Assert.AreEqual(0, request.Bytes[16], "place = Top");
        CollectionAssert.AreEqual(new[] { alone, red, blue }, await QueryChildrenAsync(c, frame), "改道:不再处理");
        await SelectInputAsync(wm, frame, 0);
        await wm.SyncAsync();

        // 没有改道:LowerHighest 把挡着别人的最高的 blue 压到最下,red 原先被挡的部分重画。
        await SelectInputAsync(c, frame, SubstructureNotifyMask);
        await c.SendAsync(13, 1, b => b.U32(frame));   // CirculateWindow LowerHighest
        XMessage notify = await c.NextEventAsync(CirculateNotify);
        Assert.AreEqual(blue, notify.U32(8));
        Assert.AreEqual(1, notify.Bytes[16], "place = Bottom");
        CollectionAssert.AreEqual(new[] { blue, alone, red }, await QueryChildrenAsync(c, frame));
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, Pixel(30, 10), "原先 red 露出来的部分还留着 blue 的旧像素");
        Assert.AreEqual(0x0000FFu, Pixel(50, 10), "blue 没被挡的部分照旧");

        // 谁也不挡谁:什么也不做,不发 CirculateNotify。
        await c.SendAsync(13, 0, b => b.U32(alone));
        await c.SyncAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => c.NextEventAsync(CirculateNotify, timeoutMs: 100));
    }

    [TestMethod]
    public async Task DestroySubwindows按堆叠次序从下到上销毁()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint parent = await CreateChildAsync(c, c.RootWindow, 0, 0, 100, 100);
        uint bottom = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        uint middle = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        uint top = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        await SelectInputAsync(c, parent, SubstructureNotifyMask);
        await c.SendAsync(5, 0, b => b.U32(parent));   // DestroySubwindows
        uint[] order = [(await c.NextEventAsync(DestroyNotify)).U32(8), (await c.NextEventAsync(DestroyNotify)).U32(8),
            (await c.NextEventAsync(DestroyNotify)).U32(8)];
        CollectionAssert.AreEqual(new[] { bottom, middle, top }, order, "协议「DestroySubwindows」:从下到上(原先从上到下)");
    }
}
