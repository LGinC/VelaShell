using System.Text;
using System.Text.RegularExpressions;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 一次认证里的 keyboard-interactive 应答:认得出的口令提示用已有的密码答,其余弹框问用户。
/// </summary>
/// <remarks>
/// <para>
/// 最常见的 2FA 形状是 PAM 先问 <c>Password:</c> 再问 <c>Verification code:</c>,两轮各一条不回显提示。
/// 库自带的「密码兼答 keyboard-interactive」只看形状不看内容,会把密码也填进验证码那一轮 ——
/// 所以接上这个应答器时要关掉那一路(见 <see cref="SshConnectionAssembler.BuildCredentialsAsync" />)。
/// </para>
/// <para>
/// <b>私钥 / 证书 / agent 那几路不回退到口令</b>(<paramref name="password" /> 为 <see langword="null" />):
/// 只有一条的口令提示直接答空串让服务端拒掉,只有验证码之类的才弹框 ——
/// 那是 <c>AuthenticationMethods publickey,keyboard-interactive</c>(钥 + 动态码)的第二步。
/// 钥被拒之后冒出一个密码框,与「只用用户选的那一种认证方式」相悖。
/// </para>
/// </remarks>
/// <param name="prompt">界面。</param>
/// <param name="target">给用户看的连接目标。</param>
/// <param name="password">用户选的是密码认证时它的密码(可能为空串);其余认证方式为 <see langword="null" />。</param>
internal sealed partial class KeyboardInteractiveResponder(IKeyboardInteractivePrompt prompt, string target, string? password)
{
    private const int MaxNameLength = 128;
    private const int MaxInstructionLength = 2048;
    private const int MaxPromptLength = 256;

    private bool _passwordUsed;
    private string? _pendingNotice;

    /// <summary>
    /// 用户在框上点了取消。
    /// </summary>
    /// <remarks>
    /// 库把应答回调抛出的异常记成「凭据取不到材料」、接着以「方法试完了」收场(规格 04 §3.4),
    /// 取消则会被它当成自己的计时器到点 —— 两条路都说不出「用户不连了」。
    /// 所以取消记在这里,由装配处在连接失败之后认回来(见 <c>SshConnectionAssembler.ConnectAsync</c>)。
    /// </remarks>
    public bool Cancelled { get; private set; }

    /// <summary>按连接信息建应答器:密码认证把它的密码交给应答器代答口令提示,其余认证方式不代答。</summary>
    public static KeyboardInteractiveResponder For(Core.Models.ConnectionInfo info, IKeyboardInteractivePrompt prompt) =>
        new(prompt, $"{info.Username}@{info.Host}:{info.Port}",
            info.AuthMethod == Core.Models.AuthMethod.Password ? info.Password ?? "" : null);

    /// <summary>包成库的凭据。</summary>
    public KeyboardInteractiveCredential ToCredential() => new(RespondAsync, "keyboard-interactive");

    /// <summary>应答一轮询问。</summary>
    public async ValueTask<IReadOnlyList<string>> RespondAsync(SshKeyboardChallenge challenge, CancellationToken cancellationToken)
    {
        if (challenge.IsInformationalOnly)
        {
            // 纯展示的一轮不弹框(规格 04 §6.4),说明攒到下一个要输入的框里一起显示 ——
            // 那往往就是「请在手机上确认」之后紧跟着的那一问。
            string notice = Clean(challenge.Instruction, MaxInstructionLength);
            if (notice.Length > 0)
            {
                _pendingNotice = _pendingNotice is null ? notice : $"{_pendingNotice}\n{notice}";
            }
            return [];
        }

        if (challenge.Prompts is [{ Echo: false } only] && LooksLikePasswordPrompt(only.Text))
        {
            if (password is null)
            {
                return [string.Empty];
            }
            // 只代答一次:同一次认证里再问口令,说明刚才那个不对,交给用户重输。
            if (!_passwordUsed && password.Length > 0)
            {
                _passwordUsed = true;
                return [password];
            }
        }

        string instruction = Clean(challenge.Instruction, MaxInstructionLength);
        if (_pendingNotice is not null)
        {
            instruction = instruction.Length == 0 ? _pendingNotice : $"{_pendingNotice}\n{instruction}";
            _pendingNotice = null;
        }
        KeyboardInteractiveField[] fields =
        [
            .. challenge.Prompts.Select(p => new KeyboardInteractiveField(Clean(p.Text, MaxPromptLength), p.Echo))
        ];
        IReadOnlyList<string>? answers = await prompt
            .AskAsync(new(target, Clean(challenge.Name, MaxNameLength), instruction, fields), cancellationToken)
            .ConfigureAwait(false);
        if (answers is null)
        {
            // 令牌触发(关了标签、认证超时)仍按取消上报,由库按它自己的口径处理;
            // 只有用户在框上点了取消,才是「不连了」。
            cancellationToken.ThrowIfCancellationRequested();
            Cancelled = true;
            throw new VelaSshAuthenticationCancelledException(Strings.Get("SshErr_KbdAuthCancelled"));
        }
        // 条数对不上时库会报协议错误并卡在半截 —— 界面层的错不该变成一句看不懂的协议异常。
        return answers.Count == fields.Length
            ? answers
            : throw new InvalidOperationException($"keyboard-interactive: expected {fields.Length} answers, got {answers.Count}.");
    }

    /// <summary>是不是一条单纯要口令的提示(带验证码字样的不算:那要的往往是「口令 + 动态码」拼起来的串)。</summary>
    internal static bool LooksLikePasswordPrompt(string text) =>
        PasswordWords().IsMatch(text) && !OneTimeCodeWords().IsMatch(text);

    /// <summary>
    /// 去掉控制字符与双向文本控制符、统一换行、限长 —— 文字来自尚未认证的对端。
    /// </summary>
    internal static string Clean(string text, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (char c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (c is '\n' or '\t' || !(char.IsControl(c) || IsBidiControl(c)))
            {
                builder.Append(c is '\t' ? ' ' : c);
            }
        }
        string cleaned = builder.ToString().Trim();
        return cleaned.Length <= maxLength ? cleaned : string.Concat(cleaned.AsSpan(0, maxLength - 1), "…");
    }

    private static bool IsBidiControl(char c) => c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏' or '؜';

    [GeneratedRegex(@"\bpass(word|phrase)?\b|密码|口令|密碼|パスワード|비밀번호|암호", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordWords();

    [GeneratedRegex(@"\b(otp|totp|token|code|verification|one[- ]?time|pin|2fa|mfa)\b|验证码|動態|动态|令牌|一次性|驗證碼|認証コード|ワンタイム|인증 ?코드|일회용", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneTimeCodeWords();
}
