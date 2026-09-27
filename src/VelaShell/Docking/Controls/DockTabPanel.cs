using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace VelaShell.Docking.Controls;

/// <summary>
/// 标签条的排版面板(#521),取代原先的 <see cref="StackPanel" />。横排有两种排法:
/// <list type="bullet">
/// <item><b>单排(默认)</b>:放得下就按标题原宽排;放不下先把<b>最宽的几个</b>收窄
/// (标题出省略号),短标签原样不动,收到 <see cref="MinTabWidth" /> 仍放不下才溢出,交给外层滚动。</item>
/// <item><b>多排</b>(<see cref="MultiRow" />):一排排满就换到下一排,标签条随排数长高,不再有溢出。</item>
/// </list>
/// 纵排(标签条停在左 / 右侧)与 <see cref="StackPanel" /> 完全一样。
/// </summary>
/// <remarks>
/// <para>
/// 收窄照的是 VS Code 的 <c>tabSizing: shrink</c>:只削最宽的那几个、削到同一宽度为止,
/// 而不是 Chrome 那样人人等宽 —— 「FTP」这种短标签没有理由陪着长标签一起变窄。
/// </para>
/// <para>
/// 单排时外层是一个能横向滚动的 <see cref="ScrollViewer" />,它量内容时给的宽度是无穷大,
/// 面板从这个参数里看不出到底有多宽的地方。所以面板盯着祖先 ScrollViewer 的视口宽度:
/// 视口一变就重新量一遍,拿视口宽度当预算。首次布局时视口还是 0,先按原宽排,
/// ScrollViewer 排完、视口有了宽度,同一个布局周期里就会再量一遍,不会闪一帧原宽。
/// </para>
/// <para>
/// 这里不会来回振荡:收窄后放得下 → 不溢出 → 溢出按钮不出现 → 视口不变;
/// 收到下限仍放不下 → 溢出 → 溢出按钮出现、视口变窄 → 在更窄的视口里照样放不下。两头都是稳态。
/// </para>
/// </remarks>
public sealed class DockTabPanel : Panel
{
    /// <summary><see cref="Orientation" /> 的样式属性。</summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<DockTabPanel, Orientation>(nameof(Orientation), Orientation.Horizontal);

    /// <summary><see cref="MultiRow" /> 的样式属性。</summary>
    public static readonly StyledProperty<bool> MultiRowProperty =
        AvaloniaProperty.Register<DockTabPanel, bool>(nameof(MultiRow));

    /// <summary><see cref="MinTabWidth" /> 的样式属性。</summary>
    public static readonly StyledProperty<double> MinTabWidthProperty =
        AvaloniaProperty.Register<DockTabPanel, double>(nameof(MinTabWidth), 120);

    /// <summary><see cref="MinRowHeight" /> 的样式属性。</summary>
    public static readonly StyledProperty<double> MinRowHeightProperty =
        AvaloniaProperty.Register<DockTabPanel, double>(nameof(MinRowHeight));

    /// <summary>上一次量出来的每个标签的落位(与 <see cref="Panel.Children" /> 一一对应),排版时照着放。</summary>
    private readonly List<Rect> _slots = [];

    private ScrollViewer? _scrollViewer;
    private double _lastViewportWidth;

    static DockTabPanel() =>
        AffectsMeasure<DockTabPanel>(OrientationProperty, MultiRowProperty, MinTabWidthProperty, MinRowHeightProperty);

    /// <summary>标签的排列方向;纵排时忽略 <see cref="MultiRow" />。</summary>
    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>横排时放不下是否换行成多排(否则收窄,再不行就溢出)。</summary>
    public bool MultiRow
    {
        get => GetValue(MultiRowProperty);
        set => SetValue(MultiRowProperty, value);
    }

    /// <summary>
    /// 单排收窄的下限。约合一个标签的图标、关闭钮外加七八个字符 ——
    /// 再窄下去标题只剩一个省略号,认不出是哪台机器,不如滚动。
    /// </summary>
    public double MinTabWidth
    {
        get => GetValue(MinTabWidthProperty);
        set => SetValue(MinTabWidthProperty, value);
    }

    /// <summary>每排的最小高度。标签自己只有 32px,单排时被标签条撑到 35px;多排时每排也得是这个高度,看起来才是同一种标签。</summary>
    public double MinRowHeight
    {
        get => GetValue(MinRowHeightProperty);
        set => SetValue(MinRowHeightProperty, value);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        _scrollViewer?.PropertyChanged += OnScrollViewerPropertyChanged;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _scrollViewer?.PropertyChanged -= OnScrollViewerPropertyChanged;
        _scrollViewer = null;
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.ViewportProperty || Orientation != Orientation.Horizontal || MultiRow)
        {
            return;
        }
        double width = ((Size)e.NewValue!).Width;
        if (Math.Abs(width - _lastViewportWidth) > 0.01)
        {
            InvalidateMeasure();
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _slots.Clear();
        if (Orientation == Orientation.Vertical)
        {
            return MeasureColumn(availableSize);
        }
        return MultiRow && double.IsFinite(availableSize.Width)
                   ? MeasureRows(availableSize.Width)
                   : MeasureSingleRow(availableSize);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        bool singleRow = Orientation == Orientation.Horizontal && !MultiRow;
        for (int i = 0; i < Children.Count; i++)
        {
            Control child = Children[i];
            Rect slot = i < _slots.Count ? _slots[i] : new Rect(child.DesiredSize);
            child.Arrange(Orientation == Orientation.Vertical
                              ? slot.WithWidth(finalSize.Width)
                              : singleRow
                                  ? slot.WithHeight(finalSize.Height)
                                  : slot);
        }
        return finalSize;
    }

    private Size MeasureColumn(Size availableSize)
    {
        double width = 0;
        double y = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            _slots.Add(new Rect(0, y, child.DesiredSize.Width, child.DesiredSize.Height));
            width = Math.Max(width, child.DesiredSize.Width);
            y += child.DesiredSize.Height;
        }
        return new Size(width, y);
    }

    private Size MeasureSingleRow(Size availableSize)
    {
        _lastViewportWidth = _scrollViewer?.Viewport.Width ?? 0;
        double budget = double.IsFinite(availableSize.Width)
                            ? availableSize.Width
                            : _lastViewportWidth > 0 ? _lastViewportWidth : double.PositiveInfinity;

        int count = Children.Count;
        double[] natural = new double[count];
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            Control child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            natural[i] = child.DesiredSize.Width;
            total += natural[i];
        }

        double cap = total > budget ? ShrinkCap(natural, budget, MinTabWidth) : double.PositiveInfinity;
        double x = 0;
        double height = MinRowHeight;
        for (int i = 0; i < count; i++)
        {
            Control child = Children[i];
            double width = Math.Min(natural[i], cap);
            if (width < natural[i])
            {
                // 按收窄后的宽度再量一次:标题这才知道要出省略号。
                child.Measure(new Size(width, availableSize.Height));
            }
            _slots.Add(new Rect(x, 0, width, child.DesiredSize.Height));
            x += width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(x, height);
    }

    /// <summary>
    /// 收窄的封顶宽度:找最大的 <c>c</c>,使 Σ min(原宽, c) 不超过预算 ——
    /// 比 <c>c</c> 窄的标签原样保留,其余一律削到 <c>c</c>。取整到整像素,免得小数累加后超出视口半个像素、
    /// 把溢出按钮闪出来。低于下限就按下限算,剩下的交给滚动。
    /// </summary>
    internal static double ShrinkCap(IReadOnlyList<double> natural, double budget, double minWidth)
    {
        double[] sorted = [.. natural];
        Array.Sort(sorted);
        double prefix = 0;
        for (int k = 0; k < sorted.Length; k++)
        {
            double cap = (budget - prefix) / (sorted.Length - k);
            if (cap <= sorted[k])
            {
                return Math.Max(Math.Floor(cap), minWidth);
            }
            prefix += sorted[k];
        }
        return double.PositiveInfinity; // 本来就放得下
    }

    private Size MeasureRows(double limit)
    {
        int count = Children.Count;
        var widths = new double[count];
        var rowOf = new int[count];
        var xs = new double[count];
        List<double> rowHeights = [];
        double x = 0;
        double rowHeight = MinRowHeight;
        double contentWidth = 0;
        for (int i = 0; i < count; i++)
        {
            Control child = Children[i];
            child.Measure(Size.Infinity);
            double width = child.DesiredSize.Width;
            if (width > limit)
            {
                // 一个标签比整排还宽(标题极长、窗格极窄):独占一排,标题出省略号。
                width = limit;
                child.Measure(new Size(limit, double.PositiveInfinity));
            }
            if (x > 0 && x + width > limit + 0.5)
            {
                rowHeights.Add(rowHeight);
                x = 0;
                rowHeight = MinRowHeight;
            }
            widths[i] = width;
            rowOf[i] = rowHeights.Count;
            xs[i] = x;
            x += width;
            contentWidth = Math.Max(contentWidth, x);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        if (count > 0)
        {
            rowHeights.Add(rowHeight);
        }

        var rowTops = new double[rowHeights.Count];
        double y = 0;
        for (int row = 0; row < rowHeights.Count; row++)
        {
            rowTops[row] = y;
            y += rowHeights[row];
        }
        for (int i = 0; i < count; i++)
        {
            _slots.Add(new Rect(xs[i], rowTops[rowOf[i]], widths[i], rowHeights[rowOf[i]]));
        }
        return new Size(contentWidth, Math.Max(y, MinRowHeight));
    }
}
