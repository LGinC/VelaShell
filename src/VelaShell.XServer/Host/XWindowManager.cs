// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>窗口类型(EWMH <c>_NET_WM_WINDOW_TYPE</c>)。宿主据此决定装饰、任务栏、置顶与焦点行为。</summary>
public enum XWindowType
{
    /// <summary>普通应用窗口。</summary>
    Normal,
    /// <summary>对话框。</summary>
    Dialog,
    /// <summary>工具面板(调色板之类)。</summary>
    Utility,
    /// <summary>拆下来的工具栏。</summary>
    Toolbar,
    /// <summary>启动画面。</summary>
    Splash,
    /// <summary>拆下来的菜单。</summary>
    Menu,
    /// <summary>下拉菜单(菜单栏展开的)。</summary>
    DropdownMenu,
    /// <summary>弹出菜单(右键菜单)。</summary>
    PopupMenu,
    /// <summary>提示框。</summary>
    Tooltip,
    /// <summary>通知气泡。</summary>
    Notification,
    /// <summary>组合框的下拉列表。</summary>
    Combo,
    /// <summary>拖放时跟着指针的图标。</summary>
    Dnd,
    /// <summary>停靠栏 / 面板。</summary>
    Dock,
    /// <summary>桌面(画图标的底层窗口)。</summary>
    Desktop,
}

/// <summary>窗口状态(EWMH <c>_NET_WM_STATE</c>)。</summary>
[Flags]
public enum XWindowStates
{
    /// <summary>没有任何状态。</summary>
    None = 0,
    /// <summary>模态(挡住它的瞬态父窗口)。</summary>
    Modal = 1 << 0,
    /// <summary>在所有工作区都显示。</summary>
    Sticky = 1 << 1,
    /// <summary>纵向最大化。</summary>
    MaximizedVertical = 1 << 2,
    /// <summary>横向最大化。</summary>
    MaximizedHorizontal = 1 << 3,
    /// <summary>卷起(只剩标题栏)。</summary>
    Shaded = 1 << 4,
    /// <summary>不出现在任务栏。</summary>
    SkipTaskbar = 1 << 5,
    /// <summary>不出现在分页器。</summary>
    SkipPager = 1 << 6,
    /// <summary>最小化(同时对应 ICCCM 的 IconicState)。</summary>
    Hidden = 1 << 7,
    /// <summary>全屏。</summary>
    Fullscreen = 1 << 8,
    /// <summary>总在最前。</summary>
    Above = 1 << 9,
    /// <summary>总在最后。</summary>
    Below = 1 << 10,
    /// <summary>要求引起注意(任务栏闪烁)。</summary>
    DemandsAttention = 1 << 11,
    /// <summary>窗口有焦点(由服务端按键盘焦点维护,客户端据此画「非活动」外观)。</summary>
    Focused = 1 << 12,
    /// <summary>两个方向都最大化。</summary>
    Maximized = MaximizedVertical | MaximizedHorizontal,
}

/// <summary><c>_NET_WM_MOVERESIZE</c> 的方向:从哪条边 / 哪个角缩放,或者移动。</summary>
public enum XMoveResizeDirection
{
    /// <summary>从左上角缩放。</summary>
    SizeTopLeft = 0,
    /// <summary>从上边缩放。</summary>
    SizeTop = 1,
    /// <summary>从右上角缩放。</summary>
    SizeTopRight = 2,
    /// <summary>从右边缩放。</summary>
    SizeRight = 3,
    /// <summary>从右下角缩放。</summary>
    SizeBottomRight = 4,
    /// <summary>从下边缩放。</summary>
    SizeBottom = 5,
    /// <summary>从左下角缩放。</summary>
    SizeBottomLeft = 6,
    /// <summary>从左边缩放。</summary>
    SizeLeft = 7,
    /// <summary>移动。</summary>
    Move = 8,
    /// <summary>用键盘缩放。</summary>
    SizeKeyboard = 9,
    /// <summary>用键盘移动。</summary>
    MoveKeyboard = 10,
    /// <summary>取消正在进行的拖动 / 缩放。</summary>
    Cancel = 11,
}

/// <summary>一个窗口图标(<c>_NET_WM_ICON</c>):非预乘的 <c>0xAARRGGBB</c>,行优先。</summary>
public sealed record XWindowIcon(int Width, int Height, uint[] Pixels);

/// <summary>客户端向窗口管理器(即宿主)提出的请求。宿主按自己的规则决定是否照办。</summary>
/// <param name="Window">提出请求的顶层窗口。</param>
public abstract record XWindowManagerRequest(XTopLevelWindow Window);

/// <summary>
/// 让用户用指针拖动 / 缩放窗口(<c>_NET_WM_MOVERESIZE</c>)—— 客户端自绘标题栏(GTK 的 HeaderBar)被按下时发出。
/// 宿主应当开始原生的拖动 / 缩放(比如 Avalonia 的 BeginMoveDrag);服务端已经把按着的按钮当作交给了窗口管理器。
/// </summary>
public sealed record XMoveResizeRequest(XTopLevelWindow Window, XMoveResizeDirection Direction, int Button, int RootX, int RootY)
    : XWindowManagerRequest(Window);

/// <summary>改窗口状态(<c>_NET_WM_STATE</c>):最大化、全屏、置顶……宿主照办后调 <see cref="X11Server.SetTopLevelStates" />。</summary>
public sealed record XStateChangeRequest(XTopLevelWindow Window, XWindowStates Add, XWindowStates Remove)
    : XWindowManagerRequest(Window);

/// <summary>
/// 激活窗口(<c>_NET_ACTIVE_WINDOW</c>):拿到前台、得到焦点。任何 X 客户端都能随时发它 —— 宿主应当只在 <see cref="UserInitiated" /> 时照办
/// (而且用户此刻正在用 X 窗口),否则改为提醒(闪任务栏),免得远端程序在用户输口令时跳到前台接走按键(EWMH 的焦点窃取防护)。
/// </summary>
public sealed record XActivateRequest(XTopLevelWindow Window) : XWindowManagerRequest(Window)
{
    /// <summary>EWMH 的来源指示:0 旧客户端没给、1 普通程序、2 分页器 / 任务栏(直接代表用户的操作)。</summary>
    public int Source { get; init; }

    /// <summary>请求带的时间戳(引起它的那次用户操作的时间);0 = CurrentTime,说明不了什么。</summary>
    public uint Timestamp { get; init; }

    /// <summary>
    /// 服务端的判断:来源是分页器,或时间戳不早于用户最近一次在 X 窗口里按键 / 按按钮的时间 —— 是那次操作引起的。
    /// CurrentTime、过期的时间戳都不算。
    /// </summary>
    public bool UserInitiated { get; init; }
}

/// <summary>
/// 把窗口抬到最上面(客户端对顶层发了 stack-mode 为 Above 的 ConfigureWindow:XRaiseWindow、XMapRaised、Java 的 toFront)。
/// 只是次序,不要求得到焦点;宿主按自己的规则决定照不照办(别从用户正在用的本机窗口那里抢走前台)。
/// </summary>
public sealed record XRaiseRequest(XTopLevelWindow Window) : XWindowManagerRequest(Window);

/// <summary>
/// 客户端自己把键盘焦点挪到了这个顶层(SetInputFocus / XISetFocus,不是 <see cref="X11Server.FocusTopLevel" /> 引起的),按键此刻送往它。
/// 宿主应当让用户看得出键盘去了哪儿:激活它的原生窗口;不愿意(用户正在用本机的其它窗口)可以调 <see cref="X11Server.FocusTopLevel" />
/// 把焦点交回宿主认定的那个顶层。
/// </summary>
public sealed record XFocusRequest(XTopLevelWindow Window) : XWindowManagerRequest(Window);

/// <summary>
/// 窗口对 <c>_NET_WM_PING</c> 没有回应(用户点了关闭,服务端发 WM_DELETE_WINDOW 的同时 ping 了它,时限内没回):程序多半卡住了。
/// 宿主可以问用户要不要强制结束(<see cref="X11Server.KillTopLevelClient" />)。只对在 WM_PROTOCOLS 里声明了 <c>_NET_WM_PING</c> 的窗口发。
/// </summary>
public sealed record XNotRespondingRequest(XTopLevelWindow Window) : XWindowManagerRequest(Window);

/// <summary>关闭窗口(<c>_NET_CLOSE_WINDOW</c>,一般由任务栏 / 分页器发出)。</summary>
public sealed record XCloseRequest(XTopLevelWindow Window) : XWindowManagerRequest(Window);

/// <summary>最小化(ICCCM 的 <c>WM_CHANGE_STATE</c> → IconicState)。</summary>
public sealed record XMinimizeRequest(XTopLevelWindow Window) : XWindowManagerRequest(Window);
