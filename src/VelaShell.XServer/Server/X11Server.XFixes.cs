// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Fixes Extension, Version 5.0 —— §5「Save Set processing changes」、§6「Selection Tracking」(SelectSelectionInput 与
//   XFixesSelectionNotify 的三种子类型)、§7「Cursor Image Monitoring」(SelectCursorInput、CursorNotify、GetCursorImage)、
//   §8「Region Objects」(CreateRegion… ExpandRegion、SetGCClipRegion、SetWindowShapeRegion)、
//   §9「Cursor Names」、§10「Region Expansion」、§11「Cursor Visibility」(HideCursor / ShowCursor)、§12「Pointer Barriers」、
//   附录「Protocol Encoding」(请求次操作码 0–32、事件、错误 BadRegion、BadBarrier —— 按 §8.2、§12.2 定义的先后编号)
//
//   实现到版本 5。指针屏障(v5)只登记不生效 —— 宿主的系统指针不归我们限制。

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>SelectSelectionInput 的登记:(客户端, 窗口, 选区) → 掩码。</summary>
    private readonly Dictionary<(XClient Client, XWindow Window, uint Selection), uint> _selectionInputs = [];

    /// <summary>SelectCursorInput 的登记:(客户端, 窗口) → 掩码。</summary>
    private readonly Dictionary<(XClient Client, XWindow Window), uint> _cursorInputs = [];

    /// <summary>HideCursor 的计数:(客户端, 窗口) → 次数。指针在这些窗口(含后代)里时不显示光标。</summary>
    private readonly Dictionary<(XClient Client, XWindow Window), int> _hiddenCursors = [];

    private uint _cursorSerial = 1;

    /// <summary>区域对象换一份新值:按新的块数对账(xs_plan X-2),记不下回 Alloc、区域不变。</summary>
    private void SetRegion(XRegionResource resource, Region value)
    {
        Recharge(resource, ResourceOverheadBytes + RegionBytes(value));
        resource.Region = value;
    }

    private XRegionResource RegionRes(uint id) =>
        Lookup<XRegionResource>(id) ?? throw new XProtocolError((XErrorCode)XFixesErrorBase, id);

    private static Region ReadRegionRects(XRequestReader r)
    {
        List<XRect> rects = [];
        while (r.Remaining >= 8)
        {
            rects.Add(new XRect(r.I16(), r.I16(), r.U16(), r.U16()));
        }
        return Region.FromRects(rects);
    }

    private void XFixes(XClient c, XRequestReader r)
    {
        switch (r.Data)
        {
            case 0:   // QueryVersion
                {
                    uint major = r.U32(), minor = r.U32();
                    (uint maj, uint min) = major >= 5 ? (5u, 0u) : (major, minor);
                    c.Reply(0, w => w.U32(maj).U32(min).Zero(16));
                    break;
                }
            case 1:   // ChangeSaveSet:mode(0 插入、1 删除)、target(0 最近的祖先、1 根窗口)、map(0 补映射、1 不补)、窗口
                {
                    byte mode = r.U8(), target = r.U8(), map = r.U8();
                    r.Skip(1);
                    XWindow window = Window(r.U32());
                    ChangeSaveSet(c, window, mode == 0, toRoot: target == 1, map: map == 0);
                    break;
                }
            case 2:   // SelectSelectionInput
                {
                    XWindow window = Window(r.U32());
                    uint selection = r.U32();
                    uint mask = r.U32();
                    CheckAtom(selection);
                    if (mask == 0)
                    {
                        _selectionInputs.Remove((c, window, selection));
                    }
                    else
                    {
                        _selectionInputs[(c, window, selection)] = mask;
                    }
                    break;
                }
            case 3:   // SelectCursorInput
                {
                    XWindow window = Window(r.U32());
                    uint mask = r.U32();
                    if (mask == 0)
                    {
                        _cursorInputs.Remove((c, window));
                    }
                    else
                    {
                        _cursorInputs[(c, window)] = mask;
                    }
                    break;
                }
            case 4:   // GetCursorImage:光标由宿主的系统光标画,这里给一个 1×1 透明像素与热点
                {
                    int px = Math.Max(0, _pointerX), py = Math.Max(0, _pointerY);
                    uint serial = _cursorSerial;
                    c.Reply(0, w => w.I16(px).I16(py).U16(1).U16(1).U16(0).U16(0).U32(serial).Zero(8).U32(0));
                    break;
                }
            case 5:   // CreateRegion
                {
                    uint id = r.U32();
                    AddResource(c, new XRegionResource(id, c, Exact(ReadRegionRects(r))));
                    break;
                }
            case 6:   // CreateRegionFromBitmap
                {
                    uint id = r.U32();
                    uint bitmapId = r.U32();
                    XPixmap bitmap = Lookup<XPixmap>(bitmapId) ?? throw new XProtocolError(XErrorCode.Pixmap, bitmapId);
                    if (bitmap.Depth != 1)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    AddResource(c, new XRegionResource(id, c, Exact(RegionFromBitmap(bitmap.Buffer))));
                    break;
                }
            case 7:   // CreateRegionFromWindow
                {
                    uint id = r.U32();
                    XWindow window = Window(r.U32());
                    byte kind = r.U8();
                    AddResource(c, new XRegionResource(id, c, EffectiveShape(window, kind)));
                    break;
                }
            case 8:   // CreateRegionFromGC
                {
                    uint id = r.U32();
                    XGc gc = Gc(r.U32());
                    if (gc.ClipPixmap is not null)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    AddResource(c, new XRegionResource(id, c, Exact(Region.FromRects(gc.ClipRects ?? []))));
                    break;
                }
            case 9:   // CreateRegionFromPicture
                {
                    uint id = r.U32();
                    AddResource(c, new XRegionResource(id, c, PictureClipRegion(r.U32())));
                    break;
                }
            case 10:  // DestroyRegion
                {
                    uint id = r.U32();
                    _ = RegionRes(id);
                    RemoveResource(id);
                    break;
                }
            case 11:  // SetRegion
                SetRegion(RegionRes(r.U32()), Exact(ReadRegionRects(r)));
                break;
            case 12:  // CopyRegion
                {
                    Region src = RegionRes(r.U32()).Region;
                    SetRegion(RegionRes(r.U32()), src.Clone());
                    break;
                }
            case 13:  // UnionRegion
            case 14:  // IntersectRegion
            case 15:  // SubtractRegion
                {
                    byte op = r.Data;
                    Region a = RegionRes(r.U32()).Region.Clone();
                    Region b = RegionRes(r.U32()).Region;
                    XRegionResource dst = RegionRes(r.U32());
                    SetRegion(dst, Exact(op switch
                    {
                        13 => a.Union(b),
                        14 => a.Intersect(b),
                        _ => a.Subtract(b),
                    }));
                    break;
                }
            case 16:  // InvertRegion:dst = bounds − src
                {
                    Region src = RegionRes(r.U32()).Region;
                    XRect bounds = new(r.I16(), r.I16(), r.U16(), r.U16());
                    SetRegion(RegionRes(r.U32()), Exact(new Region(bounds).Subtract(src)));
                    break;
                }
            case 17:  // TranslateRegion
                {
                    XRegionResource region = RegionRes(r.U32());
                    region.Region.Translate(r.I16(), r.I16());
                    break;
                }
            case 18:  // RegionExtents
                {
                    XRect extents = RegionRes(r.U32()).Region.Bounds;
                    SetRegion(RegionRes(r.U32()), new Region(extents));
                    break;
                }
            case 19:  // FetchRegion
                {
                    Region region = RegionRes(r.U32()).Region;
                    XRect e = region.Bounds;
                    List<XRect> rects = [.. region.Rects.OrderBy(x => x.Y).ThenBy(x => x.X)];
                    c.Reply(0, w =>
                    {
                        w.I16(e.X).I16(e.Y).U16((ushort)e.Width).U16((ushort)e.Height).Zero(16);
                        foreach (XRect rect in rects)
                        {
                            w.I16(rect.X).I16(rect.Y).U16((ushort)rect.Width).U16((ushort)rect.Height);
                        }
                    });
                    break;
                }
            case 20:  // SetGCClipRegion
                {
                    XGc gc = Gc(r.U32());
                    uint regionId = r.U32();
                    short x = r.I16(), y = r.I16();
                    gc.ClipPixmap = null;
                    gc.ClipXOrigin = x;
                    gc.ClipYOrigin = y;
                    gc.ClipRects = regionId == 0 ? null : [.. RegionRes(regionId).Region.Rects];
                    break;
                }
            case 21:  // SetWindowShapeRegion
                {
                    XWindow window = Window(r.U32());
                    byte kind = r.U8();
                    r.Skip(3);
                    short dx = r.I16(), dy = r.I16();
                    uint regionId = r.U32();
                    SetShape(window, kind, regionId == 0 ? null : RegionRes(regionId).Region.Clone().Translate(dx, dy));
                    break;
                }
            case 22:  // SetPictureClipRegion
                {
                    uint picture = r.U32();
                    uint regionId = r.U32();
                    short x = r.I16(), y = r.I16();
                    SetPictureClipRegion(picture, regionId == 0 ? null : RegionRes(regionId).Region.Clone(), x, y);
                    break;
                }
            case 23:  // SetCursorName:记下名字,宿主据此推出光标形状
                {
                    XCursorResource cursor = CursorRes(r.U32());
                    int length = r.U16();
                    r.Skip(2);
                    string name = r.String8(length);
                    if (name.Length != 0)
                    {
                        InternForClient(name);   // 规范:「interns name as an atom」—— 与 InternAtom 同一套上限(原先在 GetCursorName 时不设限地建)
                    }
                    SetCursorName(cursor, name);
                    break;
                }
            case 26:  // ChangeCursor
            case 27:  // ChangeCursorByName
                break;
            case 24:  // GetCursorName
                {
                    string name = CursorRes(r.U32()).Name ?? "";
                    uint atom = name.Length == 0 ? 0 : _atomsByName.GetValueOrDefault(name);   // SetCursorName 时已经建好
                    byte[] bytes = XWire.Latin1.GetBytes(name);
                    c.Reply(0, w => w.U32(atom).U16((ushort)bytes.Length).Zero(18).Bytes(bytes));
                    break;
                }
            case 25:  // GetCursorImageAndName
                {
                    int px = Math.Max(0, _pointerX), py = Math.Max(0, _pointerY);
                    uint serial = _cursorSerial;
                    c.Reply(0, w => w.I16(px).I16(py).U16(1).U16(1).U16(0).U16(0).U32(serial).U32(0).U16(0).Zero(2).U32(0));
                    break;
                }
            case 28:  // ExpandRegion
                {
                    Region src = RegionRes(r.U32()).Region;
                    XRegionResource dst = RegionRes(r.U32());
                    int left = r.U16(), right = r.U16(), top = r.U16(), bottom = r.U16();
                    SetRegion(dst, Exact(Region.FromRects(src.Rects.Select(rect =>
                        new XRect(rect.X - left, rect.Y - top, rect.Width + left + right, rect.Height + top + bottom)))));
                    break;
                }
            case 29:  // HideCursor
                {
                    XWindow window = Window(r.U32());
                    _hiddenCursors[(c, window)] = _hiddenCursors.GetValueOrDefault((c, window)) + 1;
                    UpdateCursor();
                    break;
                }
            case 30:  // ShowCursor
                {
                    XWindow window = Window(r.U32());
                    if (!_hiddenCursors.TryGetValue((c, window), out int count))
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    if (count <= 1)
                    {
                        _hiddenCursors.Remove((c, window));
                    }
                    else
                    {
                        _hiddenCursors[(c, window)] = count - 1;
                    }
                    UpdateCursor();
                    break;
                }
            case 31:  // CreatePointerBarrier:只登记,不限制(宿主的系统指针不归我们管)
                {
                    uint id = r.U32();
                    _ = Window(r.U32());
                    short x1 = r.I16(), y1 = r.I16(), x2 = r.I16(), y2 = r.I16();
                    // §12.3:必须与坐标轴平行 —— x1 == x2 或 y1 == y2,但不能两个都相等。
                    if ((x1 == x2) == (y1 == y2))
                    {
                        throw new XProtocolError(XErrorCode.Value);
                    }
                    AddResource(c, new XPointerBarrier(id, c));
                    break;
                }
            case 32:  // DestroyPointerBarrier:只认屏障,别的资源一律 BadBarrier(§12.2)
                {
                    uint id = r.U32();
                    _ = Lookup<XPointerBarrier>(id) ?? throw new XProtocolError((XErrorCode)(XFixesErrorBase + 1), id);
                    RemoveResource(id);
                    break;
                }
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    /// <summary>指针所在窗口(或其祖先)被某个客户端 HideCursor 了。</summary>
    private bool CursorHiddenAt(XWindow window)
    {
        if (_hiddenCursors.Count == 0)
        {
            return false;
        }
        foreach ((XClient _, XWindow hidden) in _hiddenCursors.Keys)
        {
            if (ReferenceEquals(hidden, window) || window.IsDescendantOf(hidden) || hidden.IsRoot)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 选区属主变了:给 SelectSelectionInput 登记过的客户端发 XFixesSelectionNotify。
    /// </summary>
    /// <param name="selection">选区原子。</param>
    /// <param name="subtype">0 SetSelectionOwner,1 SelectionWindowDestroy,2 SelectionClientClose。</param>
    /// <param name="owner">新属主窗口;没有为 0。</param>
    /// <param name="selectionTime">属主获取选区的时间。</param>
    private void NotifySelectionChange(uint selection, byte subtype, uint owner, uint selectionTime)
    {
        if (_selectionInputs.Count == 0)
        {
            return;
        }
        uint bit = 1u << subtype;
        uint time = Now;
        foreach (((XClient client, XWindow window, uint sel), uint mask) in _selectionInputs)
        {
            if (sel == selection && (mask & bit) != 0 && !client.Closed)
            {
                client.Event(XFixesEventBase, subtype, w => w
                    .U32(window.Id).U32(owner).U32(selection).U32(time).U32(selectionTime));
            }
        }
    }

    /// <summary>光标形状变了:给 SelectCursorInput 登记过的客户端发 CursorNotify。</summary>
    private void NotifyCursorChange()
    {
        _cursorSerial++;
        if (_cursorInputs.Count == 0)
        {
            return;
        }
        uint serial = _cursorSerial, time = Now;
        foreach (((XClient client, XWindow window), uint mask) in _cursorInputs)
        {
            if ((mask & 1) != 0 && !client.Closed)
            {
                client.Event(XFixesEventBase + 1, 0, w => w.U32(window.Id).U32(serial).U32(time).U32(0));
            }
        }
    }

    private XCursorResource CursorRes(uint id) => Lookup<XCursorResource>(id) ?? throw new XProtocolError(XErrorCode.Cursor, id);

    /// <summary>客户端断开:清掉它的 XFIXES 登记。</summary>
    private void CleanupXFixes(XClient client) => RemoveXFixesEntries((c, _) => ReferenceEquals(c, client));

    /// <summary>窗口销毁:清掉登记在它上面的 XFIXES 登记。</summary>
    private void CleanupXFixes(XWindow window) => RemoveXFixesEntries((_, w) => ReferenceEquals(w, window));

    private void RemoveXFixesEntries(Func<XClient, XWindow, bool> match)
    {
        foreach ((XClient Client, XWindow Window, uint Selection) key in _selectionInputs.Keys.Where(k => match(k.Client, k.Window)).ToArray())
        {
            _selectionInputs.Remove(key);
        }
        foreach ((XClient Client, XWindow Window) key in _cursorInputs.Keys.Where(k => match(k.Client, k.Window)).ToArray())
        {
            _cursorInputs.Remove(key);
        }
        foreach ((XClient Client, XWindow Window) key in _hiddenCursors.Keys.Where(k => match(k.Client, k.Window)).ToArray())
        {
            _hiddenCursors.Remove(key);
        }
    }
}
