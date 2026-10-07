// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   Inter-Client Communication Conventions Manual (ICCCM) 2.0 —— §2.2「Responsibilities of the Selection Owner」
//   (回应 SelectionRequest:写属性再发 SelectionNotify;property 为 None 的旧式请求用 target 当属性名)、
//   §2.4「Requesting a Selection」(作为请求方:ConvertSelection、读属性、删属性)、
//   §2.5「Large Data Transfers」(INCR 分块协议)、§2.6.2「Target Atoms」(TARGETS、TIMESTAMP、TEXT、STRING)、
//   §2.7.1「Text Properties」(TEXT 由属主挑 STRING / UTF8_STRING / COMPOUND_TEXT,取回的按类型解码)
//   X Window System Protocol —— 「SetSelectionOwner」「ConvertSelection」及 SelectionRequest / SelectionNotify 事件
//
//   与宿主的剪贴板互通:
//   · 宿主 → X:SetClipboardText 让服务端自己占有 CLIPBOARD(可选 PRIMARY),X 客户端来要时直接回;
//   · X → 宿主:X 客户端占有 CLIPBOARD(可选 PRIMARY)时,服务端以一个隐藏的 InputOnly 窗口为请求方
//     把内容要过来(支持 INCR),交给宿主的 ClipboardChanged。

using System.Buffers.Binary;
using System.Text;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    /// <summary>服务端作为选区请求方 / 属主时用的窗口(不映射、不挂进窗口树,客户端的 QueryTree 看不到)。</summary>
    private const uint SelectionWindowId = 0x43;

    private XWindow? _selectionWindow;

    /// <summary>宿主最近一次给的文本;服务端占有选区时拿它回应(见 <see cref="SetHostClipboard" />)。</summary>
    private string _hostClipboard = "";

    /// <summary>宿主文本的 UTF-8 / Latin-1 / COMPOUND_TEXT 编码:第一次有人要时编一次,之后各次 ConvertSelection 共用(原先每次都重编码)。</summary>
    private byte[]? _hostClipboardUtf8, _hostClipboardLatin1, _hostClipboardCompound;

    private void SetHostClipboard(string text)
    {
        _hostClipboard = text;
        _hostClipboardUtf8 = null;
        _hostClipboardLatin1 = null;
        _hostClipboardCompound = null;
    }

    private byte[] HostClipboardUtf8 => _hostClipboardUtf8 ??= Encoding.UTF8.GetBytes(_hostClipboard);

    private byte[] HostClipboardLatin1 => _hostClipboardLatin1 ??= Encoding.Latin1.GetBytes(_hostClipboard);

    /// <summary>最近一次交给宿主的文本 —— 宿主把它写回来时不再抢选区(防回声)。</summary>
    private string? _lastDeliveredText;

    /// <summary>
    /// 进行中的「从 X 客户端取选区」,每个选区一份(CLIPBOARD 与 PRIMARY 同时变化时各取各的,原先只有一个槽,后来的把先来的冲掉)。
    /// </summary>
    private readonly Dictionary<uint, SelectionFetch> _fetches = [];

    /// <summary>取选区时每一步(等 SelectionNotify、等下一块 INCR)等属主的时限,过了就放弃(测试可以调短)。</summary>
    internal TimeSpan FetchStepTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>一次进行中的「从 X 客户端取选区」。</summary>
    private sealed class SelectionFetch(uint selection, uint target, uint time, uint property)
    {
        public uint Selection { get; } = selection;

        public uint Target { get; set; } = target;

        public uint Time { get; } = time;

        /// <summary>属主把内容写到服务端请求窗口上的这个属性(每个选区一个,互不干扰)。</summary>
        public uint Property { get; } = property;

        /// <summary>INCR 传输中累积的字节;不在 INCR 里为 null。</summary>
        public List<byte>? Incr { get; set; }

        public uint IncrType { get; set; }

        /// <summary>第几步:限时检查只认安排它时的那一步。</summary>
        public int Step { get; set; }
    }

    private XWindow SelectionWindow
    {
        get
        {
            if (_selectionWindow is null)
            {
                _selectionWindow = new XWindow(SelectionWindowId, null, Root)
                {
                    Class = 2,   // InputOnly
                    Width = 1,
                    Height = 1,
                    Visual = RootVisualId,
                };
                _resources[SelectionWindowId] = _selectionWindow;
            }
            return _selectionWindow;
        }
    }

    /// <summary>宿主的剪贴板有了新文本(见 <see cref="SetClipboardText" />):服务端替宿主占有 CLIPBOARD(与 PRIMARY)。</summary>
    private void ApplyClipboardText(string text)
    {
        if (!_options.SyncClipboard || text == _lastDeliveredText)
        {
            return;   // 宿主把我们刚给的写回来了
        }
        SetHostClipboard(text);
        TakeSelectionForHost(Intern("CLIPBOARD"));
        if (_options.SyncPrimary)
        {
            TakeSelectionForHost(XAtom.Primary);
        }
    }

    private void TakeSelectionForHost(uint selection)
    {
        // 宿主的占有也是一次换属主:不早于最后一次换属主的时间(否则之后带着更早事件时间的客户端反而能抢回来),并推进它。
        uint now = Now;
        if (_selectionLastChange.TryGetValue(selection, out uint lastChange) && unchecked((int)(lastChange - now)) > 0)
        {
            now = lastChange;
        }
        _selectionLastChange[selection] = now;
        if (_selections.TryGetValue(selection, out (XWindow Window, XClient? Client, uint Time) current) && current.Client is { } previous)
        {
            XWindow old = current.Window;
            previous.Event(XEventCode.SelectionClear, 0, w => w.U32(now).U32(old.Id).U32(selection));
        }
        _selections[selection] = (SelectionWindow, null, now);
        // XFIXES 的属主变化通知只发给读得到宿主文本的会话(InFocusedSession):服务端已经是属主时 GetSelectionOwner 看不出变化,
        // 原先别的会话靠这条通知精确得知「宿主剪贴板有了新内容」,等用户切到它的窗口时去取(xs_plan WN-S11)。
        NotifySelectionChange(selection, 0, SelectionWindowId, now, InFocusedSession);
    }

    /// <summary>
    /// 同步的选区没了属主(属主 SetSelectionOwner(None)、属主窗口销毁、属主断开):服务端替宿主接管,内容是最近一次交给宿主的文本 ——
    /// 相当于剪贴板管理器。原先 X 程序复制之后一退出,别的 X 程序就再也粘贴不到,宿主手里明明还有这段文本(两道防回声都拦着它)。
    /// </summary>
    private void OnSelectionOwnerLost(uint selection)
    {
        if (IsSyncedSelection(selection) && _lastDeliveredText is { } text && !_selections.ContainsKey(selection))
        {
            SetHostClipboard(text);
            TakeSelectionForHost(selection);
        }
    }

    private bool IsSyncedSelection(uint selection) =>
        _options.SyncClipboard && (selection == Intern("CLIPBOARD") || (_options.SyncPrimary && selection == XAtom.Primary));

    /// <summary>
    /// 这个客户端此刻能不能与宿主的剪贴板来往(<see cref="X11ServerOptions.ClipboardFollowsFocus" />):它是键盘焦点所在顶层的客户端
    /// (焦点是 PointerRoot 时看指针所在的顶层),或与那个客户端的连接名相同(同一个会话)。
    /// </summary>
    private bool InFocusedSession(XClient client)
    {
        if (!_options.ClipboardFollowsFocus)
        {
            return true;
        }
        XWindow? focused = _focus is null ? null : ReferenceEquals(_focus, Root) ? _pointerWindow : _focus;
        return focused?.TopLevel?.Owner is { } peer
               && (ReferenceEquals(peer, client) || (peer.Label is { } label && label == client.Label));
    }

    // ------------------------------------------------------------------ 服务端当属主

    /// <summary>服务端占有的选区被 ConvertSelection 了:按目标写属性,再发 SelectionNotify(ICCCM §2.2)。</summary>
    private void ServeSelection(XClient c, XWindow requestor, uint selection, uint target, uint property, uint time, uint ownerTime)
    {
        bool oldStyle = property == 0;
        if (oldStyle)
        {
            property = target;   // 旧式请求方(ICCCM §2.2)
        }
        if (selection != Intern("CLIPBOARD") && selection != XAtom.Primary)
        {
            property = 0;   // 服务端占有的其它选区(_XSETTINGS_S0 这类管理器选区)没有可转换的内容
        }
        else if (!InFocusedSession(c))
        {
            property = 0;   // 宿主的文本只给键盘焦点所在的会话:原先本机复制的密码在用户点一下任意 X 窗口后,所有会话的所有客户端都读得到
        }
        else if (target == Intern("MULTIPLE"))
        {
            if (oldStyle || !ServeMultiple(requestor, property, ownerTime))
            {
                property = 0;   // MULTIPLE 必须给属性(放目标与属性对的那个)
            }
        }
        else if (ConvertHostSelection(target, ownerTime) is { } value)
        {
            WriteConverted(requestor, property, value);
        }
        else
        {
            property = 0;   // 不支持的目标:拒绝
        }
        XClient to = requestor.Owner is { Closed: false } creator ? creator : c;
        to.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(selection).U32(target).U32(property));
    }

    /// <summary>宿主的文本按目标转换;不支持的目标为 null。</summary>
    private (uint Type, byte Format, byte[] Data)? ConvertHostSelection(uint target, uint ownerTime)
    {
        uint utf8 = Intern("UTF8_STRING");
        uint targets = Intern("TARGETS");
        uint timestamp = Intern("TIMESTAMP");
        uint text = Intern("TEXT");
        uint plainUtf8 = Intern("text/plain;charset=utf-8");
        uint compound = Intern("COMPOUND_TEXT");
        if (target == targets)
        {
            // ICCCM §2.6.2:属主必须支持 TARGETS、MULTIPLE、TIMESTAMP。原先不列 MULTIPLE 与 COMPOUND_TEXT。
            uint[] atoms = [targets, Intern("MULTIPLE"), timestamp, utf8, plainUtf8, compound, XAtom.String, text];
            byte[] data = new byte[atoms.Length * 4];
            for (int i = 0; i < atoms.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), atoms[i]);
            }
            return (XAtom.Atom, 32, data);
        }
        if (target == timestamp)
        {
            byte[] data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, ownerTime);
            return (XAtom.Integer, 32, data);
        }
        if (target == utf8 || target == plainUtf8)
        {
            return (target, 8, HostClipboardUtf8);
        }
        if (target == compound)
        {
            return (compound, 8, _hostClipboardCompound ??= XText.EncodeCompoundText(_hostClipboard));   // Motif / Xaw 要的
        }
        if (target == text && !IsLatin1(_hostClipboard))
        {
            // TEXT 由属主挑编码(ICCCM §2.6.2):Latin-1 装不下(中日韩)就回 UTF8_STRING —— 原先按 Latin-1 有损转换,汉字变成「?」。
            return (utf8, 8, HostClipboardUtf8);
        }
        if (target == XAtom.String || target == text)
        {
            return (XAtom.String, 8, HostClipboardLatin1);
        }
        return null;
    }

    /// <summary>转换结果写到请求方的属性上:大的分块交(ICCCM §2.5)。</summary>
    private void WriteConverted(XWindow requestor, uint property, (uint Type, byte Format, byte[] Data) value)
    {
        if (value.Format == 8 && value.Data.Length > IncrChunkBytes)
        {
            StartIncrTransfer(requestor, property, value.Type, value.Data);
            return;
        }
        StoreServerProperty(requestor, property, new XProperty(value.Type, value.Format, value.Data));
        SendPropertyNotify(requestor, property, deleted: false);
    }

    /// <summary>MULTIPLE 里最多看这么多对(真实的请求方一次要几个目标)。</summary>
    private const int MaxMultiplePairs = 64;

    /// <summary>
    /// ICCCM §2.6.2「MULTIPLE」:请求方在 <paramref name="property" /> 里放一串(目标, 属性)对(ATOM_PAIR);逐个转换写到各自的属性上,
    /// 转换不了的把那一对的属性换成 None 再写回去。属性不在或格式不对时整个拒绝。
    /// </summary>
    private bool ServeMultiple(XWindow requestor, uint property, uint ownerTime)
    {
        if (!requestor.Properties.TryGetValue(property, out XProperty? list) || list.Format != 32)
        {
            return false;
        }
        uint[] pairs = ReadCard32s(list, MaxMultiplePairs * 2);
        bool refused = false;
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            uint target = pairs[i], destination = pairs[i + 1];
            if (destination == 0)
            {
                continue;
            }
            // 属性名必须是存在的原子(同 ConvertSelection);MULTIPLE 不能套 MULTIPLE。
            if (target == Intern("MULTIPLE") || AtomName(destination) is null || ConvertHostSelection(target, ownerTime) is not { } value)
            {
                pairs[i + 1] = 0;
                refused = true;
                continue;
            }
            WriteConverted(requestor, destination, value);
        }
        if (refused)
        {
            SetProperty(requestor, property, list.Type, pairs);
        }
        return true;
    }

    /// <summary>
    /// 服务端当属主时,超过这么多字节的文本按 INCR 分块交,每块这么大。原先整份写成一个属性:绕过了单个属性的上限,
    /// 请求方一次取回几十 MB 又会撞上输出积压上限被断开。
    /// </summary>
    internal const int IncrChunkBytes = 256 * 1024;

    /// <summary>同时进行的 INCR 传输上限(多了丢掉最早的)与每一步等请求方的时限。</summary>
    private const int MaxOutgoingIncr = 32;

    internal TimeSpan IncrStepTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>一次服务端当属主的 INCR 传输:(请求窗口, 属性) → 进度。</summary>
    private readonly Dictionary<(XWindow Window, uint Property), OutgoingIncr> _outgoingIncr = [];

    private sealed class OutgoingIncr(uint type, byte[] data)
    {
        public uint Type { get; } = type;

        public byte[] Data { get; } = data;

        public int Offset { get; set; }

        /// <summary>最后那块空的已经写出:请求方删掉它就结束。</summary>
        public bool Finished { get; set; }

        /// <summary>第几步:限时检查只认安排它时的那一步。</summary>
        public int Step { get; set; }
    }

    /// <summary>
    /// ICCCM §2.5:属性先写成类型 INCR、值是总长的下限,发 SelectionNotify;请求方每删一次属性,就写下一块(类型是真正的类型),
    /// 最后写一块空的表示结束。块之间请求方迟迟不删,传输作废。
    /// </summary>
    private void StartIncrTransfer(XWindow requestor, uint property, uint type, byte[] data)
    {
        if (_outgoingIncr.Count >= MaxOutgoingIncr)
        {
            _outgoingIncr.Remove(_outgoingIncr.Keys.First());
        }
        OutgoingIncr transfer = new(type, data);
        _outgoingIncr[(requestor, property)] = transfer;
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        StoreServerProperty(requestor, property, new XProperty(Intern("INCR"), 32, size));
        SendPropertyNotify(requestor, property, deleted: false);
        ExpireIncrLater(requestor, property, transfer);
    }

    /// <summary>请求方删掉了属性(见 <see cref="SendPropertyNotify" />):是一次 INCR 传输在等的,就写下一块。</summary>
    private void OnIncrPropertyDeleted(XWindow window, uint property)
    {
        if (!_outgoingIncr.TryGetValue((window, property), out OutgoingIncr? transfer))
        {
            return;
        }
        if (transfer.Finished)
        {
            _outgoingIncr.Remove((window, property));
            return;
        }
        int length = Math.Min(IncrChunkBytes, transfer.Data.Length - transfer.Offset);
        byte[] chunk = transfer.Data.AsSpan(transfer.Offset, length).ToArray();
        transfer.Offset += length;
        transfer.Finished = length == 0;
        transfer.Step++;
        StoreServerProperty(window, property, new XProperty(transfer.Type, 8, chunk));
        SendPropertyNotify(window, property, deleted: false);
        ExpireIncrLater(window, property, transfer);
    }

    private void ExpireIncrLater(XWindow window, uint property, OutgoingIncr transfer)
    {
        int step = transfer.Step;
        _ = DelayThenPostAsync((uint)IncrStepTimeout.TotalMilliseconds, () =>
        {
            if (_outgoingIncr.TryGetValue((window, property), out OutgoingIncr? current) && ReferenceEquals(current, transfer) && current.Step == step)
            {
                _outgoingIncr.Remove((window, property));   // 请求方不再取了(或窗口已经没了)
            }
        }, _lifetime.Token);
    }

    // ------------------------------------------------------------------ 服务端当请求方

    /// <summary>X 客户端占有了同步的选区:向它要 UTF8_STRING(不给再退回 STRING)。</summary>
    private void OnClientTookSelection(XClient owner, XWindow ownerWindow, uint selection, uint time)
    {
        if (!IsSyncedSelection(selection) || !InFocusedSession(owner))
        {
            return;   // 后台会话的复制不进系统剪贴板:否则远端程序可以反复改写本机剪贴板,用户往别处粘贴时中招
        }
        SelectionFetch fetch = new(selection, Intern("UTF8_STRING"), time, Intern("_VELASHELL_" + (AtomName(selection) ?? "SELECTION")));
        _fetches[selection] = fetch;   // 同一个选区又换了属主:旧的那次作废
        RequestFetch(fetch, owner, ownerWindow);
    }

    private void RequestFetch(SelectionFetch fetch, XClient owner, XWindow ownerWindow)
    {
        uint requestor = SelectionWindow.Id;
        owner.Event(XEventCode.SelectionRequest, 0, w => w
            .U32(fetch.Time).U32(ownerWindow.Id).U32(requestor).U32(fetch.Selection).U32(fetch.Target).U32(fetch.Property));
        ExpireFetchLater(fetch);
    }

    /// <summary>属主迟迟不回(卡死、不理 SelectionRequest、INCR 写到一半不写了):过了时限放弃这次,不一直占着。原先没有时限。</summary>
    private void ExpireFetchLater(SelectionFetch fetch)
    {
        int step = ++fetch.Step;
        _ = DelayThenPostAsync((uint)FetchStepTimeout.TotalMilliseconds, () =>
        {
            if (_fetches.TryGetValue(fetch.Selection, out SelectionFetch? current) && ReferenceEquals(current, fetch) && current.Step == step)
            {
                EndFetch(fetch);
            }
        }, _lifetime.Token);
    }

    private void EndFetch(SelectionFetch fetch)
    {
        if (_fetches.TryGetValue(fetch.Selection, out SelectionFetch? current) && ReferenceEquals(current, fetch))
        {
            _fetches.Remove(fetch.Selection);
        }
        DeleteSelectionProperty(fetch.Property);
    }

    /// <summary>某个客户端断开了:正在取的选区若已没了属主,这次取不回来了。</summary>
    private void DropOrphanedFetches()
    {
        foreach (SelectionFetch fetch in _fetches.Values.ToArray())
        {
            if (!_selections.ContainsKey(fetch.Selection))
            {
                EndFetch(fetch);
            }
        }
    }

    /// <summary>属主用 SendEvent 把 SelectionNotify 发到了我们的请求窗口。</summary>
    private void OnSelectionWindowEvent(byte[] raw, bool bigEndian)
    {
        if ((raw[0] & 0x7F) != XEventCode.SelectionNotify)
        {
            return;
        }
        uint selection = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(12)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        uint property = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(20)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(20));
        if (!_fetches.TryGetValue(selection, out SelectionFetch? fetch) || fetch.Incr is not null)
        {
            return;
        }
        if (property == 0)
        {
            // 属主不给这个目标:UTF8_STRING 不行就退回 STRING,再不行就算了。
            if (fetch.Target != XAtom.String && _selections.TryGetValue(selection, out (XWindow Window, XClient? Client, uint Time) owner) && owner.Client is { } client)
            {
                fetch.Target = XAtom.String;
                RequestFetch(fetch, client, owner.Window);
            }
            else
            {
                EndFetch(fetch);
            }
            return;
        }

        if (property != fetch.Property || !SelectionWindow.Properties.TryGetValue(property, out XProperty? value))
        {
            EndFetch(fetch);
            return;
        }
        if (value.Type == Intern("INCR"))
        {
            // 大数据:删掉属性表示「准备好了」,属主随后一块块往里写(ICCCM §2.5)。
            fetch.Incr = [];
            DeleteSelectionProperty(property);
            ExpireFetchLater(fetch);
            return;
        }
        EndFetch(fetch);
        Deliver(value.Type, value.Data.ToArray());
    }

    /// <summary>INCR 传输中:属主往请求窗口写了一块。空块表示结束。</summary>
    private void OnSelectionWindowProperty(uint property, bool deleted)
    {
        if (deleted || _fetches.Count == 0)
        {
            return;
        }
        SelectionFetch? fetch = null;
        foreach (SelectionFetch candidate in _fetches.Values)
        {
            if (candidate.Property == property && candidate.Incr is not null)
            {
                fetch = candidate;
            }
        }
        if (fetch is not { Incr: { } buffer })
        {
            return;
        }
        XProperty chunk = SelectionWindow.Properties[property];
        DeleteSelectionProperty(property);
        if (chunk.Data.Length == 0)
        {
            EndFetch(fetch);
            Deliver(fetch.IncrType, [.. buffer]);
            return;
        }
        fetch.IncrType = chunk.Type;
        buffer.AddRange(chunk.Data);
        if (buffer.Count > MaxClipboardBytes)
        {
            EndFetch(fetch);   // 太大:放弃。属主写下一块时没人删属性,它自己会超时
            return;
        }
        ExpireFetchLater(fetch);
    }

    private static bool IsLatin1(string text) => !text.AsSpan().ContainsAnyExceptInRange('\0', '\u00FF');

    private void DeleteSelectionProperty(uint property)
    {
        if (SelectionWindow.Properties.Remove(property, out XProperty? removed))
        {
            ReleaseProperty(removed);
            SendPropertyNotify(SelectionWindow, property, deleted: true);
        }
    }

    private void Deliver(uint type, byte[] data)
    {
        if (data.Length > MaxClipboardBytes)
        {
            return;
        }
        // 我们要的是 UTF8_STRING(不给再要 STRING),属主回的类型照样按类型解码:STRING 是 Latin-1,COMPOUND_TEXT 解转义序列。
        string text = XText.Decode(data, type == XAtom.String ? XTextEncoding.Latin1
            : type == Intern("COMPOUND_TEXT") ? XTextEncoding.CompoundText
            : XTextEncoding.Utf8);
        _lastDeliveredText = text;
        _host.ClipboardChanged(text);
    }
}
