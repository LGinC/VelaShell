using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.XServer;
using VelaShell.Views;
using VelaShell.Views.XServer;
using VelaShell.XServer;

namespace VelaShell.Services.XServer;

/// <summary>
/// 内置 X 服务端的 Avalonia 宿主:每个 X 顶层窗口一个原生窗口(rootless),宿主扮演窗口管理器。
/// </summary>
/// <remarks>
/// <para>
/// 服务端的回调在它的执行线程上来,这里一律 <see cref="Dispatcher.Post(Action, DispatcherPriority)" /> 到 UI 线程,
/// 不阻塞执行线程;注入方向(指针、键盘、移动、缩放)只是把工作项排进服务端,本身不阻塞 UI 线程。
/// </para>
/// <para>
/// <b>根窗口 = 整个虚拟桌面。</b>所有显示器的外接矩形平移到原点 (0,0) 交给服务端当根窗口,
/// 每台显示器一个 RANDR 输出;<see cref="RootOrigin" /> 是这个外接矩形的左上角(物理像素),
/// X 坐标加上它就是系统坐标。显示器增减、DPI 变化时重新告诉服务端。
/// </para>
/// </remarks>
public sealed class AvaloniaXServerHost : IEmbeddedXServerHost
{
    private readonly Dictionary<uint, XNativeWindow> _windows = [];
    private volatile X11Server? _server;
    private Screens? _watchedScreens;
    private string? _lastClipboard;
    private (object? Source, WindowIcon? Icon) _iconCache;
    private nint _keyboardLayout;
    private HostKeymapResult? _appliedKeymap;
    private volatile string _chosenLayout = "";

    /// <summary>每个窗口攒着、还没投递到 UI 线程的损伤矩形上限;再多就合成外接矩形。</summary>
    private const int MaxQueuedDamageRects = 32;

    private readonly Lock _damageGate = new();
    private readonly Action _deliverDamage;
    private Dictionary<uint, List<XRect>> _incomingDamage = [];
    private Dictionary<uint, List<XRect>> _deliveringDamage = [];
    private bool _damagePosted;

    /// <summary>新建一个宿主;经 <see cref="AttachAsync" /> 接到服务端上。</summary>
    public AvaloniaXServerHost() => _deliverDamage = DeliverDamage;

    /// <summary>当前附着的服务端;没在运行时为 <see langword="null" />。窗口的注入经它走。</summary>
    public X11Server? Server => _server;

    /// <summary>当前开着的原生窗口(UI 线程上读;测试用)。</summary>
    internal IReadOnlyCollection<XNativeWindow> Windows => _windows.Values;

    /// <summary>用户此刻在用 X 窗口(某个 X 窗口是活动窗口)。UI 线程上读。</summary>
    public bool XActive => _windows.Values.Any(w => w.IsActive);

    /// <summary>根窗口原点在系统虚拟桌面里的位置(物理像素)。只在 UI 线程上读写。</summary>
    public (int X, int Y) RootOrigin { get; private set; }

    // ================================================================== 生命周期

    /// <summary>当前附着着的宿主:本机活动上报给它的服务端(见 <see cref="HookLocalActivity" />)。</summary>
    private static volatile AvaloniaXServerHost? s_attached;

    private static bool s_activityHooked;

    /// <summary>
    /// 用户在 VelaShell 自己的任何窗口里按键、点击、滚动、移动鼠标时告诉服务端(<see cref="X11Server.NoteUserActivity" />):
    /// 远端程序看到的空闲时间不再只按 X 窗口里的输入算 —— 原先用户整小时在本机终端里打字,远端的「离开」状态与空闲锁屏照样触发。
    /// 挂一次全局的类处理器(隧道阶段、已处理的事件也算),服务端那边自己节流。只在 UI 线程上调。
    /// </summary>
    private static void HookLocalActivity()
    {
        if (s_activityHooked)
        {
            return;
        }
        s_activityHooked = true;
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, _) => ReportLocalActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => ReportLocalActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((_, _) => ReportLocalActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>((_, _) => ReportLocalActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void ReportLocalActivity() => s_attached?._server?.NoteUserActivity();

    /// <inheritdoc />
    public async Task AttachAsync(X11Server server, CancellationToken cancellationToken)
    {
        _server = server;
        s_attached = this;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            HookLocalActivity();
            _keyboardLayout = 0;
            _appliedKeymap = null;   // 新起的服务端是 US 键位表:按当前布局重推一次
            ApplyKeyboardLayout(server);
            if (MainWindow() is { } main)
            {
                ApplyLayout(server, main.Screens);
                if (!ReferenceEquals(_watchedScreens, main.Screens))
                {
                    _watchedScreens = main.Screens;
                    _watchedScreens.Changed += (_, _) =>
                    {
                        if (_server is { } current && _watchedScreens is { } screens)
                        {
                            (int, int) before = RootOrigin;
                            ApplyLayout(current, screens);
                            if (RootOrigin != before)
                            {
                                // 左侧 / 上方的显示器插拔:根原点挪了,原生窗口没动,X 坐标却整体差了这么多(菜单、对话框会摆到别处)。
                                // 按每个窗口此刻的原生位置重报一次。
                                foreach (XNativeWindow window in _windows.Values)
                                {
                                    window.ReportPosition();
                                }
                            }
                        }
                    };
                }
            }
        }, DispatcherPriority.Normal, cancellationToken);
    }

    /// <inheritdoc />
    public void Detach()
    {
        _server = null;
        if (ReferenceEquals(s_attached, this))
        {
            s_attached = null;
        }
        Dispatcher.UIThread.Post(() =>
        {
            XNativeWindow[] windows = [.. _windows.Values];
            _windows.Clear();
            // 先全部打上标记再关:关 owner 时 Avalonia 先问它的子窗口,子窗口不拦,owner 才关得掉(原先留下关不掉的空壳)。
            foreach (XNativeWindow window in windows)
            {
                window.MarkClosingByHost();
            }
            foreach (XNativeWindow window in windows)
            {
                window.CloseByHost();
            }
        });
    }

    /// <summary>
    /// 显示器布局与 DPI 告诉服务端:根窗口是所有显示器的外接矩形,每台一个输出;DPI 取主显示器的缩放。
    /// </summary>
    private void ApplyLayout(X11Server server, Screens screens)
    {
        IReadOnlyList<Screen> all = LimitScreens(screens.All);
        if (all.Count == 0)
        {
            return;
        }
        int minX = all.Min(s => s.Bounds.X), minY = all.Min(s => s.Bounds.Y);
        // 虚拟桌面超出根窗口的上限时截到上限(多出来的部分 X 程序摆不过去);服务端对超限的参数抛异常,原先异常落在 UI 线程上。
        int maxX = Math.Min(all.Max(s => s.Bounds.Right), minX + X11ServerOptions.MaxScreenSize);
        int maxY = Math.Min(all.Max(s => s.Bounds.Bottom), minY + X11ServerOptions.MaxScreenSize);
        RootOrigin = (minX, minY);
        List<XMonitor> monitors = [];
        for (int i = 0; i < all.Count; i++)
        {
            Screen screen = all[i];
            PixelRect b = screen.Bounds;
            double dpi = 96 * Math.Max(1, screen.Scaling);
            // 工作区(去掉任务栏 / Dock):服务端据此算 _NET_WORKAREA,菜单、最大化、对话框才不会落到任务栏后面。
            PixelRect work = screen.WorkingArea.Intersect(b);
            monitors.Add(new XMonitor(b.X - minX, b.Y - minY, b.Width, b.Height)
            {
                Name = string.IsNullOrWhiteSpace(screen.DisplayName) ? $"SCREEN-{i + 1}" : screen.DisplayName,
                Primary = screen.IsPrimary,
                WidthMillimeters = (int)Math.Round(b.Width / dpi * 25.4),
                HeightMillimeters = (int)Math.Round(b.Height / dpi * 25.4),
                WorkArea = work.Width > 0 && work.Height > 0 && work != b
                    ? new XRect(work.X - minX, work.Y - minY, work.Width, work.Height)
                    : null,
            });
        }
        server.SetScreenLayout(maxX - minX, maxY - minY, monitors);

        // 分数缩放(125%、150%)只调 DPI,整数倍(200%)时再让 GTK 按倍数放大控件 —— GTK 的窗口缩放只认整数。
        double scaling = (screens.Primary ?? all[0]).Scaling;
        int scale = scaling >= 2 ? (int)Math.Floor(scaling) : 1;
        server.SetDisplayScale(Math.Max(1, (int)Math.Round(96 * scaling)), scale);
    }

    /// <summary>
    /// 服务端最多接受 <see cref="X11Server.MaxMonitors" /> 台显示器:多了只交主显示器与排在前面的几台(原先整个列表交过去,
    /// 服务端抛的异常落在 UI 线程上,布局一次也没换成)。
    /// </summary>
    private static IReadOnlyList<Screen> LimitScreens(IReadOnlyList<Screen> all) => LimitScreens(all, s => s.IsPrimary);

    internal static IReadOnlyList<T> LimitScreens<T>(IReadOnlyList<T> all, Func<T, bool> isPrimary)
    {
        if (all.Count <= X11Server.MaxMonitors)
        {
            return all;
        }
        T[] primary = [.. all.Where(isPrimary).Take(1)];
        return [.. primary, .. all.Where(s => !isPrimary(s)).Take(X11Server.MaxMonitors - primary.Length)];
    }

    /// <summary>
    /// 按键盘布局换服务端的键位表:设置里手选了布局时用随程序带的表(<see cref="BundledKeymaps" />);否则跟随系统当前的布局 ——
    /// Windows 见 <see cref="WindowsKeymap" />,macOS 见 <see cref="MacKeymap" />,Linux 见 <see cref="LinuxKeymap" />。
    /// 算出来的与上次推给服务端的一样时什么也不做;取不到布局时沿用服务端内置的 US 键位表。
    /// </summary>
    private void ApplyKeyboardLayout(X11Server server)
    {
        HostKeymapResult? keymap;
        try
        {
            keymap = BuildHostKeymap(server);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return;   // 系统库缺了哪一个:沿用现在的键位表
        }
        if (keymap is null || keymap.SameAs(_appliedKeymap))
        {
            return;
        }
        _appliedKeymap = keymap;
        // 一次交过去:键值、右 Alt 是不是 AltGr(macOS 上是右 Option)、布局名 —— 服务端只通知客户端一轮。
        HasAltGr = keymap.HasAltGr;
        server.SetKeymap(keymap.ToXKeymap());
    }

    /// <inheritdoc />
    public void UseKeyboardLayout(string layout) => _chosenLayout = layout ?? "";

    private long _layoutCheckedAt;

    /// <summary>
    /// X 窗口里按下了一个键:系统布局可能刚在 X 窗口里切过(Win+Space、Alt+Shift、输入法的切换)—— 先看一眼,变了就把新的键位表推过去,
    /// 再注入这个键(同一个工作队列,按先后处理)。原先只在激活 X 窗口时重推,在 X 窗口里切了布局,继续敲出的仍是旧布局。
    /// Windows 上只比一下布局句柄(很便宜);别的系统算一遍键位表较贵,至多每秒看一次。设置里手选了布局时不跟随系统。
    /// 服务端只改与上次不同的键,用户在 X 里做的 xmodmap 改动不受影响。
    /// </summary>
    internal void RefreshKeyboardLayoutOnKey()
    {
        if (_server is not { } server || _chosenLayout.Length != 0)
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            long now = Environment.TickCount64;
            if (now - _layoutCheckedAt < 1000)
            {
                return;
            }
            _layoutCheckedAt = now;
        }
        ApplyKeyboardLayout(server);
    }

    private HostKeymapResult? BuildHostKeymap(X11Server server)
    {
        // 设置里手选了布局:用随程序带的键位表,不再跟随系统。
        if (_chosenLayout.Length != 0 && HostKeymap.FromBundled(_chosenLayout) is { } chosen)
        {
            return chosen;
        }
        if (OperatingSystem.IsWindows())
        {
            // Windows 上布局句柄没变就不必重算(句柄是逐线程的,取一次很便宜)。
            nint layout = WindowsKeymap.CurrentLayout();
            if (layout == 0 || layout == _keyboardLayout)
            {
                return null;
            }
            _keyboardLayout = layout;
            return WindowsKeymap.Build(layout);
        }
        if (OperatingSystem.IsMacOS())
        {
            return MacKeymap.Build();
        }
        if (OperatingSystem.IsLinux())
        {
            return LinuxKeymap.Build(server.DisplayNumber);
        }
        return null;
    }

    /// <summary>
    /// 当前布局有 AltGr 层(macOS 上是 Option 层)。Windows 上这时按 AltGr 系统会先补一个假的左 Ctrl 按下,
    /// 窗口要把它从 X 那边撤掉(见 <see cref="XNativeWindow" />)。
    /// </summary>
    public bool HasAltGr { get; private set; }

    private static Window? MainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main } ? main : null;

    // ================================================================== 服务端回调(执行线程上来)

    /// <inheritdoc />
    public void TopLevelMapped(XTopLevelWindow window) => Dispatcher.UIThread.Post(() => Map(window));

    /// <inheritdoc />
    public void TopLevelUnmapped(XTopLevelWindow window) => Dispatcher.UIThread.Post(() =>
    {
        if (_windows.Remove(window.Id, out XNativeWindow? native))
        {
            CloseWithOwnedWindows(native);
        }
    });

    /// <summary>
    /// 收掉一个原生窗口:Avalonia 关 owner 时连带关掉它拥有的窗口(对话框、瞬态窗口)。它们在 X 里可能还映射着 ——
    /// 主窗口先于对话框取消映射、程序只把主窗口藏起来 —— 那样就成了看不见的幽灵。所以先把它们一起收掉,
    /// 再把 X 里还映射着的不带 owner 重新显示。
    /// </summary>
    private void CloseWithOwnedWindows(XNativeWindow native)
    {
        XNativeWindow[] owned = [.. _windows.Values.Where(w => ReferenceEquals(w.Owner, native))];
        foreach (XNativeWindow child in owned)
        {
            _windows.Remove(child.Handle.Id);
            child.MarkClosingByHost();
        }
        native.CloseByHost();
        foreach (XNativeWindow child in owned)
        {
            child.CloseByHost();   // 已经随 owner 关了的,再关一次是空操作
            if (child.Handle.Snapshot.IsMapped)
            {
                Map(child.Handle);
            }
        }
    }

    /// <inheritdoc />
    public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes) => Dispatcher.UIThread.Post(() =>
    {
        if (_windows.TryGetValue(window.Id, out XNativeWindow? native))
        {
            native.ApplyProperties(changes);
        }
    });

    /// <inheritdoc />
    /// <remarks>
    /// 执行线程每放一次锁就可能报一批损伤(负载重时一秒几百批):先按窗口攒在这里,UI 线程上同一时刻最多排着一次投递,
    /// 不为每批各 Post 一次。像素由窗口在下一帧按攒下的矩形去取。
    /// </remarks>
    public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
    {
        lock (_damageGate)
        {
            if (!_incomingDamage.TryGetValue(window.Id, out List<XRect>? rects))
            {
                _incomingDamage[window.Id] = rects = [];
            }
            rects.AddRange(damage);
            if (rects.Count > MaxQueuedDamageRects)
            {
                int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
                foreach (XRect r in rects)
                {
                    (x1, y1) = (Math.Min(x1, r.X), Math.Min(y1, r.Y));
                    (x2, y2) = (Math.Max(x2, r.X + r.Width), Math.Max(y2, r.Y + r.Height));
                }
                rects.Clear();
                rects.Add(new XRect(x1, y1, x2 - x1, y2 - y1));
            }
            if (_damagePosted)
            {
                return;
            }
            _damagePosted = true;
        }
        Dispatcher.UIThread.Post(_deliverDamage, DispatcherPriority.Render);
    }

    /// <summary>UI 线程:把攒下的损伤交给各自的原生窗口。</summary>
    private void DeliverDamage()
    {
        Dictionary<uint, List<XRect>> batch;
        lock (_damageGate)
        {
            (batch, _incomingDamage, _deliveringDamage) = (_incomingDamage, _deliveringDamage, _incomingDamage);
            _damagePosted = false;
        }
        foreach ((uint id, List<XRect> rects) in batch)
        {
            if (_windows.TryGetValue(id, out XNativeWindow? native))
            {
                native.AddDamage(rects);
            }
        }
        batch.Clear();
    }

    /// <inheritdoc />
    public void CursorChanged(XTopLevelWindow? window, XCursor cursor) => Dispatcher.UIThread.Post(() =>
    {
        if (window is not null && _windows.TryGetValue(window.Id, out XNativeWindow? native))
        {
            native.ApplyCursor(cursor);
        }
    });

    /// <inheritdoc />
    public void BellRequested(int volume)
    {
        if (volume > 0)   // 系统提示音没有音量可调;音量 0(xset b 0、Bell -100)就是不响
        {
            Dispatcher.UIThread.Post(SystemSound.Alert);
        }
    }

    /// <inheritdoc />
    public void ClipboardChanged(string text) => Dispatcher.UIThread.Post(() => FireAndForget.Run(async () =>
    {
        _lastClipboard = text;
        if (MainWindow()?.Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[XServer] clipboard write failed: {ex.Message}");
            }
        }
    }));

    /// <inheritdoc />
    public void WindowManagerRequested(XWindowManagerRequest request) => Dispatcher.UIThread.Post(() =>
    {
        if (!_windows.TryGetValue(request.Window.Id, out XNativeWindow? native))
        {
            return;
        }
        switch (request)
        {
            case XMoveResizeRequest move:
                native.BeginInteractive(move.Direction);
                break;
            case XStateChangeRequest state:
                native.ApplyStateRequest(state.Add, state.Remove);
                break;
            case XActivateRequest activate when activate.UserInitiated && XActive:
                native.Activate();
                break;
            case XActivateRequest:
                // 不是用户操作引起的(CurrentTime、过期的时间戳),或用户此刻在用本机窗口:只闪任务栏,不切前台 ——
                // 原先无条件激活,远端程序能在用户输 sudo 口令时跳到前台接走按键。
                WindowAttention.Request(native);
                break;
            case XRaiseRequest when !ReferenceEquals(native, _windows.Values.FirstOrDefault(w => w.IsActive))
                                    && _windows.Values.Any(w => w.IsActive):
                // 只是抬高次序:用户此刻正在用这个 X 程序(另一个 X 窗口是活动的)才照办,不从本机窗口那里抢走前台。
                native.Activate();
                break;
            case XFocusRequest when !native.IsActive && _windows.Values.Any(w => w.IsActive):
                // X 客户端自己把键盘焦点挪到了这个窗口:按键已经送往它,把它的原生窗口激活,用户才看得出键盘去了哪儿。
                // 用户正在用本机的其它窗口时不抢前台 —— 那时按键本来就不进 X,用户回到某个 X 窗口时焦点随激活重新给出。
                native.Activate();
                break;
            case XMinimizeRequest:
                native.WindowState = WindowState.Minimized;
                break;
            case XCloseRequest:
                _server?.CloseTopLevel(request.Window);
                break;
            case XNotRespondingRequest:
                FireAndForget.Run(() => ConfirmKillAsync(native, request.Window));
                break;
        }
    });

    /// <summary>
    /// 用户点了关闭,窗口却对 <c>_NET_WM_PING</c> 没有回应(程序卡住了):问用户要不要强制结束这个 X 程序 ——
    /// 原先声明了 WM_DELETE_WINDOW 却卡死的程序关不掉,只能停掉整个 X Server,所有会话的程序一起断。
    /// </summary>
    private async Task ConfirmKillAsync(XNativeWindow native, XTopLevelWindow handle)
    {
        XTopLevelSnapshot snapshot = handle.Snapshot;
        string name = snapshot.Title.Length > 0 ? snapshot.Title : snapshot.ClassName;
        bool kill = await MessageDialog.ConfirmAsync(native,
            Strings.Get("XServer_NotRespondingTitle"),
            Strings.Format("XServer_NotRespondingMessage", name),
            Strings.Get("XServer_ForceQuit"),
            kind: MessageDialogKind.Warning,
            danger: true);
        if (kill)
        {
            _server?.KillTopLevelClient(handle);
        }
    }

    // ================================================================== UI 线程

    private void Map(XTopLevelWindow handle)
    {
        if (_server is null || _windows.ContainsKey(handle.Id) || handle.Snapshot.InputOnly)
        {
            return;   // InputOnly 的顶层(GtkInvisible 之类)看不见:不开原生窗口(原先多出一个黑窗口)
        }
        XNativeWindow window = new(this, handle);
        _windows[handle.Id] = window;
        PlaceIfUnpositioned(handle, window);
        window.ApplyProperties(XTopLevelChanges.All);
        window.ApplyInitialStates();   // 映射前就设好的最大化 / 全屏 / initial_state = Iconic

        // 对话框、瞬态窗口(连同声明了 WM_TRANSIENT_FOR 的弹出菜单)压在父窗口之上。没声明的弹层不借用「当前活动的 X 窗口」当 owner:
        // 那个窗口可能属于别的程序甚至别的会话,owner 关闭时会把它连带关掉(弹层本身照样置顶,不需要 owner)。
        XTopLevelSnapshot snapshot = handle.Snapshot;
        XNativeWindow? owner = snapshot.TransientFor is { } transientFor && _windows.TryGetValue(transientFor.Id, out XNativeWindow? parent)
            ? parent
            : null;
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// 客户端没给位置(映射在 0,0)的普通窗口,像窗口管理器那样摆:对话框居中压在父窗口上,其余放在主显示器工作区正中。
    /// 用户指定的位置(USPosition,如 <c>xterm -geometry +0+0</c>)哪怕是 (0, 0) 也照办 —— 原先一律当成「没给位置」挪到屏幕中央。
    /// </summary>
    private void PlaceIfUnpositioned(XTopLevelWindow handle, XNativeWindow window)
    {
        XTopLevelSnapshot snapshot = handle.Snapshot;
        if (snapshot.OverrideRedirect || snapshot.UserPosition || snapshot.X != 0 || snapshot.Y != 0 || _server is not { } server)
        {
            return;
        }
        PixelRect area;
        if (snapshot.TransientFor is { } transientFor && _windows.TryGetValue(transientFor.Id, out XNativeWindow? parent))
        {
            (int ox, int oy) = RootOrigin;
            XTopLevelSnapshot p = parent.Handle.Snapshot;
            area = new PixelRect(p.X + ox, p.Y + oy, p.Width, p.Height);
        }
        else if ((MainWindow()?.Screens ?? window.Screens).Primary is { } primary)
        {
            area = primary.WorkingArea;
        }
        else
        {
            return;
        }
        int x = area.X + Math.Max(0, (area.Width - snapshot.Width) / 2) - RootOrigin.X;
        int y = area.Y + Math.Max(0, (area.Height - snapshot.Height) / 2) - RootOrigin.Y;
        window.PlaceAt(x, y);
        server.MoveTopLevel(handle, x, y);
    }

    /// <summary>某个 X 窗口成了活动窗口:键盘焦点给它;顺带把系统剪贴板里别的程序复制的新文本交给 X。</summary>
    public void OnWindowActivated(XNativeWindow window)
    {
        if (_server is not { } server)
        {
            return;
        }
        UpdateTopmost(xActive: true);
        server.FocusTopLevel(window.Handle);
        if (HostLockState.Read(server.DisplayNumber) is var (capsLock, numLock))
        {
            server.SetLockState(capsLock, numLock);   // 用户可能在别的程序里切过 CapsLock / NumLock
        }
        ApplyKeyboardLayout(server);   // 用户可能在别的程序里切了输入法 / 布局
        FireAndForget.Run(() => OfferSystemClipboardAsync(server, window));
    }

    /// <summary>系统剪贴板里有别的程序复制的新文本时交给 X(没有剪贴板变化事件可用,活动窗口切进来时看一眼)。</summary>
    private async Task OfferSystemClipboardAsync(X11Server server, XNativeWindow window)
    {
        try
        {
            if (window.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text
                && text != _lastClipboard)
            {
                _lastClipboard = text;
                server.SetClipboardText(text);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[XServer] clipboard read failed: {ex.Message}");
        }
    }

    /// <summary>某个 X 窗口不再活动:如果活动窗口也不是别的 X 窗口,X 这边就没有焦点。</summary>
    public void OnWindowDeactivated(XNativeWindow window) => Dispatcher.UIThread.Post(() =>
    {
        if (_server is { } server && !_windows.Values.Any(w => w.IsActive))
        {
            server.FocusTopLevel(null);
        }
        if (!XActive)
        {
            UpdateTopmost(xActive: false);   // 用户回到了本机窗口:X 的弹出层与「总在最前」的窗口退到后面
        }
    });

    private void UpdateTopmost(bool xActive)
    {
        foreach (XNativeWindow window in _windows.Values)
        {
            window.UpdateTopmost(xActive);
        }
    }

    /// <summary>原生窗口已关闭(宿主关的,或系统强制关的)。</summary>
    public void OnWindowClosed(XNativeWindow window)
    {
        if (_windows.TryGetValue(window.Handle.Id, out XNativeWindow? current) && ReferenceEquals(current, window))
        {
            _windows.Remove(window.Handle.Id);
        }
    }

    /// <summary>窗口图标:取不超过 256 的最大一幅(<c>_NET_WM_ICON</c> 是非预乘的 ARGB)。同一份图标只转换一次。</summary>
    public WindowIcon? IconFor(IReadOnlyList<XWindowIcon> icons)
    {
        if (ReferenceEquals(_iconCache.Source, icons))
        {
            return _iconCache.Icon;
        }
        XWindowIcon? best = icons.Where(i => i.Width <= 256 && i.Height <= 256).MaxBy(i => i.Width * i.Height);
        WindowIcon? icon = null;
        if (best is { Width: > 0, Height: > 0 })
        {
            // 位图归图标所有,不释放(图标换掉时随缓存一起被回收)。
            WriteableBitmap bitmap = new(new PixelSize(best.Width, best.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (ILockedFramebuffer frame = bitmap.Lock())
            {
                int[] row = new int[best.Width];
                for (int y = 0; y < best.Height; y++)
                {
                    Buffer.BlockCopy(best.Pixels, y * best.Width * 4, row, 0, best.Width * 4);
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + (y * frame.RowBytes), best.Width);
                }
            }
            icon = new WindowIcon(bitmap);
        }
        _iconCache = (icons, icon);
        return icon;
    }
}
