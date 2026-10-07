// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「ChangeKeyboardControl」(key-click-percent、bell-percent、bell-pitch、
//   bell-duration 的 −1 = 恢复默认;led / led-mode;key / auto-repeat-mode:全局与逐键的自动重复,「自动重复的键交替产生 KeyPress
//   与 KeyRelease」,「当作修饰键用的键最好不重复」;只给 led 不给 led-mode、只给 key 不给 auto-repeat-mode 是 Match 错误)、
//   「GetKeyboardControl」「Bell」(音量公式)、「SetPointerMapping」「GetPointerMapping」(下标是物理按钮、元素是生效的按钮号,
//   0 停用;长度与 GetPointerMapping 一致,非零元素不重复,否则 Value;要改的按钮按着时 Busy;成功时发 MappingNotify(Pointer))
//   The X Keyboard Extension —— 「The RepeatKeys Control」「The PerKeyRepeat Control」(与核心的自动重复互为表里)、
//   「Detectable Autorepeat」(开了它的客户端在自动重复时收不到中间的 KeyRelease)、XkbGetControls / XkbSetControls(附录 D 的编码)
//   X Input Extension 2.2 ——「DeviceEvent」「RawEvent」的 flags:KeyRepeat(XI2.h 的 1 << 16):物理状态没变、只为重复而发
//
//   自动重复的节奏由宿主定(系统的重复延迟与速率),宿主每次重复调一次 InjectKey(…, repeat: true);这里决定重复发不发、怎么发。
//   XKB 的 repeatDelay / repeatInterval 只记下、回报。

using VelaShell.XServer.Input;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>XI2 事件的 flags:KeyRepeat。</summary>
    private const uint XiKeyRepeatFlag = 1u << 16;

    private const int DefaultKeyClickPercent = 0, DefaultBellPercent = 50, DefaultBellPitch = 400, DefaultBellDuration = 100;

    /// <summary>全局的自动重复(核心 auto-repeat-mode;XKB 的 RepeatKeys 开关)。</summary>
    private bool _autoRepeat = true;

    /// <summary>逐键的自动重复(核心 auto-repeats;XKB 的 PerKeyRepeat):键码 8N … 8N+7 在第 N 字节,低位在前。</summary>
    private readonly byte[] _autoRepeats = DefaultAutoRepeats();

    private int _keyClickPercent = DefaultKeyClickPercent;
    private int _bellPercent = DefaultBellPercent;
    private int _bellPitch = DefaultBellPitch;
    private int _bellDuration = DefaultBellDuration;

    /// <summary>客户端点亮的 LED(第 0 位是 1 号 LED)。锁定键的指示灯另按锁定状态算(见 <see cref="GetKeyboardControl" />)。</summary>
    private uint _leds;

    /// <summary>XKB RepeatKeys 的参数(毫秒):只回报 —— 重复的节奏是宿主的系统设置。</summary>
    private ushort _repeatDelay = 660, _repeatInterval = 40;

    /// <summary>
    /// 逐键自动重复的默认值:除了起步时的修饰键(Shift、Caps Lock、Control、Alt、Num Lock、Super、右 Alt / AltGr)都重复 ——
    /// 宿主自己就会重复修饰键(Windows 上按住 Shift 一直有 KeyDown),原先 X 这边看到的是一串 Shift 按下。
    /// </summary>
    private static byte[] DefaultAutoRepeats()
    {
        byte[] bits = new byte[32];
        Array.Fill(bits, (byte)0xFF);
        ReadOnlySpan<byte> modifierKeys =
        [
            XKeycodes.ShiftLeft, XKeycodes.ShiftRight, XKeycodes.CapsLock, XKeycodes.ControlLeft, XKeycodes.ControlRight,
            XKeycodes.AltLeft, XKeycodes.AltRight, XKeycodes.NumLock, XKeycodes.SuperLeft, XKeycodes.SuperRight,
        ];
        foreach (byte keycode in modifierKeys)
        {
            bits[keycode >> 3] &= (byte)~(1 << (keycode & 7));
        }
        return bits;
    }

    /// <summary>auto-repeat-mode 为 Default 时这个键回到的值(见 <see cref="DefaultAutoRepeats" />)。</summary>
    private static bool DefaultRepeats(byte keycode) => (DefaultAutoRepeats()[keycode >> 3] & (1 << (keycode & 7))) != 0;

    /// <summary>
    /// 这个键按住时该不该自动重复:全局开着、这个键自己开着,而且它此刻不是修饰键(协议「ChangeKeyboardControl」:
    /// 当作修饰键用的键最好不重复 —— xmodmap 把别的键改成修饰键之后同样不重复)。
    /// </summary>
    private bool Repeats(byte keycode) =>
        _autoRepeat && (_autoRepeats[keycode >> 3] & (1 << (keycode & 7))) != 0 && _keymap.ModifierBitOf(keycode) == 0;

    private bool IsKeyDown(byte keycode) => (_keysDown[keycode >> 3] & (1 << (keycode & 7))) != 0;

    /// <summary>
    /// 宿主报的自动重复(键一直按着,见 <see cref="InjectKey(byte, bool, bool)" />)。X 这边没按着这个键(宿主在焦点换过之后补过松开)
    /// 就当普通的按下;这个键不该重复(<see cref="Repeats" />)就丢掉。否则照协议的「交替产生 KeyPress 与 KeyRelease」:先给核心客户端一个
    /// KeyRelease、再给所有人一个 KeyPress —— 开了 DetectableAutoRepeat 的客户端收不到中间的 KeyRelease(XKB「Detectable Autorepeat」),
    /// XI2 客户端只收带 KeyRepeat 标志的 KeyPress。键的按下状态、修饰状态与抓取都不因此改变:中间那个 KeyRelease 不解除被动抓取。
    /// 原先宿主每次重复都注入一次普通的按下,客户端看到「按下、按下……松开」,DetectableAutoRepeat 只登记从不生效、修饰键也跟着重复。
    /// </summary>
    private void KeyRepeat(byte keycode)
    {
        if (!IsKeyDown(keycode))
        {
            KeyEvent(keycode, pressed: true);
            return;
        }
        if (!Repeats(keycode))
        {
            return;
        }
        _motionHintEpoch++;
        SendRawEvent(XiRawKeyPress, keycode, 0, 0, XiKeyRepeatFlag);
        if (KeyboardSource() is not { } source)
        {
            return;
        }
        DeliverDeviceEvent(XEventCode.KeyRelease, keycode, XEventMask.KeyRelease, source, RepeatPhase.Release);
        Delivery? delivered = DeliverDeviceEvent(XEventCode.KeyPress, keycode, XEventMask.KeyPress, source, RepeatPhase.Press);
        if (delivered is not null && KeyboardGrab is not null)
        {
            NoteKeyboardEventReported(keycode, pressed: true);
        }
    }

    /// <summary>自动重复时这一次投递是哪一半(见 <see cref="KeyRepeat" />)。</summary>
    private enum RepeatPhase : byte
    {
        None,

        /// <summary>中间那个 KeyRelease:只给没开 DetectableAutoRepeat 的核心客户端。</summary>
        Release,

        /// <summary>重复的 KeyPress:XI2 的带 KeyRepeat 标志。</summary>
        Press,
    }

    /// <summary>这个客户端在自动重复时收不收中间的 KeyRelease。</summary>
    private bool WantsRepeatRelease(XClient client) => !_xkbDetectableRepeat.Contains(client);

    // ================================================================== 核心请求

    /// <summary>
    /// ChangeKeyboardControl:先读完、核对完再改(出错的请求不产生效果)。值表里每个值占 4 字节、靠右;INT8 / INT16 取低字节按有符号读 ——
    /// Xlib 把 −1 符号扩展成 0xFFFFFFFF,只填低字节的也认。
    /// </summary>
    private void ChangeKeyboardControl(XRequestReader r)
    {
        uint mask = r.U32();
        if ((mask & ~0xFFu) != 0)
        {
            throw new XProtocolError(XErrorCode.Value, mask);
        }
        int? click = null, bell = null, pitch = null, duration = null, led = null, ledMode = null, key = null, repeatMode = null;
        if ((mask & 0x01) != 0)
        {
            click = Percent(r.U32());
        }
        if ((mask & 0x02) != 0)
        {
            bell = Percent(r.U32());
        }
        if ((mask & 0x04) != 0)
        {
            pitch = NonNegative16(r.U32());
        }
        if ((mask & 0x08) != 0)
        {
            duration = NonNegative16(r.U32());
        }
        if ((mask & 0x10) != 0)
        {
            uint v = r.U32() & 0xFF;
            led = v is >= 1 and <= 32 ? (int)v : throw new XProtocolError(XErrorCode.Value, v);
        }
        if ((mask & 0x20) != 0)
        {
            uint v = r.U32() & 0xFF;
            ledMode = v <= 1 ? (int)v : throw new XProtocolError(XErrorCode.Value, v);
        }
        if ((mask & 0x40) != 0)
        {
            uint v = r.U32() & 0xFF;
            key = v >= Keymap.MinKeycode ? (int)v : throw new XProtocolError(XErrorCode.Value, v);
        }
        if ((mask & 0x80) != 0)
        {
            uint v = r.U32() & 0xFF;
            repeatMode = v <= 2 ? (int)v : throw new XProtocolError(XErrorCode.Value, v);
        }
        if ((led is not null && ledMode is null) || (key is not null && repeatMode is null))
        {
            throw new XProtocolError(XErrorCode.Match);
        }

        if (click is { } c)
        {
            _keyClickPercent = c < 0 ? DefaultKeyClickPercent : c;
        }
        if (bell is { } b)
        {
            _bellPercent = b < 0 ? DefaultBellPercent : b;
        }
        if (pitch is { } p)
        {
            _bellPitch = p < 0 ? DefaultBellPitch : p;
        }
        if (duration is { } d)
        {
            _bellDuration = d < 0 ? DefaultBellDuration : d;
        }
        if (ledMode is { } on)
        {
            uint bits = led is { } n ? 1u << (n - 1) : uint.MaxValue;
            _leds = on == 1 ? _leds | bits : _leds & ~bits;
        }
        if (repeatMode is { } mode)
        {
            if (key is { } k)
            {
                byte keycode = (byte)k;
                bool repeat = mode == 2 ? DefaultRepeats(keycode) : mode == 1;
                _autoRepeats[keycode >> 3] = repeat
                    ? (byte)(_autoRepeats[keycode >> 3] | (1 << (keycode & 7)))
                    : (byte)(_autoRepeats[keycode >> 3] & ~(1 << (keycode & 7)));
            }
            else
            {
                _autoRepeat = mode != 0;   // On 与 Default 都是开(xset r off / xset r on)
            }
        }

        static int Percent(uint value)
        {
            int v = (sbyte)(byte)value;
            return v is >= -1 and <= 100 ? v : throw new XProtocolError(XErrorCode.Value, value);
        }

        static int NonNegative16(uint value)
        {
            int v = (short)(ushort)value;
            return v >= -1 ? v : throw new XProtocolError(XErrorCode.Value, value);
        }
    }

    /// <summary>GetKeyboardControl:LED 掩码里另外点亮锁定键的指示灯(1 号 Caps Lock、2 号 Num Lock,与 XKB 的指示灯同号)。</summary>
    private void GetKeyboardControl(XClient c)
    {
        uint leds = _leds | IndicatorState();
        (byte click, byte bell, ushort pitch, ushort duration) = ((byte)_keyClickPercent, (byte)_bellPercent, (ushort)_bellPitch, (ushort)_bellDuration);
        byte[] repeats = [.. _autoRepeats];
        c.Reply(_autoRepeat ? (byte)1 : (byte)0, w => w.U32(leds).U8(click).U8(bell).U16(pitch).U16(duration).Zero(2).Bytes(repeats));
    }

    private void Bell(XRequestReader r)
    {
        sbyte percent = (sbyte)r.Data;
        if (percent is < -100 or > 100)
        {
            throw new XProtocolError(XErrorCode.Value, unchecked((uint)percent));
        }
        RingBell(percent);
    }

    /// <summary>
    /// 响铃(核心 Bell、XKB Bell、XI DeviceBell 共用):<paramref name="percent" /> 是相对基准音量(ChangeKeyboardControl 的 bell-percent)的
    /// −100…100,按协议「Bell」的公式换算成实际音量 0–100 交给宿主。<c>xset b off</c> 把基准音量设成 0:Bell 0 算出来是 0,宿主不出声。
    /// </summary>
    private void RingBell(int percent)
    {
        percent = Math.Clamp(percent, -100, 100);
        int baseVolume = _bellPercent;
        int volume = percent >= 0
            ? baseVolume - (baseVolume * percent / 100) + percent
            : baseVolume + (baseVolume * percent / 100);
        _host.BellRequested(volume);
    }

    // ================================================================== 指针按钮映射

    /// <summary>
    /// 指针的按钮映射:下标 i 是物理按钮 i + 1,元素是它生效时的按钮号(0 = 停用)。长度与 XI 报的按钮数一致;
    /// 更大号的按钮(宿主最多可以注入 255)不经映射。
    /// </summary>
    private readonly byte[] _pointerMap = [.. Enumerable.Range(1, XiButtonCount).Select(b => (byte)b)];

    /// <summary>物理上按着的按钮(按物理按钮号,与 <see cref="_buttonsDown" /> 的生效按钮号区分开):松开与 SetPointerMapping 的 Busy 看它。</summary>
    private readonly byte[] _physicalButtonsDown = new byte[32];

    /// <summary>物理按钮 → 生效的按钮号(0 = 停用)。</summary>
    private int MapButton(int physical) => physical <= _pointerMap.Length ? _pointerMap[physical - 1] : physical;

    private bool IsPhysicalButtonDown(int physical) => (_physicalButtonsDown[physical >> 3] & (1 << (physical & 7))) != 0;

    /// <summary>
    /// SetPointerMapping:左手用户 <c>xmodmap -e "pointer = 3 2 1"</c>。原先回 Success 却不生效、也不发 MappingNotify,
    /// GetPointerMapping 还只报 5 个按钮(XI 报 9 个)。
    /// </summary>
    private void SetPointerMapping(XClient c, XRequestReader r)
    {
        int count = r.Data;
        byte[] map = r.Bytes(count);
        if (count != _pointerMap.Length)
        {
            throw new XProtocolError(XErrorCode.Value, (uint)count);
        }
        HashSet<byte> seen = [];
        foreach (byte b in map)
        {
            if (b != 0 && !seen.Add(b))
            {
                throw new XProtocolError(XErrorCode.Value, b);
            }
        }
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] != _pointerMap[i] && IsPhysicalButtonDown(i + 1))
            {
                c.Reply(1, w => w.Zero(24));   // Busy:要改的按钮正按着,映射不变
                return;
            }
        }
        map.CopyTo(_pointerMap, 0);
        c.Reply(0, w => w.Zero(24));
        foreach (XClient client in _clients.Values)
        {
            client.Event(XEventCode.MappingNotify, 0, w => w.U8(2).U8(0).U8(0));   // request = Pointer
        }
    }

    private void GetPointerMapping(XClient c)
    {
        byte[] map = [.. _pointerMap];
        c.Reply((byte)map.Length, w => w.Zero(24).Bytes(map).Pad4());
    }

    // ================================================================== XKB 的键盘控制

    private void XkbGetControls(XClient c)
    {
        (ushort delay, ushort interval) = (_repeatDelay, _repeatInterval);
        uint enabled = _autoRepeat ? 1u : 0u;   // RepeatKeys
        byte[] perKey = [.. _autoRepeats];
        c.Reply(XkbDeviceId, w =>
        {
            w.U8(0).U8(1).U8(0).U8(0).U8(0).U8(0).U8(0).Zero(1)   // mouseKeysDfltBtn、numGroups = 1、groupsWrap、内部 / 忽略锁定修饰
                .U16(0).U16(0)
                .U16(delay).U16(interval)                          // repeatDelay、repeatInterval(毫秒)
                .U16(300).U16(300).U16(160).U16(40).U16(30).U16(10).I16(0)
                .U16(0).U16(120).U16(0).U16(0).Zero(2)
                .U32(0).U32(0)
                .U32(enabled)
                .Bytes(perKey);
        });
    }

    /// <summary>
    /// XkbSetControls:RepeatKeys(重复延迟与间隔,只记下)、PerKeyRepeat(逐键的自动重复)、ControlsEnabled 里的 RepeatKeys 位(全局的自动重复)
    /// 生效 —— 它们与核心的自动重复是同一份状态(XKB「The RepeatKeys Control」)。别的控制接受但不生效(键盘描述由核心键位表推出)。
    /// changeControls 里有未定义的位、给了 RepeatKeys 而延迟或间隔为 0,回 BadValue。
    /// </summary>
    private void XkbSetControls(XRequestReader r)
    {
        const uint repeatKeys = 1, perKeyRepeat = 0x40000000, controlsEnabled = 0x80000000;
        const uint known = 0x1FFF | 0xF8000000;
        r.Skip(18);   // 内部 / 忽略锁定修饰(真、虚)、mouseKeysDfltBtn、groupsWrap、accessXOptions、pad
        uint affectEnabled = r.U32(), enabled = r.U32(), change = r.U32();
        ushort delay = r.U16(), interval = r.U16();
        r.Skip(28);   // slowKeys、debounce、mouseKeys 五项、accessXTimeout、两个 BOOLCTRL、两个 AXOPTION
        byte[] perKey = r.Bytes(32);
        if ((change & ~known) != 0)
        {
            throw new XProtocolError(XErrorCode.Value, change);
        }
        if ((change & repeatKeys) != 0 && (delay == 0 || interval == 0))
        {
            throw new XProtocolError(XErrorCode.Value, delay == 0 ? delay : interval);
        }
        if ((change & repeatKeys) != 0)
        {
            (_repeatDelay, _repeatInterval) = (delay, interval);
        }
        if ((change & perKeyRepeat) != 0)
        {
            perKey.CopyTo(_autoRepeats, 0);
        }
        if ((change & controlsEnabled) != 0 && (affectEnabled & repeatKeys) != 0)
        {
            _autoRepeat = (enabled & repeatKeys) != 0;
        }
    }
}
