// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 10 节「Events」的「Input Device events」
//   (KeyPress / KeyRelease / ButtonPress / ButtonRelease / MotionNotify 的字段、state 是事件发生前的状态、
//   向上传播与 do-not-propagate、按钮按下的自动抓取)、「Pointer Window events」(EnterNotify / LeaveNotify 的
//   detail:Ancestor / Virtual / Inferior / Nonlinear / NonlinearVirtual)、「Input Focus events」;
//   「GrabPointer」「UngrabPointer」「GrabButton」「UngrabButton」「ChangeActivePointerGrab」「GrabKeyboard」
//   「UngrabKeyboard」「GrabKey」「UngrabKey」「QueryPointer」「WarpPointer」「SetInputFocus」「GetInputFocus」
//   「GetKeyboardMapping」「ChangeKeyboardMapping」「GetModifierMapping」「SetModifierMapping」
//   「GetKeyboardControl」「GetPointerMapping」;「MappingNotify」;附录 A「KEYSYM Encoding」(ISO_Level3_Shift、Alt_R)
//
//   同步抓取(pointer-mode / keyboard-mode = Synchronous)与 AllowEvents 的冻结、放行、重放见 X11Server.GrabFreeze.cs;
//   光标见 X11Server.Cursors.cs。

using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private XWindow _pointerWindow;

    /// <summary>PointerMotionHint 的轮次:按键 / 按钮变化、指针换窗口时推进(见 <see cref="XClient.MotionHint" />)。</summary>
    private uint _motionHintEpoch;
    private int _pointerX;
    private int _pointerY;

    /// <summary>
    /// 指针设备自己的位置(根坐标):宿主与 XTEST 给的位置,WarpPointer 不动它 —— 原始移动(XI_RawMotion)报的就是它。
    /// 还没有过设备输入时为 −1。
    /// </summary>
    private int _deviceX = -1, _deviceY = -1;
    private ushort _buttons;
    /// <summary>生效的修饰键 = 按着的(base)| 锁存的(latched)| 锁定的(locked)。</summary>
    private ushort _modifiers;
    private byte _baseMods;
    private byte _latchedMods;
    private byte _lockedMods;
    private readonly byte[] _keysDown = new byte[32];

    /// <summary>按着的按钮(1–255;XI2 的 buttons 掩码要全部按钮,核心 state 只有 1–5)。</summary>
    private readonly byte[] _buttonsDown = new byte[32];

    /// <summary>键盘焦点:null = None;<see cref="Root" /> = PointerRoot;其余为具体窗口。</summary>
    private XWindow? _focus;
    private byte _focusRevertTo;

    /// <summary>协议「SetInputFocus」的 last-focus-change time:客户端带的时间戳早于它的改焦点请求不生效。</summary>
    private uint _lastFocusChangeTime;

    /// <summary>用户最近一次在 X 窗口里按键 / 按按钮(宿主注入)的时间;判断 _NET_ACTIVE_WINDOW 是不是用户操作引起的(见 <see cref="XActivateRequest.UserInitiated" />)。</summary>
    private uint _lastUserInputTime;

    private ushort State => (ushort)(_modifiers | _buttons);

    // ================================================================== 宿主注入的输入(见 X11Server.cs 的公开方法)

    /// <summary>指针在顶层窗口里移动(内区坐标)。</summary>
    private void ApplyPointerMotion(XWindow top, int x, int y)
    {
        NoteUserActivity();
        _pointerTop = top;
        // 根坐标在注入的那一刻算好:指针冻着时事件排队,之后窗口可能挪了。
        int rootX = top.X + top.BorderWidth + x, rootY = top.Y + top.BorderWidth + y;
        ProcessPointerInput(() => MovePointer(rootX, rootY), motion: true);
    }

    private void ApplyPointerButton(XWindow top, int x, int y, int button, bool pressed)
    {
        NoteUserActivity();
        if (pressed)
        {
            _lastUserInputTime = Math.Max(1u, Now);
        }
        _pointerTop = top;
        int rootX = top.X + top.BorderWidth + x, rootY = top.Y + top.BorderWidth + y;
        ProcessPointerInput(() =>
        {
            MovePointer(rootX, rootY);
            ButtonEvent(button, pressed);
        });
    }

    /// <summary>松开一个(物理)按钮,指针留在原处(按下它的那个顶层已经不在了);X 这边并没按着它就什么也不做。</summary>
    private void ApplyPointerButtonRelease(int button)
    {
        NoteUserActivity();
        ProcessPointerInput(() =>
        {
            if (IsPhysicalButtonDown(button))
            {
                ButtonEvent(button, false);
            }
        });
    }

    /// <summary>指针离开了所有顶层窗口。</summary>
    private void ApplyPointerLeave()
    {
        _pointerTop = null;
        ProcessPointerInput(() => MovePointer(-1, -1));
    }

    private void ApplyKey(byte keycode, bool pressed, bool repeat)
    {
        NoteUserActivity();
        if (pressed)
        {
            _lastUserInputTime = Math.Max(1u, Now);
        }
        if (repeat)
        {
            // 重复不改变键盘状态:冻结的队列满了时可以像移动一样丢掉。
            ProcessInput(pointer: false, motion: true, () => KeyRepeat(keycode));
            return;
        }
        ProcessKeyboardInput(() => KeyEvent(keycode, pressed));
    }

    // ================================================================== 宿主换键位表

    /// <summary>右 Alt 当 AltGr 时的键值(ISO_Level3_Shift)与平时的键值(Alt_R),协议附录 A「KEYSYM Encoding」。</summary>
    private const uint IsoLevel3ShiftKeysym = 0xfe03, AltRightKeysym = 0xffea;

    /// <summary>
    /// 修饰键表(8 个修饰位 × 每位 2 个键码):Shift、Lock、Control、Mod1(Alt)、Mod2(Num Lock)、Mod3、Mod4(Super)、Mod5。
    /// 有 AltGr 时右 Alt 从 Mod1 挪到 Mod5 —— 四级键类型按 Mod5 选第三、四级。
    /// </summary>
    private static readonly byte[] DefaultModifierMap = [50, 62, 66, 0, 37, 105, 64, 108, 77, 0, 0, 0, 133, 134, 0, 0];

    private static readonly byte[] AltGrModifierMap = [50, 62, 66, 0, 37, 105, 64, 0, 77, 0, 0, 0, 133, 134, 108, 0];

    /// <summary><see cref="XKeymap" /> 在调用方线程上拷下来的一份(之后宿主再改那个对象不影响这里)。</summary>
    private sealed record KeymapChange(string Layout, int KeysymsPerKeycode, bool AltGr, (byte Keycode, uint[] Keysyms)[] Keys)
    {
        public static KeymapChange From(XKeymap keymap) =>
            new(keymap.Layout, keymap.KeysymsPerKeycode, keymap.AltGr, [.. keymap.Keys.Select(kv => (kv.Key, (uint[])kv.Value.Clone()))]);
    }

    /// <summary>
    /// 一次换掉键位表:各键码的键值、右 Alt 的键值与修饰位、布局名。客户端各收到一次 MappingNotify(键盘;修饰键表真的变了时再加一次修饰键)
    /// 与一次 XKB 的 MapNotify,而不是每改一段就通知一轮。
    /// </summary>
    private void ApplyKeymap(KeymapChange change)
    {
        int per = change.KeysymsPerKeycode;
        byte first = XKeycodes.AltRight, last = XKeycodes.AltRight;
        foreach ((byte keycode, uint[] keysyms) in change.Keys)
        {
            _keymap.Change(keycode, per, keysyms);
            first = Math.Min(first, keycode);
            last = Math.Max(last, keycode);
        }
        uint altRight = change.AltGr ? IsoLevel3ShiftKeysym : AltRightKeysym;
        _keymap.Change(XKeycodes.AltRight, 2, [altRight, altRight]);
        byte[] modifiers = change.AltGr ? AltGrModifierMap : DefaultModifierMap;
        bool modifiersChanged = !_keymap.ModifierMap.AsSpan().SequenceEqual(modifiers);
        if (modifiersChanged)
        {
            _keymap.SetModifierMap([.. modifiers]);
        }
        if (change.Layout != KeyboardLayout)
        {
            _keyboardLayout = change.Layout;
            PublishXkbRulesNames();
        }
        NotifyKeyboardMappingChanged(first, last - first + 1);
        if (modifiersChanged)
        {
            NotifyModifierMappingChanged();
        }
        NotifyXkbMapChanged();
    }

    /// <summary>核心 MappingNotify(request = Keyboard):这一段键码的键值变了,客户端该重新取。</summary>
    private void NotifyKeyboardMappingChanged(byte first, int count)
    {
        foreach (XClient client in _clients.Values)
        {
            client.Event(XEventCode.MappingNotify, 0, w => w.U8(1).U8(first).U8((byte)count));
        }
    }

    /// <summary>核心 MappingNotify(request = Modifier)。</summary>
    private void NotifyModifierMappingChanged()
    {
        foreach (XClient client in _clients.Values)
        {
            client.Event(XEventCode.MappingNotify, 0, w => w.U8(0).U8(0).U8(0));
        }
    }

    // ================================================================== 指针

    /// <summary>
    /// 宿主最近一次注入指针事件时指名的顶层(指针此刻在它的原生窗口里)。命中先在它里面找:顶层之间谁在上面由宿主的
    /// 原生 z 序决定,X 这边的堆叠只随创建先后与客户端的请求变 —— 原先一律从根按 X 的堆叠找,两个窗口一重叠,
    /// 点击就落到屏幕上被压在后面的那个(xs_plan WN-E1)。
    /// </summary>
    private XWindow? _pointerTop;

    private XWindow WindowAt(int rootX, int rootY)
    {
        (int rx, int ry) = Root.AbsoluteInner();
        if (_pointerTop is { IsTopLevel: true, Mapped: true } hinted && Hits(hinted, rx, ry, rootX, rootY))
        {
            return Descend(hinted, rx + hinted.X + hinted.BorderWidth, ry + hinted.Y + hinted.BorderWidth, rootX, rootY);
        }
        return Descend(Root, rx, ry, rootX, rootY);
    }

    /// <summary>从 <paramref name="current" />(内区原点在根坐标 (ix, iy))往下找指针所在的最深的窗口。</summary>
    private XWindow Descend(XWindow current, int ix, int iy, int rootX, int rootY)
    {
        while (true)
        {
            XWindow? hit = null;
            for (int i = current.Children.Count - 1; i >= 0; i--)
            {
                XWindow child = current.Children[i];
                if (child.Mapped && Hits(child, ix, iy, rootX, rootY) && !(current.IsRoot && IsMinimized(child)))
                {
                    hit = child;
                    break;
                }
            }
            if (hit is null)
            {
                return current;
            }
            ix += hit.X + hit.BorderWidth;
            iy += hit.Y + hit.BorderWidth;
            current = hit;
        }
    }

    /// <summary>根坐标 (rootX, rootY) 落不落在 <paramref name="child" /> 的外框(含边框、按输入形状)里;(ix, iy) 是它父窗口内区原点的根坐标。</summary>
    private static bool Hits(XWindow child, int ix, int iy, int rootX, int rootY)
    {
        int x = ix + child.X, y = iy + child.Y;
        int w = child.Width + (2 * child.BorderWidth), h = child.Height + (2 * child.BorderWidth);
        return rootX >= x && rootY >= y && rootX < x + w && rootY < y + h
               && (child.InputShape ?? child.BoundingShape) is var shape
               && (shape is null || shape.Contains(rootX - x - child.BorderWidth, rootY - y - child.BorderWidth));
    }

    /// <summary>宿主把这个顶层最小化了:原生窗口不在屏幕上,它在 X 里仍映射着(ICCCM 的 IconicState),但不该再接住指针。</summary>
    private bool IsMinimized(XWindow top) =>
        _topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle) && (handle.Snapshot.States & XWindowStates.Hidden) != 0;

    /// <summary>
    /// 指针挪到根坐标 (<paramref name="rootX" />, <paramref name="rootY" />)(−1 = 离开了所有顶层)。<paramref name="warp" /> 为真时是
    /// WarpPointer / XIWarpPointer:服务端合成的,不是设备运动 —— 不发原始移动,设备位置不变。原先 Warp 也发,值还是增量:
    /// 用户移动 +10、程序 Warp 回中心,随即收到一条 −10 的原始移动,两者抵消,靠「原始移动 + Warp 回中心」做相对鼠标的程序
    /// (3D 视图、游戏、虚拟机控制台)视角不动或抖。原始值现在与轴的声明(Abs X / Abs Y、Absolute)一致:报设备的位置。
    /// </summary>
    private void MovePointer(int rootX, int rootY, bool warp = false)
    {
        bool moved = rootX != _pointerX || rootY != _pointerY;
        if (!warp && rootX >= 0 && (rootX != _deviceX || rootY != _deviceY))
        {
            (_deviceX, _deviceY) = (rootX, rootY);
            SendRawEvent(XiRawMotion, 0, rootX, rootY);
        }
        _pointerX = rootX;
        _pointerY = rootY;
        UpdatePointerWindow();
        if (moved && rootX >= 0)
        {
            XEventMask mask = XEventMask.PointerMotion;   // 只选了 PointerMotionHint 的不算选了移动事件
            if (_buttons != 0)
            {
                mask |= XEventMask.ButtonMotion;
                for (int b = 0; b < 5; b++)
                {
                    if ((_buttons & (0x100 << b)) != 0)
                    {
                        mask |= (XEventMask)(0x100 << b);
                    }
                }
            }
            DeliverDeviceEvent(XEventCode.MotionNotify, 0, mask, _pointerWindow);
        }
    }

    /// <summary>
    /// 按钮按下的投递:被动抓取的激活(<paramref name="ignoreGrabsThrough" /> 及其以上的不看 —— ReplayPointer 用)、投递、
    /// 自动抓取、同步模式的冻结。<paramref name="replay" /> 时是重放:按钮状态与原始事件已经在第一次处理时记过了。
    /// </summary>
    private void PressButton(int button, XWindow? ignoreGrabsThrough, bool replay)
    {
        PassiveGrab? activated = null;
        if (PointerGrab is null && FindPassiveGrab(_pointerWindow, isButton: true, button, ignoreGrabsThrough) is { } passive)
        {
            activated = passive.Grab;
            PointerGrab = new ActiveGrab
            {
                Client = passive.Grab.Client,
                Window = passive.Window,
                OwnerEvents = passive.Grab.OwnerEvents,
                EventMask = passive.Grab.EventMask,
                Cursor = passive.Grab.Cursor,
                ConfineTo = passive.Grab.ConfineTo,
                ReleaseWhenButtonsUp = true,
                Xi2 = passive.Grab.Xi2,
                Xi2Mask = passive.Grab.Xi2Mask,
            };
        }
        if (PointerGrab is null && !IsFloating(pointer: true)
            && Propagate(_pointerWindow, XEventMask.ButtonPress, XEventCode.ButtonPress, null, null) is { } target)
        {
            // 这次按下会建立自动抓取,抓取窗口就是收到它的那个窗口(与下面投递的落点一样)。协议「Pointer Window events」:抓取激活时、
            // 在激活它的 ButtonPress 之前,按「指针从 P 瞬移到 G」发 Grab 模式的 crossing(只报给抓取方)—— 自动抓取也一样。
            // 投递仍按没有抓取时的规则走(同一窗口上选了的客户端都收到),所以只在发 crossing 的这一会儿把抓取挂上。
            _pointerGrab = AutomaticGrab(target);
            GenerateCrossing(_pointerWindow, target.Window, CrossingModeGrab);
            _pointerGrab = null;
        }
        // XI2 事件里的 buttons 是事件之前的按钮状态(XI2 协议「DeviceEvent」):这个按钮的位在投递之后才置上,与核心的 state 一致。
        Delivery? delivered = DeliverDeviceEvent(XEventCode.ButtonPress, (byte)button, XEventMask.ButtonPress, _pointerWindow);
        if (!replay)
        {
            _buttonsDown[button >> 3] |= (byte)(1 << (button & 7));
        }
        if (PointerGrab is null && delivered is { } d)
        {
            // 自动抓取:按下的那个窗口在所有按钮松开之前独占指针事件(协议「ButtonPress」;XI2 同理,格式跟着收到的那种走)。
            PointerGrab = AutomaticGrab(d);
        }
        if (activated is not null && PointerGrab is { } grab)
        {
            // 被动抓取以同步模式激活:事件已经交出去,设备此刻冻结;这一次按下可被 ReplayPointer 重放。
            ApplyGrabModes(grab, activated.PointerSync, activated.KeyboardSync);
            if (activated.PointerSync)
            {
                _pointerReplay = (button, grab.Window);
            }
        }
        else if (delivered is not null && PointerGrab is not null)
        {
            NotePointerEventReported(button, pressed: true);
        }
    }

    /// <summary>按钮按下投递到 <paramref name="d" /> 时建立的自动抓取。</summary>
    private static ActiveGrab AutomaticGrab(Delivery d) => new()
    {
        Client = d.Client,
        Window = d.Window,
        OwnerEvents = (d.Mask & (uint)XEventMask.OwnerGrabButton) != 0,
        EventMask = d.Mask,
        ReleaseWhenButtonsUp = true,
        Automatic = true,
        Xi2 = d.Xi2,
        Xi2Mask = d.Xi2Mask,
    };

    /// <summary>重新算指针所在窗口;变了就发 Enter / Leave、更新光标。窗口树变化后也调它。</summary>
    internal void UpdatePointerWindow()
    {
        XWindow now = _pointerX < 0 ? Root : WindowAt(_pointerX, _pointerY);
        if (!ReferenceEquals(now, _pointerWindow))
        {
            XWindow old = _pointerWindow;
            _pointerWindow = now;
            _motionHintEpoch++;
            GenerateCrossing(old, now);
        }
        UpdateCursor();
    }

    /// <summary>
    /// 物理按钮 <paramref name="physical" /> 按下 / 松开(宿主与 XTEST 给的):原始事件报物理按钮(XI2「RawEvent」是驱动给的数据),
    /// 其余一律按 SetPointerMapping 换成生效的按钮号;映射成 0 的按钮停用。
    /// </summary>
    private void ButtonEvent(int physical, bool pressed)
    {
        if (pressed)
        {
            _physicalButtonsDown[physical >> 3] |= (byte)(1 << (physical & 7));
        }
        else
        {
            _physicalButtonsDown[physical >> 3] &= (byte)~(1 << (physical & 7));
        }
        SendRawEvent(pressed ? XiRawButtonPress : XiRawButtonRelease, (uint)physical, 0, 0);
        int button = MapButton(physical);
        if (button == 0)
        {
            return;
        }
        _motionHintEpoch++;   // 按钮状态变了:PointerMotionHint 的客户端可以再收一条提示
        // 只有按钮 1–5 在 state 里有位(协议 SETofKEYBUTMASK);6 以上(水平滚轮等)照样投递,但不进 state。
        ushort bit = button <= 5 ? (ushort)(0x100 << (button - 1)) : (ushort)0;
        if (pressed)
        {
            PressButton(button, ignoreGrabsThrough: null, replay: false);
            _buttons |= bit;
        }
        else
        {
            Delivery? released = DeliverDeviceEvent(XEventCode.ButtonRelease, (byte)button, XEventMask.ButtonRelease, _pointerWindow);
            if (released is not null && PointerGrab is not null)
            {
                NotePointerEventReported(button, pressed: false);
            }
            _buttonsDown[button >> 3] &= (byte)~(1 << (button & 7));
            _buttons &= (ushort)~bit;
            if (!_buttonsDown.AsSpan().ContainsAnyExcept((byte)0) && PointerGrab is { ReleaseWhenButtonsUp: true })
            {
                PointerGrab = null;   // 所有按钮都松开了(6 号以上不在 state 里,也要等它们松开)
                UpdateCursor();
            }
        }
    }

    /// <summary>一次投递的落点:收到事件的窗口、客户端、核心掩码;经 XI2 收到时 <see cref="Xi2" /> 为真。</summary>
    private readonly record struct Delivery(XWindow Window, XClient Client, uint Mask, bool Xi2, ulong Xi2Mask, bool Xi2Slave);

    /// <summary>
    /// 投递一个设备事件:有主动抓取按抓取规则走,否则从源窗口向上传播到第一个有人选了它的窗口
    /// (核心事件掩码或 XI2 事件掩码都算)。在那个窗口上,选了核心事件的收核心事件,选了 XI2 的收 XI2 事件。
    /// XI2 的 evtype 与核心事件码相同(KeyPress 2 … Motion 6)。返回第一个收到事件的落点,没人收时为 null。
    /// <paramref name="repeat" /> 是自动重复的哪一半(见 <see cref="KeyRepeat" />):中间那个 KeyRelease 跳过 XI2 与开了 DetectableAutoRepeat 的客户端,
    /// 重复的 KeyPress 在 XI2 里带 KeyRepeat 标志。
    /// </summary>
    private Delivery? DeliverDeviceEvent(byte code, byte detail, XEventMask mask, XWindow source, RepeatPhase repeat = RepeatPhase.None)
    {
        bool isKey = code is XEventCode.KeyPress or XEventCode.KeyRelease;
        if (IsFloating(!isKey))
        {
            return DeliverFloating(code, detail, source);
        }
        ActiveGrab? grab = isKey ? KeyboardGrab : PointerGrab;
        XWindow? stopAt = null;
        if (isKey && grab is null && _focus is { } focus && !ReferenceEquals(focus, Root))
        {
            stopAt = focus;
        }

        if (grab is not null)
        {
            if (grab.OwnerEvents && Propagate(source, mask, code, grab.Client, stopAt) is { } own)
            {
                Send(own);
                return own;
            }
            if (grab.Xi2)
            {
                if ((grab.Xi2Mask & (1UL << code)) == 0 || repeat == RepeatPhase.Release)
                {
                    return null;
                }
                Delivery xi = new(grab.Window, grab.Client, 0, true, grab.Xi2Mask, false);
                Send(xi);
                return xi;
            }
            if ((isKey || (grab.EventMask & (uint)mask) != 0) && (repeat != RepeatPhase.Release || WantsRepeatRelease(grab.Client)))
            {
                SendDeviceEvent(grab.Client, code, detail, grab.Window, source, grab.EventMask);
                return new Delivery(grab.Window, grab.Client, grab.EventMask, false, 0, false);
            }
            return null;
        }

        return Propagate(source, mask, code, null, stopAt) is { } hit ? SendToAll(hit.Window) : null;

        void Send(Delivery d)
        {
            if (d.Xi2)
            {
                if (repeat != RepeatPhase.Release)
                {
                    SendXi2DeviceEvent(d.Client, code, detail, d.Window, source, d.Xi2Slave, repeat == RepeatPhase.Press ? XiKeyRepeatFlag : 0);
                }
            }
            else if (repeat != RepeatPhase.Release || WantsRepeatRelease(d.Client))
            {
                SendDeviceEvent(d.Client, code, detail, d.Window, source, d.Mask);
            }
        }

        Delivery? SendToAll(XWindow window)
        {
            Delivery? first = null;
            foreach ((XClient client, uint selected) in window.EventSelections)
            {
                if ((selected & (uint)mask) != 0 && !client.Closed && (repeat != RepeatPhase.Release || WantsRepeatRelease(client)))
                {
                    SendDeviceEvent(client, code, detail, window, source, selected);
                    first ??= new Delivery(window, client, selected, false, 0, false);
                }
            }
            if (repeat == RepeatPhase.Release)
            {
                return first;
            }
            uint flags = repeat == RepeatPhase.Press ? XiKeyRepeatFlag : 0;
            foreach ((XClient client, (ulong master, ulong slave)) in window.Xi2Selections)
            {
                if (((master | slave) & (1UL << code)) != 0 && !client.Closed)
                {
                    bool slaveOnly = (master & (1UL << code)) == 0;
                    SendXi2DeviceEvent(client, code, detail, window, source, slaveOnly, flags);
                    first ??= new Delivery(window, client, 0, true, master | slave, slaveOnly);
                }
            }
            return first;
        }
    }

    /// <summary>
    /// 物理从设备浮动了(XIChangeHierarchy DetachSlave):不产生核心事件、不受主设备的抓取影响,
    /// 只以从设备的身份报 XI2 事件 —— 从源窗口向上找第一个为从设备(或 XIAllDevices)选了它的窗口。
    /// </summary>
    private Delivery? DeliverFloating(byte code, byte detail, XWindow source)
    {
        for (XWindow? w = source; w is not null; w = w.Parent)
        {
            Delivery? first = null;
            foreach ((XClient client, (_, ulong slave)) in w.Xi2Selections)
            {
                if ((slave & (1UL << code)) != 0 && !client.Closed)
                {
                    SendXi2DeviceEvent(client, code, detail, w, source, slave: true);
                    first ??= new Delivery(w, client, 0, true, slave, true);
                }
            }
            if (first is not null)
            {
                return first;
            }
        }
        return null;
    }

    /// <summary>
    /// 从源窗口向上找第一个(指定客户端)选了这类事件的窗口 —— 核心掩码或 XI2 的 <paramref name="evtype" /> 都算;
    /// 碰上 do-not-propagate 或 <paramref name="stopAt" /> 就停。
    /// </summary>
    private static Delivery? Propagate(XWindow source, XEventMask mask, int evtype, XClient? only, XWindow? stopAt)
    {
        for (XWindow? w = source; w is not null; w = w.Parent)
        {
            foreach ((XClient client, uint selected) in w.EventSelections)
            {
                if ((selected & (uint)mask) != 0 && !client.Closed && (only is null || ReferenceEquals(client, only)))
                {
                    return new Delivery(w, client, selected, false, 0, false);
                }
            }
            foreach ((XClient client, (ulong master, ulong slave)) in w.Xi2Selections)
            {
                if (((master | slave) & (1UL << evtype)) != 0 && !client.Closed && (only is null || ReferenceEquals(client, only)))
                {
                    return new Delivery(w, client, 0, true, master | slave, (master & (1UL << evtype)) == 0);
                }
            }
            if ((w.DoNotPropagateMask & (uint)mask) != 0 || ReferenceEquals(w, stopAt))
            {
                return null;
            }
        }
        return null;
    }

    private void SendDeviceEvent(XClient client, byte code, byte detail, XWindow eventWindow, XWindow source, uint clientMask)
    {
        (int ex, int ey) = eventWindow.AbsoluteInner();
        // child:事件窗口的、位于通往源窗口路径上的那个子窗口;源就是事件窗口时为 None。
        uint child = 0;
        for (XWindow? w = source; w is not null && !ReferenceEquals(w, eventWindow); w = w.Parent)
        {
            if (ReferenceEquals(w.Parent, eventWindow))
            {
                child = w.Id;
                break;
            }
        }
        if (code == XEventCode.MotionNotify && (clientMask & (uint)XEventMask.PointerMotionHint) != 0)
        {
            // 协议「MotionNotify」:选了 PointerMotionHint 的客户端,在按键 / 按钮状态变化、指针离开事件窗口、
            // 或者它发 QueryPointer / GetMotionEvents 之前,同一个事件窗口只收一条(detail = Hint)。
            if (ReferenceEquals(client.MotionHint.Window, eventWindow) && client.MotionHint.Epoch == _motionHintEpoch)
            {
                return;
            }
            client.MotionHint = (eventWindow, _motionHintEpoch);
            detail = 1;   // Hint
        }
        // 指针移动每次都走这里:值经状态传进静态 lambda,不为每个事件分配闭包。
        client.Event(code, detail, (Time: Now, Root: Root.Id, Event: eventWindow.Id, Child: child,
                X: _pointerX, Y: _pointerY, EventX: _pointerX - ex, EventY: _pointerY - ey, State),
            static (w, e) => w.U32(e.Time).U32(e.Root).U32(e.Event).U32(e.Child)
                .I16(e.X).I16(e.Y).I16(e.EventX).I16(e.EventY).U16(e.State).Bool(true));
    }

    /// <summary>Enter / Leave 的 mode(协议附录 B「EnterNotify」)。</summary>
    private const byte CrossingModeNormal = 0, CrossingModeGrab = 1, CrossingModeUngrab = 2;

    /// <summary>
    /// Enter / Leave(协议「Pointer Window events」的五种 detail)。只发给选了 EnterWindow / LeaveWindow 的客户端,不传播;
    /// 指针被抓着时见 <see cref="Crossing" />。
    /// </summary>
    private void GenerateCrossing(XWindow from, XWindow to, byte mode = CrossingModeNormal)
    {
        const byte ancestor = 0, @virtual = 1, inferior = 2, nonlinear = 3, nonlinearVirtual = 4;
        if (ReferenceEquals(from, to))
        {
            return;
        }
        // Leave 的 child 指向指针原先所在的那一支,Enter 的指向指针现在所在的那一支(协议「EnterNotify / LeaveNotify」)。
        if (to.IsDescendantOf(from))
        {
            Crossing(XEventCode.LeaveNotify, from, inferior, to, mode);
            foreach (XWindow w in PathBetween(to, from))
            {
                Crossing(XEventCode.EnterNotify, w, @virtual, to, mode);
            }
            Crossing(XEventCode.EnterNotify, to, ancestor, to, mode);
        }
        else if (from.IsDescendantOf(to))
        {
            Crossing(XEventCode.LeaveNotify, from, ancestor, from, mode);
            foreach (XWindow w in Enumerable.Reverse(PathBetween(from, to)))
            {
                Crossing(XEventCode.LeaveNotify, w, @virtual, from, mode);
            }
            Crossing(XEventCode.EnterNotify, to, inferior, from, mode);
        }
        else
        {
            XWindow common = CommonAncestor(from, to);
            Crossing(XEventCode.LeaveNotify, from, nonlinear, from, mode);
            foreach (XWindow w in Enumerable.Reverse(PathBetween(from, common)))
            {
                Crossing(XEventCode.LeaveNotify, w, nonlinearVirtual, from, mode);
            }
            foreach (XWindow w in PathBetween(to, common))
            {
                Crossing(XEventCode.EnterNotify, w, nonlinearVirtual, to, mode);
            }
            Crossing(XEventCode.EnterNotify, to, nonlinear, to, mode);
        }
    }

    /// <summary><paramref name="window" /> 的哪个子窗口包含 <paramref name="pointerWindow" />(自己或后代);都不是时为 0(None)。</summary>
    private static uint ChildToward(XWindow window, XWindow pointerWindow)
    {
        for (XWindow? w = pointerWindow; w?.Parent is { } parent; w = parent)
        {
            if (ReferenceEquals(parent, window))
            {
                return w.Id;
            }
        }
        return 0;
    }

    /// <summary><paramref name="descendant" /> 与 <paramref name="ancestor" /> 之间的窗口(都不含),从上到下。</summary>
    private static List<XWindow> PathBetween(XWindow descendant, XWindow ancestor)
    {
        List<XWindow> path = [];
        for (XWindow? w = descendant.Parent; w is not null && !ReferenceEquals(w, ancestor); w = w.Parent)
        {
            path.Add(w);
        }
        path.Reverse();
        return path;
    }

    private static XWindow CommonAncestor(XWindow a, XWindow b)
    {
        HashSet<XWindow> chain = [];
        for (XWindow? w = a; w is not null; w = w.Parent)
        {
            chain.Add(w);
        }
        for (XWindow? w = b; w is not null; w = w.Parent)
        {
            if (chain.Contains(w))
            {
                return w;
            }
        }
        return a;
    }

    /// <summary>
    /// 一个 Enter / Leave(核心与 XI2)。指针被抓着时(mode 为 Normal 或 Grab)按协议「GrabPointer」只报给抓取方:owner-events 为 True
    /// 且它自己在这个窗口上选了的照常报;否则只报抓取窗口上、抓取的事件掩码里有的。别的客户端一条都收不到。
    /// Ungrab 的 crossing 发出时抓取已经解除,照常报给所有选了的客户端。
    /// </summary>
    private void Crossing(byte code, XWindow window, byte detail, XWindow pointerWindow, byte mode = CrossingModeNormal)
    {
        XEventMask mask = code == XEventCode.EnterNotify ? XEventMask.EnterWindow : XEventMask.LeaveWindow;
        int evtype = code == XEventCode.EnterNotify ? XiEnter : XiLeave;
        uint child = ChildToward(window, pointerWindow);
        if (mode != CrossingModeUngrab && PointerGrab is { } grab)
        {
            XClient owner = grab.Client;
            if (grab.OwnerEvents && (window.Selects(owner, mask) || window.Xi2Selects(owner, evtype)))
            {
                SendXi2Crossing(evtype, window, detail, mode, child, only: owner);
                if (window.Selects(owner, mask))
                {
                    SendCoreCrossing(owner, code, window, detail, child, mode);
                }
            }
            else if (ReferenceEquals(window, grab.Window))
            {
                if (grab.Xi2 && (grab.Xi2Mask & (1UL << evtype)) != 0)
                {
                    SendXi2Crossing(evtype, window, detail, mode, child, only: owner, force: true);
                }
                else if (!grab.Xi2 && (grab.EventMask & (uint)mask) != 0)
                {
                    SendCoreCrossing(owner, code, window, detail, child, mode);
                }
            }
            return;
        }
        SendXi2Crossing(evtype, window, detail, mode, child);
        DeliverToSelectors(window, mask, c => SendCoreCrossing(c, code, window, detail, child, mode));
    }

    /// <summary>核心的 EnterNotify / LeaveNotify;Enter 之后给同时选了 KeymapState 的客户端补一个 KeymapNotify。</summary>
    private void SendCoreCrossing(XClient client, byte code, XWindow window, byte detail, uint child, byte mode)
    {
        if (client.Closed)
        {
            return;
        }
        (int ex, int ey) = window.AbsoluteInner();
        uint time = Now;
        ushort state = State;
        int px = _pointerX, py = _pointerY;
        bool focus = _focus is { } f && (ReferenceEquals(f, window) || window.IsDescendantOf(f));
        client.Event(code, detail, w => w
            .U32(time).U32(Root.Id).U32(window.Id).U32(child)
            .I16(px).I16(py).I16(px - ex).I16(py - ey).U16(state)
            .U8(mode)
            .U8((byte)(0x02 | (focus ? 0x01 : 0))));        // same-screen | focus
        if (code == XEventCode.EnterNotify)
        {
            SendKeymapNotify(client, window);
        }
    }

    // ================================================================== 键盘

    private void KeyEvent(byte keycode, bool pressed)
    {
        _motionHintEpoch++;   // 按键状态变了:同上
        if (keycode < Keymap.MinKeycode)
        {
            return;
        }

        bool wasDown = (_keysDown[keycode >> 3] & (1 << (keycode & 7))) != 0;
        if (pressed)
        {
            _keysDown[keycode >> 3] |= (byte)(1 << (keycode & 7));
        }
        else
        {
            _keysDown[keycode >> 3] &= (byte)~(1 << (keycode & 7));
        }

        SendRawEvent(pressed ? XiRawKeyPress : XiRawKeyRelease, keycode, 0, 0);
        // 事件里的 state 是事件发生前的:修饰键状态在投递之后才更新。
        if (pressed)
        {
            PressKey(keycode, ignoreGrabsThrough: null, replay: false);
        }
        else if (KeyboardSource() is { } source)
        {
            Delivery? released = DeliverDeviceEvent(XEventCode.KeyRelease, keycode, XEventMask.KeyRelease, source);
            if (released is not null && KeyboardGrab is not null)
            {
                NoteKeyboardEventReported(keycode, pressed: false);
            }
        }

        // 更新修饰键状态:Lock 类(Caps_Lock、Num_Lock)按下翻转锁定位;其余由「当前按着的键」重新算出,
        // 两个 Shift 同时按着、松开一个时 Shift 仍然生效。
        ushort modBit = _keymap.ModifierBitOf(keycode);
        if (modBit != 0)
        {
            if (_keymap.IsLockingKey(keycode) && pressed && !wasDown)
            {
                _lockedMods ^= (byte)modBit;
            }
            UpdateModifierState(keycode, pressed ? XEventCode.KeyPress : XEventCode.KeyRelease);
        }
        else if (pressed && _latchedMods != 0)
        {
            // XKB「Locking and Latching Modifiers and Groups」:锁存的修饰键只作用于下一个不改变键盘状态的按键事件 ——
            // 这个事件已经带着它们发出去了,随后解除(修饰键本身按下不算,Shift 锁存之后再锁存 Ctrl,两个都还留着)。
            _latchedMods = 0;
            UpdateModifierState(keycode, XEventCode.KeyPress);
        }

        if (!pressed && KeyboardGrab is { ReleaseWhenButtonsUp: true } && _passiveKeyGrabKey == keycode)
        {
            KeyboardGrab = null;
            _passiveKeyGrabKey = 0;
        }
    }

    private byte _passiveKeyGrabKey;

    /// <summary>按键按下的投递:被动抓取的激活(<paramref name="ignoreGrabsThrough" /> 及其以上的不看)、投递、同步模式的冻结。</summary>
    private void PressKey(byte keycode, XWindow? ignoreGrabsThrough, bool replay)
    {
        XWindow? source = KeyboardSource();
        if (source is null)
        {
            return;
        }
        PassiveGrab? activated = null;
        if (KeyboardGrab is null && FindPassiveGrab(source, isButton: false, keycode, ignoreGrabsThrough) is { } passive)
        {
            activated = passive.Grab;
            KeyboardGrab = new ActiveGrab
            {
                Client = passive.Grab.Client,
                Window = passive.Window,
                OwnerEvents = passive.Grab.OwnerEvents,
                ReleaseWhenButtonsUp = true,   // 对键盘:这个键松开时解除
                Xi2 = passive.Grab.Xi2,
                Xi2Mask = passive.Grab.Xi2Mask,
            };
            _passiveKeyGrabKey = keycode;
        }
        Delivery? delivered = DeliverDeviceEvent(XEventCode.KeyPress, keycode, XEventMask.KeyPress, source);
        if (activated is not null && KeyboardGrab is { } grab)
        {
            ApplyGrabModes(grab, activated.PointerSync, activated.KeyboardSync);
            if (activated.KeyboardSync)
            {
                _keyboardReplay = (keycode, grab.Window);
            }
        }
        else if (!replay && delivered is not null && KeyboardGrab is not null)
        {
            NoteKeyboardEventReported(keycode, pressed: true);
        }
    }

    /// <summary>宿主的锁定键状态(见 <see cref="SetLockState" />):锁定位按键位表里 Caps_Lock / Num_Lock 所在的修饰位改,变了照常通知。</summary>
    private void ApplyLockState(bool capsLock, bool numLock)
    {
        byte locked = _lockedMods;
        byte capsBit = (byte)_keymap.ModifierBitOf(XKeycodes.CapsLock), numBit = (byte)_keymap.ModifierBitOf(XKeycodes.NumLock);
        locked = capsLock ? (byte)(locked | capsBit) : (byte)(locked & ~capsBit);
        locked = numLock ? (byte)(locked | numBit) : (byte)(locked & ~numBit);
        if (locked != _lockedMods)
        {
            _lockedMods = locked;
            UpdateModifierState(0, 0);
        }
    }

    /// <summary>从按着的修饰键重新算 base,合成生效状态;变了就通知(XKB 的 StateNotify)。</summary>
    private void UpdateModifierState(byte keycode, byte eventType, byte requestMajor = 0, byte requestMinor = 0)
    {
        byte oldBase = _baseMods, oldLatched = _latchedMods, oldLocked = _lockedMods;
        ushort oldEffective = _modifiers;
        byte held = 0;
        for (int code = Keymap.MinKeycode; code <= Keymap.MaxKeycode; code++)
        {
            if ((_keysDown[code >> 3] & (1 << (code & 7))) != 0 && !_keymap.IsLockingKey((byte)code))
            {
                held |= (byte)_keymap.ModifierBitOf((byte)code);
            }
        }
        _baseMods = held;
        _modifiers = (ushort)(_baseMods | _latchedMods | _lockedMods);
        ushort changed = 0;
        changed |= (ushort)(_modifiers != oldEffective ? 1 : 0);
        changed |= (ushort)(_baseMods != oldBase ? 2 : 0);
        changed |= (ushort)(_latchedMods != oldLatched ? 4 : 0);
        changed |= (ushort)(_lockedMods != oldLocked ? 8 : 0);
        if (changed != 0)
        {
            NotifyXkbState(changed, keycode, eventType, requestMajor, requestMinor);
        }
    }

    /// <summary>按键事件的源窗口:焦点是 PointerRoot 时是指针所在窗口;指针在焦点窗口里面时是指针所在窗口;否则是焦点窗口。</summary>
    /// <summary>
    /// 按键事件的源窗口:焦点是 PointerRoot 时是指针所在的窗口;焦点是某个窗口时,指针在它里面就是指针所在的窗口,否则是焦点本身。
    /// 键盘被抓着时照样按焦点算 —— 协议「GrabKeyboard」:owner-events 为 True 时按键事件「照常报告」就是按焦点报告;
    /// 照常报告不到(焦点为 None)才以抓取窗口为源。
    /// </summary>
    private XWindow? KeyboardSource()
    {
        XWindow? normal = _focus switch
        {
            null => null,
            { } f when ReferenceEquals(f, Root) => _pointerWindow,
            { } f => ReferenceEquals(_pointerWindow, f) || _pointerWindow.IsDescendantOf(f) ? _pointerWindow : f,
        };
        return KeyboardGrab is { } grab ? normal ?? grab.Window : normal;
    }

    /// <summary>被动抓取:从根往下到源窗口,第一个匹配的生效(协议「GrabButton」「GrabKey」)。</summary>
    private (XWindow Window, PassiveGrab Grab)? FindPassiveGrab(XWindow source, bool isButton, int detail, XWindow? ignoreThrough = null)
    {
        // 从根往下找(协议:离根最近的那个被动抓取生效);递归回溯父链,不为每次按键分配链表。
        // ignoreThrough:它及其祖先上的被动抓取不看(Replay 重放时「不看抓取窗口及其以上」)。
        ushort mods = (ushort)(_modifiers & 0xFF);
        return Find(source);

        (XWindow Window, PassiveGrab Grab)? Find(XWindow w)
        {
            if (w.Parent is { } parent && Find(parent) is { } outer)
            {
                return outer;
            }
            if (ignoreThrough is not null && (ReferenceEquals(w, ignoreThrough) || ignoreThrough.IsDescendantOf(w)))
            {
                return null;
            }
            foreach (PassiveGrab grab in isButton ? w.ButtonGrabs : w.KeyGrabs)
            {
                if (grab.Matches(detail, mods) && !grab.Client.Closed)
                {
                    return (w, grab);
                }
            }
            return null;
        }
    }

    // ================================================================== 焦点

    private void SetFocus(XWindow? focus, byte revertTo)
    {
        XWindow? old = _focus;
        _focusRevertTo = revertTo;
        if (ReferenceEquals(old, focus))
        {
            return;
        }
        _focus = focus;
        UpdateActiveWindow(old, focus);
        GenerateFocusEvents(old, focus, KeyboardGrab is null ? FocusModeNormal : FocusModeWhileGrabbed);
    }

    /// <summary>焦点事件的 mode(协议附录 B「FocusIn」)。</summary>
    private const byte FocusModeNormal = 0, FocusModeGrab = 1, FocusModeUngrab = 2, FocusModeWhileGrabbed = 3;

    /// <summary>
    /// 焦点从 <paramref name="from" /> 移到 <paramref name="to" /> 时的 FocusOut / FocusIn,逐条照协议「Input Focus events」:
    /// 按两个窗口的上下级关系分 Ancestor / Virtual / Inferior / Nonlinear / NonlinearVirtual,指针所在的那一支另发 Pointer,
    /// 与 PointerRoot / None 之间的切换在根窗口上发 PointerRoot / None。null = None,<see cref="Root" /> = PointerRoot
    /// (只有一块屏幕:「所有根窗口」就是它)。每个 FocusIn 之后紧跟 KeymapNotify。
    /// </summary>
    private void GenerateFocusEvents(XWindow? from, XWindow? to, byte mode)
    {
        const byte ancestor = 0, @virtual = 1, inferior = 2, nonlinear = 3, nonlinearVirtual = 4, pointer = 5, pointerRoot = 6, none = 7;
        if (ReferenceEquals(from, to))
        {
            return;
        }
        XWindow p = _pointerWindow;
        bool fromSpecial = from is null || from.IsRoot, toSpecial = to is null || to.IsRoot;
        if (fromSpecial)
        {
            // 从 PointerRoot / None 出来:旧的是 PointerRoot 时指针所在的那一支(连根)先发 Pointer。
            if (from is not null)
            {
                FocusEach(false, UpFrom(p, null), pointer, mode);
            }
            Focus(false, Root, from is null ? none : pointerRoot, mode);
            if (toSpecial)
            {
                Focus(true, Root, to is null ? none : pointerRoot, mode);
                if (to is not null)
                {
                    FocusEach(true, DownTo(p, null), pointer, mode);
                }
                return;
            }
            XWindow a = to!;
            FocusEach(true, DownTo(a.Parent!, null), nonlinearVirtual, mode);   // 从 A 的根往下,不含 A
            Focus(true, a, nonlinear, mode);
            if (p.IsDescendantOf(a))
            {
                FocusEach(true, DownTo(p, a), pointer, mode);
            }
            return;
        }
        XWindow source = from!;
        if (toSpecial)
        {
            if (p.IsDescendantOf(source))
            {
                FocusEach(false, UpFrom(p, source), pointer, mode);
            }
            Focus(false, source, nonlinear, mode);
            FocusEach(false, UpFrom(source.Parent!, null), nonlinearVirtual, mode);   // A 以上直到根(含根)
            Focus(true, Root, to is null ? none : pointerRoot, mode);
            if (to is not null)
            {
                FocusEach(true, DownTo(p, null), pointer, mode);
            }
            return;
        }
        XWindow target = to!;
        if (source.IsDescendantOf(target))
        {
            Focus(false, source, ancestor, mode);
            FocusEach(false, UpFrom(source.Parent!, target), @virtual, mode);
            Focus(true, target, inferior, mode);
            if (p.IsDescendantOf(target) && !ReferenceEquals(p, source) && !p.IsDescendantOf(source) && !source.IsDescendantOf(p))
            {
                FocusEach(true, DownTo(p, target), pointer, mode);
            }
        }
        else if (target.IsDescendantOf(source))
        {
            if (p.IsDescendantOf(source) && !ReferenceEquals(p, target) && !p.IsDescendantOf(target) && !target.IsDescendantOf(p))
            {
                FocusEach(false, UpFrom(p, source), pointer, mode);
            }
            Focus(false, source, inferior, mode);
            FocusEach(true, DownTo(target.Parent!, source), @virtual, mode);
            Focus(true, target, ancestor, mode);
        }
        else
        {
            XWindow common = CommonAncestor(source, target);
            if (p.IsDescendantOf(source))
            {
                FocusEach(false, UpFrom(p, source), pointer, mode);
            }
            Focus(false, source, nonlinear, mode);
            FocusEach(false, UpFrom(source.Parent!, common), nonlinearVirtual, mode);
            FocusEach(true, DownTo(target.Parent!, common), nonlinearVirtual, mode);
            Focus(true, target, nonlinear, mode);
            if (p.IsDescendantOf(target))
            {
                FocusEach(true, DownTo(p, target), pointer, mode);
            }
        }
    }

    /// <summary>从 <paramref name="start" /> 往上,直到 <paramref name="stop" />(不含;null = 一直到根,含根)。</summary>
    private static List<XWindow> UpFrom(XWindow start, XWindow? stop)
    {
        List<XWindow> path = [];
        for (XWindow? w = start; w is not null && !ReferenceEquals(w, stop); w = w.Parent)
        {
            path.Add(w);
        }
        return path;
    }

    /// <summary><see cref="UpFrom" /> 倒过来:从上往下,到 <paramref name="end" /> 为止(含)。</summary>
    private static List<XWindow> DownTo(XWindow end, XWindow? stop)
    {
        List<XWindow> path = UpFrom(end, stop);
        path.Reverse();
        return path;
    }

    private void FocusEach(bool focusIn, List<XWindow> windows, byte detail, byte mode)
    {
        foreach (XWindow w in windows)
        {
            Focus(focusIn, w, detail, mode);
        }
    }

    /// <summary>一个 FocusIn / FocusOut(核心与 XI2);FocusIn 之后给同时选了 KeymapState 的客户端补一个 KeymapNotify。</summary>
    private void Focus(bool focusIn, XWindow window, byte detail, byte mode)
    {
        byte code = focusIn ? XEventCode.FocusIn : XEventCode.FocusOut;
        DeliverToSelectors(window, XEventMask.FocusChange, c =>
        {
            c.Event(code, detail, w => w.U32(window.Id).U8(mode));
            if (focusIn)
            {
                SendKeymapNotify(c, window);
            }
        });
        SendXi2Crossing(focusIn ? XiFocusIn : XiFocusOut, window, detail, mode);
    }

    /// <summary>
    /// KeymapNotify(协议「KeymapNotify」):每个 EnterNotify 与 FocusIn 之后紧跟一个,发给在那个窗口上选了 KeymapState 的客户端。
    /// 这个事件没有序号:第 0 字节是事件码,后 31 字节是键码 8–255 的按下位图(QueryKeymap 的格式,略去键码 0–7 那一字节)。
    /// </summary>
    private void SendKeymapNotify(XClient client, XWindow window)
    {
        if (!window.Selects(client, XEventMask.KeymapState))
        {
            return;
        }
        byte[] e = new byte[32];
        e[0] = XEventCode.KeymapNotify;
        Array.Copy(_keysDown, 1, e, 1, 31);
        client.Send(e);
    }

    /// <summary>焦点窗口不可见了:按 revert-to 退回(None / PointerRoot / 最近的可见祖先)。</summary>
    private void RevertFocus(XWindow lost)
    {
        switch (_focusRevertTo)
        {
            case 1:
                SetFocus(Root, 1);
                break;
            case 2:
                XWindow? w = lost.Parent;
                while (w is not null && !w.IsViewable)
                {
                    w = w.Parent;
                }
                SetFocus(w is null || w.IsRoot ? null : w, 0);
                break;
            default:
                SetFocus(null, 0);
                break;
        }
    }

    private void SetInputFocus(XRequestReader r)
    {
        byte revertTo = r.Data;
        uint id = r.U32(), time = r.U32();
        if (revertTo > 2)
        {
            throw new XProtocolError(XErrorCode.Value, revertTo);   // None / PointerRoot / Parent
        }
        XWindow? focus = id switch
        {
            0 => null,
            1 => Root,
            _ => Window(id),
        };
        if (focus is { IsRoot: false, IsViewable: false })
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        SetFocusFromClient(focus, revertTo, time);
    }

    /// <summary>
    /// 客户端改焦点(SetInputFocus、XI 的 SetDeviceFocus / XISetFocus)。照协议「SetInputFocus」:时间戳早于 last-focus-change time
    /// 或晚于当前服务端时间的请求不生效(CurrentTime 换成当前时间),生效时 last-focus-change time 改成它。宿主 FocusTopLevel 也推进它
    /// (见 <see cref="ApplyFocus" />),所以经 SSH 迟到的 SetInputFocus —— 用户点了 A 又点了 B,A 对 WM_TAKE_FOCUS 的回应这才到 ——
    /// 不再把焦点拉回 A(原先不看时间戳,宿主上亮着的是 B,敲的字进了 A)。
    /// 焦点因此挪到了另一个顶层时请宿主激活它的原生窗口(<see cref="XFocusRequest" />):原先宿主不知道,用户看不出键盘去了哪儿。
    /// </summary>
    private void SetFocusFromClient(XWindow? focus, byte revertTo, uint time)
    {
        uint now = Math.Max(1u, Now);
        if (time == 0)
        {
            time = now;
        }
        if (unchecked((int)(time - _lastFocusChangeTime)) < 0 || unchecked((int)(time - now)) > 0)
        {
            return;
        }
        _lastFocusChangeTime = time;
        XWindow? before = _focus?.TopLevel;
        SetFocus(focus, revertTo);
        if (focus?.TopLevel is { IsViewable: true, OverrideRedirect: false } top && !ReferenceEquals(top, before)
            && _topLevelHandles.TryGetValue(top, out XTopLevelWindow? handle))
        {
            _host.WindowManagerRequested(new XFocusRequest(handle));
        }
    }

    private void GetInputFocus(XClient c)
    {
        uint id = _focus switch
        {
            null => 0,
            { } f when ReferenceEquals(f, Root) => 1,
            { } f => f.Id,
        };
        c.Reply(_focusRevertTo, id, static (w, focus) => w.U32(focus).Zero(20));   // XSync 的往返都是它:不分配闭包
    }

    // ================================================================== 抓取请求

    private void GrabPointer(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        ushort mask = r.U16();
        (bool pointerSync, bool keyboardSync) = ReadGrabModes(r);
        uint confine = r.U32();
        uint cursorId = r.U32();
        r.U32();
        XWindow? confineTo = confine == 0 ? null : Window(confine);
        byte status;
        if (PointerGrab is { } existing && !ReferenceEquals(existing.Client, c))
        {
            status = 1;   // AlreadyGrabbed
        }
        else if (!window.IsViewable || confineTo is { IsViewable: false })
        {
            status = 3;   // GrabNotViewable
        }
        else
        {
            PointerGrab = new ActiveGrab
            {
                Client = c,
                Window = window,
                OwnerEvents = ownerEvents,
                EventMask = mask,
                Cursor = cursorId == 0 ? null : Lookup<XCursorResource>(cursorId),
                ConfineTo = confineTo,
            };
            ApplyGrabModes(PointerGrab, pointerSync, keyboardSync);
            status = 0;
            UpdateCursor();
        }
        c.Reply(status, w => w.Zero(24));
    }

    /// <summary>
    /// 抓取窗口(或指针抓取的 confine-to 窗口)变得不可见时,抓取自动解除(协议「GrabPointer」「GrabKeyboard」)。
    /// 有窗口取消映射时调:它连同全部后代都变得不可见。
    /// </summary>
    private void ReleaseUnviewableGrabs()
    {
        if (PointerGrab is { } pointer && (!pointer.Window.IsViewable || pointer.ConfineTo is { IsViewable: false }))
        {
            PointerGrab = null;
            UpdateCursor();
        }
        if (KeyboardGrab is { } keyboard && !keyboard.Window.IsViewable)
        {
            KeyboardGrab = null;
        }
    }

    private void UngrabPointer(XClient c)
    {
        if (PointerGrab is { } grab && ReferenceEquals(grab.Client, c))
        {
            PointerGrab = null;
            UpdateCursor();
        }
    }

    private void ChangeActivePointerGrab(XClient c, XRequestReader r)
    {
        uint cursorId = r.U32();
        r.U32();
        ushort mask = r.U16();
        if (PointerGrab is { } grab && ReferenceEquals(grab.Client, c))
        {
            grab.EventMask = mask;
            grab.Cursor = cursorId == 0 ? null : Lookup<XCursorResource>(cursorId);
            UpdateCursor();
        }
    }

    private void GrabButton(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        ushort mask = r.U16();
        (bool pointerSync, bool keyboardSync) = ReadGrabModes(r);
        uint confine = r.U32();
        uint cursorId = r.U32();
        byte button = r.U8();
        r.U8();
        ushort modifiers = r.U16();
        foreach (PassiveGrab g in window.ButtonGrabs)
        {
            if (!ReferenceEquals(g.Client, c) && g.Detail == button && g.Modifiers == modifiers)
            {
                throw new XProtocolError(XErrorCode.Access);
            }
        }
        window.ButtonGrabs.RemoveAll(g => ReferenceEquals(g.Client, c) && g.Detail == button && g.Modifiers == modifiers);
        window.ButtonGrabs.Add(new PassiveGrab(c, button, modifiers, ownerEvents, mask,
            confine == 0 ? null : Lookup<XWindow>(confine), cursorId == 0 ? null : Lookup<XCursorResource>(cursorId),
            PointerSync: pointerSync, KeyboardSync: keyboardSync));
    }

    private void UngrabButton(XClient c, XRequestReader r)
    {
        byte button = r.Data;
        XWindow window = Window(r.U32());
        ushort modifiers = r.U16();
        window.ButtonGrabs.RemoveAll(g => ReferenceEquals(g.Client, c)
                                          && (button == 0 || g.Detail == button)
                                          && (modifiers == 0x8000 || g.Modifiers == modifiers));
    }

    private void GrabKeyboard(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        r.U32();   // time
        (bool pointerSync, bool keyboardSync) = ReadGrabModes(r);
        byte status;
        if (KeyboardGrab is { } existing && !ReferenceEquals(existing.Client, c))
        {
            status = 1;
        }
        else if (!window.IsViewable)
        {
            status = 3;
        }
        else
        {
            KeyboardGrab = new ActiveGrab { Client = c, Window = window, OwnerEvents = ownerEvents };
            ApplyGrabModes(KeyboardGrab, pointerSync, keyboardSync);
            status = 0;
        }
        c.Reply(status, w => w.Zero(24));
    }

    private void UngrabKeyboard(XClient c)
    {
        if (KeyboardGrab is { } grab && ReferenceEquals(grab.Client, c))
        {
            KeyboardGrab = null;
        }
    }

    private void GrabKey(XClient c, XRequestReader r)
    {
        bool ownerEvents = r.Data != 0;
        XWindow window = Window(r.U32());
        ushort modifiers = r.U16();
        byte key = r.U8();
        (bool pointerSync, bool keyboardSync) = ReadGrabModes(r);
        if (key is not 0 and < Keymap.MinKeycode)
        {
            throw new XProtocolError(XErrorCode.Value, key);
        }
        foreach (PassiveGrab g in window.KeyGrabs)
        {
            if (!ReferenceEquals(g.Client, c) && g.Detail == key && g.Modifiers == modifiers)
            {
                throw new XProtocolError(XErrorCode.Access);
            }
        }
        window.KeyGrabs.RemoveAll(g => ReferenceEquals(g.Client, c) && g.Detail == key && g.Modifiers == modifiers);
        window.KeyGrabs.Add(new PassiveGrab(c, key, modifiers, ownerEvents, 0, null, null,
            PointerSync: pointerSync, KeyboardSync: keyboardSync));
    }

    private void UngrabKey(XClient c, XRequestReader r)
    {
        byte key = r.Data;
        XWindow window = Window(r.U32());
        ushort modifiers = r.U16();
        window.KeyGrabs.RemoveAll(g => ReferenceEquals(g.Client, c)
                                       && (key == 0 || g.Detail == key)
                                       && (modifiers == 0x8000 || g.Modifiers == modifiers));
    }

    /// <summary>pointer-mode、keyboard-mode 两个字节:Synchronous 0、Asynchronous 1,别的值 BadValue。</summary>
    private static (bool PointerSync, bool KeyboardSync) ReadGrabModes(XRequestReader r)
    {
        byte pointerMode = r.U8(), keyboardMode = r.U8();
        if (pointerMode > 1)
        {
            throw new XProtocolError(XErrorCode.Value, pointerMode);
        }
        if (keyboardMode > 1)
        {
            throw new XProtocolError(XErrorCode.Value, keyboardMode);
        }
        return (pointerMode == 0, keyboardMode == 0);
    }

    // ================================================================== 查询

    private void QueryPointer(XClient c, XRequestReader r)
    {
        XWindow window = Window(r.U32());
        c.MotionHint = default;   // 客户端来问了位置:下一次移动再给它一条提示
        (int wx, int wy) = window.AbsoluteInner();
        uint child = 0;
        for (XWindow? w = _pointerWindow; w is not null; w = w.Parent)
        {
            if (ReferenceEquals(w.Parent, window))
            {
                child = w.Id;
                break;
            }
        }
        int px = Math.Max(0, _pointerX), py = Math.Max(0, _pointerY);
        ushort state = State;
        c.Reply(1, w => w.U32(Root.Id).U32(child).I16(px).I16(py).I16(px - wx).I16(py - wy).U16(state).Zero(6));
    }

    private void WarpPointer(XRequestReader r)
    {
        r.U32();
        uint dst = r.U32();
        r.I16();
        r.I16();
        r.U16();
        r.U16();
        short dx = r.I16(), dy = r.I16();
        if (dst == 0)
        {
            MovePointer(_pointerX + dx, _pointerY + dy, warp: true);
            return;
        }
        (int x, int y) = Window(dst).AbsoluteInner();
        // 宿主的系统指针挪不动(那是用户的鼠标);这里只改服务端认为的指针位置。
        MovePointer(x + dx, y + dy, warp: true);
    }

    private void GetKeyboardMapping(XClient c, XRequestReader r)
    {
        byte first = r.U8();
        byte count = r.U8();
        if (first < Keymap.MinKeycode || first + count - 1 > Keymap.MaxKeycode)
        {
            throw new XProtocolError(XErrorCode.Value, first);
        }
        int per = _keymap.KeysymsPerKeycode;
        c.Reply((byte)per, w =>
        {
            w.Zero(24);
            for (int k = 0; k < count; k++)
            {
                for (int col = 0; col < per; col++)
                {
                    w.U32(_keymap.Keysym((byte)(first + k), col));
                }
            }
        });
    }

    private void ChangeKeyboardMapping(XClient c, XRequestReader r)
    {
        byte count = r.Data;
        byte first = r.U8();
        byte per = r.U8();
        r.Skip(2);
        if (first < Keymap.MinKeycode || first + count - 1 > Keymap.MaxKeycode || per == 0)
        {
            throw new XProtocolError(XErrorCode.Value, first);
        }
        uint[] keysyms = new uint[count * per];
        for (int i = 0; i < keysyms.Length; i++)
        {
            keysyms[i] = r.U32();
        }
        _keymap.Change(first, per, keysyms);
        NotifyXkbMapChanged();
        NotifyKeyboardMappingChanged(first, count);
    }

    private void GetModifierMapping(XClient c)
    {
        byte[] map = _keymap.ModifierMap;
        c.Reply((byte)_keymap.KeycodesPerModifier, w => w.Zero(24).Bytes(map));
    }

    /// <summary>
    /// SetModifierMapping(协议「SetModifierMapping」):非零键码不在 min-keycode … max-keycode 里回 BadValue;某个修饰位的键换了、
    /// 而它的新键或旧键正按着时回 Busy,什么都不改。成功后按新表重算当前的修饰状态 —— 原先一律回 Success,按着 Shift 时把 Shift 换走,
    /// 状态里的 Shift 位要等下一次按键才消失。
    /// </summary>
    private void SetModifierMapping(XClient c, XRequestReader r)
    {
        int per = r.Data;
        byte[] map = r.Bytes(per * 8);
        foreach (byte keycode in map)
        {
            if (keycode is not 0 and < Keymap.MinKeycode)
            {
                throw new XProtocolError(XErrorCode.Value, keycode);
            }
        }
        byte[] old = _keymap.ModifierMap;
        int oldPer = _keymap.KeycodesPerModifier;
        for (int m = 0; m < 8; m++)
        {
            ReadOnlySpan<byte> before = old.AsSpan(m * oldPer, oldPer), after = map.AsSpan(m * per, per);
            if (SameKeys(before, after))
            {
                continue;
            }
            if (AnyDown(before) || AnyDown(after))
            {
                c.Reply(1, w => w.Zero(24));   // Busy
                return;
            }
        }
        _keymap.SetModifierMap(map);
        UpdateModifierState(0, 0);
        NotifyXkbMapChanged();
        c.Reply(0, w => w.Zero(24));
        NotifyModifierMappingChanged();

        static bool SameKeys(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            foreach (byte k in a)
            {
                if (k != 0 && !b.Contains(k))
                {
                    return false;
                }
            }
            foreach (byte k in b)
            {
                if (k != 0 && !a.Contains(k))
                {
                    return false;
                }
            }
            return true;
        }

        bool AnyDown(ReadOnlySpan<byte> keys)
        {
            foreach (byte k in keys)
            {
                if (k != 0 && IsKeyDown(k))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
