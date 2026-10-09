// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「GrabPointer」「GrabButton」「GrabKeyboard」「GrabKey」各节
//   (主动抓取、被动抓取的激活条件、owner-events 的含义、按钮全部松开时解除、pointer-mode / keyboard-mode)

using VelaShell.XServer.Resources;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Input;

/// <summary>被动抓取(GrabButton / GrabKey 登记在窗口上的)。</summary>
/// <param name="Client">登记的客户端。</param>
/// <param name="Detail">按钮号或键码;0 = AnyButton / AnyKey。</param>
/// <param name="Modifiers">修饰键组合;0x8000 = AnyModifier。</param>
/// <param name="OwnerEvents">owner-events。</param>
/// <param name="EventMask">指针抓取的事件掩码(按键抓取不用)。</param>
/// <param name="ConfineTo">限制指针的窗口:只约束 WarpPointer(用户的鼠标由宿主管,约束它要靠宿主,xs_plan F8)。</param>
/// <param name="Cursor">抓取期间的光标。</param>
/// <param name="Xi2">XInput2 的被动抓取(XIPassiveGrabDevice):激活后事件以 XI2 格式投递。</param>
/// <param name="Xi2Mask">XI2 抓取的事件掩码(按 evtype)。</param>
/// <param name="PointerSync">pointer-mode 是 Synchronous:激活之后指针冻结,等 AllowEvents。</param>
/// <param name="KeyboardSync">keyboard-mode 是 Synchronous:激活之后键盘冻结。</param>
internal sealed record PassiveGrab(
    XClient Client, int Detail, ushort Modifiers, bool OwnerEvents, uint EventMask, XWindow? ConfineTo, XCursorResource? Cursor,
    bool Xi2 = false, ulong Xi2Mask = 0, bool PointerSync = false, bool KeyboardSync = false)
{
    /// <summary>AnyButton / AnyKey。</summary>
    public const int AnyDetail = 0;

    /// <summary>AnyModifier。</summary>
    public const ushort AnyModifier = 0x8000;

    /// <summary>一个抓取里最多减掉这么多个组合(见 <see cref="Exclusions" />)。</summary>
    public const int MaxExclusions = 1024;

    /// <summary>
    /// 从这个抓取里减掉的组合(各自也可以是 Any):同一客户端后来对其中一部分 Ungrab,或者对其中一部分另登记了抓取。
    /// 协议:AnyModifier / AnyButton 等于对所有组合各登记一次,所以 Ungrab 掉其中一个组合,其余的照样有效(原先 Ungrab 不拆分,
    /// 要么整个删掉,要么什么都不做)。
    /// </summary>
    public HashSet<(int Detail, ushort Modifiers)>? Exclusions { get; set; }

    public bool Matches(int detail, ushort modifiers) =>
        (Detail == AnyDetail || Detail == detail) && (Modifiers == AnyModifier || Modifiers == (modifiers & 0xFF))
        && !Excludes(detail, (ushort)(modifiers & 0xFF));

    /// <summary>具体的组合 (<paramref name="detail" />, <paramref name="modifiers" />) 是不是减掉了。</summary>
    private bool Excludes(int detail, ushort modifiers)
    {
        if (Exclusions is null)
        {
            return false;
        }
        foreach ((int d, ushort m) in Exclusions)
        {
            if ((d == AnyDetail || d == detail) && (m == AnyModifier || m == modifiers))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>与 (<paramref name="detail" />, <paramref name="modifiers" />)(都可以是 Any)有没有共同的组合。</summary>
    public bool Overlaps(int detail, ushort modifiers) =>
        (Detail == AnyDetail || detail == AnyDetail || Detail == detail)
        && (Modifiers == AnyModifier || modifiers == AnyModifier || Modifiers == modifiers)
        && !(detail != AnyDetail && modifiers != AnyModifier && Excludes(detail, modifiers));

    /// <summary>(<paramref name="detail" />, <paramref name="modifiers" />) 把这个抓取的全部组合都盖住了。</summary>
    public bool CoveredBy(int detail, ushort modifiers) =>
        (detail == AnyDetail || detail == Detail) && (modifiers == AnyModifier || modifiers == Modifiers);
}

/// <summary>
/// 一个窗口上的被动抓取(按钮或按键),按 detail 分桶:按下时只看这个 detail 的与 AnyButton / AnyKey 的两桶,不扫整张表。
/// 原先是一张表,每次按键、按钮都从根到源窗口逐个窗口线性扫。
/// </summary>
internal sealed class PassiveGrabTable : IEnumerable<PassiveGrab>
{
    private readonly Dictionary<int, List<PassiveGrab>> _byDetail = [];

    public int Count { get; private set; }

    public void Add(PassiveGrab grab)
    {
        if (!_byDetail.TryGetValue(grab.Detail, out List<PassiveGrab>? bucket))
        {
            _byDetail[grab.Detail] = bucket = [];
        }
        bucket.Add(grab);
        Count++;
    }

    public int RemoveAll(Predicate<PassiveGrab> match)
    {
        int removed = 0;
        foreach ((int detail, List<PassiveGrab> bucket) in _byDetail)
        {
            removed += bucket.RemoveAll(match);
            if (bucket.Count == 0)
            {
                _byDetail.Remove(detail);   // 枚举中删除当前键:Dictionary 允许
            }
        }
        Count -= removed;
        return removed;
    }

    /// <summary>与 <paramref name="detail" /> 有关的抓取:这个 detail 的与 Any 的;<paramref name="detail" /> 本身是 Any 时全部。</summary>
    public IEnumerable<PassiveGrab> Overlapping(int detail)
    {
        if (detail == PassiveGrab.AnyDetail)
        {
            return this;
        }
        IEnumerable<PassiveGrab> specific = _byDetail.TryGetValue(detail, out List<PassiveGrab>? bucket) ? bucket : [];
        return _byDetail.TryGetValue(PassiveGrab.AnyDetail, out List<PassiveGrab>? any) ? specific.Concat(any) : specific;
    }

    /// <summary>匹配这次按下(<paramref name="detail" />、修饰状态)的抓取:先看这个 detail 的,再看 Any 的;属主已断开的不算。</summary>
    public PassiveGrab? Find(int detail, ushort modifiers)
    {
        if (Count == 0)
        {
            return null;
        }
        return Match(detail) ?? Match(PassiveGrab.AnyDetail);

        PassiveGrab? Match(int key)
        {
            if (_byDetail.TryGetValue(key, out List<PassiveGrab>? bucket))
            {
                foreach (PassiveGrab grab in bucket)
                {
                    if (grab.Matches(detail, modifiers) && !grab.Client.Closed)
                    {
                        return grab;
                    }
                }
            }
            return null;
        }
    }

    public IEnumerator<PassiveGrab> GetEnumerator() => _byDetail.Values.SelectMany(bucket => bucket).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>一个生效中的抓取(指针或键盘)。</summary>
internal sealed class ActiveGrab
{
    public required XClient Client { get; init; }

    public required XWindow Window { get; init; }

    public bool OwnerEvents { get; init; }

    public uint EventMask { get; set; }

    public XCursorResource? Cursor { get; set; }

    /// <summary>指针抓取的 confine-to 窗口;它变得不可见时抓取随之解除。</summary>
    public XWindow? ConfineTo { get; init; }

    /// <summary>XInput2 的抓取:事件以 XI2 格式、按 <see cref="Xi2Mask" /> 投递。</summary>
    public bool Xi2 { get; init; }

    public ulong Xi2Mask { get; set; }

    /// <summary>由按钮按下自动(或被动抓取)激活 —— 按钮全部松开时自动解除。</summary>
    public bool ReleaseWhenButtonsUp { get; init; }

    /// <summary>按钮按下时服务端自动建立的抓取(协议「ButtonPress」);激活时的 Grab 模式 crossing 由 PressButton 在投递 ButtonPress 之前发。</summary>
    public bool Automatic { get; init; }

    /// <summary>
    /// 这个抓取生效的服务端时间(协议的 last-pointer-grab / last-keyboard-grab time):主动抓取是请求里的时间(CurrentTime 换成当前时间),
    /// 被动与自动抓取是激活它的那个事件的时间。0 = 还没定,抓取生效时由服务端填。
    /// </summary>
    public uint Time { get; set; }
}
