using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VelaShell.Views;

/// <summary>
/// 审计日志窗口(设置 → 安全审计 → 查看审计日志),非模态;数据与筛选全在 <see cref="ViewModels.AuditLogViewModel" />。
/// </summary>
public partial class AuditLogView : Window
{
    /// <summary>构造窗口并按平台装上工具窗外框。</summary>
    public AuditLogView()
    {
        InitializeComponent();
        // 按平台装外框;最大化时卡片铺满、右下角手柄让位也由它管(见 WindowChrome)。
        WindowChrome.Apply(this, WindowChromeKind.Tool, ResizeGrip);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void Header_DoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>右下角缩放手柄:无边框窗口经此拖拽调整大小。</summary>
    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
        }
    }

    // 推迟关闭:同步 Close 会让本轮点击 / 按键的后续路由打到已销毁的窗口(见 WindowCloseExtensions)。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <summary>Esc 关闭窗口 —— 但搜索框里有字时先让 Esc 清掉关键字。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is ViewModels.AuditLogViewModel { SearchText.Length: > 0 } vm)
            {
                vm.SearchText = string.Empty;
            }
            else
            {
                this.PostClose();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
