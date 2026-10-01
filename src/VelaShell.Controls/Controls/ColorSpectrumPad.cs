using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 取色器的饱和度 / 明度面板:横轴是饱和度(左 0 右 1),纵轴是明度(上 1 下 0),底色是当前色相的纯色。
/// </summary>
/// <remarks>
/// <para>
/// 拖动过程中只改 <see cref="Saturation" /> / <see cref="Value" />,松手时才发 <see cref="Committed" /> ——
/// 由宿主决定什么时候把颜色写回设置。强调色一改就要重派生全套主题令牌,终端颜色一改就要重刷所有终端,
/// 拖一下发几十次是白费;取色器自己的预览色块照样跟手。
/// </para>
/// <para>
/// 键盘可用:方向键左右调饱和度、上下调明度,每次 1%,按住 Shift 每次 10%;每按一次算一次提交。
/// </para>
/// <para>
/// 这里的白与黑不是界面配色,是 HSV 色彩空间本身(饱和度 0 = 白,明度 0 = 黑),
/// 与主题无关,所以不走令牌。滑块的白圈加暗描边也是同一个理由:它要压在任意颜色上都看得见。
/// </para>
/// </remarks>
public sealed class ColorSpectrumPad : Control
{
    /// <summary>色相,0–360。决定面板底色。</summary>
    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<ColorSpectrumPad, double>(nameof(Hue));

    /// <summary>饱和度,0–1(横轴)。</summary>
    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<ColorSpectrumPad, double>(nameof(Saturation), 1, coerce: Unit);

    /// <summary>明度,0–1(纵轴,上为 1)。</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ColorSpectrumPad, double>(nameof(Value), 1, coerce: Unit);

    /// <summary>面板的描边。</summary>
    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<ColorSpectrumPad, IBrush?>(nameof(BorderBrush));

    /// <summary>键盘聚焦时的描边(只在 Tab / 方向键导航过来时画,鼠标点进来不画)。</summary>
    public static readonly StyledProperty<IBrush?> FocusBrushProperty =
        AvaloniaProperty.Register<ColorSpectrumPad, IBrush?>(nameof(FocusBrush));

    /// <summary>圆角半径。</summary>
    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<ColorSpectrumPad, double>(nameof(CornerRadius), 4);

    private const double ThumbRadius = 7;
    private const double KeyStep = 0.01;
    private const double LargeKeyStep = 0.1;

    private static readonly IBrush WhiteToClear = Gradient(
        new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative),
        Colors.White, Color.FromArgb(0, 255, 255, 255));

    private static readonly IBrush ClearToBlack = Gradient(
        new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative),
        Color.FromArgb(0, 0, 0, 0), Colors.Black);

    private static readonly IPen ThumbRing = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 2);
    private static readonly IPen ThumbShadow = new ImmutablePen(new ImmutableSolidColorBrush(Colors.Black, 0.45), 1);

    private bool _dragging;
    private bool _keyboardFocus;

    static ColorSpectrumPad()
    {
        FocusableProperty.OverrideDefaultValue<ColorSpectrumPad>(true);
        AffectsRender<ColorSpectrumPad>(
            HueProperty, SaturationProperty, ValueProperty, BorderBrushProperty, FocusBrushProperty, CornerRadiusProperty);
    }

    /// <summary>创建面板。</summary>
    public ColorSpectrumPad()
    {
        GotFocus += (_, e) =>
        {
            _keyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
            InvalidateVisual();
        };
        LostFocus += (_, _) =>
        {
            _keyboardFocus = false;
            InvalidateVisual();
        };
        PointerCaptureLost += (_, _) => EndDrag();
    }

    /// <summary>拖拽松手、或用键盘调过一次之后触发:这时的颜色是用户要的,可以写回去了。</summary>
    public event EventHandler? Committed;

    /// <inheritdoc cref="HueProperty" />
    public double Hue
    {
        get => GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    /// <inheritdoc cref="SaturationProperty" />
    public double Saturation
    {
        get => GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    /// <inheritdoc cref="ValueProperty" />
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="BorderBrushProperty" />
    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    /// <inheritdoc cref="FocusBrushProperty" />
    public IBrush? FocusBrush
    {
        get => GetValue(FocusBrushProperty);
        set => SetValue(FocusBrushProperty, value);
    }

    /// <inheritdoc cref="CornerRadiusProperty" />
    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>把面板上的一点换算成饱和度与明度(越界的点夹到边上)。</summary>
    /// <param name="point">面板坐标系里的点。</param>
    /// <param name="size">面板尺寸。</param>
    /// <returns>饱和度与明度,各 0–1。</returns>
    public static (double Saturation, double Value) FromPoint(Point point, Size size) =>
        (size.Width <= 0 ? 0 : Math.Clamp(point.X / size.Width, 0, 1),
         size.Height <= 0 ? 0 : 1 - Math.Clamp(point.Y / size.Height, 0, 1));

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect rect = new(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }
        RoundedRect rounded = new(rect, CornerRadius);
        using (context.PushClip(rounded))
        {
            context.FillRectangle(new ImmutableSolidColorBrush(HsvColor.ToRgb(Hue, 1, 1, 1)), rect);
            context.FillRectangle(WhiteToClear, rect);
            context.FillRectangle(ClearToBlack, rect);
        }
        IBrush? frame = _keyboardFocus && IsFocused ? FocusBrush ?? BorderBrush : BorderBrush;
        if (frame is not null)
        {
            context.DrawRectangle(null, new Pen(frame, _keyboardFocus && IsFocused ? 2 : 1), rounded.Deflate(0.5, 0.5));
        }

        Point center = new(Saturation * rect.Width, (1 - Value) * rect.Height);
        IBrush fill = new ImmutableSolidColorBrush(HsvColor.ToRgb(Hue, Saturation, Value, 1));
        context.DrawEllipse(null, ThumbShadow, center, ThumbRadius + 1.5, ThumbRadius + 1.5);
        context.DrawEllipse(fill, ThumbRing, center, ThumbRadius, ThumbRadius);
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _dragging = true;
        e.Pointer.Capture(this);
        Focus();
        MoveTo(e.GetPosition(this));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            MoveTo(e.GetPosition(this));
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            // 先释放捕获再收尾:释放会触发 PointerCaptureLost,EndDrag 靠 _dragging 去重。
            e.Pointer.Capture(null);
            EndDrag();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? LargeKeyStep : KeyStep;
        (double ds, double dv) = e.Key switch
        {
            Key.Left => (-step, 0d),
            Key.Right => (step, 0d),
            Key.Up => (0d, step),
            Key.Down => (0d, -step),
            _ => (0d, 0d)
        };
        if (ds == 0 && dv == 0)
        {
            return;
        }
        Saturation += ds;
        Value += dv;
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void MoveTo(Point point)
    {
        (double saturation, double value) = FromPoint(point, Bounds.Size);
        Saturation = saturation;
        Value = value;
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    private static double Unit(AvaloniaObject _, double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    private static IBrush Gradient(RelativePoint start, RelativePoint end, Color from, Color to) =>
        new LinearGradientBrush
        {
            StartPoint = start,
            EndPoint = end,
            GradientStops = [new GradientStop(from, 0), new GradientStop(to, 1)]
        }.ToImmutable();
}
