using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>代码片段设置页:管理可复用的命令片段(Snippets)。</summary>
/// <remarks>
/// 列表行里的「编辑」「删除」「恢复默认」走代码后置而不是绑定。
/// <para>
/// 行的 DataContext 是 <see cref="QuickCommandViewModel" />(条目自己),要够到页面的
/// <see cref="SettingsViewModel" /> 就得写 <c>$parent[UserControl].DataContext.…</c> ——
/// 而 <c>.DataContext</c> 在绑定路径里是**显式的一环**:页面刚 new 出来、还没挂进可视树
/// 那一刻它就是 null,于是每开一次设置就往调试输出刷一串 "Value is null"。
/// (Avalonia 的编译绑定里 <c>RelativeSource</c> 给的同样是控件对象,绕不开这一环。)
/// </para>
/// <para>
/// 从 <c>sender</c> 取条目、从自己的 DataContext 取页面视图模型,两者都不经过绑定,
/// 也就没有那个空窗期。同目录的密钥管理页与常规页出于同样的理由也是这么写的。
/// </para>
/// <para>
/// 拖动排序(#555)用指针捕获在页内完成,不走系统拖放:落点只在这张列表里,
/// 用不着跨窗口的数据载荷,也就不会把一串内部标识拖进终端或别的程序里。
/// </para>
/// </remarks>
public partial class SnippetsPage : UserControl
{
    /// <summary>按下到移动超过该像素才算拖动,与会话树、文件面板一致。</summary>
    private const double DragThreshold = 5;

    /// <summary>指针离滚动区上下边缘这么近时自动滚动。</summary>
    private const double AutoScrollEdge = 40;

    /// <summary>自动滚动每一拍最多滚这么多像素(越贴边越快)。</summary>
    private const double AutoScrollMaxStep = 18;

    /// <summary>
    /// 指针离开列表超过这个距离就不再有落点,松手即取消。纵向在这个范围内仍按最近的插入位置算,
    /// 拖到第一组上面一点、最后一组下面一点照样能放到头尾。
    /// </summary>
    private const double DropSlack = 40;

    /// <summary>命令的插入线从手柄之后开始,与贯穿整行的分组插入线区分开。</summary>
    private const double CommandLineIndent = 24;

    private QuickCommandsViewModel? _hooked;

    private object? _dragItem;
    private Control? _dragGrip;
    private Border? _dragVisual;
    private Point _dragOrigin;

    /// <summary>指针最近一次在可视根里的位置。不用列表坐标:自动滚动时列表在动,可视根不动。</summary>
    private Point _pointerInRoot;

    private bool _isDragging;
    private DispatcherTimer? _autoScrollTimer;

    /// <summary>初始化代码片段设置页并加载 XAML 组件。</summary>
    public SnippetsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookSnippets();
    }

    /// <summary>拖动的落点。<c>LineY</c> 是插入线在列表(<c>SnippetListHost</c>)里的纵坐标。</summary>
    internal abstract record SnippetDropTarget(double LineY);

    /// <summary>命令插到 <c>Group</c> 里 <c>Before</c> 之前(null = 组尾)。</summary>
    internal sealed record CommandDropTarget(
        QuickCommandGroupViewModel Group,
        QuickCommandViewModel? Before,
        double LineY
    ) : SnippetDropTarget(LineY);

    /// <summary>分组插到 <c>Before</c> 之前(null = 排在最后,仍在「未分组」之前)。</summary>
    internal sealed record GroupDropTarget(QuickCommandGroupViewModel? Before, double LineY)
        : SnippetDropTarget(LineY);

    /// <summary>当前拖动的落点;不在拖动、或松手会落回原处时为 null(此时也不画插入线)。</summary>
    internal SnippetDropTarget? DropTarget { get; private set; }

    private QuickCommandsViewModel? Snippets => (DataContext as SettingsViewModel)?.Snippets;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookSnippets();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EndDrag();
        // 视图模型是全局单例,页面不在树上时不挂着它的事件,否则每开一次设置窗口就漏一张页面。
        HookTo(null);
    }

    private void HookSnippets() => HookTo(TopLevel.GetTopLevel(this) is null ? null : Snippets);

    private void HookTo(QuickCommandsViewModel? snippets)
    {
        if (ReferenceEquals(_hooked, snippets))
        {
            return;
        }
        _hooked?.PropertyChanged -= OnSnippetsPropertyChanged;
        _hooked = snippets;
        _hooked?.PropertyChanged += OnSnippetsPropertyChanged;
    }

    private void OnSnippetsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickCommandsViewModel.EditingCommand) || _hooked?.EditingCommand is null)
        {
            return;
        }
        // 编辑区在整张列表下面:点了上面某一行的铅笔,不滚过去就看不到表单。
        // 等编辑区显示出来、排好版再滚,否则滚到的是它隐藏时的位置。
        Dispatcher.UIThread.Post(
            () =>
            {
                EditPanel.BringIntoView();
                EditNameBox.Focus();
            },
            DispatcherPriority.Loaded
        );
    }

    private void EditSnippet_Click(object? sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is var (snippets, command))
        {
            snippets.BeginEditCommand.Execute(command).Subscribe();
        }
    }

    private void DeleteSnippet_Click(object? sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is var (snippets, command))
        {
            snippets.DeleteCommandCommand.Execute(command).Subscribe();
        }
    }

    private void RestoreSnippet_Click(object? sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is var (snippets, command))
        {
            snippets.RestoreBuiltInCommand.Execute(command).Subscribe();
        }
    }

    /// <summary>恢复全部内置命令会撤掉对内置命令的所有修改,先问一句。</summary>
    private void RestoreAllBuiltIns_Click(object? sender, RoutedEventArgs e)
    {
        if (Snippets is not { } snippets || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        FireAndForget.Run(async () =>
        {
            bool confirmed = await MessageDialog.ConfirmAsync(
                owner,
                Strings.Get("SetSnippets_RestoreBuiltIns"),
                Strings.Get("SetSnippets_RestoreBuiltInsBody"),
                Strings.Get("SetSnippets_RestoreBuiltIns")
            );
            if (confirmed)
            {
                snippets.RestoreAllBuiltInsCommand.Execute().Subscribe();
            }
        });
    }

    /// <summary>取"这一行是哪个片段"与"页面的片段视图模型";任一缺失即返回 null。</summary>
    private (QuickCommandsViewModel Snippets, QuickCommandViewModel Command)? Resolve(object? sender) =>
        sender is Control { DataContext: QuickCommandViewModel command }
        && DataContext is SettingsViewModel { Snippets: { } snippets }
            ? (snippets, command)
            : null;

    // ── 拖动排序 ────────────────────────────────────────────────

    private void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (
            sender is not Control grip
            || TopLevel.GetTopLevel(this) is not { } root
            || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed
            || Snippets is not { ErrorMessage.Length: 0 }
        )
        {
            return;
        }
        object? item = grip.DataContext switch
        {
            QuickCommandViewModel command => command,
            QuickCommandGroupViewModel { IsDefault: false } group => group,
            _ => null,
        };
        if (item is null)
        {
            return;
        }
        EndDrag();
        _dragItem = item;
        _dragGrip = grip;
        _dragOrigin = _pointerInRoot = e.GetPosition(root);
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void Grip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (
            _dragItem is null
            || !ReferenceEquals(sender, _dragGrip)
            || TopLevel.GetTopLevel(this) is not { } root
        )
        {
            return;
        }
        _pointerInRoot = e.GetPosition(root);
        if (!_isDragging)
        {
            if (
                Math.Abs(_pointerInRoot.X - _dragOrigin.X) < DragThreshold
                && Math.Abs(_pointerInRoot.Y - _dragOrigin.Y) < DragThreshold
            )
            {
                return;
            }
            BeginDrag();
        }
        RefreshDropTarget();
        e.Handled = true;
    }

    private void Grip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragItem is null || !ReferenceEquals(sender, _dragGrip))
        {
            return;
        }
        object item = _dragItem;
        SnippetDropTarget? target = _isDragging ? DropTarget : null;
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
        if (target is not null && Snippets is { } snippets)
        {
            FireAndForget.Run(() => Drop(snippets, item, target));
        }
    }

    /// <summary>指针捕获被别处拿走(切到别的窗口、弹出了对话框)等于取消这次拖动。</summary>
    private void Grip_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(sender, _dragGrip))
        {
            EndDrag();
        }
    }

    private static Task Drop(QuickCommandsViewModel snippets, object item, SnippetDropTarget target) =>
        (item, target) switch
        {
            (QuickCommandViewModel command, CommandDropTarget drop) => snippets.MoveCommandAsync(
                command,
                drop.Group,
                drop.Before
            ),
            (QuickCommandGroupViewModel group, GroupDropTarget drop) => snippets.MoveGroupAsync(
                group,
                drop.Before
            ),
            _ => Task.CompletedTask,
        };

    private void BeginDrag()
    {
        _isDragging = true;
        string visualClass = _dragItem is QuickCommandViewModel ? "snippet-row" : "snippet-group";
        _dragVisual = _dragGrip
            ?.GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains(visualClass));
        _dragVisual?.Classes.Add("dragging");
        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(30),
            };
            _autoScrollTimer.Tick += OnAutoScrollTick;
        }
        _autoScrollTimer.Start();
    }

    private void EndDrag()
    {
        _autoScrollTimer?.Stop();
        _dragVisual?.Classes.Remove("dragging");
        _dragVisual = null;
        DropIndicator.IsVisible = false;
        DropTarget = null;
        _isDragging = false;
        _dragItem = null;
        _dragGrip = null;
    }

    /// <summary>按指针当前位置重算落点并摆插入线。</summary>
    private void RefreshDropTarget()
    {
        if (!_isDragging || TopLevel.GetTopLevel(this) is not { } root || Snippets is not { } snippets)
        {
            return;
        }
        DropTarget = root.TranslatePoint(_pointerInRoot, SnippetListHost) is { } point
            && IsNearList(point)
            ? _dragItem switch
            {
                QuickCommandViewModel command => FindCommandDrop(snippets, command, point.Y),
                QuickCommandGroupViewModel group => FindGroupDrop(snippets, group, point.Y),
                _ => null,
            }
            : null;
        ShowIndicator(DropTarget);
    }

    private bool IsNearList(Point point) =>
        point.X >= -DropSlack
        && point.X <= SnippetListHost.Bounds.Width + DropSlack
        && point.Y >= -DropSlack
        && point.Y <= SnippetListHost.Bounds.Height + DropSlack;

    /// <summary>
    /// 命令的落点:列出所有可插入的位置(每个分组头下方 = 组首,每一行下方 = 这一行之后),取离指针最近的一个。
    /// 收起的分组只有组首一个位置,拖到它的组头上就是放进这一组的最前面。
    /// </summary>
    private CommandDropTarget? FindCommandDrop(
        QuickCommandsViewModel snippets,
        QuickCommandViewModel dragged,
        double y
    )
    {
        var slots = new List<CommandDropTarget>();
        foreach (QuickCommandGroupViewModel group in snippets.FilteredGroups)
        {
            if (
                SnippetGroups.ContainerFromItem(group) is not Control card
                || card.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault() is not { } header
                || header.TranslatePoint(new Point(0, header.Bounds.Height), SnippetListHost) is not { } headerBottom
            )
            {
                continue;
            }
            QuickCommandViewModel? first = group.IsExpanded
                ? group.FilteredCommands.FirstOrDefault()
                : group.Commands.FirstOrDefault();
            slots.Add(new(group, first, headerBottom.Y));
            if (!group.IsExpanded || card.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault() is not { } rows)
            {
                continue;
            }
            foreach (QuickCommandViewModel row in group.FilteredCommands)
            {
                if (
                    rows.ContainerFromItem(row) is not Control rowControl
                    || rowControl.TranslatePoint(new Point(0, rowControl.Bounds.Height), SnippetListHost)
                        is not { } rowBottom
                )
                {
                    continue;
                }
                int index = group.Commands.IndexOf(row);
                QuickCommandViewModel? next = index + 1 < group.Commands.Count ? group.Commands[index + 1] : null;
                slots.Add(new(group, next, rowBottom.Y));
            }
        }
        CommandDropTarget? best = slots.MinBy(slot => Math.Abs(slot.LineY - y));
        return best is null || LandsInPlace(dragged, best) ? null : best;
    }

    private static bool LandsInPlace(QuickCommandViewModel dragged, CommandDropTarget drop)
    {
        int index = drop.Group.Commands.IndexOf(dragged);
        if (index < 0)
        {
            return false;
        }
        QuickCommandViewModel? next = index + 1 < drop.Group.Commands.Count ? drop.Group.Commands[index + 1] : null;
        return ReferenceEquals(drop.Before, dragged) || ReferenceEquals(drop.Before, next);
    }

    /// <summary>
    /// 分组的落点:每个可见分组的上方各一个位置,外加最后一组之后;「未分组」固定垫底,它后面没有位置。
    /// 插入线画在分组之间那道缝的正中。
    /// </summary>
    private GroupDropTarget? FindGroupDrop(
        QuickCommandsViewModel snippets,
        QuickCommandGroupViewModel dragged,
        double y
    )
    {
        var slots = new List<GroupDropTarget>();
        double gap = 0;
        double? end = null;
        QuickCommandGroupViewModel[] visible = [.. snippets.FilteredGroups];
        foreach (QuickCommandGroupViewModel group in visible)
        {
            if (
                SnippetGroups.ContainerFromItem(group) is not Control card
                || card.TranslatePoint(default, SnippetListHost) is not { } top
            )
            {
                continue;
            }
            gap = (card as ContentPresenter)?.Child?.Margin.Bottom ?? 0;
            slots.Add(new(group, top.Y - gap / 2));
            if (group.IsDefault)
            {
                end = null;
                break;
            }
            end = top.Y + card.Bounds.Height - gap / 2;
        }
        if (end is { } last)
        {
            slots.Add(new(null, last));
        }
        GroupDropTarget? best = slots.MinBy(slot => Math.Abs(slot.LineY - y));
        if (best is null)
        {
            return null;
        }
        int self = Array.IndexOf(visible, dragged);
        QuickCommandGroupViewModel? next = self >= 0 && self + 1 < visible.Length ? visible[self + 1] : null;
        return ReferenceEquals(best.Before, dragged) || ReferenceEquals(best.Before, next) ? null : best;
    }

    private void ShowIndicator(SnippetDropTarget? target)
    {
        if (target is null)
        {
            DropIndicator.IsVisible = false;
            return;
        }
        double indent = target is CommandDropTarget ? CommandLineIndent : 0;
        Canvas.SetLeft(DropIndicator, indent);
        Canvas.SetTop(DropIndicator, target.LineY - (DropIndicator.Height / 2));
        DropIndicator.Width = Math.Max(0, SnippetListHost.Bounds.Width - indent);
        DropIndicator.IsVisible = true;
    }

    /// <summary>拖到设置页滚动区的上下边缘时自动滚动,越贴边越快;滚动后按新位置重算落点。</summary>
    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (
            !_isDragging
            || this.FindAncestorOfType<ScrollViewer>() is not { } scroller
            || TopLevel.GetTopLevel(this) is not { } root
            || root.TranslatePoint(_pointerInRoot, scroller) is not { } point
        )
        {
            return;
        }
        double viewport = scroller.Viewport.Height;
        double overshoot =
            point.Y < AutoScrollEdge ? point.Y - AutoScrollEdge
            : point.Y > viewport - AutoScrollEdge ? point.Y - (viewport - AutoScrollEdge)
            : 0;
        if (overshoot == 0)
        {
            return;
        }
        double step = Math.Clamp(overshoot / AutoScrollEdge * AutoScrollMaxStep, -AutoScrollMaxStep, AutoScrollMaxStep);
        double offset = Math.Clamp(scroller.Offset.Y + step, 0, Math.Max(0, scroller.Extent.Height - viewport));
        if (offset == scroller.Offset.Y)
        {
            return;
        }
        scroller.Offset = scroller.Offset.WithY(offset);
        // 新的偏移要等下一次排版才落到坐标上,排完再算落点。
        Dispatcher.UIThread.Post(RefreshDropTarget, DispatcherPriority.Background);
    }
}
