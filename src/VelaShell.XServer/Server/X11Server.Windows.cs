// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「CreateWindow」「ChangeWindowAttributes」「GetWindowAttributes」
//   「DestroyWindow」「DestroySubwindows」「ChangeSaveSet」「ReparentWindow」「MapWindow」「MapSubwindows」
//   「UnmapWindow」「UnmapSubwindows」「ConfigureWindow」(含 stack-mode 与 SubstructureRedirect 的改道)
//   「CirculateWindow」「GetGeometry」「QueryTree」「TranslateCoordinates」;
//   第 10 节「Events」里 CreateNotify / DestroyNotify / MapNotify / MapRequest / UnmapNotify / ReparentNotify /
//   ConfigureNotify / ConfigureRequest / CirculateNotify 的字段

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>
    /// 窗口嵌套层数上限(顶层为 1)。协议没有规定上限,但逐层往上走的可见区域计算每条绘图请求都要做一遍,
    /// 无限嵌套就能把执行线程拖住;真实程序里嵌套最深的 Xt / Motif 也不过几十层。超出回 BadAlloc(协议允许任何请求回 Alloc)。
    /// </summary>
    internal const int MaxWindowDepth = 256;

    /// <summary>每个客户端同时拥有的窗口数上限。每个窗口都带着几张事件 / 属性 / 抓取表,不设上限一个客户端就能把内存吃光。</summary>
    internal const int MaxWindowsPerClient = 32768;

    private void CreateWindow(XClient c, XRequestReader r)
    {
        byte depth = r.Data;
        uint id = r.U32();
        XWindow parent = Window(r.U32());
        short x = r.I16(), y = r.I16();
        ushort width = r.U16(), height = r.U16(), border = r.U16(), cls = r.U16();
        uint visual = r.U32();
        uint mask = r.U32();

        if (width == 0 || height == 0)
        {
            throw new XProtocolError(XErrorCode.Value, 0);
        }
        if (cls > 2)
        {
            throw new XProtocolError(XErrorCode.Value, cls);
        }
        if (cls == 0)
        {
            cls = parent.Class;
        }
        if (cls == 1 && parent.IsInputOnly)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (c.WindowCount >= MaxWindowsPerClient || parent.Level >= MaxWindowDepth)
        {
            throw new XProtocolError(XErrorCode.Alloc);
        }

        XWindow window = new(id, c, parent)
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            BorderWidth = border,
            Class = cls,
            BorderPixel = parent.BorderPixel,
            Colormap = parent.Colormap,
        };

        if (cls == 2)
        {
            if (depth != 0 || border != 0)
            {
                throw new XProtocolError(XErrorCode.Match);
            }
            window.Depth = 0;
            window.Visual = visual == 0 ? parent.Visual : visual;
        }
        else
        {
            window.Depth = depth == 0 ? parent.Depth : depth;
            window.Visual = visual == 0 ? parent.Visual : visual;
            bool ok = (window.Depth == 24 && window.Visual == RootVisualId) || (window.Depth == 32 && window.Visual == ArgbVisualId);
            if (!ok)
            {
                throw new XProtocolError(XErrorCode.Match);
            }
        }

        ApplyWindowAttributes(c, window, mask, r);
        AddResource(c, window);
        c.WindowCount++;
        parent.Children.Add(window);
        InvalidateVisibility();

        DeliverToSelectors(parent, XEventMask.SubstructureNotify, client =>
            client.Event(XEventCode.CreateNotify, 0, w => w
                .U32(parent.Id).U32(window.Id).I16(x).I16(y).U16(width).U16(height).U16(border)
                .Bool(window.OverrideRedirect)));
    }

    private void ChangeWindowAttributes(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        uint mask = r.U32();
        ApplyWindowAttributes(c, window, mask, r);
        if ((mask & (uint)XWindowAttrMask.Cursor) != 0)
        {
            UpdateCursor();
        }
    }

    private void ApplyWindowAttributes(XClient c, XWindow window, uint mask, XRequestReader r)
    {
        const uint inputOnlyAllowed = (uint)(XWindowAttrMask.WinGravity | XWindowAttrMask.OverrideRedirect
            | XWindowAttrMask.EventMask | XWindowAttrMask.DoNotPropagateMask | XWindowAttrMask.Cursor);
        if (window.IsInputOnly && (mask & ~inputOnlyAllowed) != 0)
        {
            throw new XProtocolError(XErrorCode.Match);
        }

        for (int bit = 0; bit < 15; bit++)
        {
            if ((mask & (1u << bit)) == 0)
            {
                continue;
            }
            uint v = r.U32();
            switch ((XWindowAttrMask)(1u << bit))
            {
                case XWindowAttrMask.BackgroundPixmap:
                    window.BackgroundPixmap = v;
                    window.BackgroundPixel = null;
                    window.BackgroundTile = v > 1 ? Lookup<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v) : null;
                    break;
                case XWindowAttrMask.BackgroundPixel:
                    window.BackgroundPixmap = XWindow.BackgroundPixmapNone;
                    window.BackgroundTile = null;
                    window.BackgroundPixel = v;
                    break;
                case XWindowAttrMask.BorderPixmap:
                    window.BorderTile = v == 0 ? window.Parent?.BorderTile : Lookup<XPixmap>(v) ?? throw new XProtocolError(XErrorCode.Pixmap, v);
                    break;
                case XWindowAttrMask.BorderPixel:
                    window.BorderTile = null;
                    window.BorderPixel = v;
                    break;
                case XWindowAttrMask.BitGravity:
                    window.BitGravity = (byte)v;
                    break;
                case XWindowAttrMask.WinGravity:
                    window.WinGravity = (byte)v;
                    break;
                case XWindowAttrMask.BackingStore:
                    window.BackingStore = (byte)v;
                    break;
                case XWindowAttrMask.BackingPlanes:
                    window.BackingPlanes = v;
                    break;
                case XWindowAttrMask.BackingPixel:
                    window.BackingPixel = v;
                    break;
                case XWindowAttrMask.OverrideRedirect:
                    window.OverrideRedirect = v != 0;
                    break;
                case XWindowAttrMask.SaveUnder:
                    window.SaveUnder = v != 0;
                    break;
                case XWindowAttrMask.EventMask:
                    SelectEvents(c, window, v);
                    break;
                case XWindowAttrMask.DoNotPropagateMask:
                    window.DoNotPropagateMask = (ushort)v;
                    break;
                case XWindowAttrMask.Colormap:
                    window.Colormap = v == 0 ? window.Parent?.Colormap ?? DefaultColormapId : v;
                    break;
                case XWindowAttrMask.Cursor:
                    window.Cursor = v == 0 ? null : Lookup<XCursorResource>(v) ?? throw new XProtocolError(XErrorCode.Cursor, v);
                    break;
            }
        }
    }

    /// <summary>设置某客户端在窗口上选的事件。三种「独占」事件同一时间只能有一个客户端选(否则 BadAccess)。</summary>
    private static void SelectEvents(XClient c, XWindow window, uint mask)
    {
        if ((mask & ~(uint)XEventMask.AllValid) != 0)
        {
            throw new XProtocolError(XErrorCode.Value, mask);
        }
        if (window.IsRoot && (mask & (uint)XEventMask.SubstructureRedirect) != 0)
        {
            // 窗口管理器是服务端(宿主)自己:根窗口的 SubstructureRedirect 一直有人占着,与真实桌面上已有窗口管理器时一样回 BadAccess。
            // 原先谁都选得上 —— 远端误跑 openbox / xfwm4,所有会话的新窗口都变成发给它的 MapRequest、被它套进自己的外框。
            throw new XProtocolError(XErrorCode.Access);
        }
        foreach (XEventMask exclusive in (XEventMask[])[XEventMask.SubstructureRedirect, XEventMask.ResizeRedirect, XEventMask.ButtonPress])
        {
            if ((mask & (uint)exclusive) == 0)
            {
                continue;
            }
            foreach ((XClient other, uint selected) in window.EventSelections)
            {
                if (!ReferenceEquals(other, c) && (selected & (uint)exclusive) != 0)
                {
                    throw new XProtocolError(XErrorCode.Access);
                }
            }
        }
        if (mask == 0)
        {
            window.EventSelections.Remove(c);
        }
        else
        {
            window.EventSelections[c] = mask;
        }
    }

    private void GetWindowAttributes(XClient c, XRequestReader r)
    {
        XWindow w0 = Window(r.U32());
        uint yours = w0.EventSelections.GetValueOrDefault(c);
        c.Reply(w0.BackingStore, w => w
            .U32(w0.Visual).U16(w0.Class).U8(w0.BitGravity).U8(w0.WinGravity)
            .U32(w0.BackingPlanes).U32(w0.BackingPixel)
            .Bool(w0.SaveUnder).Bool(true).U8(w0.MapState).Bool(w0.OverrideRedirect)
            .U32(w0.Colormap).U32(w0.AllEventMasks).U32(yours).U16(w0.DoNotPropagateMask).Zero(2));
    }

    // ------------------------------------------------------------------ 销毁

    private void DestroyWindow(XClient c, XRequestReader r) => Destroy(Window(r.U32()));

    private void DestroySubwindows(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        for (int i = window.Children.Count - 1; i >= 0; i--)
        {
            if (i < window.Children.Count)
            {
                Destroy(window.Children[i]);
            }
        }
    }

    internal void Destroy(XWindow window)
    {
        if (window.IsRoot || window.Id == SelectionWindowId || !_resources.ContainsKey(window.Id))
        {
            return;
        }
        if (window.Mapped)
        {
            Unmap(window);
        }
        DestroyTree(window);
        window.Parent?.Children.Remove(window);
        InvalidateVisibility();
        UpdatePointerWindow();
    }

    /// <summary>
    /// 从最深的后代开始逐个发 DestroyNotify 并释放(协议规定:先下级后上级)。显式栈做后序遍历、不递归 ——
    /// 客户端断开时的清理也走这里,递归的话一棵够深的树就能把执行线程的栈压爆、整个进程退出。
    /// 顺序与递归写法相同:兄弟按堆叠顺序从下到上,每个子窗口的子表在轮到它时才取。
    /// </summary>
    private void DestroyTree(XWindow window)
    {
        Stack<(XWindow Window, XWindow[] Children, int Next)> pending = new();
        pending.Push((window, [.. window.Children], 0));
        while (pending.TryPop(out (XWindow Window, XWindow[] Children, int Next) item))
        {
            if (item.Next < item.Children.Length)
            {
                pending.Push(item with { Next = item.Next + 1 });
                XWindow child = item.Children[item.Next];
                pending.Push((child, [.. child.Children], 0));
                continue;
            }
            DestroyOne(item.Window);
        }
    }

    /// <summary>销毁一个窗口本身(它的后代已经销毁过了)。</summary>
    private void DestroyOne(XWindow window)
    {
        DeliverStructure(window, XEventCode.DestroyNotify, 0, w => w.U32(window.Id));
        RemoveResource(window.Id);
        if (window.Owner is { } creator)
        {
            creator.WindowCount--;
        }
        foreach (Extension extension in _extensionList)
        {
            extension.WindowDestroyed?.Invoke(window);
        }
        CleanupEwmh(window);
        foreach ((uint atom, (XWindow Window, XClient? Client, uint Time) owner) in _selections.ToArray())
        {
            if (ReferenceEquals(owner.Window, window))
            {
                _selections.Remove(atom);
                NotifySelectionChange(atom, 1, 0, owner.Time);
                OnSelectionOwnerLost(atom);
            }
        }
        if (ReferenceEquals(_focus, window))
        {
            RevertFocus(window);
        }
        if (ReferenceEquals(PointerGrab?.Window, window))
        {
            PointerGrab = null;
        }
        if (ReferenceEquals(KeyboardGrab?.Window, window))
        {
            KeyboardGrab = null;
        }
        if (_topLevelHandles.Remove(window, out XTopLevelWindow? handle))
        {
            SetMapped(handle, false);
        }
        _damage.Remove(window);
        window.Buffer = null;
        SyncBufferCharge(window);
        foreach (XClient client in _clients.Values)
        {
            client.SaveSet.Remove(window.Id);
        }
        // 属性随窗口而去:写它们的客户端的账退还(最后做 —— 上面的清理还可能读它们)。
        foreach (XProperty property in window.Properties.Values)
        {
            ReleaseProperty(property);
        }
        window.Properties.Clear();
    }

    /// <summary>
    /// save-set:客户端断开时(见 <see cref="ProcessSaveSet" />)把挂在它的窗口下面的这些窗口还回去。用它的是 reparent 别人窗口的程序 ——
    /// XEmbed 的嵌入方(托盘、插件宿主)、窗口管理器。窗口不能是这个客户端自己建的(BadMatch)。
    /// </summary>
    private void ChangeSaveSet(XClient c, XRequestReader r)
    {
        byte mode = r.Data;
        XWindow window = Window(r.U32());
        ChangeSaveSet(c, window, mode == 0, toRoot: false, map: true);
    }

    /// <summary>核心与 XFIXES 的 ChangeSaveSet 共用;<paramref name="toRoot" /> 与 <paramref name="map" /> 是 XFIXES 的 target / map。</summary>
    internal static void ChangeSaveSet(XClient c, XWindow window, bool insert, bool toRoot, bool map)
    {
        if (ReferenceEquals(window.Owner, c) || window.IsRoot)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (insert)
        {
            c.SaveSet[window.Id] = (toRoot, map);
        }
        else
        {
            c.SaveSet.Remove(window.Id);
        }
    }

    /// <summary>
    /// 协议第 10 节「Connection Close」:save-set 里的每个窗口,若在这个客户端建的某个窗口之下,就 reparent 到最近的一个祖先,
    /// 使它不再在这个客户端建的任何窗口之下(根坐标不变;XFIXES 可以指定挂到根窗口);没映射的补映射(XFIXES 可以指定不补)。
    /// 在销毁资源之前做,不论 close-down mode。原先只登记不生效:嵌入方(或远端窗口管理器)一退出,外框被销毁,别人的窗口跟着被销毁。
    /// </summary>
    private void ProcessSaveSet(XClient client)
    {
        foreach ((uint id, (bool toRoot, bool map)) in client.SaveSet.ToArray())
        {
            if (Lookup<XWindow>(id) is not { Parent: not null } window)
            {
                continue;
            }
            XWindow? outermost = null;   // 这个客户端建的、最靠近根的那个祖先
            for (XWindow? w = window.Parent; w is { IsRoot: false }; w = w.Parent)
            {
                if (ReferenceEquals(w.Owner, client))
                {
                    outermost = w;
                }
            }
            if (outermost?.Parent is { } keep)
            {
                XWindow target = toRoot ? Root : keep;
                (int ax, int ay) = window.Parent.AbsoluteInner();
                (int tx, int ty) = target.AbsoluteInner();
                Reparent(window, target, (short)Math.Clamp(ax + window.X - tx, short.MinValue, short.MaxValue),
                    (short)Math.Clamp(ay + window.Y - ty, short.MinValue, short.MaxValue));
            }
            if (map && !window.Mapped)
            {
                Map(null, window);
            }
        }
        client.SaveSet.Clear();
    }

    // ------------------------------------------------------------------ 映射

    private void MapWindow(XClient c, XRequestReader r) => Map(c, Window(r.U32()));

    internal void Map(XClient? requester, XWindow window)
    {
        if (window.Mapped && requester is not null && window.IsTopLevel && !window.OverrideRedirect
            && _topLevelHandles.TryGetValue(window, out XTopLevelWindow? iconic) && (iconic.Snapshot.States & XWindowStates.Hidden) != 0)
        {
            // ICCCM §4.1.4:从 IconicState 回到 NormalState,客户端 map 窗口(Tk 的 wm deiconify、Emacs 的 make-frame-visible、
            // xdotool windowmap)。最小化时窗口在 X 里仍映射着,这一下原先是空操作;转成「去掉 Hidden」请宿主还原。
            _host.WindowManagerRequested(new XStateChangeRequest(iconic, XWindowStates.None, XWindowStates.Hidden));
            return;
        }
        if (window.Mapped || window.IsRoot)
        {
            return;
        }
        if (!window.OverrideRedirect && window.Parent is { } parent
            && RedirectClient(parent, XEventMask.SubstructureRedirect) is { } wm && !ReferenceEquals(wm, requester))
        {
            wm.Event(XEventCode.MapRequest, 0, w => w.U32(parent.Id).U32(window.Id));
            return;
        }
        if (requester is not null && window.IsTopLevel)
        {
            RequireBufferMemory(window, window.Width, window.Height);   // 映射时才建缓冲:先核账(xs_plan X-2)
        }

        window.Mapped = true;
        InvalidateVisibility();
        DeliverStructure(window, XEventCode.MapNotify, 0, w => w.U32(window.Id).Bool(window.OverrideRedirect));

        if (!window.IsViewable)
        {
            return;
        }
        if (window.IsTopLevel)
        {
            window.Buffer ??= new Drawing.PixelBuffer(window.Width, window.Height, window.Depth == 32 ? (byte)32 : (byte)24);
            window.Buffer.Resize(window.Width, window.Height);
            SyncBufferCharge(window);
            XTopLevelWindow handle = HandleFor(window);
            RefreshSnapshot(window, handle);
            SetMapped(handle, true);
            ExposeWindowTree(window, new Drawing.Region(window.Buffer.Bounds));
            _host.TopLevelMapped(handle);
            OnTopLevelMappedEwmh(window);
        }
        else
        {
            ExposeWindowTree(window, VisibleOuter(window));   // 映射只露出它自己与它的下级
        }
        UpdatePointerWindow();
    }

    private void MapSubwindows(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        for (int i = window.Children.Count - 1; i >= 0; i--)
        {
            Map(c, window.Children[i]);
        }
    }

    private void UnmapWindow(XRequestReader r) => Unmap(Window(r.U32()));

    internal void Unmap(XWindow window, bool fromConfigure = false)
    {
        if (!window.Mapped || window.IsRoot)
        {
            return;
        }
        bool wasViewable = window.IsViewable;
        Drawing.Region old = wasViewable ? VisibleOuter(window) : new Drawing.Region();
        window.Mapped = false;
        InvalidateVisibility();
        DeliverStructure(window, XEventCode.UnmapNotify, 0, w => w.U32(window.Id).Bool(fromConfigure));

        if (window.IsTopLevel)
        {
            if (_topLevelHandles.TryGetValue(window, out XTopLevelWindow? handle))
            {
                SetMapped(handle, false);
                _host.TopLevelUnmapped(handle);
                OnTopLevelUnmappedEwmh(window);
            }
        }
        else if (wasViewable)
        {
            ExposeWindowTree(window.Parent!, old);   // 露出来的是父窗口、下面的兄弟及其子树:都在父窗口这棵子树里
        }
        if (_focus is { } focus && (ReferenceEquals(focus, window) || focus.IsDescendantOf(window)))
        {
            RevertFocus(focus);
        }
        ReleaseUnviewableGrabs();
        UpdatePointerWindow();
    }

    private void UnmapSubwindows(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        foreach (XWindow child in window.Children.ToArray())
        {
            Unmap(child);
        }
    }

    // ------------------------------------------------------------------ 配置

    private void ConfigureWindow(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        ushort mask = r.U16();
        r.Skip(2);
        int x = window.X, y = window.Y, width = window.Width, height = window.Height, border = window.BorderWidth;
        XWindow? sibling = null;
        int stackMode = -1;
        for (int bit = 0; bit < 7; bit++)
        {
            if ((mask & (1 << bit)) == 0)
            {
                continue;
            }
            uint v = r.U32();
            switch ((XConfigMask)(1 << bit))
            {
                case XConfigMask.X: x = (short)v; break;
                case XConfigMask.Y: y = (short)v; break;
                case XConfigMask.Width: width = (ushort)v; break;
                case XConfigMask.Height: height = (ushort)v; break;
                case XConfigMask.BorderWidth: border = (ushort)v; break;
                case XConfigMask.Sibling: sibling = Window(v); break;
                case XConfigMask.StackMode: stackMode = (byte)v; break;
            }
        }
        if (width == 0 || height == 0)
        {
            throw new XProtocolError(XErrorCode.Value, 0);
        }
        if (sibling is not null && (stackMode < 0 || !ReferenceEquals(sibling.Parent, window.Parent) || ReferenceEquals(sibling, window)))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (window.IsInputOnly && (mask & (ushort)XConfigMask.BorderWidth) != 0 && border != 0)
        {
            throw new XProtocolError(XErrorCode.Match);
        }

        if (!window.OverrideRedirect && window.Parent is { } parent
            && RedirectClient(parent, XEventMask.SubstructureRedirect) is { } wm && !ReferenceEquals(wm, c))
        {
            wm.Event(XEventCode.ConfigureRequest, (byte)Math.Max(0, stackMode), w => w
                .U32(parent.Id).U32(window.Id).U32(sibling?.Id ?? 0)
                .I16(x).I16(y).U16((ushort)width).U16((ushort)height).U16((ushort)border).U16(mask));
            return;
        }

        if (window.IsTopLevel && window.Buffer is not null && (width != window.Width || height != window.Height))
        {
            RequireBufferMemory(window, width, height);   // 缓冲要跟着变大:先核账(xs_plan X-2)
        }
        Configure(window, x, y, width, height, border, sibling, stackMode);
        if (stackMode == 0 && sibling is null && window.IsTopLevel && window.Mapped && !window.OverrideRedirect
            && _topLevelHandles.TryGetValue(window, out XTopLevelWindow? handle))
        {
            // 客户端把顶层抬到最上面(XRaiseWindow、XMapRaised、Java 的 toFront):原生窗口的次序归宿主管,请它照办。
            _host.WindowManagerRequested(new XRaiseRequest(handle));
        }
    }

    /// <summary>真正改几何与堆叠,发 ConfigureNotify、重画露出的部分、通知宿主。</summary>
    internal void Configure(XWindow window, int x, int y, int width, int height, int border, XWindow? sibling, int stackMode)
    {
        if (window.IsRoot)
        {
            return;
        }
        bool viewable = window.IsViewable;
        Drawing.Region old = viewable && !window.IsTopLevel ? VisibleOuter(window) : new Drawing.Region();
        bool resized = width != window.Width || height != window.Height;
        bool moved = x != window.X || y != window.Y;

        window.X = x;
        window.Y = y;
        window.Width = width;
        window.Height = height;
        window.BorderWidth = border;
        if (resized)
        {
            ResizeBackBuffer(window);
        }
        if ((resized || moved) && _presentContexts.Count != 0)
        {
            NotifyPresentConfigure(window);
        }
        if (stackMode >= 0 && window.Parent is { } parent)
        {
            Restack(parent, window, sibling, stackMode);
            if (window.IsTopLevel && window.Mapped)
            {
                UpdateClientLists();
            }
        }
        InvalidateVisibility();

        XWindow? above = window.Parent is { } p && p.Children.IndexOf(window) is var i and > 0 ? p.Children[i - 1] : null;
        DeliverStructure(window, XEventCode.ConfigureNotify, 0, w => w
            .U32(window.Id).U32(above?.Id ?? 0).I16(x).I16(y).U16((ushort)width).U16((ushort)height)
            .U16((ushort)border).Bool(window.OverrideRedirect));

        if (!viewable)
        {
            return;
        }
        if (window.IsTopLevel)
        {
            if (resized && window.Buffer is { } buffer)
            {
                buffer.Resize(width, height);
                SyncBufferCharge(window);
                // bit-gravity 默认 Forget:整窗重画(并发 Expose)。NorthWest 时只画新露出的部分。
                Drawing.Region exposed = new(buffer.Bounds);
                ExposeWindowTree(window, exposed);
            }
            if (resized || moved)
            {
                RefreshTopLevel(window);
            }
        }
        else if (window.TopLevel is { } top)
        {
            ExposeWindowTree(top, old.Union(VisibleOuter(window)));
        }
        UpdatePointerWindow();
    }

    /// <summary>stack-mode:0 Above,1 Below,2 TopIf,3 BottomIf,4 Opposite。后三种按遮挡判断太贵,按 Above / Below 近似。</summary>
    private static void Restack(XWindow parent, XWindow window, XWindow? sibling, int stackMode)
    {
        List<XWindow> list = parent.Children;
        list.Remove(window);
        bool above = stackMode is 0 or 2 or 4;
        if (sibling is null)
        {
            if (above)
            {
                list.Add(window);
            }
            else
            {
                list.Insert(0, window);
            }
            return;
        }
        int index = list.IndexOf(sibling);
        list.Insert(above ? index + 1 : index, window);
    }

    private void CirculateWindow(XRequestReader r)
    {
        byte direction = r.Data;
        XWindow window = Window(r.U32());
        List<XWindow> mapped = [.. window.Children.Where(w => w.Mapped)];
        if (mapped.Count < 2)
        {
            return;
        }
        XWindow moving = direction == 0 ? mapped[0] : mapped[^1];
        window.Children.Remove(moving);
        if (direction == 0)
        {
            window.Children.Add(moving);
        }
        else
        {
            window.Children.Insert(0, moving);
        }
        InvalidateVisibility();
        // 附录 B:event、window、4 字节不用,place 在第 16 字节(Top 0、Bottom 1)。
        DeliverStructure(moving, XEventCode.CirculateNotify, 0, w => w.U32(moving.Id).U32(0).U8(direction == 0 ? (byte)0 : (byte)1));
        if (moving.TopLevel is { } top && !moving.IsTopLevel)
        {
            ExposeWindowTree(top, VisibleOuter(moving));
        }
    }

    private void ReparentWindow(XRequestReader r)
    {
        XWindow window = Window(r.U32());
        XWindow parent = Window(r.U32());
        short x = r.I16(), y = r.I16();
        if (window.IsRoot || ReferenceEquals(parent, window) || parent.IsDescendantOf(window))
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (!window.IsInputOnly && parent.IsInputOnly)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (parent.Level + 1 + window.SubtreeHeight() > MaxWindowDepth)
        {
            throw new XProtocolError(XErrorCode.Alloc);   // 挪过去整棵子树就超过嵌套上限了
        }
        Reparent(window, parent, x, y);
    }

    /// <summary>ReparentWindow 的执行部分(参数已核对);save-set 收尾也用它。</summary>
    private void Reparent(XWindow window, XWindow parent, short x, short y)
    {
        bool wasMapped = window.Mapped;
        if (wasMapped)
        {
            Unmap(window);
        }
        XWindow oldParent = window.Parent!;
        bool wasTopLevel = window.IsTopLevel;
        oldParent.Children.Remove(window);
        parent.Children.Add(window);
        window.Parent = parent;
        window.X = x;
        window.Y = y;
        InvalidateVisibility();
        if (wasTopLevel && !window.IsTopLevel)
        {
            if (_topLevelHandles.Remove(window, out XTopLevelWindow? handle))
            {
                SetMapped(handle, false);
            }
            window.Buffer = null;
            SyncBufferCharge(window);
        }

        void Body(XWriter w) => w.U32(window.Id).U32(parent.Id).I16(x).I16(y).Bool(window.OverrideRedirect);
        DeliverToSelectors(window, XEventMask.StructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(window.Id); Body(w); }));
        DeliverToSelectors(oldParent, XEventMask.SubstructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(oldParent.Id); Body(w); }));
        DeliverToSelectors(parent, XEventMask.SubstructureNotify, c => c.Event(XEventCode.ReparentNotify, 0, w => { w.U32(parent.Id); Body(w); }));

        if (wasMapped)
        {
            Map(null, window);
        }
    }

    // ------------------------------------------------------------------ 查询

    private void GetGeometry(XClient c, XRequestReader r)
    {
        uint id = r.U32();
        switch (Lookup<XResource>(id))
        {
            case XWindow w0:
                c.Reply(w0.Depth, w => w.U32(Root.Id).I16(w0.X).I16(w0.Y).U16((ushort)w0.Width).U16((ushort)w0.Height)
                    .U16((ushort)w0.BorderWidth).Zero(10));
                break;
            case XPixmap p:
                c.Reply(p.Depth, w => w.U32(Root.Id).I16(0).I16(0).U16((ushort)p.Width).U16((ushort)p.Height).U16(0).Zero(10));
                break;
            default:
                throw new XProtocolError(XErrorCode.Drawable, id);
        }
    }

    private void QueryTree(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        c.Reply(0, w =>
        {
            w.U32(Root.Id).U32(window.Parent?.Id ?? 0).U16((ushort)window.Children.Count).Zero(14);
            foreach (XWindow child in window.Children)
            {
                w.U32(child.Id);
            }
        });
    }

    private void TranslateCoordinates(XClient c, XRequestReader r)
    {
        XWindow src = Window(r.U32());
        XWindow dst = Window(r.U32());
        short sx = r.I16(), sy = r.I16();
        (int ax, int ay) = src.AbsoluteInner();
        (int bx, int by) = dst.AbsoluteInner();
        int dx = ax + sx - bx, dy = ay + sy - by;
        uint child = 0;
        for (int i = dst.Children.Count - 1; i >= 0; i--)
        {
            XWindow ch = dst.Children[i];
            if (ch.Mapped && dx >= ch.X && dy >= ch.Y
                && dx < ch.X + ch.Width + (2 * ch.BorderWidth) && dy < ch.Y + ch.Height + (2 * ch.BorderWidth))
            {
                child = ch.Id;
                break;
            }
        }
        c.Reply(1, w => w.U32(child).I16(dx).I16(dy).Zero(16));
    }
}
