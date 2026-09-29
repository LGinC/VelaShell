using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.Core.Ssh;
using VelaShell.Views;

namespace VelaShell.Services;

/// <summary>
/// keyboard-interactive(2FA / OTP):在 UI 线程弹 <see cref="KeyboardInteractivePromptDialog" />,交回用户的答案。
/// </summary>
/// <remarks>
/// <para>
/// <b>一次只弹一个</b>(与 <see cref="AgentSignPromptDialogService" /> 同一口径):启动时恢复的几条会话
/// 同时要动态码,叠在一起的几个框分不清哪个是哪台机器。排队期间认证的期限照样在走。
/// </para>
/// <para>
/// 没有主窗口、弹窗出错一律按用户取消处理(返回 null);令牌触发时抛
/// <see cref="OperationCanceledException" />,由库按它自己的口径判成取消或认证超时。
/// </para>
/// </remarks>
public sealed class KeyboardInteractivePromptDialogService : IKeyboardInteractivePrompt
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> AskAsync(KeyboardInteractiveRequest request, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string>? answers = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested
                    || Application.Current?.ApplicationLifetime
                        is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
                {
                    return null;
                }
                try
                {
                    return await KeyboardInteractivePromptDialog.ShowAsync(owner, request, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
            });
            cancellationToken.ThrowIfCancellationRequested();
            return answers;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
