using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>核心绘图请求经协议走一遍:PolyLine 的闭合、CopyPlane、GC 的参数校验、CopyArea 的 GraphicsExposure。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class CoreDrawingTests
{
    private static async Task<(uint Window, XTopLevelWindow Handle)> MapWindowAsync(XTestClient c, RecordingHost host, int width = 64, int height = 48)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(c.RootWindow).I16(0).I16(0).U16((ushort)width).U16((ushort)height)
            .U16(0).U16(1).U32(0).U32(0x2).U32(0x000000));
        await c.SendAsync(8, 0, b => b.U32(id));
        await host.WaitForAsync(() => host.Mapped.ContainsKey(id));
        return (id, host.Mapped[id]);
    }

    /// <summary>CreateGC:按掩码位从低到高给值。</summary>
    private static async Task<uint> CreateGcAsync(XTestClient c, uint drawable, params (uint Bit, uint Value)[] values)
    {
        uint gc = c.NewId();
        uint mask = 0;
        foreach ((uint bit, _) in values)
        {
            mask |= bit;
        }
        await c.SendAsync(55, 0, b =>
        {
            b.U32(gc).U32(drawable).U32(mask);
            foreach ((_, uint value) in values.OrderBy(v => v.Bit))
            {
                b.U32(value);
            }
        });
        return gc;
    }

    private const uint GcFunction = 0x1, GcForeground = 0x4, GcLineWidth = 0x10;

    private static Task<ushort> PolyLineAsync(XTestClient c, uint drawable, uint gc, params (short X, short Y)[] points) =>
        c.SendAsync(65, 0, b =>
        {
            b.U32(drawable).U32(gc);
            foreach ((short x, short y) in points)
            {
                b.I16(x).I16(y);
            }
        });

    private static uint Pixel(XTopLevelWindow handle, int x, int y)
    {
        (uint[] px, int w, _) = RecordingHost.Snapshot(handle);
        return px[(y * w) + x] & 0xFFFFFF;
    }

    [TestMethod]
    public async Task PolyLine首尾重合时当闭合路径_细线起点只画一次_宽线首尾按接头连起来()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);

        // GXxor 的细线三角形:起点原先被第一段画一次、最后一段的终点又画一次,两次抵消。
        uint xor = await CreateGcAsync(c, window, (GcFunction, 6), (GcForeground, 0xFFFFFF));
        await PolyLineAsync(c, window, xor, (5, 5), (20, 5), (20, 20), (5, 5));
        await c.SyncAsync();
        Assert.AreEqual(0xFFFFFFu, Pixel(handle, 5, 5), "起点只画一次");
        Assert.AreEqual(0xFFFFFFu, Pixel(handle, 20, 5));

        // lw = 6 的方框(五个点,首尾重合):首尾两段在 (30,10) 按 Miter 连起来,角是方的;原先是两个 Butt 端帽,角上缺一块。
        uint wide = await CreateGcAsync(c, window, (GcForeground, 0x00FF00), (GcLineWidth, 6));
        await PolyLineAsync(c, window, wide, (30, 10), (50, 10), (50, 30), (30, 30), (30, 10));
        await c.SyncAsync();
        Assert.AreEqual(0x00FF00u, Pixel(handle, 27, 7), "起点 / 终点处的角");
        Assert.AreEqual(0x00FF00u, Pixel(handle, 52, 7), "中间的角");
    }
}
