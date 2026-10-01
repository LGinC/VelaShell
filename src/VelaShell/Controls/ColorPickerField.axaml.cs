using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using VelaShell.Controls.Controls;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.Controls;

/// <summary>
/// 颜色字段:一个长得像输入框的色块按钮,点开是取色浮层 —— 饱和度 / 明度面板、色相条、
/// 新旧对比色块、十六进制输入框、色板,以及(值允许为空时)一键清除。
/// </summary>
/// <remarks>
/// <para>
/// 取代各处「色块 + 手敲 <c>#RRGGBB</c> 的输入框」:设置 → 外观的强调色与终端四色,
/// 新建 / 编辑连接的标签颜色。<see cref="Value" /> 是双向绑定的十六进制字符串,
/// 写出去一律是 <c>#RRGGBB</c>(<see cref="ColorHex.Format" />),三处的解析都认。
/// </para>
/// <para>
/// <b>什么时候写回 <see cref="Value" />:</b>面板与色相条松手时、键盘调过一次时、点色板时、
/// 十六进制框回车或失焦时。拖动过程中只刷浮层里的预览 —— 强调色一改就要重派生全套主题令牌,
/// 终端颜色一改就要重刷所有终端,跟着鼠标每像素写一次是白费。
/// </para>
/// <para>
/// 状态以 H / S / V 三个数保存(而不是 <see cref="HsvColor" />:它会把色相 360 折回 0,色相条拖到最右端一松手就跳回最左):在灰色上拖饱和度时色相不会跳回红色;
/// 选一个灰色色板时保留原来的色相,色相条不乱跑。
/// </para>
/// </remarks>
public partial class ColorPickerField : UserControl
{
    /// <summary>当前色值(十六进制);空串或 null 表示「没有颜色」(跟随主题 / 自动配色)。默认双向绑定。</summary>
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<ColorPickerField, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>没有颜色时字段上显示的文字(如「跟随主题」「自动配色」)。</summary>
    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<ColorPickerField, string?>(nameof(EmptyText));

    /// <summary>浮层底部「清除」按钮的文字;为 null 时不给这个按钮(值不允许为空的地方,如终端前景色)。</summary>
    public static readonly StyledProperty<string?> ClearTextProperty =
        AvaloniaProperty.Register<ColorPickerField, string?>(nameof(ClearText));

    /// <summary>色板;为 null 或空时用当前主题的强调色板(<c>VelaAccentPalette0..7</c>)。</summary>
    public static readonly StyledProperty<IEnumerable<string>?> SwatchesProperty =
        AvaloniaProperty.Register<ColorPickerField, IEnumerable<string>?>(nameof(Swatches));

    /// <summary>色板的标题;只在给了 <see cref="Swatches" /> 时用,主题色板有自己的标题。</summary>
    public static readonly StyledProperty<string?> SwatchesTitleProperty =
        AvaloniaProperty.Register<ColorPickerField, string?>(nameof(SwatchesTitle));

    /// <summary>值为空时打开浮层的起始颜色(色相 210、饱和度 60%、明度 90%:一个中性的蓝)。</summary>
    private static readonly Hsv NeutralStart = new(210, 0.6, 0.9);

    private Hsv _hsv = NeutralStart;

    /// <summary>取色浮层(挂在字段按钮上;Flyout 不进名称作用域,没有生成字段)。</summary>
    private Flyout Picker => (Flyout)FieldButton.Flyout!;

    /// <summary>正在把状态同步进面板与色相条:这时它们的属性变化不是用户操作,不能回流。</summary>
    private bool _syncing;

    /// <summary>创建颜色字段。</summary>
    public ColorPickerField()
    {
        InitializeComponent();
        Picker.Opening += (_, _) => OnFlyoutOpening();
        Pad.PropertyChanged += OnPadPropertyChanged;
        HueBar.PropertyChanged += OnHuePropertyChanged;
        Pad.Committed += (_, _) => Commit();
        HueBar.Committed += (_, _) => Commit();
        HexBox.KeyDown += OnHexKeyDown;
        HexBox.LostFocus += (_, _) => CommitHexText();
        ClearButton.Click += OnClearClick;
        RefreshField();
        RefreshClearButton();
    }

    /// <inheritdoc cref="ValueProperty" />
    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="EmptyTextProperty" />
    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <inheritdoc cref="ClearTextProperty" />
    public string? ClearText
    {
        get => GetValue(ClearTextProperty);
        set => SetValue(ClearTextProperty, value);
    }

    /// <inheritdoc cref="SwatchesProperty" />
    public IEnumerable<string>? Swatches
    {
        get => GetValue(SwatchesProperty);
        set => SetValue(SwatchesProperty, value);
    }

    /// <inheritdoc cref="SwatchesTitleProperty" />
    public string? SwatchesTitle
    {
        get => GetValue(SwatchesTitleProperty);
        set => SetValue(SwatchesTitleProperty, value);
    }

    /// <summary>打开取色浮层(与点字段一个效果;测试与键盘入口用)。</summary>
    public void Open() => Picker.ShowAt(FieldButton);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == EmptyTextProperty)
        {
            RefreshField();
        }
        else if (change.Property == ClearTextProperty)
        {
            RefreshClearButton();
        }
    }

    /// <summary>字段上的色块与文字跟着 <see cref="Value" /> 走;认不出的色值当作没有颜色显示。</summary>
    private void RefreshField()
    {
        bool hasColor = ColorHex.TryParse(Value, out Color color);
        Chip.Background = hasColor ? new ImmutableSolidColorBrush(color) : null;
        ValueText.Text = hasColor ? Value!.Trim().ToUpperInvariant() : EmptyText ?? string.Empty;
        // 空态的文字走样式(.empty → VelaTextMuted),换主题时跟着变;这里只切类。
        ValueText.Classes.Set("empty", !hasColor);
    }

    private void RefreshClearButton()
    {
        ClearButton.Content = ClearText;
        ClearButton.IsVisible = ClearText is not null;
    }

    /// <summary>浮层打开:从当前值起步,铺好面板、色相条、对比色块、十六进制框与色板。</summary>
    private void OnFlyoutOpening()
    {
        IReadOnlyList<Color> swatches = ResolveSwatches();
        Color? original = ColorHex.TryParse(Value, out Color parsed) ? parsed : null;
        _hsv = original is { } start
            ? Hsv.From(start)
            : swatches.Count > 0 ? Hsv.From(swatches[0]) : NeutralStart;
        OriginalChip.Background = original is { } before ? new ImmutableSolidColorBrush(before) : null;
        SyncEditors();
        BuildSwatches(swatches);
    }

    private void OnPadPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncing || (e.Property != ColorSpectrumPad.SaturationProperty && e.Property != ColorSpectrumPad.ValueProperty))
        {
            return;
        }
        _hsv = _hsv with { Saturation = Pad.Saturation, Value = Pad.Value };
        ShowPreview();
    }

    private void OnHuePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncing || e.Property != HueStrip.HueProperty)
        {
            return;
        }
        _hsv = _hsv with { Hue = HueBar.Hue };
        _syncing = true;
        try
        {
            Pad.Hue = HueBar.Hue;
        }
        finally
        {
            _syncing = false;
        }
        ShowPreview();
    }

    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHexText();
            e.Handled = true;
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Value = string.Empty;
        Picker.Hide();
    }

    /// <summary>
    /// 十六进制框回车 / 失焦:认得出就写回并把面板挪过去;认不出就把框里的字退回当前颜色 ——
    /// 不能把一个敲错的色值写进设置(终端四色认不出会整套落回出厂色)。
    /// </summary>
    private void CommitHexText()
    {
        if (ColorHex.TryParse(HexBox.Text, out Color color))
        {
            Apply(color);
        }
        else
        {
            HexBox.Text = ColorHex.Format(_hsv.ToColor());
        }
    }

    /// <summary>选定一个具体颜色(色板 / 十六进制框):挪面板、写回值。</summary>
    private void Apply(Color color)
    {
        Hsv next = Hsv.From(color);
        // 灰色(饱和度 0)与黑色(明度 0)没有色相:保留原来的,色相条不跳回红色。
        if (next.Saturation <= 0 || next.Value <= 0)
        {
            next = next with { Hue = _hsv.Hue };
        }
        _hsv = next;
        SyncEditors();
        Commit();
    }

    /// <summary>把当前颜色写回 <see cref="Value" />。值没变就不写(免得触发一轮无谓的主题重算)。</summary>
    private void Commit()
    {
        string hex = ColorHex.Format(_hsv.ToColor());
        if (!string.Equals(Value?.Trim(), hex, StringComparison.OrdinalIgnoreCase))
        {
            Value = hex;
        }
        ShowPreview();
        MarkCurrentSwatch();
    }

    private void SyncEditors()
    {
        _syncing = true;
        try
        {
            HueBar.Hue = _hsv.Hue;
            Pad.Hue = _hsv.Hue;
            Pad.Saturation = _hsv.Saturation;
            Pad.Value = _hsv.Value;
        }
        finally
        {
            _syncing = false;
        }
        ShowPreview();
    }

    private void ShowPreview()
    {
        Color color = _hsv.ToColor();
        PreviewChip.Background = new ImmutableSolidColorBrush(color);
        // 用户正在框里敲字时不覆盖他敲到一半的内容。
        if (!HexBox.IsFocused)
        {
            HexBox.Text = ColorHex.Format(color);
        }
    }

    private IReadOnlyList<Color> ResolveSwatches()
    {
        List<Color> colors = [];
        foreach (string hex in Swatches ?? [])
        {
            if (ColorHex.TryParse(hex, out Color color))
            {
                colors.Add(color);
            }
        }
        if (colors.Count > 0)
        {
            SwatchTitle.Text = SwatchesTitle ?? string.Empty;
            SwatchTitle.IsVisible = !string.IsNullOrEmpty(SwatchesTitle);
            return colors;
        }
        SwatchTitle.Text = Strings.Get("ColorPicker_ThemePalette");
        SwatchTitle.IsVisible = true;
        return ConnectionAccent.PaletteColors();
    }

    private void BuildSwatches(IReadOnlyList<Color> colors)
    {
        SwatchPanel.Children.Clear();
        foreach (Color color in colors)
        {
            string hex = ColorHex.Format(color);
            Button swatch = new()
            {
                Theme = this.FindResource("ColorSwatchTheme") as ControlTheme,
                Background = new ImmutableSolidColorBrush(color),
                Tag = color
            };
            // 提示写的就是色值:悬停能看到十六进制,读屏器也念得出这是哪个颜色。
            ToolTip.SetTip(swatch, hex);
            Avalonia.Automation.AutomationProperties.SetName(swatch, hex);
            swatch.Click += (_, _) => Apply(color);
            SwatchPanel.Children.Add(swatch);
        }
        SwatchSection.IsVisible = colors.Count > 0;
        MarkCurrentSwatch();
    }

    /// <summary>与当前颜色相同的那一格加粗描边,一眼看出现在用的是哪个。</summary>
    private void MarkCurrentSwatch()
    {
        Color current = _hsv.ToColor();
        foreach (Button swatch in SwatchPanel.Children.OfType<Button>())
        {
            swatch.Classes.Set("current", swatch.Tag is Color color && color.R == current.R && color.G == current.G && color.B == current.B);
        }
    }

    /// <summary>取色器的工作状态:色相 0–360、饱和度与明度 0–1。</summary>
    private readonly record struct Hsv(double Hue, double Saturation, double Value)
    {
        public static Hsv From(Color color)
        {
            HsvColor hsv = color.ToHsv();
            return new(hsv.H, hsv.S, hsv.V);
        }

        public Color ToColor() => HsvColor.ToRgb(Hue, Saturation, Value, 1);
    }
}
