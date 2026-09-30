using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views.Settings;

/// <summary>捐赠页:展示 Wise 付款链接并提供打开/复制操作。</summary>
public partial class DonatePage : UserControl
{
    private const string WiseLink = "https://wise.com/pay/me/yud162";

    /// <summary>初始化捐赠页并加载 XAML 组件。</summary>
    public DonatePage() => InitializeComponent();

    /// <summary>点击链接文本:在系统默认浏览器中打开 Wise 付款页。</summary>
    private void WiseLink_PointerPressed(object? sender, PointerPressedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (TopLevel.GetTopLevel(this) is { } top && Uri.TryCreate(WiseLink, UriKind.Absolute, out Uri? uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    });

    /// <summary>复制 Wise 付款链接,按钮文案短暂切为“已复制”作为反馈。</summary>
    private void CopyWiseLink_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }
        await clipboard.SetTextAsync(WiseLink);
        string copied = Strings.Get("SetDonate_Copied");
        if (sender is Button button && button.Content is string original && original != copied)
        {
            // SetCurrentValue 而不是直接赋值:直接赋值会把 {loc:Localize} 的绑定顶掉,之后换语言这个按钮就不跟了
            button.SetCurrentValue(ContentControl.ContentProperty, copied);
            DispatcherTimer.RunOnce(() => button.SetCurrentValue(ContentControl.ContentProperty, original), TimeSpan.FromSeconds(1.5));
        }
    });
}
