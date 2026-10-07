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

    /// <summary>宿主文本的 UTF-8 / Latin-1 编码:第一次有人要时编一次,之后各次 ConvertSelection 共用(原先每次都重编码)。</summary>
    private byte[]? _hostClipboardUtf8, _hostClipboardLatin1;

    private void SetHostClipboard(string text)
    {
        _hostClipboard = text;
        _hostClipboardUtf8 = null;
        _hostClipboardLatin1 = null;
    }

    private byte[] HostClipboardUtf8 => _hostClipboardUtf8 ??= Encoding.UTF8.GetBytes(_hostClipboard);

    private byte[] HostClipboardLatin1 => _hostClipboardLatin1 ??= Encoding.Latin1.GetBytes(_hostClipboard);

    /// <summary>最近一次交给宿主的文本 —— 宿主把它写回来时不再抢选区(防回声)。</summary>
    private string? _lastDeliveredText;

    private SelectionFetch? _fetch;

    /// <summary>一次进行中的「从 X 客户端取选区」。</summary>
    private sealed class SelectionFetch(uint selection, uint target, uint time)
    {
        public uint Selection { get; } = selection;

        public uint Target { get; set; } = target;

        public uint Time { get; } = time;

        /// <summary>INCR 传输中累积的字节;不在 INCR 里为 null。</summary>
        public List<byte>? Incr { get; set; }

        public uint IncrType { get; set; }
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
        if (property == 0)
        {
            property = target;   // 旧式请求方(ICCCM §2.2)
        }
        uint utf8 = Intern("UTF8_STRING");
        uint targets = Intern("TARGETS");
        uint timestamp = Intern("TIMESTAMP");
        uint text = Intern("TEXT");
        uint plainUtf8 = Intern("text/plain;charset=utf-8");

        (uint Type, byte Format, byte[] Data)? value = null;
        if (selection != Intern("CLIPBOARD") && selection != XAtom.Primary)
        {
            // 服务端占有的其它选区(_XSETTINGS_S0 这类管理器选区)没有可转换的内容。
        }
        else if (!InFocusedSession(c))
        {
            // 宿主的文本只给键盘焦点所在的会话:原先本机复制的密码在用户点一下任意 X 窗口后,所有会话的所有客户端都读得到。
        }
        else if (target == targets)
        {
            uint[] atoms = [targets, timestamp, utf8, plainUtf8, XAtom.String, text];
            byte[] data = new byte[atoms.Length * 4];
            for (int i = 0; i < atoms.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), atoms[i]);
            }
            value = (XAtom.Atom, 32, data);
        }
        else if (target == timestamp)
        {
            byte[] data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, ownerTime);
            value = (XAtom.Integer, 32, data);
        }
        else if (target == utf8 || target == plainUtf8)
        {
            value = (target, 8, HostClipboardUtf8);
        }
        else if (target == text && !IsLatin1(_hostClipboard))
        {
            // TEXT 由属主挑编码(ICCCM §2.6.2):Latin-1 装不下(中日韩)就回 UTF8_STRING —— 原先按 Latin-1 有损转换,汉字变成「?」。
            value = (utf8, 8, HostClipboardUtf8);
        }
        else if (target == XAtom.String || target == text)
        {
            value = (XAtom.String, 8, HostClipboardLatin1);
        }

        if (value is not { } v)
        {
            property = 0;   // 不支持的目标:拒绝
        }
        else if (v.Format == 8 && v.Data.Length > IncrChunkBytes)
        {
            StartIncrTransfer(requestor, property, v.Type, v.Data);   // 大的分块交(ICCCM §2.5)
        }
        else
        {
            StoreServerProperty(requestor, property, new XProperty(v.Type, v.Format, v.Data));
            SendPropertyNotify(requestor, property, deleted: false);
        }
        XClient to = requestor.Owner is { Closed: false } creator ? creator : c;
        to.Event(XEventCode.SelectionNotify, 0, w => w.U32(time).U32(requestor.Id).U32(selection).U32(target).U32(property));
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
        _fetch = new SelectionFetch(selection, Intern("UTF8_STRING"), time);
        RequestFetch(owner, ownerWindow);
    }

    private void RequestFetch(XClient owner, XWindow ownerWindow)
    {
        if (_fetch is not { } fetch)
        {
            return;
        }
        uint property = Intern("_VELASHELL_SELECTION");
        uint requestor = SelectionWindow.Id;
        owner.Event(XEventCode.SelectionRequest, 0, w => w
            .U32(fetch.Time).U32(ownerWindow.Id).U32(requestor).U32(fetch.Selection).U32(fetch.Target).U32(property));
    }

    /// <summary>属主用 SendEvent 把 SelectionNotify 发到了我们的请求窗口。</summary>
    private void OnSelectionWindowEvent(byte[] raw, bool bigEndian)
    {
        if ((raw[0] & 0x7F) != XEventCode.SelectionNotify || _fetch is not { } fetch)
        {
            return;
        }
        uint selection = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(12)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        uint property = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(20)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(20));
        if (selection != fetch.Selection)
        {
            return;
        }
        if (property == 0)
        {
            // 属主不给这个目标:UTF8_STRING 不行就退回 STRING,再不行就算了。
            if (fetch.Target != XAtom.String && _selections.TryGetValue(selection, out (XWindow Window, XClient? Client, uint Time) owner) && owner.Client is { } client)
            {
                fetch.Target = XAtom.String;
                RequestFetch(client, owner.Window);
            }
            else
            {
                _fetch = null;
            }
            return;
        }

        XWindow window = SelectionWindow;
        if (!window.Properties.TryGetValue(property, out XProperty? value))
        {
            _fetch = null;
            return;
        }
        if (value.Type == Intern("INCR"))
        {
            // 大数据:删掉属性表示「准备好了」,属主随后一块块往里写(ICCCM §2.5)。
            fetch.Incr = [];
            DeleteSelectionProperty(property);
            return;
        }
        DeleteSelectionProperty(property);
        _fetch = null;
        Deliver(value.Type, value.Data.ToArray());
    }

    /// <summary>INCR 传输中:属主往请求窗口写了一块。空块表示结束。</summary>
    private void OnSelectionWindowProperty(uint property, bool deleted)
    {
        if (deleted || _fetch is not { Incr: { } buffer } fetch || property != Intern("_VELASHELL_SELECTION"))
        {
            return;
        }
        XProperty chunk = SelectionWindow.Properties[property];
        DeleteSelectionProperty(property);
        if (chunk.Data.Length == 0)
        {
            _fetch = null;
            Deliver(fetch.IncrType, [.. buffer]);
            return;
        }
        fetch.IncrType = chunk.Type;
        buffer.AddRange(chunk.Data);
        if (buffer.Count > MaxClipboardBytes)
        {
            _fetch = null;   // 太大:放弃。属主写下一块时没人删属性,它自己会超时
        }
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
