// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer.Drawing;

/// <summary>
/// 由互不重叠的矩形组成的区域:窗口可见区域、裁剪区域、Expose 区域、SHAPE / XFIXES 的区域都用它。
/// </summary>
/// <remarks>
/// <para>
/// 按 y 分带(YX-banded)存放:同一带的矩形 Y 与高度相同、按 X 升序、互不重叠也不相接;带按 Y 升序、互不重叠;
/// 上下相接且各段 x 完全相同的两带合并成一带。并、交、差都是两个区域按带归并:从上往下切成一条条横带,
/// 每条带里两边的 x 段各自有序,一趟线性合并 —— 代价与两边的矩形数加结果的矩形数成正比,
/// 不再是逐个矩形互减的 O(n²)(一个上万块的形状并一个矩形就要算上亿次,还会越减越碎)。
/// </para>
/// <para>
/// <see cref="Rects" /> 的顺序因此是先 y 后 x。只在执行线程上用,不是线程安全的。
/// </para>
/// </remarks>
internal sealed class Region
{
    private enum Op
    {
        Union,
        Intersect,
        Subtract,
    }

    private List<XRect> _rects = [];

    public Region()
    {
    }

    public Region(XRect rect)
    {
        if (!rect.IsEmpty)
        {
            _rects.Add(rect);
        }
    }

    /// <summary>组成区域的矩形(互不重叠,先 y 后 x)。</summary>
    public IReadOnlyList<XRect> Rects => _rects;

    public bool IsEmpty => _rects.Count == 0;

    public Region Clone()
    {
        Region copy = new();
        copy._rects.AddRange(_rects);
        return copy;
    }

    /// <summary>外接矩形。</summary>
    public XRect Bounds
    {
        get
        {
            if (_rects.Count == 0)
            {
                return default;
            }
            // 带按 Y 排好了:上边取第一块,下边取最后一块;左右要扫一遍。
            int x1 = int.MaxValue, x2 = int.MinValue;
            foreach (XRect r in _rects)
            {
                x1 = Math.Min(x1, r.X);
                x2 = Math.Max(x2, r.Right);
            }
            int y1 = _rects[0].Y, y2 = _rects[^1].Bottom;
            return new(x1, y1, x2 - x1, y2 - y1);
        }
    }

    public bool Contains(int x, int y)
    {
        foreach (XRect r in _rects)
        {
            if (r.Y > y)
            {
                return false;   // 后面的带都在更下面
            }
            if (r.Contains(x, y))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>就地减去一个矩形。</summary>
    public Region Subtract(XRect cut)
    {
        // 快路径:与外接矩形不相交就原样不动(可见区域计算里绝大多数兄弟窗口与之不相交),不分配。
        if (cut.IsEmpty || _rects.Count == 0 || Bounds.Intersect(cut).IsEmpty)
        {
            return this;
        }
        _rects = Combine(_rects, [cut], Op.Subtract);
        return this;
    }

    /// <summary>就地减去另一个区域。</summary>
    public Region Subtract(Region other)
    {
        if (other._rects.Count <= 1)
        {
            return other._rects.Count == 0 ? this : Subtract(other._rects[0]);
        }
        if (_rects.Count != 0)
        {
            _rects = Combine(_rects, other._rects, Op.Subtract);
        }
        return this;
    }

    /// <summary>
    /// 就地与矩形求交:逐块裁剪,带的结构不变(裁剪不会让两段 x 相接,也不会让带重叠)。
    /// 有块被裁过或裁没了的话,上下相邻的两带可能变得一模一样,再过一遍把它们合起来。
    /// </summary>
    public Region Intersect(XRect clip)
    {
        int kept = 0;
        bool changed = false;
        for (int i = 0; i < _rects.Count; i++)
        {
            XRect original = _rects[i];
            XRect r = original.Intersect(clip);
            changed |= r != original;
            if (!r.IsEmpty)
            {
                _rects[kept++] = r;
            }
        }
        _rects.RemoveRange(kept, _rects.Count - kept);
        if (changed && _rects.Count > 1)
        {
            _rects = Combine(_rects, [], Op.Union);
        }
        return this;
    }

    /// <summary>就地与另一个区域求交。</summary>
    public Region Intersect(Region other)
    {
        if (other._rects.Count <= 1)
        {
            if (other._rects.Count == 0)
            {
                _rects.Clear();
                return this;
            }
            return Intersect(other._rects[0]);
        }
        if (_rects.Count != 0)
        {
            _rects = Combine(_rects, other._rects, Op.Intersect);
        }
        return this;
    }

    /// <summary>就地并上一个矩形。</summary>
    public Region Union(XRect add)
    {
        if (add.IsEmpty)
        {
            return this;
        }
        if (_rects.Count == 0)
        {
            _rects.Add(add);
            return this;
        }
        XRect bounds = Bounds;
        if (add.X <= bounds.X && add.Y <= bounds.Y && add.Right >= bounds.Right && add.Bottom >= bounds.Bottom)
        {
            _rects.Clear();   // 把原来的整个盖住了
            _rects.Add(add);
            return this;
        }
        _rects = Combine(_rects, [add], Op.Union);
        return this;
    }

    /// <summary>就地并上另一个区域。</summary>
    public Region Union(Region other)
    {
        if (other._rects.Count <= 1)
        {
            return other._rects.Count == 0 ? this : Union(other._rects[0]);
        }
        if (_rects.Count == 0)
        {
            _rects.AddRange(other._rects);
            return this;
        }
        _rects = Combine(_rects, other._rects, Op.Union);
        return this;
    }

    /// <summary>
    /// 一批任意(可以重叠、无序)的矩形并成一个区域 —— 逐个 <see cref="Union(XRect)" /> 在矩形很多时是 O(n²)。
    /// 先切成一段段本身已经分好带的连续矩形(位图逐行扫出来的、另一个区域的 <see cref="Rects" />,整批就是一段),
    /// 再两两归并:每一轮把相邻的两段并起来,一共 log n 轮。
    /// </summary>
    public static Region FromRects(IEnumerable<XRect> rects)
    {
        List<List<XRect>> parts = [];
        List<XRect>? run = null;
        foreach (XRect r in rects)
        {
            if (r.IsEmpty)
            {
                continue;
            }
            if (run is not null && Follows(run[^1], r))
            {
                run.Add(r);
                continue;
            }
            run = [r];
            parts.Add(run);
        }
        if (parts.Count == 1)
        {
            parts[0] = Combine(parts[0], [], Op.Union);   // 只有一段:过一遍,把上下相接且相同的带合起来
        }
        while (parts.Count > 1)
        {
            List<List<XRect>> merged = [with((parts.Count + 1) / 2)];
            for (int i = 0; i + 1 < parts.Count; i += 2)
            {
                merged.Add(Combine(parts[i], parts[i + 1], Op.Union));
            }
            if (parts.Count % 2 == 1)
            {
                merged.Add(parts[^1]);
            }
            parts = merged;
        }
        Region region = new();
        if (parts.Count == 1)
        {
            region._rects = parts[0];
        }
        return region;
    }

    public Region Translate(int dx, int dy)
    {
        for (int i = 0; i < _rects.Count; i++)
        {
            _rects[i] = _rects[i].Offset(dx, dy);
        }
        return this;
    }

    /// <summary><paramref name="next" /> 接在 <paramref name="previous" /> 后面仍是合法的分带序列:同一带里靠右且不相接,或者另起一带在下面。</summary>
    private static bool Follows(XRect previous, XRect next) =>
        (next.Y == previous.Y && next.Height == previous.Height && next.X > previous.Right) || next.Y >= previous.Bottom;

    // ------------------------------------------------------------------ 按带归并

    /// <summary>
    /// 两个分带区域的并 / 交 / 差。从上往下走:每次取出「此刻两边各自覆盖着这一行的那一带」,切出一条两边都不变的横带
    /// (到任一边的带结束或开始为止),在这条横带里合并两边的 x 段,结果作为一带追加(能与上一带合并就合并)。
    /// </summary>
    private static List<XRect> Combine(List<XRect> a, List<XRect> b, Op op)
    {
        List<XRect> result = [with(a.Count + b.Count)];
        List<(int X1, int X2)> spans = [];
        int previousBand = -1;   // 结果里上一带的起始下标(用来与新的一带合并)
        int ia = 0, ib = 0;
        int y = int.MinValue;
        while (ia < a.Count || ib < b.Count)
        {
            int aEnd = BandEnd(a, ia), bEnd = BandEnd(b, ib);
            bool aCovers = ia < a.Count && a[ia].Y <= y;
            bool bCovers = ib < b.Count && b[ib].Y <= y;
            if (!aCovers && !bCovers)
            {
                // 两边在 y 这一行都是空的:跳到下一个开始的带。
                y = Math.Min(ia < a.Count ? a[ia].Y : int.MaxValue, ib < b.Count ? b[ib].Y : int.MaxValue);
                continue;
            }
            // 这条横带到哪一行为止:覆盖着的带在哪结束、没覆盖的带从哪开始,取最早的。
            int bottom = int.MaxValue;
            if (ia < a.Count)
            {
                bottom = Math.Min(bottom, aCovers ? a[ia].Bottom : a[ia].Y);
            }
            if (ib < b.Count)
            {
                bottom = Math.Min(bottom, bCovers ? b[ib].Bottom : b[ib].Y);
            }

            bool emits = op switch
            {
                Op.Union => true,
                Op.Intersect => aCovers && bCovers,
                _ => aCovers,
            };
            if (emits)
            {
                spans.Clear();
                MergeSpans(a, aCovers ? ia : aEnd, aEnd, b, bCovers ? ib : bEnd, bEnd, op, spans);
                previousBand = AppendBand(result, previousBand, spans, y, bottom);
            }

            y = bottom;
            if (aCovers && a[ia].Bottom == y)
            {
                ia = aEnd;
            }
            if (bCovers && b[ib].Bottom == y)
            {
                ib = bEnd;
            }
        }
        return result;
    }

    /// <summary>从 <paramref name="start" /> 起的这一带在哪结束(下一带的起始下标)。</summary>
    private static int BandEnd(List<XRect> rects, int start)
    {
        if (start >= rects.Count)
        {
            return start;
        }
        int y = rects[start].Y;
        int end = start + 1;
        while (end < rects.Count && rects[end].Y == y)
        {
            end++;
        }
        return end;
    }

    /// <summary>一条横带里两边的 x 段(各自有序、不重叠)按 <paramref name="op" /> 合并成有序、不重叠、不相接的 x 段。</summary>
    private static void MergeSpans(List<XRect> a, int ia, int aEnd, List<XRect> b, int ib, int bEnd, Op op, List<(int X1, int X2)> output)
    {
        switch (op)
        {
            case Op.Union:
                while (ia < aEnd || ib < bEnd)
                {
                    XRect next = ib >= bEnd || (ia < aEnd && a[ia].X <= b[ib].X) ? a[ia++] : b[ib++];
                    if (output.Count != 0 && next.X <= output[^1].X2)
                    {
                        output[^1] = (output[^1].X1, Math.Max(output[^1].X2, next.Right));   // 重叠或相接:接上
                    }
                    else
                    {
                        output.Add((next.X, next.Right));
                    }
                }
                break;
            case Op.Intersect:
                while (ia < aEnd && ib < bEnd)
                {
                    int x1 = Math.Max(a[ia].X, b[ib].X), x2 = Math.Min(a[ia].Right, b[ib].Right);
                    if (x2 > x1)
                    {
                        output.Add((x1, x2));
                    }
                    // 先结束的那一段不会再与对面的后续段相交。
                    if (a[ia].Right < b[ib].Right)
                    {
                        ia++;
                    }
                    else
                    {
                        ib++;
                    }
                }
                break;
            default:
                for (; ia < aEnd; ia++)
                {
                    int x1 = a[ia].X, x2 = a[ia].Right;
                    // 跳过整个在这一段左边的减数段。
                    while (ib < bEnd && b[ib].Right <= x1)
                    {
                        ib++;
                    }
                    for (int k = ib; k < bEnd && b[k].X < x2; k++)
                    {
                        if (b[k].X > x1)
                        {
                            output.Add((x1, b[k].X));
                        }
                        x1 = Math.Max(x1, b[k].Right);
                        if (x1 >= x2)
                        {
                            break;
                        }
                    }
                    if (x2 > x1)
                    {
                        output.Add((x1, x2));
                    }
                }
                break;
        }
    }

    /// <summary>把一带追加到结果里;与上一带上下相接且各段 x 相同就直接把上一带加高。返回结果里「上一带」的新起始下标。</summary>
    private static int AppendBand(List<XRect> result, int previousBand, List<(int X1, int X2)> spans, int top, int bottom)
    {
        if (spans.Count == 0)
        {
            return previousBand;
        }
        if (previousBand >= 0 && result.Count - previousBand == spans.Count && result[previousBand].Bottom == top)
        {
            bool same = true;
            for (int i = 0; i < spans.Count && same; i++)
            {
                XRect r = result[previousBand + i];
                same = r.X == spans[i].X1 && r.Right == spans[i].X2;
            }
            if (same)
            {
                for (int i = previousBand; i < result.Count; i++)
                {
                    XRect r = result[i];
                    result[i] = r with { Height = bottom - r.Y };
                }
                return previousBand;
            }
        }
        int start = result.Count;
        foreach ((int x1, int x2) in spans)
        {
            result.Add(new XRect(x1, top, x2 - x1, bottom - top));
        }
        return start;
    }
}
