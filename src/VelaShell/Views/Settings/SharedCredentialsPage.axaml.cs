using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Core.Credentials;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views.Settings;

/// <summary>设置 → 共享凭据页(#550):新建、编辑、删除共享凭据,以及挂上 / 摘下使用它的连接。</summary>
public partial class SharedCredentialsPage : UserControl
{
    /// <summary>初始化页面并加载 XAML 组件。</summary>
    public SharedCredentialsPage() => InitializeComponent();

    private void New_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(() => EditAsync(null));

    private void Edit_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is Control { DataContext: SharedCredentialRow row })
        {
            await EditAsync(row.Credential);
        }
    });

    /// <summary>
    /// 删除前先确认:在用它的连接会各自留一份同样的凭据,删完照样能连 —— 这句话要摆在确认框里,
    /// 不然用户会以为删了凭据,那几十条连接就连不上了,因而不敢删。
    /// </summary>
    private void Delete_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is not Control { DataContext: SharedCredentialRow row }
            || DataContext is not SettingsViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(
            owner,
            Strings.Get("SetCred_Delete"),
            SharedCredentialsViewModel.DeleteConfirmMessage(row),
            Strings.Delete,
            Strings.Cancel,
            MessageDialogKind.Warning,
            danger: true);
        if (confirmed)
        {
            await viewModel.SharedCredentials.DeleteAsync(row.Credential);
        }
    });

    private async Task EditAsync(SharedCredential? existing)
    {
        if (DataContext is not SettingsViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner
            || await viewModel.SharedCredentials.CreateEditorAsync(existing) is not { } editor)
        {
            return;
        }
        var dialog = new SharedCredentialEditorView { DataContext = editor };
        if (await dialog.ShowDialog<SharedCredentialEditResult?>(owner) is { } result)
        {
            await viewModel.SharedCredentials.SaveAsync(result);
        }
    }
}
