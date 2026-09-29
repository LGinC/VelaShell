using VelaShell.Ssh.Auth;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 底层 SSH 库是否支持键盘交互式认证(2FA / OTP)。
/// </summary>
/// <remarks>
/// <para>
/// <b>这条引信已经烧过一次了。</b>它原先断言的是「还不支持」——
/// F-11 的结论当时是「做不了,只能把话说清楚」:堡垒机上的 Google Authenticator、
/// Duo 一类走的是 SSH 的 <c>keyboard-interactive</c>,而上一版底层库压根没实现它,
/// 凭据类型只有密码 / 私钥 / 证书 / Kerberos / ssh-agent / 无。
/// </para>
/// <para>
/// 换到 VelaShell.Ssh 之后它是一等公民,于是引信响了,断言方向随之翻转:
/// <b>现在盯的是「别把它丢了」</b> —— 哪天凭据类型里没有它,这里会红。
/// </para>
/// <para>
/// 宿主的动态码界面已经接上(<c>KeyboardInteractiveResponder</c> + 界面层的弹框,plan.md §133),
/// 失败文案里那句「本版不会两步验证」随之撤掉。应答逻辑的用例在 <c>KeyboardInteractiveResponderTests</c>。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class KeyboardInteractiveSupportTests
{
    [TestMethod]
    public void TheSshLibraryOffersAKeyboardInteractiveCredential()
    {
        Type[] credentials =
        [
            .. typeof(SshCredential).Assembly
                                    .GetExportedTypes()
                                    .Where(t => typeof(SshCredential).IsAssignableFrom(t) && t != typeof(SshCredential))
        ];

        Assert.IsNotEmpty(credentials, "一个凭据类型都没找到 —— 反射扫描失效了,这条用例等于没测。");
        Assert.Contains(
            t => t.Name.Contains("KeyboardInteractive", StringComparison.OrdinalIgnoreCase), credentials,
            "键盘交互式凭据不见了:2FA / OTP 的服务器会重新变成连不上,而失败文案会把用户引向一条改不对的路。");
    }

    /// <summary>
    /// 密码凭据默认也应答 <c>keyboard-interactive</c>。
    /// </summary>
    /// <remarks>
    /// 这一条覆盖的是最常见的那种「2FA」——其实只是服务端关了 <c>PasswordAuthentication</c>
    /// 而走 PAM,用户填的还是同一个密码。它不需要任何界面改动就能生效。
    /// </remarks>
    [TestMethod]
    public void PasswordCredentialAnswersKeyboardInteractiveByDefault()
    {
        PasswordCredential credential = new("hunter2");

        Assert.IsTrue(credential.AlsoAnswerKeyboardInteractive);
    }
}
