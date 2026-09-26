// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer.Drawing;

/// <summary>
/// 一块软件帧缓冲:每像素一个 <see cref="uint" />,不论深度。
/// </summary>
/// <remarks>
/// 深度 1 存 0/1;深度 24 存 <c>0x00RRGGBB</c>(像素值就是 TrueColor 的 RGB,见架构 §7);
/// 深度 32 存 <c>0xAARRGGBB</c>。统一用 32 位是为了让光栅化只有一条代码路径 ——
/// 位图(深度 1)在 X 程序里只用作点画 / 裁剪 / 光标,量很小,多占的内存不值一提。
/// <para>
/// <b>像素数有上限</b>(<see cref="MaxPixels" />):X 的尺寸是 16 位,32767² 一块就是 4 GB,<c>Width * Height</c> 在 int 上还会溢出。
/// 客户端要的像素图在请求入口按上限回 BadAlloc;窗口的尺寸协议上可以到 32767²,顶层缓冲超了上限就只保留左上的一块
/// (多出来的行不存,画上去也看不见 —— 宿主的原生窗口反正也显示不了那么大)。
/// </para>
/// </remarks>
internal sealed class PixelBuffer
{
    /// <summary>一块缓冲最多这么多像素(6400 万,256 MB):8K 屏的整屏(3300 万)放得下。</summary>
    public const long MaxPixels = 1L << 26;

    public PixelBuffer(int width, int height, byte depth)
    {
        (Width, Height) = Clamp(width, height);
        Depth = depth;
        Pixels = new uint[Width * Height];
    }

    /// <summary>宽高至少 1;像素数超过 <see cref="MaxPixels" /> 时削掉多出来的行。</summary>
    private static (int Width, int Height) Clamp(int width, int height)
    {
        width = Math.Clamp(width, 1, (int)MaxPixels);
        height = Math.Max(1, height);
        if ((long)width * height > MaxPixels)
        {
            height = (int)(MaxPixels / width);
        }
        return (width, height);
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public byte Depth { get; }

    public uint[] Pixels { get; private set; }

    /// <summary>这个深度下像素值的有效位。</summary>
    public uint DepthMask => DepthMaskOf(Depth);

    public static uint DepthMaskOf(byte depth) => depth >= 32 ? 0xFFFFFFFFu : (1u << depth) - 1;

    public XRect Bounds => new(0, 0, Width, Height);

    public uint Get(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? Pixels[(y * Width) + x] : 0;

    /// <summary>在两块缓冲之间拷一块矩形(两边都裁到各自范围内)。</summary>
    public static void CopyRect(PixelBuffer src, int sx, int sy, PixelBuffer dst, int dx, int dy, int width, int height)
    {
        XRect srcRect = new XRect(sx, sy, width, height).Intersect(src.Bounds);
        for (int row = srcRect.Y; row < srcRect.Bottom; row++)
        {
            int ty = row - sy + dy;
            if ((uint)ty >= (uint)dst.Height)
            {
                continue;
            }
            int x1 = Math.Max(srcRect.X, sx - dx);
            int x2 = Math.Min(srcRect.Right, sx - dx + dst.Width);
            if (x2 > x1)
            {
                Array.Copy(src.Pixels, (row * src.Width) + x1, dst.Pixels, (ty * dst.Width) + (x1 - sx + dx), x2 - x1);
            }
        }
    }

    /// <summary>改尺寸,左上角对齐保留原内容(bit-gravity NorthWest),新露出的部分填 <paramref name="fill" />。</summary>
    public void Resize(int width, int height, uint fill = 0)
    {
        (width, height) = Clamp(width, height);
        if (width == Width && height == Height)
        {
            return;
        }
        uint[] next = new uint[width * height];
        if (fill != 0)
        {
            Array.Fill(next, fill);
        }
        int copyW = Math.Min(width, Width);
        int copyH = Math.Min(height, Height);
        for (int y = 0; y < copyH; y++)
        {
            Array.Copy(Pixels, y * Width, next, y * width, copyW);
        }
        Pixels = next;
        Width = width;
        Height = height;
    }
}
