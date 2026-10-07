using VelaShell.XServer.Drawing;
using VelaShell.XServer.Resources;

namespace VelaShell.XServer.Tests.Drawing;

/// <summary>
/// 光栅化先与裁剪求交:画出来的像素必须与「从头走到尾、逐像素判裁剪」完全一样 —— 用随机的线与裁剪区域对拍一个照搬原算法的参照实现。
/// </summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RasterizerTests
{
    private const int Size = 64;

    /// <summary>参照:原先的写法 —— 从起点逐步走到终点,每一步都问一次裁剪(虚线的走位逐像素推进)。</summary>
    private static void ReferenceLine(PixelBuffer buffer, Region clip, int x1, int y1, int x2, int y2, bool drawLast,
        byte[]? dashes, ref int dashIndex, ref int dashRemaining)
    {
        int dx = Math.Abs(x2 - x1), sx = x1 < x2 ? 1 : -1;
        int dy = -Math.Abs(y2 - y1), sy = y1 < y2 ? 1 : -1;
        int err = dx + dy;
        int x = x1, y = y1;
        while (true)
        {
            bool last = x == x2 && y == y2;
            if (!last || drawLast)
            {
                bool on = dashes is null || dashIndex % 2 == 0;
                if (on && clip.Contains(x, y))
                {
                    buffer.Pixels[(y * buffer.Width) + x] ^= 0xFFFFFF;   // GXxor:画两次会互相抵消,能看出重复画
                }
                if (dashes is not null)
                {
                    dashRemaining--;
                    if (dashRemaining <= 0)
                    {
                        dashIndex = (dashIndex + 1) % dashes.Length;
                        dashRemaining = dashes[dashIndex];
                    }
                }
            }
            if (last)
            {
                break;
            }
            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private static Region RandomClip(Random random)
    {
        Region clip = new();
        int rects = random.Next(1, 4);
        for (int i = 0; i < rects; i++)
        {
            int x = random.Next(0, Size), y = random.Next(0, Size);
            clip.Union(new XRect(x, y, random.Next(1, Size - x + 1), random.Next(1, Size - y + 1)));
        }
        return clip;
    }

    private static int RandomCoordinate(Random random) => random.Next(4) switch
    {
        0 => random.Next(-3000, 3000),        // 大多在窗口外
        1 => random.Next(-32768, 32768),     // 坐标的整个范围
        _ => random.Next(-20, Size + 20),     // 窗口附近
    };

    [TestMethod]
    public void 细线只走与裁剪相交的一段_像素与逐步走完全一致()
    {
        Random random = new(20261006);
        for (int round = 0; round < 3000; round++)
        {
            Region clip = RandomClip(random);
            int x1 = RandomCoordinate(random), y1 = RandomCoordinate(random), x2 = RandomCoordinate(random), y2 = RandomCoordinate(random);
            bool drawLast = random.Next(2) == 0;

            PixelBuffer actual = new(Size, Size, 24);
            XGc gc = new(1, null, 24) { Foreground = 0xFFFFFF, Function = 6 };
            new Rasterizer(actual, 0, 0, clip, gc).ThinLine(x1, y1, x2, y2, drawLast);

            PixelBuffer expected = new(Size, Size, 24);
            int index = 0, remaining = 0;
            ReferenceLine(expected, clip, x1, y1, x2, y2, drawLast, null, ref index, ref remaining);
            CollectionAssert.AreEqual(expected.Pixels, actual.Pixels, $"第 {round} 条:({x1},{y1})–({x2},{y2}) drawLast={drawLast}");
        }
    }

    [TestMethod]
    public void 虚线跨段接着走_跳过的部分也推进图案()
    {
        Random random = new(7);
        byte[] dashes = [3, 2, 5];
        for (int round = 0; round < 500; round++)
        {
            Region clip = RandomClip(random);
            XGc gc = new(1, null, 24) { Foreground = 0xFFFFFF, Function = 6, LineStyle = 1, Dashes = dashes, DashOffset = (ushort)random.Next(10) };
            PixelBuffer actual = new(Size, Size, 24);
            Rasterizer raster = new(actual, 0, 0, clip, gc);
            Rasterizer.DashState dash = raster.NewDashState();

            PixelBuffer expected = new(Size, Size, 24);
            Rasterizer.DashState reference = new Rasterizer(new PixelBuffer(1, 1, 24), 0, 0, new Region(), gc).NewDashState();
            int index = reference.Index, remaining = reference.Remaining;

            int x = RandomCoordinate(random), y = RandomCoordinate(random);
            for (int segment = 0; segment < 4; segment++)
            {
                int nx = RandomCoordinate(random), ny = RandomCoordinate(random);
                bool drawLast = segment == 3;
                raster.ThinLine(x, y, nx, ny, drawLast, dash);
                ReferenceLine(expected, clip, x, y, nx, ny, drawLast, dashes, ref index, ref remaining);
                (x, y) = (nx, ny);
            }
            CollectionAssert.AreEqual(expected.Pixels, actual.Pixels, $"第 {round} 组");
        }
    }

    [TestMethod]
    public void 填充矩形先与裁剪求交()
    {
        PixelBuffer buffer = new(10, 10, 24);
        XGc gc = new(1, null, 24) { Foreground = 5 };
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(3, -32768, 2, 65535);
        Assert.AreEqual(20, buffer.Pixels.Count(p => p == 5), "整列 2 × 10 个像素");
    }

    [TestMethod]
    public void GC裁剪区域按平移量缓存_换了裁剪矩形或原点就重建()
    {
        XGc gc = new(1, null, 24) { Foreground = 1, ClipRects = [new XRect(0, 0, 2, 2)] };
        PixelBuffer buffer = new(10, 10, 24);
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(0, 0, 10, 10);
        Assert.AreEqual(4, buffer.Pixels.Count(p => p == 1));

        gc.ClipXOrigin = 5;   // 原点变了:裁剪区域跟着挪
        buffer = new(10, 10, 24);
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(0, 0, 10, 10);
        Assert.AreEqual((1u, 0u), (buffer.Get(5, 0), buffer.Get(0, 0)));

        gc.ClipRects = [new XRect(0, 0, 3, 1)];   // 整份换掉:缓存作废
        buffer = new(10, 10, 24);
        new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc).FillRect(0, 0, 10, 10);
        Assert.AreEqual(3, buffer.Pixels.Count(p => p == 1));

        gc.ClipRects = null;   // 不裁剪:可见区域原样用
        buffer = new(10, 10, 24);
        new Rasterizer(buffer, 0, 0, new Region(new XRect(-5, -5, 30, 30)), gc).FillRect(0, 0, 10, 10);
        Assert.AreEqual(100, buffer.Pixels.Count(p => p == 1), "可见区域伸出缓冲时仍与缓冲求交");
    }

    // ------------------------------------------------------------------ 宽线与弧的 line-style / join-style / cap-style

    private static PixelBuffer Stroke(XGc gc, Action<Rasterizer> draw, int size = Size)
    {
        PixelBuffer buffer = new(size, size, 24);
        draw(new Rasterizer(buffer, 0, 0, new Region(buffer.Bounds), gc));
        return buffer;
    }

    [TestMethod]
    public void 宽线的OnOffDash只画偶数段_DoubleDash的奇数段用背景色()
    {
        // lw = 3 的水平线 y = 10:第 9..11 行;虚线 [4, 4] 从 x = 2 量起 —— 偶数段 [2, 6)、[10, 14),奇数段 [6, 10)。
        XGc onOff = new(1, null, 24) { Foreground = 1, LineWidth = 3, LineStyle = 1, Dashes = [4, 4] };
        PixelBuffer a = Stroke(onOff, r => r.PolyLine([(2, 10), (42, 10)]));
        Assert.AreEqual((1u, 0u, 1u), (a.Get(3, 10), a.Get(7, 10), a.Get(11, 9)), "偶数段画、奇数段不画");

        XGc doubleDash = new(1, null, 24) { Foreground = 1, Background = 2, LineWidth = 3, LineStyle = 2, Dashes = [4, 4] };
        PixelBuffer b = Stroke(doubleDash, r => r.PolyLine([(2, 10), (42, 10)]));
        Assert.AreEqual((1u, 2u, 1u), (b.Get(3, 10), b.Get(7, 11), b.Get(11, 10)), "奇数段按背景色画");
    }

    [TestMethod]
    public void 宽线DoubleDash的像素与Solid完全相同且每个只画一次()
    {
        // 协议:DoubleDash 两种段合起来的像素集合与 Solid 相同。GXxor、前景背景都是全 1:画两次的像素会被抵消。
        Random random = new(42);
        for (int round = 0; round < 200; round++)
        {
            List<(int X, int Y)> points = [.. Enumerable.Range(0, random.Next(2, 6)).Select(_ => (random.Next(-10, Size + 10), random.Next(-10, Size + 10)))];
            bool closed = random.Next(3) == 0;
            if (closed)
            {
                points.Add(points[0]);
            }
            XGc gc = new(1, null, 24)
            {
                Foreground = 0xFFFFFF,
                Background = 0xFFFFFF,
                Function = 6,
                LineWidth = (ushort)random.Next(1, 12),
                JoinStyle = (byte)random.Next(3),
                CapStyle = (byte)random.Next(4),
                Dashes = [(byte)random.Next(1, 9), (byte)random.Next(1, 9), (byte)random.Next(1, 9)],
                DashOffset = (ushort)random.Next(20),
            };
            PixelBuffer solid = Stroke(gc, r => r.PolyLine(points, closed));
            gc.LineStyle = 2;
            PixelBuffer dashed = Stroke(gc, r => r.PolyLine(points, closed));
            CollectionAssert.AreEqual(solid.Pixels, dashed.Pixels, $"第 {round} 条:{string.Join(" ", points)} closed={closed} lw={gc.LineWidth}");
        }
    }

    [TestMethod]
    public void 宽线的接头按join_style_Miter是尖角_Bevel切掉_Round是圆()
    {
        // lw = 6 的矩形边框 (10,10)–(30,30):外沿在 7。角上的像素 (7,7) 只有 Miter 盖得住;
        // Bevel 的斜边是 x + y = 17,(8,8) 在外、(9,9) 在内;Round 的圆心 (10,10)、半径 3,(8,8) 在内。
        (int X, int Y)[] rect = [(10, 10), (30, 10), (30, 30), (10, 30), (10, 10)];
        PixelBuffer miter = Stroke(new XGc(1, null, 24) { Foreground = 1, LineWidth = 6, JoinStyle = 0 }, r => r.PolyLine(rect, closed: true));
        PixelBuffer round = Stroke(new XGc(1, null, 24) { Foreground = 1, LineWidth = 6, JoinStyle = 1 }, r => r.PolyLine(rect, closed: true));
        PixelBuffer bevel = Stroke(new XGc(1, null, 24) { Foreground = 1, LineWidth = 6, JoinStyle = 2 }, r => r.PolyLine(rect, closed: true));
        Assert.AreEqual((1u, 1u, 1u, 1u), (miter.Get(7, 7), miter.Get(32, 7), miter.Get(7, 32), miter.Get(32, 32)), "四个角都是方的");
        Assert.AreEqual((0u, 1u), (round.Get(7, 7), round.Get(8, 8)));
        Assert.AreEqual((0u, 0u, 1u), (bevel.Get(7, 7), bevel.Get(8, 8), bevel.Get(9, 9)));
        Assert.AreEqual(26 * 26 - (14 * 14), miter.Pixels.Count(p => p == 1), "Miter 的边框正好是两个方块之差");
    }

    [TestMethod]
    public void 端点重合的宽线按端帽画_Projecting是方块_Round是圆_Butt什么都不画()
    {
        XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 4, CapStyle = 3 };
        PixelBuffer projecting = Stroke(gc, r => r.PolyLine([(20, 20), (20, 20)]));
        Assert.AreEqual(16, projecting.Pixels.Count(p => p == 1), "与坐标轴对齐、边长为线宽的方块");
        Assert.AreEqual((1u, 1u, 0u), (projecting.Get(18, 18), projecting.Get(21, 21), projecting.Get(22, 20)));
        gc.CapStyle = 2;
        Assert.IsGreaterThan(8, Stroke(gc, r => r.PolyLine([(20, 20), (20, 20)])).Pixels.Count(p => p == 1), "直径为线宽的圆");
        gc.CapStyle = 1;
        Assert.AreEqual(0, Stroke(gc, r => r.PolyLine([(20, 20), (20, 20)])).Pixels.Count(p => p == 1));
    }

    [TestMethod]
    public void 宽弧两端按cap_style加端帽()
    {
        // 圆心 (30,30)、半径 20 的弧从 0° 逆时针到 90°:起点 (50,30) 处沿弧往上走,端帽朝下伸。
        XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = 6, CapStyle = 1 };
        Assert.AreEqual(0u, Stroke(gc, r => r.Arc(10, 10, 40, 40, 0, 90 * 64)).Get(50, 32), "Butt:端面齐着起点");
        gc.CapStyle = 3;
        PixelBuffer projecting = Stroke(gc, r => r.Arc(10, 10, 40, 40, 0, 90 * 64));
        Assert.AreEqual((1u, 0u), (projecting.Get(50, 32), projecting.Get(50, 33)), "Projecting:往外伸半个线宽");
        Assert.AreEqual((1u, 0u), (projecting.Get(28, 10), projecting.Get(26, 10)), "终点 (30,10) 处朝左伸");
        gc.CapStyle = 2;
        Assert.AreEqual(1u, Stroke(gc, r => r.Arc(10, 10, 40, 40, 0, 90 * 64)).Get(50, 32), "Round:半圆");
    }

    [TestMethod]
    public void 弧也按line_style画虚线()
    {
        XGc gc = new(1, null, 24) { Foreground = 1 };
        int solidThin = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64)).Pixels.Count(p => p == 1);
        gc.LineStyle = 1;
        gc.Dashes = [3, 3];
        int dashedThin = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64)).Pixels.Count(p => p == 1);
        Assert.IsTrue(dashedThin > solidThin / 3 && dashedThin < solidThin * 2 / 3, $"细弧:实线 {solidThin}、虚线 {dashedThin}");

        gc.LineStyle = 0;
        gc.LineWidth = 4;
        int solidWide = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64)).Pixels.Count(p => p == 1);
        gc.LineStyle = 1;
        gc.Dashes = [6, 6];
        int dashedWide = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64)).Pixels.Count(p => p == 1);
        Assert.IsTrue(dashedWide > solidWide / 3 && dashedWide < solidWide * 2 / 3, $"宽弧:实线 {solidWide}、虚线 {dashedWide}");

        // DoubleDash 的宽弧:两种段合起来就是实线。
        gc.LineStyle = 2;
        gc.Background = 2;
        PixelBuffer doubleDash = Stroke(gc, r => r.Arc(5, 5, 50, 50, 0, 360 * 64));
        Assert.AreEqual(solidWide, doubleDash.Pixels.Count(p => p != 0));
        Assert.IsGreaterThan(solidWide / 3, doubleDash.Pixels.Count(p => p == 2), "奇数段用背景色");
    }

    [TestMethod]
    public void 宽弧的形状只取决于宽高与线宽_挪一个像素就整体挪一个像素()
    {
        // lw = 1 时外框原先按 Math.Round(x − 0.5) 取整(银行家舍入):x = 4 与 x = 5 都取到 4,两条弧画在同一处。
        foreach (ushort lw in new ushort[] { 1, 3, 4 })
        {
            XGc gc = new(1, null, 24) { Foreground = 1, LineWidth = lw };
            PixelBuffer at4 = Stroke(gc, r => r.Arc(4, 4, 21, 15, 0, 360 * 64));
            PixelBuffer at5 = Stroke(gc, r => r.Arc(5, 4, 21, 15, 0, 360 * 64));
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size - 1; x++)
                {
                    Assert.AreEqual(at4.Get(x, y), at5.Get(x + 1, y), $"lw = {lw}:({x},{y})");
                }
            }
        }
    }
}
