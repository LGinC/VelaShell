// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Composite Extension, Version 0.4 —— QueryVersion 0、RedirectWindow 1、RedirectSubwindows 2、UnredirectWindow 3、
//   UnredirectSubwindows 4(同一窗口只能有一个客户端做 Manual 重定向,否则 BadAccess)、
//   CreateRegionFromBorderClip 5、NameWindowPixmap 6、GetOverlayWindow 7、ReleaseOverlayWindow 8
//
//   rootless 下每个顶层本来就有自己的像素缓冲,Composite 的「重定向到离屏」是天然状态:
//   重定向只做登记与互斥检查,NameWindowPixmap 对顶层窗口给出与它共享像素的像素图(之后的绘制照样看得到;
//   窗口改尺寸、重新映射或销毁时窗口换一块新缓冲,像素图留着旧的 —— 规范:窗口每次映射或改尺寸都换一个新的像素图),
//   对子窗口给出一份当下内容的拷贝。叠加窗口不挂进窗口树,不会被宿主当成顶层窗口。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>Composite 叠加窗口(服务端自己的 ID,不挂进窗口树)。</summary>
    private const uint OverlayWindowId = 0x46;

    private XWindow? _overlayWindow;

    /// <summary>Manual 重定向的持有者:窗口 → 客户端(同一窗口只能有一个)。</summary>
    private readonly Dictionary<XWindow, XClient> _manualRedirects = [];

    private void CompositeExtension(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                c.Reply(0, w => w.U32(0).U32(4).Zero(16));
                break;
            case 1:   // RedirectWindow
            case 2:   // RedirectSubwindows
                {
                    XWindow window = Window(r.U32());
                    byte update = r.U8();
                    if (update > 1)
                    {
                        throw new XProtocolError(XErrorCode.Value, update);
                    }
                    if (window.IsRoot && r.Data == 1)
                    {
                        throw new XProtocolError(XErrorCode.Match);   // 根窗口不能被重定向
                    }
                    if (update == 1)
                    {
                        if (_manualRedirects.TryGetValue(window, out XClient? holder) && !ReferenceEquals(holder, c))
                        {
                            throw new XProtocolError(XErrorCode.Access);
                        }
                        _manualRedirects[window] = c;
                    }
                    break;
                }
            case 3:   // UnredirectWindow
            case 4:   // UnredirectSubwindows
                {
                    XWindow window = Window(r.U32());
                    if (_manualRedirects.TryGetValue(window, out XClient? holder) && ReferenceEquals(holder, c))
                    {
                        _manualRedirects.Remove(window);
                    }
                    break;
                }
            case 5:   // CreateRegionFromBorderClip:窗口外框露在外面的部分(窗口坐标,原点在内区左上角)
                {
                    uint regionId = r.U32();
                    XWindow window = Window(r.U32());
                    Region clip = VisibleOuter(window);
                    if (!window.IsTopLevel)
                    {
                        (int ox, int oy) = window.OffsetInTopLevel();
                        clip.Translate(-ox, -oy);
                    }
                    else if (window.IsViewable)
                    {
                        clip = new Region(new XRect(-window.BorderWidth, -window.BorderWidth,
                            window.Width + (2 * window.BorderWidth), window.Height + (2 * window.BorderWidth)));
                    }
                    AddResource(c, new XRegionResource(regionId, c, clip));
                    break;
                }
            case 6:   // NameWindowPixmap
                {
                    XWindow window = Window(r.U32());
                    uint pixmapId = r.U32();
                    if (!window.IsViewable || window.IsRoot || window.TopLevel is not { Buffer: { } buffer })
                    {
                        throw new XProtocolError(XErrorCode.Match);   // 只有映射着的(即已重定向的)窗口才有像素图
                    }
                    XPixmap pixmap;
                    if (window.IsTopLevel)
                    {
                        pixmap = new XPixmap(pixmapId, c, buffer);   // 与顶层共享像素,直到窗口改尺寸、重新映射或销毁
                        AddResource(c, pixmap);
                        if (!_namedWindowBuffers.TryGetValue(buffer, out (XWindow Window, List<XPixmap> Pixmaps) named))
                        {
                            _namedWindowBuffers[buffer] = named = (window, []);
                        }
                        named.Pixmaps.Add(pixmap);
                        break;
                    }
                    else
                    {
                        RequireMemory(c, ResourceOverheadBytes + PixelBytes(window.Width, window.Height));
                        pixmap = new XPixmap(pixmapId, c, window.Width, window.Height, window.Depth);
                        (int ox, int oy) = window.OffsetInTopLevel();
                        PixelBuffer.CopyRect(buffer, ox, oy, pixmap.Buffer, 0, 0, window.Width, window.Height);
                    }
                    AddResource(c, pixmap);
                    break;
                }
            case 7:   // GetOverlayWindow
                {
                    _ = Window(r.U32());
                    _overlayWindow ??= CreateOverlayWindow();
                    c.Reply(0, w => w.U32(OverlayWindowId).Zero(20));
                    break;
                }
            case 8:   // ReleaseOverlayWindow
                _ = Window(r.U32());
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private XWindow CreateOverlayWindow()
    {
        XWindow overlay = new(OverlayWindowId, null, Root)
        {
            Width = Root.Width,
            Height = Root.Height,
            Depth = 24,
            Visual = RootVisualId,
            Colormap = DefaultColormapId,
            Mapped = true,
        };
        _resources[OverlayWindowId] = overlay;
        return overlay;
    }

    /// <summary>客户端断开:它做的 Manual 重定向随之解除。</summary>
    private void CleanupComposite(XClient client)
    {
        foreach (XWindow w in _manualRedirects.Where(kv => ReferenceEquals(kv.Value, client)).Select(kv => kv.Key).ToArray())
        {
            _manualRedirects.Remove(w);
        }
    }

    /// <summary>窗口销毁:它的重定向登记随之消失;NameWindowPixmap 给出的像素图留着销毁前的内容(规范:像素图一直有效,直到释放)。</summary>
    private void CleanupComposite(XWindow window)
    {
        _manualRedirects.Remove(window);
        ReleaseNamedWindowPixmaps(window);
    }

    /// <summary>NameWindowPixmap 给顶层的像素图包住的那块顶层缓冲 → 那个顶层与这些像素图(按缓冲索引,画进像素图时能找到顶层)。</summary>
    private readonly Dictionary<PixelBuffer, (XWindow Window, List<XPixmap> Pixmaps)> _namedWindowBuffers = [];

    /// <summary>
    /// 顶层的缓冲要改尺寸、重新分配或丢掉之前调:之前 NameWindowPixmap 给出的像素图从此独占这块旧缓冲 —— 规范说窗口每次映射或改尺寸
    /// 都换一块新的像素图,旧的保持原样直到释放(原先缓冲就地 Resize,像素图的宽高跟着窗口变)。这块缓冲改记在第一个像素图的客户端名下,
    /// 它释放时退还。返回是否有这样的像素图(有的话调用方要给窗口换一块新缓冲,不能再就地改)。
    /// </summary>
    private bool ReleaseNamedWindowPixmaps(XWindow window)
    {
        if (window.Buffer is not { } buffer || !_namedWindowBuffers.Remove(buffer, out (XWindow Window, List<XPixmap> Pixmaps) named))
        {
            return false;
        }
        if (named.Pixmaps.Count > 0)
        {
            XPixmap holder = named.Pixmaps[0];
            long bytes = PixelBytes(buffer.Width, buffer.Height);
            ChargeMemory(holder.Owner, bytes, force: true);
            holder.Charged += bytes;   // 释放(RemoveResource)时连同固定开销一起退还
        }
        return true;
    }

    /// <summary>画进 NameWindowPixmap 给的像素图:还与顶层共享缓冲时就是画进了顶层 —— 返回那个顶层,好让宿主收到损伤。</summary>
    private XWindow? TopLevelSharing(XPixmap pixmap) =>
        !pixmap.OwnsBuffer && _namedWindowBuffers.TryGetValue(pixmap.Buffer, out (XWindow Window, List<XPixmap> Pixmaps) named)
            ? named.Window
            : null;

    /// <summary>像素图释放了:从共享登记里去掉。</summary>
    private void CompositePixmapFreed(XPixmap pixmap)
    {
        if (!pixmap.OwnsBuffer && _namedWindowBuffers.TryGetValue(pixmap.Buffer, out (XWindow Window, List<XPixmap> Pixmaps) named))
        {
            named.Pixmaps.Remove(pixmap);
            if (named.Pixmaps.Count == 0)
            {
                _namedWindowBuffers.Remove(pixmap.Buffer);
            }
        }
    }
}
