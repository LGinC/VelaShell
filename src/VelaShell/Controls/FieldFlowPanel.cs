using Avalonia;
using Avalonia.Controls;

namespace VelaShell.Controls;

/// <summary>
/// 表单字段的流式排版:一行分成 <see cref="FullUnits" /> 份,每个字段占几份由它自己说
/// (<see cref="UnitsProperty" />,或数据上下文实现的 <see cref="IFieldFlowItem" />),
/// 按顺序往一行里放,放不下就换行。
/// </summary>
/// <remarks>
/// <para>
/// 给插件声明的字段用(<c>ProtocolSettingField.Width</c>:整行 / 半行 / 三分之一行)。
/// 用 <see cref="Grid" /> 写不出来:哪几个字段同排取决于运行期的声明与显示条件,
/// 一个三分之一宽的字段被显示条件藏起来,同排的另外两个要原地留着,而下一个字段不该挤上来 —— 那是
/// "流式"的语义,不是固定的格子。
/// </para>
/// <para>
/// 隐藏的子项(<c>IsVisible=false</c>,或量出来高度为 0 —— 条目模板根节点被显示条件藏起来时就是这样)
/// 不占位置。列宽按"整行宽 + 一份间距"均分再减去间距算,这样一行三个三分之一与一个整行的左右两端对齐。
/// </para>
/// </remarks>
public sealed class FieldFlowPanel : Panel
{
    /// <summary>一整行的份数。</summary>
    public const int FullUnits = 6;

    /// <summary>某个子项占几份(1–6);0 表示没说,看数据上下文,再没有就占整行。</summary>
    public static readonly AttachedProperty<int> UnitsProperty =
        AvaloniaProperty.RegisterAttached<FieldFlowPanel, Control, int>("Units");

    /// <summary>同一行里字段之间的间距。</summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<FieldFlowPanel, double>(nameof(ColumnSpacing), 12);

    /// <summary>行与行之间的间距。</summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FieldFlowPanel, double>(nameof(RowSpacing), 12);

    static FieldFlowPanel()
    {
        AffectsMeasure<FieldFlowPanel>(ColumnSpacingProperty, RowSpacingProperty);
        AffectsParentMeasure<FieldFlowPanel>(UnitsProperty);
    }

    /// <summary>同一行里字段之间的间距。</summary>
    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>行与行之间的间距。</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>取某个子项占几份。</summary>
    public static int GetUnits(Control control) => control.GetValue(UnitsProperty);

    /// <summary>设某个子项占几份。</summary>
    public static void SetUnits(Control control, int value) => control.SetValue(UnitsProperty, value);

    /// <summary>子项实际占几份:附加属性优先,其次数据上下文,都没有就占整行。</summary>
    internal static int UnitsOf(Control child)
    {
        int units = GetUnits(child);
        if (units is < 1 or > FullUnits && child.DataContext is IFieldFlowItem item)
        {
            units = item.FlowUnits;
        }
        return units is >= 1 and <= FullUnits ? units : FullUnits;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        if (width <= 0)
        {
            // 没给宽度(放进了水平方向不限宽的容器):按每个子项的自然宽度排成一行,够用且不炸。
            double natural = 0;
            double tallest = 0;
            foreach (Control child in Children)
            {
                child.Measure(availableSize);
                natural += child.DesiredSize.Width;
                tallest = Math.Max(tallest, child.DesiredSize.Height);
            }
            return new Size(natural, tallest);
        }
        return Layout(width, arrange: false);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>某个份数在给定行宽下的像素宽度。</summary>
    internal static double WidthOf(int units, double rowWidth, double spacing) =>
        Math.Max(0, (units * (rowWidth + spacing) / FullUnits) - spacing);

    private Size Layout(double width, bool arrange)
    {
        double spacing = ColumnSpacing;
        double rowSpacing = RowSpacing;
        double y = 0;
        double x = 0;
        int used = 0;
        double rowHeight = 0;
        bool anyRow = false;
        var row = new List<(Control Child, double X, double Width)>();

        void FlushRow()
        {
            if (row.Count == 0)
            {
                return;
            }
            if (anyRow)
            {
                y += rowSpacing;
            }
            if (arrange)
            {
                foreach ((Control child, double cx, double cw) in row)
                {
                    child.Arrange(new Rect(cx, y, cw, rowHeight));
                }
            }
            y += rowHeight;
            anyRow = true;
            row.Clear();
            x = 0;
            used = 0;
            rowHeight = 0;
        }

        foreach (Control child in Children)
        {
            if (!child.IsVisible)
            {
                if (arrange)
                {
                    child.Arrange(default);
                }
                continue;
            }
            int units = UnitsOf(child);
            double childWidth = WidthOf(units, width, spacing);
            if (!arrange)
            {
                child.Measure(new Size(childWidth, double.PositiveInfinity));
            }
            if (child.DesiredSize.Height <= 0)
            {
                // 条目模板的根节点被显示条件藏起来了:容器还在,但不该占一格。
                if (arrange)
                {
                    child.Arrange(default);
                }
                continue;
            }
            if (used + units > FullUnits)
            {
                FlushRow();
            }
            row.Add((child, x, childWidth));
            x += childWidth + spacing;
            used += units;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        FlushRow();
        return new Size(width, y);
    }
}

/// <summary>流式排版里的一项,自己说占几份(见 <see cref="FieldFlowPanel" />)。</summary>
public interface IFieldFlowItem
{
    /// <summary>占一行的几份(1–6)。</summary>
    int FlowUnits { get; }
}
