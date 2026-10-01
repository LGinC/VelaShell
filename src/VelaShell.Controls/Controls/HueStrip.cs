using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace VelaShell.Controls.Controls;

/// <summary>
/// 取色器的色相条:从左到右 0–360 度,滑块是一个填着当前色相纯色的圆。
/// </summary>
/// <remarks>
/// 与 <see cref="ColorSpectrumPad" /> 同一套交互约定:拖动过程中只改 <see cref="Hue" />,松手才发
/// <see cref="Committed" />;方向键左右每次 1 度、按住 Shift 每次 10 度,Home / End 跳到两端。
/// 色带由 <see cref="HsvColor" /> 现算,没有写死任何色值。
/// </remarks>
public sealed class HueStrip : Control
{
    /// <summary>色相,0–360。</summary>
    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<HueStrip, double>(nameof(Hue), coerce: (_, value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 360));

    /// <summary>键盘聚焦时滑块外圈的颜色。</summary>
    public static readonly StyledProperty<IBrush?> FocusBrushProperty =
        AvaloniaProperty.Register<HueStrip, IBrush?>(nameof(FocusBrush));

    private const double KeyStep = 1;
    private const double LargeKeyStep = 10;

    private static readonly IBrush Spectrum = BuildSpectrum();
    private static readonly IPen ThumbRing = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 2);
    private static readonly IPen ThumbShadow = new ImmutablePen(new ImmutableSolidColorBrush(Colors.Black, 0.45), 1);

    private bool _dragging;
    private bool _keyboardFocus;

    static HueStrip()
    {
        FocusableProperty.OverrideDefaultValue<HueStrip>(true);
        AffectsRender<HueStrip>(HueProperty, FocusBrushProperty);
    }

    /// <summary>创建色相条。</summary>
    public HueStrip()
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

    /// <summary>拖拽松手、或用键盘调过一次之后触发。</summary>
    public event EventHandler? Committed;

    /// <inheritdoc cref="HueProperty" />
    public double Hue
    {
        get => GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    /// <inheritdoc cref="FocusBrushProperty" />
    public IBrush? FocusBrush
    {
        get => GetValue(FocusBrushProperty);
        set => SetValue(FocusBrushProperty, value);
    }

    /// <summary>
    /// 把色相条上的横坐标换算成色相。滑块圆心只在 [半径, 宽 − 半径] 之间走 ——
    /// 两端的滑块不会被裁掉一半,而且点在最左 / 最右那一小段上就是 0 / 360。
    /// </summary>
    /// <param name="x">色相条坐标系里的横坐标。</param>
    /// <param name="size">色相条尺寸。</param>
    /// <returns>色相,0–360。</returns>
    public static double FromX(double x, Size size)
    {
        double radius = size.Height / 2;
        double track = size.Width - (2 * radius);
        return track <= 0 ? 0 : Math.Clamp((x - radius) / track, 0, 1) * 360;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Size size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        double radius = size.Height / 2;
        double barHeight = Math.Max(4, size.Height - 6);
        Rect bar = new(radius, (size.Height - barHeight) / 2, Math.Max(0, size.Width - (2 * radius)), barHeight);
        context.DrawRectangle(Spectrum, null, new RoundedRect(bar, barHeight / 2));

        Point center = new(radius + (Hue / 360 * bar.Width), radius);
        IBrush fill = new ImmutableSolidColorBrush(HsvColor.ToRgb(Hue, 1, 1, 1));
        IPen outer = _keyboardFocus && IsFocused && FocusBrush is { } focus ? new Pen(focus, 2) : ThumbShadow;
        context.DrawEllipse(null, outer, center, radius + 0.5, radius + 0.5);
        context.DrawEllipse(fill, ThumbRing, center, radius - 1.5, radius - 1.5);
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
        Hue = FromX(e.GetPosition(this).X, Bounds.Size);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Hue = FromX(e.GetPosition(this).X, Bounds.Size);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
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
        double? hue = e.Key switch
        {
            Key.Left or Key.Down => Hue - step,
            Key.Right or Key.Up => Hue + step,
            Key.Home => 0,
            Key.End => 360,
            _ => null
        };
        if (hue is not { } next)
        {
            return;
        }
        Hue = next;
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
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

    private static IBrush BuildSpectrum()
    {
        LinearGradientBrush brush = new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative)
        };
        for (int stop = 0; stop <= 6; stop++)
        {
            brush.GradientStops.Add(new GradientStop(HsvColor.ToRgb(stop * 60, 1, 1, 1), stop / 6d));
        }
        return brush.ToImmutable();
    }
}
