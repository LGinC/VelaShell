// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Rendering Extension, Version 0.11 —— §9「Composite」(dst = (src IN mask) OP dst;遮罩有颜色通道且
//   component-alpha 为 True 时逐通道相乘;src / mask 的坐标按 (x − dst-x) 与目标对齐)、
//   §10「Trapezoids / Triangles / TriStrip / TriFan / AddTraps」(几何按像素覆盖率光栅化成 alpha 遮罩,
//   多个图元以 Add 累加进遮罩)

using System.Buffers;
using VelaShell.XServer.Protocol;

namespace VelaShell.XServer.Drawing;

/// <summary>合成目标:一块缓冲、可绘对象原点在缓冲里的位置、像素格式、可写的区域(缓冲坐标)。</summary>
internal sealed record RenderTarget(PixelBuffer Buffer, int OriginX, int OriginY, PictFormat Format, IReadOnlyList<XRect> Clip);

internal static class RenderCompositor
{
    /// <summary>
    /// 把目标上 (dstX, dstY, width, height)(可绘对象坐标)这一块合成一遍。返回实际写过的范围(缓冲坐标)。
    /// </summary>
    public static XRect Composite(byte op, RenderSource src, RenderSource? mask, bool componentAlpha, RenderTarget dst,
        int srcX, int srcY, int maskX, int maskY, int dstX, int dstY, int width, int height)
    {
        // 工作量按真正要合成的像素数先扣(快路径与逐像素路径一样算,见 WorkBudget)。
        XRect requested = new(dstX + dst.OriginX, dstY + dst.OriginY, width, height);
        long work = 1;
        foreach (XRect clip in dst.Clip)
        {
            XRect r = clip.Intersect(requested);
            work += 1 + ((long)r.Width * r.Height);
        }
        WorkBudget.Charge(work);

        // 源 / 遮罩与目标是同一块缓冲(同一张像素图、同一个顶层里的窗口):先把要读的那一块拷出来。逐行从上往下合成时,
        // 目标在源下面(或同一行靠右)的话,后面要读的源行已经被前面写过了 —— 结果得像「先读完源再写」。
        // 要读的只是目标上真正写得到的那几行几列对应的部分(可写区域之外的不合成)。
        bool srcShared = src is ImageSource { } s0 && ReferenceEquals(s0.Buffer, dst.Buffer);
        bool maskShared = mask is ImageSource { } m0 && ReferenceEquals(m0.Buffer, dst.Buffer);
        if (srcShared || maskShared)
        {
            int wx1 = int.MaxValue, wy1 = int.MaxValue, wx2 = int.MinValue, wy2 = int.MinValue;
            foreach (XRect clip in dst.Clip)
            {
                XRect r = clip.Intersect(requested);
                if (!r.IsEmpty)
                {
                    (wx1, wy1, wx2, wy2) = (Math.Min(wx1, r.X), Math.Min(wy1, r.Y), Math.Max(wx2, r.Right), Math.Max(wy2, r.Bottom));
                }
            }
            XRect local = wx2 <= wx1 ? default : new XRect(wx1 - dst.OriginX - dstX, wy1 - dst.OriginY - dstY, wx2 - wx1, wy2 - wy1);
            if (srcShared)
            {
                src = ((ImageSource)src).Detach(local.Offset(srcX, srcY));
            }
            if (maskShared)
            {
                mask = ((ImageSource)mask!).Detach(local.Offset(maskX, maskY));
            }
        }
        if (TryFastPath(op, src, mask, componentAlpha, dst, srcX, srcY, maskX, maskY, dstX, dstY, width, height, out XRect fastDirty))
        {
            return fastDirty;
        }
        XRect area = new(dstX + dst.OriginX, dstY + dst.OriginY, width, height);
        PixelBuffer buffer = dst.Buffer;
        uint depthMask = buffer.DepthMask;
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;

        // 8888 目标上最常用的三种运算走整数:源与遮罩各取成 8 位预乘的一行(渐变、变换、重复、各种源格式都在取样里处理掉),
        // 逐像素整数合成。其余运算、分量 alpha 与别的目标格式走浮点。
        // 只有 alpha 的 a8 目标(cairo 拼遮罩、Qt 的 alpha 图)上的 Porter-Duff 运算同样走整数,只算 alpha 一个通道。
        bool alphaOnly = ReferenceEquals(dst.Format, PictFormat.A8) && !componentAlpha && op <= RenderOps.Saturate;
        bool integer = alphaOnly || (Is8888(dst.Format) && !componentAlpha && op is RenderOps.Src or RenderOps.Over or RenderOps.Add);
        Argb[] srcRow = integer ? [] : ArrayPool<Argb>.Shared.Rent(Math.Max(1, width));
        Argb[] maskRow = integer ? [] : ArrayPool<Argb>.Shared.Rent(Math.Max(1, width));
        uint[] srcRow8 = integer ? ArrayPool<uint>.Shared.Rent(Math.Max(1, width)) : [];
        uint[] maskRow8 = integer && mask is not null ? ArrayPool<uint>.Shared.Rent(Math.Max(1, width)) : [];
        try
        {
            foreach (XRect clip in dst.Clip)
            {
                XRect r = clip.Intersect(area);
                if (r.IsEmpty)
                {
                    continue;
                }
                x1 = Math.Min(x1, r.X);
                y1 = Math.Min(y1, r.Y);
                x2 = Math.Max(x2, r.Right);
                y2 = Math.Max(y2, r.Bottom);

                // 纯色 + 无遮罩 + Src(或不透明的 Over):整块填同一个像素值。
                if (mask is null && src is SolidSource solid && src.Transform is null
                    && (op == RenderOps.Src || (op == RenderOps.Over && solid.Color.A >= 1f)))
                {
                    uint value = dst.Format.Encode(solid.Color) & depthMask;
                    for (int by = r.Y; by < r.Bottom; by++)
                    {
                        buffer.Pixels.AsSpan((by * buffer.Width) + r.X, r.Width).Fill(value);
                    }
                    continue;
                }

                if (integer)
                {
                    Span<uint> s8 = srcRow8.AsSpan(0, r.Width);
                    Span<uint> m8 = mask is null ? [] : maskRow8.AsSpan(0, r.Width);
                    for (int by = r.Y; by < r.Bottom; by++)
                    {
                        int dx = r.X - dst.OriginX - dstX;
                        int dy = by - dst.OriginY - dstY;
                        src.FetchRow8888(srcX + dx, srcY + dy, s8);
                        mask?.FetchRow8888(maskX + dx, maskY + dy, m8);
                        Span<uint> row = buffer.Pixels.AsSpan((by * buffer.Width) + r.X, r.Width);
                        if (alphaOnly)
                        {
                            CombineAlphaRow(op, s8, m8, row);
                        }
                        else
                        {
                            CombineRow(op, s8, m8, row, dst.Format.HasAlpha, depthMask);
                        }
                    }
                    continue;
                }

                Span<Argb> s = srcRow.AsSpan(0, r.Width);
                Span<Argb> m = maskRow.AsSpan(0, r.Width);
                for (int by = r.Y; by < r.Bottom; by++)
                {
                    int dx = r.X - dst.OriginX - dstX;
                    int dy = by - dst.OriginY - dstY;
                    src.FetchRow(srcX + dx, srcY + dy, s);
                    mask?.FetchRow(maskX + dx, maskY + dy, m);
                    int row = by * buffer.Width;
                    for (int i = 0; i < s.Length; i++)
                    {
                        Argb sc = s[i];
                        Argb sa;
                        if (mask is not null)
                        {
                            Argb mc = m[i];
                            if (componentAlpha)
                            {
                                sa = new Argb(sc.A * mc.A, sc.A * mc.R, sc.A * mc.G, sc.A * mc.B);
                                sc = new Argb(sc.A * mc.A, sc.R * mc.R, sc.G * mc.G, sc.B * mc.B);
                            }
                            else
                            {
                                sc = new Argb(sc.A * mc.A, sc.R * mc.A, sc.G * mc.A, sc.B * mc.A);
                                sa = Argb.Gray(sc.A);
                            }
                        }
                        else
                        {
                            sa = Argb.Gray(sc.A);
                        }

                        // Over 下完全透明的源不改变目标 —— 字形与覆盖率遮罩的大部分像素走这里。
                        // (Add 不能跳:预乘规则之外 alpha 为 0、颜色不为 0 的源照样要加上去。)
                        if (op == RenderOps.Over && sa.R <= 0 && sa.G <= 0 && sa.B <= 0 && sc.A <= 0)
                        {
                            continue;
                        }
                        int index = row + r.X + i;
                        Argb d = dst.Format.Decode(buffer.Pixels[index]);
                        buffer.Pixels[index] = dst.Format.Encode(RenderOps.Combine(op, sc, sa, d)) & depthMask;
                    }
                }
            }
        }
        finally
        {
            Return(srcRow);
            Return(maskRow);
            Return(srcRow8);
            Return(maskRow8);
        }
        return x2 < x1 ? default : new XRect(x1, y1, x2 - x1, y2 - y1);

        static void Return<T>(T[] array)
        {
            if (array.Length != 0)
            {
                ArrayPool<T>.Shared.Return(array);
            }
        }
    }

    // ================================================================== 整数路径

    /// <summary>
    /// 一行的整数合成:<paramref name="src" /> 是 8 位预乘的源,<paramref name="mask" /> 为空表示没有遮罩(否则只用它的 alpha),
    /// 目标是 8888(<paramref name="dstAlpha" /> 为 false 时是 x8r8g8b8:读的时候 alpha 当 1,写的时候 alpha 字节写 0)。
    /// </summary>
    private static void CombineRow(byte op, ReadOnlySpan<uint> src, ReadOnlySpan<uint> mask, Span<uint> dst, bool dstAlpha, uint depthMask)
    {
        uint keep = dstAlpha ? 0xFFFFFFFFu : 0x00FFFFFFu;
        for (int i = 0; i < dst.Length; i++)
        {
            uint s = src[i];
            if (!mask.IsEmpty)
            {
                uint m = mask[i] >> 24;
                s = m == 255 ? s : m == 0 ? 0 : Argb8.Scale(s, m);
            }
            uint sa = s >> 24;
            switch (op)
            {
                case RenderOps.Src:
                    dst[i] = s & keep & depthMask;
                    break;
                case RenderOps.Over:
                    {
                        // 完全透明的源不改变目标(同浮点路径);不透明的源直接盖上。
                        if (sa == 0)
                        {
                            break;
                        }
                        if (sa == 255)
                        {
                            dst[i] = s & keep & depthMask;
                            break;
                        }
                        uint inv = 255 - sa, d = dst[i];
                        uint oa = dstAlpha ? sa + Argb8.Div255((d >> 24) * inv) : 0;
                        uint or = ((s >> 16) & 0xFF) + Argb8.Div255(((d >> 16) & 0xFF) * inv);
                        uint og = ((s >> 8) & 0xFF) + Argb8.Div255(((d >> 8) & 0xFF) * inv);
                        uint ob = (s & 0xFF) + Argb8.Div255((d & 0xFF) * inv);
                        // 源没有按规矩预乘(颜色大于 alpha)时和会超过 255:夹住,免得进位到相邻通道。
                        dst[i] = ((Math.Min(oa, 255) << 24) | (Math.Min(or, 255) << 16) | (Math.Min(og, 255) << 8) | Math.Min(ob, 255)) & depthMask;
                        break;
                    }
                default:   // Add:逐通道饱和相加
                    {
                        uint d = dst[i];
                        uint oa = dstAlpha ? Math.Min(sa + (d >> 24), 255) : 0;
                        uint or = Math.Min(((s >> 16) & 0xFF) + ((d >> 16) & 0xFF), 255);
                        uint og = Math.Min(((s >> 8) & 0xFF) + ((d >> 8) & 0xFF), 255);
                        uint ob = Math.Min((s & 0xFF) + (d & 0xFF), 255);
                        dst[i] = ((oa << 24) | (or << 16) | (og << 8) | ob) & depthMask;
                        break;
                    }
            }
        }
    }

    /// <summary>
    /// a8 目标的一行:只有 alpha。结果 = 源 alpha × Fa + 目标 alpha × Fb(Porter-Duff 的两个因子,0–255 定点,同 <see cref="RenderOps" />
    /// 的浮点公式),夹到 255。原先逐像素浮点 Decode、四通道合成、再 Encode。
    /// </summary>
    private static void CombineAlphaRow(byte op, ReadOnlySpan<uint> src, ReadOnlySpan<uint> mask, Span<uint> dst)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            uint sa = src[i] >> 24;
            if (!mask.IsEmpty)
            {
                sa = Argb8.Div255(sa * (mask[i] >> 24));
            }
            uint da = dst[i] & 0xFF;
            (uint fa, uint fb) = op switch
            {
                RenderOps.Clear => (0u, 0u),
                RenderOps.Src => (255u, 0u),
                RenderOps.Dst => (0u, 255u),
                RenderOps.Over => (255u, 255 - sa),
                4 => (255 - da, 255u),           // OverReverse
                5 => (da, 0u),                   // In
                6 => (0u, sa),                   // InReverse
                7 => (255 - da, 0u),             // Out
                8 => (0u, 255 - sa),             // OutReverse
                9 => (da, 255 - sa),             // Atop
                10 => (255 - da, sa),            // AtopReverse
                11 => (255 - da, 255 - sa),      // Xor
                RenderOps.Add => (255u, 255u),
                _ => (255 - da >= sa ? 255u : (255 - da) * 255 / sa, 255u),   // Saturate:min(1, (1 − da) / sa)
            };
            dst[i] = Math.Min(Argb8.Div255(sa * fa) + Argb8.Div255(da * fb), 255);
        }
    }

    private static bool Is8888(PictFormat f) => ReferenceEquals(f, PictFormat.A8R8G8B8) || ReferenceEquals(f, PictFormat.X8R8G8B8);

    /// <summary>
    /// 两种占绝大多数的情形不逐行取样,直接按源的存储整块算(比 <see cref="CombineRow" /> 那条整数路径还省一次取样):
    /// <list type="number">
    /// <item>纯色源 + 单字节遮罩(字形、梯形覆盖率)+ Over → 8888 目标(Xft 画字、cairo 画抗锯齿图形);</item>
    /// <item>8888 图像源(无变换、取样范围在图像与它的缓冲之内)+ 无遮罩 + Src / Over → 8888 目标(cairo 贴图、窗口间拷贝)。</item>
    /// </list>
    /// 条件不满足时返回 false,由通用路径处理。
    /// </summary>
    private static bool TryFastPath(byte op, RenderSource src, RenderSource? mask, bool componentAlpha, RenderTarget dst,
        int srcX, int srcY, int maskX, int maskY, int dstX, int dstY, int width, int height, out XRect dirty)
    {
        dirty = default;
        if (!Is8888(dst.Format) || src.Transform is not null)
        {
            return false;
        }
        if (op == RenderOps.Over && mask is ByteMaskSource bytes && !componentAlpha && src is SolidSource solid)
        {
            dirty = OverSolidMask(solid.Color, bytes, dst, maskX - dstX, maskY - dstY, dstX, dstY, width, height);
            return true;
        }
        if (mask is null && op is RenderOps.Src or RenderOps.Over && src is ImageSource image && Is8888(image.Format)
            && srcX >= 0 && srcY >= 0 && srcX + width <= image.Width && srcY + height <= image.Height
            // 还要整块在缓冲之内:窗口 picture 伸出顶层之外的部分不在缓冲里(交给通用路径,读到的是透明)。
            && image.OriginX + srcX >= 0 && image.OriginY + srcY >= 0
            && image.OriginX + srcX + width <= image.Buffer.Width && image.OriginY + srcY + height <= image.Buffer.Height)
        {
            dirty = BlitImage(op, image, dst, srcX - dstX, srcY - dstY, dstX, dstY, width, height);
            return true;
        }
        return false;
    }

    /// <summary>纯色 IN 遮罩 OVER 目标。遮罩坐标 = 目标可绘坐标 + (maskDx, maskDy)。</summary>
    private static XRect OverSolidMask(Argb color, ByteMaskSource mask, RenderTarget dst, int maskDx, int maskDy,
        int dstX, int dstY, int width, int height)
    {
        uint sa = Argb8.ToByte(color.A), sr = Argb8.ToByte(color.R), sg = Argb8.ToByte(color.G), sb = Argb8.ToByte(color.B);
        bool dstAlpha = dst.Format.HasAlpha;
        uint opaque = (dstAlpha ? 0xFF000000u : 0) | (sr << 16) | (sg << 8) | sb;
        uint[] px = dst.Buffer.Pixels;
        int stride = dst.Buffer.Width;
        XRect area = new(dstX + dst.OriginX, dstY + dst.OriginY, width, height);
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
        foreach (XRect clip in dst.Clip)
        {
            // 再与遮罩自己的矩形求交:遮罩之外全透明,不用碰。
            XRect maskRect = new(mask.X0 - maskDx + dst.OriginX, mask.Y0 - maskDy + dst.OriginY, mask.Width, mask.Height);
            XRect r = clip.Intersect(area).Intersect(maskRect);
            if (r.IsEmpty)
            {
                continue;
            }
            (x1, y1) = (Math.Min(x1, r.X), Math.Min(y1, r.Y));
            (x2, y2) = (Math.Max(x2, r.Right), Math.Max(y2, r.Bottom));
            for (int by = r.Y; by < r.Bottom; by++)
            {
                int my = by - dst.OriginY + maskDy - mask.Y0;
                int mRow = (my * mask.Width) + (r.X - dst.OriginX + maskDx - mask.X0);
                int dRow = (by * stride) + r.X;
                for (int i = 0; i < r.Width; i++)
                {
                    uint m = mask.Alpha[mRow + i];
                    if (m == 0)
                    {
                        continue;
                    }
                    if (m == 255 && sa == 255)
                    {
                        px[dRow + i] = opaque;
                        continue;
                    }
                    uint a = Argb8.Div255(sa * m), inv = 255 - a;
                    uint d = px[dRow + i];
                    uint oa = dstAlpha ? a + Argb8.Div255((d >> 24) * inv) : 0;
                    uint or = Argb8.Div255(sr * m) + Argb8.Div255(((d >> 16) & 0xFF) * inv);
                    uint og = Argb8.Div255(sg * m) + Argb8.Div255(((d >> 8) & 0xFF) * inv);
                    uint ob = Argb8.Div255(sb * m) + Argb8.Div255((d & 0xFF) * inv);
                    // 各通道的两次取整最多凑出 256:夹到 255,免得进位到相邻通道。
                    px[dRow + i] = (Math.Min(oa, 255) << 24) | (Math.Min(or, 255) << 16) | (Math.Min(og, 255) << 8) | Math.Min(ob, 255);
                }
            }
        }
        return x2 < x1 ? default : new XRect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>8888 图像 Src / Over 到 8888 目标。源坐标 = 目标可绘坐标 + (srcDx, srcDy)(源 picture 坐标)。</summary>
    private static XRect BlitImage(byte op, ImageSource image, RenderTarget dst, int srcDx, int srcDy,
        int dstX, int dstY, int width, int height)
    {
        bool srcAlpha = image.Format.HasAlpha, dstAlpha = dst.Format.HasAlpha;
        bool copy = op == RenderOps.Src || !srcAlpha;   // 不透明的源 Over 就是 Src
        uint[] sp = image.Buffer.Pixels, dp = dst.Buffer.Pixels;
        int sStride = image.Buffer.Width, dStride = dst.Buffer.Width;
        XRect area = new(dstX + dst.OriginX, dstY + dst.OriginY, width, height);
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
        foreach (XRect clip in dst.Clip)
        {
            XRect r = clip.Intersect(area);
            if (r.IsEmpty)
            {
                continue;
            }
            (x1, y1) = (Math.Min(x1, r.X), Math.Min(y1, r.Y));
            (x2, y2) = (Math.Max(x2, r.Right), Math.Max(y2, r.Bottom));
            for (int by = r.Y; by < r.Bottom; by++)
            {
                int sy = by - dst.OriginY + srcDy + image.OriginY;
                int sRow = (sy * sStride) + (r.X - dst.OriginX + srcDx + image.OriginX);
                int dRow = (by * dStride) + r.X;
                Span<uint> to = dp.AsSpan(dRow, r.Width);
                ReadOnlySpan<uint> from = sp.AsSpan(sRow, r.Width);
                if (copy)
                {
                    if (srcAlpha == dstAlpha)
                    {
                        from.CopyTo(to);   // 同格式:整行 memmove(源、目标是同一块缓冲时也安全)
                    }
                    else
                    {
                        uint or = dstAlpha ? 0xFF000000u : 0, and = dstAlpha ? 0xFFFFFFFFu : 0x00FFFFFFu;
                        for (int i = 0; i < to.Length; i++)
                        {
                            to[i] = (from[i] & and) | or;   // xRGB → ARGB 补不透明;ARGB → xRGB 丢掉 alpha(颜色已预乘)
                        }
                    }
                    continue;
                }
                for (int i = 0; i < to.Length; i++)
                {
                    uint s = from[i];
                    uint sa = s >> 24;
                    if (sa == 0)
                    {
                        continue;
                    }
                    if (sa == 255)
                    {
                        to[i] = dstAlpha ? s : s & 0x00FFFFFF;
                        continue;
                    }
                    uint inv = 255 - sa, d = to[i];
                    uint oa = dstAlpha ? sa + Argb8.Div255((d >> 24) * inv) : 0;
                    uint or = ((s >> 16) & 0xFF) + Argb8.Div255(((d >> 16) & 0xFF) * inv);
                    uint og = ((s >> 8) & 0xFF) + Argb8.Div255(((d >> 8) & 0xFF) * inv);
                    uint ob = (s & 0xFF) + Argb8.Div255((d & 0xFF) * inv);
                    to[i] = (oa << 24) | (Math.Min(or, 255) << 16) | (Math.Min(og, 255) << 8) | Math.Min(ob, 255);
                }
            }
        }
        return x2 < x1 ? default : new XRect(x1, y1, x2 - x1, y2 - y1);
    }
}

/// <summary>
/// 把梯形 / 三角形光栅化成覆盖率(0–1)。每个像素行分 <see cref="SubRows" /> 条子扫描线,
/// 每条子扫描线上按精确的水平重叠长度累加 —— 水平方向是解析的,垂直方向是 16 级采样。
/// </summary>
internal sealed class CoverageMask
{
    private const int SubRows = 16;

    public CoverageMask(XRect bounds)
    {
        Bounds = bounds;
        Alpha = new float[Math.Max(0, bounds.Width * bounds.Height)];
    }

    /// <summary>覆盖的范围(目标可绘对象坐标)。</summary>
    public XRect Bounds { get; }

    public float[] Alpha { get; }

    /// <summary>一条直线(两点式)在高度 y 处的 x。</summary>
    public readonly record struct Line(double X1, double Y1, double X2, double Y2)
    {
        public double XAt(double y) => Y2 == Y1 ? X1 : X1 + ((y - Y1) * (X2 - X1) / (Y2 - Y1));
    }

    /// <summary>梯形:top ≤ y &lt; bottom 之间、左边线与右边线之间的部分(左在右的右边时那一段不画)。</summary>
    public void AddTrapezoid(double top, double bottom, Line left, Line right) =>
        AddBand(top, bottom, y => (left.XAt(y), right.XAt(y)));

    /// <summary>三角形:按中间顶点拆成上下两段,每条子扫描线取与各边交点的最小 / 最大值。</summary>
    public void AddTriangle((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        (double X, double Y)[] v = [a, b, c];
        Array.Sort(v, (p, q) => p.Y.CompareTo(q.Y));
        Line longEdge = new(v[0].X, v[0].Y, v[2].X, v[2].Y);
        Line upper = new(v[0].X, v[0].Y, v[1].X, v[1].Y);
        Line lower = new(v[1].X, v[1].Y, v[2].X, v[2].Y);
        AddBand(v[0].Y, v[1].Y, y => MinMax(longEdge.XAt(y), upper.XAt(y)));
        AddBand(v[1].Y, v[2].Y, y => MinMax(longEdge.XAt(y), lower.XAt(y)));

        static (double, double) MinMax(double p, double q) => p <= q ? (p, q) : (q, p);
    }

    private void AddBand(double top, double bottom, Func<double, (double Left, double Right)> span)
    {
        double yStart = Math.Max(top, Bounds.Y), yEnd = Math.Min(bottom, Bounds.Bottom);
        if (yEnd <= yStart)
        {
            return;
        }
        const float weight = 1f / SubRows;
        for (int row = (int)Math.Floor(yStart); row < (int)Math.Ceiling(yEnd); row++)
        {
            WorkBudget.Charge(SubRows);
            for (int k = 0; k < SubRows; k++)
            {
                double y = row + ((k + 0.5) / SubRows);
                if (y < top || y >= bottom)
                {
                    continue;
                }
                (double left, double right) = span(y);
                AddSpan(row, left, right, weight);
            }
        }
    }

    private void AddSpan(int row, double left, double right, float weight)
    {
        left = Math.Max(left, Bounds.X);
        right = Math.Min(right, Bounds.Right);
        if (right <= left || row < Bounds.Y || row >= Bounds.Bottom)
        {
            return;
        }
        int offset = (row - Bounds.Y) * Bounds.Width;
        int first = (int)Math.Floor(left), last = (int)Math.Ceiling(right) - 1;
        WorkBudget.Charge(1 + Math.Max(0, last - first + 1));
        for (int px = first; px <= last; px++)
        {
            double covered = Math.Min(right, px + 1) - Math.Max(left, px);
            if (covered > 0)
            {
                Alpha[offset + px - Bounds.X] += (float)covered * weight;
            }
        }
    }

    /// <summary>转成单字节遮罩(合成器走整数快路径),按遮罩格式的位数量化:1 位二值、4 位 16 级、8 位原样。</summary>
    public ByteMaskSource ToByteSource(byte depth)
    {
        byte[] bytes = new byte[Alpha.Length];
        for (int i = 0; i < Alpha.Length; i++)
        {
            float a = Math.Min(1f, Alpha[i]);
            bytes[i] = depth switch
            {
                1 => a >= 0.5f ? (byte)255 : (byte)0,
                4 => (byte)((int)((a * 15) + 0.5f) * 17),
                _ => (byte)((a * 255) + 0.5f),
            };
        }
        return new ByteMaskSource(bytes, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height);
    }
}
