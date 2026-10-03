using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using VelaShell.Services;
// VelaShell.Services 里另有一个同名的 KeyModifiers(KeyboardShortcutService 的平台无关枚举)。
using KeyModifiers = Avalonia.Input.KeyModifiers;

namespace VelaShell.Controls;

/// <summary>
/// 录键框:显示出来就抢焦点,等用户按下一个组合键,把它当作 <see cref="KeyGesture" /> 交出去。
/// </summary>
/// <remarks>
/// <para>
/// 这里收到的每一个按键都标 Handled:设置窗口的 Esc 关窗、Tab 移焦、对话框的默认按钮都不该在录键时动作 ——
/// 用户正要把 Ctrl+Tab 录成某个键位,焦点却跳走了,那就录不成。
/// </para>
/// <para>
/// 只按修饰键时不交差,只把已按住的修饰键显示出来(<c>Ctrl+Shift+…</c>),等真正的键;
/// 不带修饰键的 Esc 是「取消」。合不合格(要不要修饰键、是不是固定键位)由视图模型判定,
/// 不合格时录键框留着,用户直接再按一次。失焦等于取消。
/// </para>
/// </remarks>
public sealed class ShortcutRecorder : Border
{
    /// <summary>没按任何键时显示的提示。</summary>
    public static readonly StyledProperty<string?> PromptProperty =
        AvaloniaProperty.Register<ShortcutRecorder, string?>(nameof(Prompt));

    private readonly TextBlock _text = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
    };

    /// <summary>建一个录键框。</summary>
    public ShortcutRecorder()
    {
        Focusable = true;
        Child = _text;
        LostFocus += (_, _) =>
        {
            if (IsVisible)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>没按任何键时显示的提示。</summary>
    public string? Prompt
    {
        get => GetValue(PromptProperty);
        set => SetValue(PromptProperty, value);
    }

    /// <summary>录到一个组合键(不含只按修饰键的情况)。</summary>
    public event EventHandler<KeyGesture>? GestureCaptured;

    /// <summary>用户放弃录键(Esc 或失焦)。</summary>
    public event EventHandler? Cancelled;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PromptProperty)
        {
            _text.Text = Prompt;
        }
        else if (change.Property == IsVisibleProperty && IsVisible)
        {
            _text.Text = Prompt;
            // 刚变可见时还没排版,当场 Focus 会落空;排到下一轮再抢。
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return;
        }
        if (ShortcutGestures.IsModifierKey(e.Key))
        {
            ShowHeldModifiers(e.KeyModifiers | ModifierOf(e.Key));
            return;
        }
        if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None })
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
            return;
        }
        GestureCaptured?.Invoke(this, new KeyGesture(e.Key, e.KeyModifiers));
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        if (ShortcutGestures.IsModifierKey(e.Key))
        {
            ShowHeldModifiers(e.KeyModifiers & ~ModifierOf(e.Key));
        }
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextInputEventArgs e) => e.Handled = true;

    private void ShowHeldModifiers(KeyModifiers held)
    {
        if (held == KeyModifiers.None)
        {
            _text.Text = Prompt;
            return;
        }
        // 借键帽的写法拼出已按住的修饰键;最后那一格先用省略号占着。
        string[] caps = ShortcutGestures.Keycaps(new KeyGesture(Key.None, held), OperatingSystem.IsMacOS());
        caps[^1] = "…";
        _text.Text = string.Join('+', caps);
    }

    private static KeyModifiers ModifierOf(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
        Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
        Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
        Key.LWin or Key.RWin => KeyModifiers.Meta,
        _ => KeyModifiers.None,
    };
}
