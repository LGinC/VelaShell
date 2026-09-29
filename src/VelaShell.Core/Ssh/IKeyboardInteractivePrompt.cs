namespace VelaShell.Core.Ssh;

/// <summary>keyboard-interactive 一轮询问里要用户回答的一条提示。</summary>
/// <param name="Prompt">提示文字,如 <c>Verification code:</c>。</param>
/// <param name="Echo">是否回显;<see langword="false" /> 时输入框要遮住。</param>
public sealed record KeyboardInteractiveField(string Prompt, bool Echo);

/// <summary>
/// 服务端发来的一轮 keyboard-interactive 询问(2FA / OTP 走这条)。
/// </summary>
/// <remarks>
/// 文字来自<b>尚未认证</b>的对端:交到界面之前已经去掉控制字符与双向文本控制符、限了长度。
/// </remarks>
/// <param name="Target">哪条连接在问:<c>用户@主机:端口</c>(跳板链上各跳各报)。</param>
/// <param name="Name">服务端给的标题;没给时是空串。</param>
/// <param name="Instruction">服务端给的说明(含前几轮纯展示的说明);没给时是空串。</param>
/// <param name="Fields">要回答的提示,至少一条。</param>
public sealed record KeyboardInteractiveRequest(
    string Target,
    string Name,
    string Instruction,
    IReadOnlyList<KeyboardInteractiveField> Fields);

/// <summary>
/// keyboard-interactive 的界面:服务端要动态码时弹框问用户。
/// </summary>
/// <remarks>
/// 与 <see cref="IHostKeyPrompt" /> 同一个模式:基础设施层在后台线程上等,界面层负责弹窗。
/// </remarks>
public interface IKeyboardInteractivePrompt
{
    /// <summary>问用户这一轮的答案。</summary>
    /// <param name="request">这一轮询问。</param>
    /// <param name="cancellationToken">
    /// 连接被取消或认证超时时触发:实现方收起还开着的窗口,并抛 <see cref="OperationCanceledException" />。
    /// </param>
    /// <returns>与 <see cref="KeyboardInteractiveRequest.Fields" /> 条数相等的答案;用户点了取消返回 <see langword="null" />。</returns>
    Task<IReadOnlyList<string>?> AskAsync(KeyboardInteractiveRequest request, CancellationToken cancellationToken);
}
