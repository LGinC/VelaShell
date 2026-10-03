using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>
/// 共享凭据(#550)的新建 / 编辑框。<c>ShowDialog</c> 的结果是 <see cref="SharedCredentialEditResult" />,取消为 null。
/// </summary>
public partial class SharedCredentialEditorView : Window
{
    /// <summary>初始化编辑框,并在窗口打开后订阅保存 / 取消以随之关闭。</summary>
    public SharedCredentialEditorView()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not SharedCredentialEditorViewModel viewModel)
        {
            return;
        }
        // 命令由按钮点击触发,回调仍在输入事件栈内:推迟关闭(同登录框)。
        viewModel.SaveCommand.Subscribe(this.PostClose);
        viewModel.CancelCommand.Subscribe(_ => this.PostClose(null));
    }

    /// <summary>Esc 等价于取消。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose(null);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void BrowseKeyFile_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SharedCredentialEditorViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectKeyFile"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(this)
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            viewModel.PrivateKeyPath = path;
        }
    });

    /// <summary>选择证书文件;私钥还空着时按 <c>&lt;key&gt;-cert.pub</c> 约定顺手补上(同连接配置页)。</summary>
    private void BrowseCertificateFile_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SharedCredentialEditorViewModel viewModel)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("Profile_SelectCertFile"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(this)
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }
        viewModel.CertificatePath = path;
        if (string.IsNullOrWhiteSpace(viewModel.PrivateKeyPath) && OpenSshCertificatePaths.InferPrivateKeyPath(path) is { } keyPath)
        {
            viewModel.PrivateKeyPath = keyPath;
        }
    });
}
