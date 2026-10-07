// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenGL Graphics with the X Window System, Version 1.4 —— §2.2「Rendering Contexts and Drawing Surfaces」
//   (GLX 可绘对象的颜色缓冲来自 FBConfig:双缓冲有前后两块,深度、模板等辅助缓冲没有对外的名字)。
//   The OpenGL Graphics System, Version 1.5 —— §4.2.1「Selecting a Buffer for Writing」(FRONT / BACK)。

namespace VelaShell.XServer.Gl;

/// <summary>
/// 一个 GLX 可绘对象的帧缓冲:颜色(0xAARRGGBB,按 X 的行序 —— 第 0 行在最上面)、深度与模板。
/// 前缓冲由服务端在刷新 / 交换时拷进对应的 X 窗口或像素图。
/// </summary>
internal sealed class GlSurface
{
    public GlSurface(uint drawable, int width, int height, bool doubleBuffered, bool hasAlpha)
    {
        Drawable = drawable;
        DoubleBuffered = doubleBuffered;
        HasAlpha = hasAlpha;
        Front = [];
        Depth = [];
        Stencil = [];
        Resize(width, height);
    }

    /// <summary>这块帧缓冲属于哪个 X 可绘对象(窗口、像素图或 pbuffer 的 ID)。</summary>
    public uint Drawable { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool DoubleBuffered { get; }

    public bool HasAlpha { get; }

    public uint[] Front { get; private set; }

    public uint[]? Back { get; private set; }

    public float[] Depth { get; private set; }

    public byte[] Stencil { get; private set; }

    // 前缓冲自上次拷出后被画过的范围:[x0, x1) × [y0, y1),列与行(第 0 行在最上面)。
    private int _dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1;

    /// <summary>前缓冲自上次拷出后被画过。</summary>
    public bool FrontDirty => _dirtyX1 > _dirtyX0;

    /// <summary>前缓冲自上次拷出后被画过的外接矩形(列、行);没画过是空矩形。拷出时只拷这一块。</summary>
    public XRect FrontDirtyRect => FrontDirty ? new XRect(_dirtyX0, _dirtyY0, _dirtyX1 - _dirtyX0, _dirtyY1 - _dirtyY0) : default;

    /// <summary>前缓冲第 <paramref name="row" /> 行第 <paramref name="column" /> 列被画过。</summary>
    public void MarkFrontDirty(int column, int row)
    {
        if (!FrontDirty)
        {
            (_dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1) = (column, row, column + 1, row + 1);
            return;
        }
        _dirtyX0 = Math.Min(_dirtyX0, column);
        _dirtyY0 = Math.Min(_dirtyY0, row);
        _dirtyX1 = Math.Max(_dirtyX1, column + 1);
        _dirtyY1 = Math.Max(_dirtyY1, row + 1);
    }

    /// <summary>前缓冲的一块矩形(列、行)被画过。</summary>
    public void MarkFrontDirty(XRect rect)
    {
        if (!rect.IsEmpty)
        {
            MarkFrontDirty(rect.X, rect.Y);
            MarkFrontDirty(rect.Right - 1, rect.Bottom - 1);
        }
    }

    /// <summary>前缓冲拷出去了。</summary>
    public void ClearFrontDirty() => (_dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1) = (0, 0, 0, 0);

    /// <summary>边长的上下限(<see cref="Resize" /> 按它夹)。</summary>
    private const int MaxSide = 16384;

    /// <summary>
    /// 一块 <paramref name="width" /> × <paramref name="height" /> 的表面占的字节(记账用,与 <see cref="Resize" /> 一样先夹尺寸):
    /// 每像素前缓冲 4、后缓冲 4(双缓冲时)、深度 4、模板 1。
    /// </summary>
    public static long BytesFor(int width, int height, bool doubleBuffered) =>
        (long)Math.Clamp(width, 1, MaxSide) * Math.Clamp(height, 1, MaxSide) * (doubleBuffered ? 13 : 9);

    /// <summary>尺寸跟随 X 可绘对象;变了就重新分配(内容未定义,这里清零)。</summary>
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, MaxSide);
        height = Math.Clamp(height, 1, MaxSide);
        if (width == Width && height == Height)
        {
            return;
        }
        Width = width;
        Height = height;
        ClearFrontDirty();
        Front = new uint[width * height];
        Back = DoubleBuffered ? new uint[width * height] : null;
        Depth = new float[width * height];
        Array.Fill(Depth, 1f);
        Stencil = new byte[width * height];
    }

    /// <summary>颜色缓冲:BACK 只在双缓冲时存在,否则落到前缓冲。</summary>
    public uint[] Color(bool back) => back && Back is not null ? Back : Front;

    /// <summary>glXSwapBuffers:前后缓冲互换(交换后后缓冲的内容未定义 —— 这里就是交换前的前缓冲)。</summary>
    public void Swap()
    {
        if (Back is not null)
        {
            (Front, Back) = (Back, Front);
            MarkFrontDirty(new XRect(0, 0, Width, Height));
        }
    }
}
