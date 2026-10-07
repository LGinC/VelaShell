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

    private const uint GcFunction = 0x1, GcForeground = 0x4, GcBackground = 0x8, GcLineWidth = 0x10;

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

    private static async Task<byte> ErrorOfAsync(XTestClient c, ushort sequence) =>
        (await c.NextAsync(m => m.IsError && m.Sequence == sequence)).Detail;

    [TestMethod]
    public async Task GC的枚举值越界回BadValue_出错的ChangeGC与SetDashes不改任何值()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);
        uint gc = await CreateGcAsync(c, window, (GcForeground, 0xFF0000));

        // function 16 原先被 & 0xF 截成 GXclear;line-style / cap / join / fill-style / fill-rule / subwindow-mode / arc-mode 越界原样收下。
        foreach ((uint bit, uint value) in new (uint, uint)[] { (0x1, 16), (0x20, 3), (0x40, 4), (0x80, 3), (0x100, 4), (0x200, 2), (0x8000, 2), (0x10000, 2), (0x400000, 2) })
        {
            ushort seq = await c.SendAsync(56, 0, b => b.U32(gc).U32(bit).U32(value));   // ChangeGC
            Assert.AreEqual(2, await ErrorOfAsync(c, seq), $"掩码 0x{bit:x} = {value}:BadValue");
        }

        // 前景改成绿、同时给一个不存在的平铺像素图:BadPixmap,前景也不该变。
        ushort bad = await c.SendAsync(56, 0, b => b.U32(gc).U32(GcForeground | 0x400).U32(0x00FF00).U32(0x0BADBAD));
        Assert.AreEqual(4, await ErrorOfAsync(c, bad), "BadPixmap");
        await FillRectAsync(c, window, gc, 0, 0, 2, 2);
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, Pixel(handle, 0, 0), "出错的 ChangeGC 不产生效果");

        // OnOffDash、默认的 [4, 4]:SetDashes(offset 2, [0]) 出错 —— dash-offset 也不能改(改了的话第 2 个像素就落在空白里)。
        await c.SendAsync(56, 0, b => b.U32(gc).U32(0x20).U32(1));
        bad = await c.SendAsync(58, 0, b => b.U32(gc).U16(2).U16(1).U8(0).U8(0).U8(0).U8(0));
        Assert.AreEqual(2, await ErrorOfAsync(c, bad), "dash 为 0:BadValue");
        await PolyLineAsync(c, window, gc, (0, 10), (7, 10));
        await c.SyncAsync();
        Assert.AreEqual(0xFF0000u, Pixel(handle, 2, 10), "dash-offset 仍是 0");
        Assert.AreEqual(0x000000u, Pixel(handle, 4, 10));
    }

    [TestMethod]
    public async Task PutImage位图格式的left_pad不小于32回BadMatch()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, _) = await MapWindowAsync(c, host);
        uint gc = await CreateGcAsync(c, window, (GcForeground, 0xFF0000));
        // 1×1 的 XYBitmap、left-pad 32:每行 33 位 → 补齐到 8 字节。
        ushort seq = await c.SendAsync(72, 0, b => b.U32(window).U32(gc).U16(1).U16(1).I16(0).I16(0).U8(32).U8(1).U16(0).U32(0).U32(1));
        Assert.AreEqual(8, await ErrorOfAsync(c, seq), "BadMatch");
    }

    private static async Task<uint> PixmapAsync(XTestClient c, uint drawable, byte depth, ushort width, ushort height)
    {
        uint pixmap = c.NewId();
        await c.SendAsync(53, depth, b => b.U32(pixmap).U32(drawable).U16(width).U16(height));
        return pixmap;
    }

    private static Task<ushort> FillRectAsync(XTestClient c, uint drawable, uint gc, short x, short y, ushort width, ushort height) =>
        c.SendAsync(70, 0, b => b.U32(drawable).U32(gc).I16(x).I16(y).U16(width).U16(height));

    private static Task<ushort> CopyPlaneAsync(XTestClient c, uint src, uint dst, uint gc, short sx, short sy, short dx, short dy,
        ushort width, ushort height, uint plane) =>
        c.SendAsync(63, 0, b => b.U32(src).U32(dst).U32(gc).I16(sx).I16(sy).I16(dx).I16(dy).U16(width).U16(height).U32(plane));

    [TestMethod]
    public async Task CopyPlane按位平面贴前景背景_源拿不到的部分发GraphicsExposure_位平面超出源深度回BadValue()
    {
        using RecordingHost host = new();
        await using X11Server server = new(host: host);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        (uint window, XTopLevelWindow handle) = await MapWindowAsync(c, host);

        // 20×10 的 24 位像素图:左半 0x000100(第 8 位)、右半 0。
        uint source = await PixmapAsync(c, window, 24, 20, 10);
        await FillRectAsync(c, source, await CreateGcAsync(c, source, (GcForeground, 0x000100)), 0, 0, 10, 10);
        uint gc = await CreateGcAsync(c, window, (GcForeground, 0xFF0000), (GcBackground, 0x0000FF));

        // 从 (5, 0) 起拷 20×10:源只有 15 列拿得到,目标 (15..19, 0..9) 那一块要客户端补画。
        await CopyPlaneAsync(c, source, window, gc, 5, 0, 0, 20, 20, 10, 0x100);
        XMessage exposure = await c.NextEventAsync(13);   // GraphicsExposure
        Assert.AreEqual(window, exposure.U32(4));
        Assert.AreEqual("15,20 5×10", $"{exposure.U16(8)},{exposure.U16(10)} {exposure.U16(12)}×{exposure.U16(14)}");
        Assert.AreEqual(63, exposure.Bytes[20], "major-opcode = CopyPlane");
        Assert.AreEqual(0xFF0000u, Pixel(handle, 4, 25), "位为 1:前景");
        Assert.AreEqual(0x0000FFu, Pixel(handle, 5, 25), "位为 0:背景");
        Assert.AreEqual(0x000000u, Pixel(handle, 15, 25), "源之外不画");

        // 8 位的源没有第 8 位平面。
        uint shallow = await PixmapAsync(c, window, 8, 4, 4);
        ushort bad = await CopyPlaneAsync(c, shallow, window, gc, 0, 0, 0, 0, 4, 4, 0x100);
        XMessage error = await c.NextAsync(m => m.IsError && m.Sequence == bad);
        Assert.AreEqual(2, error.Detail, "BadValue");
    }
}
