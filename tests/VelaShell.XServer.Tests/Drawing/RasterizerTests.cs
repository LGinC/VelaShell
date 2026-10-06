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
}
