using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Views;

/// <summary>
/// keyboard-interactive(2FA / OTP)的询问框:连接目标、服务端的说明,每条提示一个输入框。
/// </summary>
/// <remarks>
/// 外壳复用 <see cref="MessageDialog.ShowCustomAsync" />。服务端说不回显的提示(<c>echo = false</c>)
/// 用遮罩输入;回显的(如 Duo 的「Passcode or option」)照原样显示。
/// </remarks>
public static class KeyboardInteractivePromptDialog
{
    /// <summary>弹框问这一轮的答案;确认时按提示顺序返回,取消或令牌触发时返回 null。</summary>
    /// <param name="owner">父窗口。</param>
    /// <param name="request">这一轮询问。</param>
    /// <param name="cancellationToken">连接被取消或认证超时:收起窗口。</param>
    public static async Task<IReadOnlyList<string>?> ShowAsync(Window owner,
        KeyboardInteractiveRequest request,
        CancellationToken cancellationToken)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Classes = { "mono", "dim" },
            Text = request.Target,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (request.Instruction.Length > 0)
        {
            content.Children.Add(new TextBlock { Text = request.Instruction, TextWrapping = TextWrapping.Wrap });
        }
        var inputs = new TextBox[request.Fields.Count];
        for (int i = 0; i < request.Fields.Count; i++)
        {
            KeyboardInteractiveField field = request.Fields[i];
            string label = field.Prompt.Length > 0 ? field.Prompt : Strings.Get("KbdAuth_Response");
            var input = new TextBox { PasswordChar = field.Echo ? default : '●' };
            AutomationProperties.SetName(input, label);
            inputs[i] = input;
            content.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                    input
                }
            });
        }
        // 打开即可直接输入;Enter 落到对话框的默认按钮上。
        TextBox first = inputs[0];
        first.Loaded += (_, _) => first.Focus();

        // 关了正在连的标签、认证超时:窗口当场收起,结果按取消算。
        await using CancellationTokenRegistration _ = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => (TopLevel.GetTopLevel(content) as Window)?.Close(false)));

        string title = request.Name.Length > 0 ? request.Name : Strings.Get("KbdAuth_Title");
        bool confirmed = await MessageDialog.ShowCustomAsync(owner, title, content,
            Strings.Get("KbdAuth_Submit"), kind: MessageDialogKind.Question);
        return confirmed && !cancellationToken.IsCancellationRequested
            ? [.. inputs.Select(t => t.Text ?? string.Empty)]
            : null;
    }
}
