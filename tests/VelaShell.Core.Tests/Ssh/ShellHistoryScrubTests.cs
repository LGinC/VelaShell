using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 摘历史那段代码的<b>拼装</b>规则。shell 语义由真 bash 的
/// <c>ShellHistoryScrubShellTests</c> 验,这里只钉住"拼出来的东西长什么样"。
/// </summary>
[TestClass]
public sealed class ShellHistoryScrubTests
{
    /// <summary>
    /// 摘历史必须接在<b>前面</b>。
    /// </summary>
    /// <remarks>
    /// 缀在后面的话,这一行的退出码就成了收尾动作的 0,而不是用户那条命令自己的 ——
    /// starship / powerlevel10k 会把上一条的退出码画在提示符上,一条失败的
    /// 「认证后执行命令」于是变得毫无痕迹。
    /// </remarks>
    [TestMethod]
    [DataRow(RemoteShellKind.Bash)]
    [DataRow(RemoteShellKind.Zsh)]
    public void TheScrubGoesInFront_SoTheCommandStillDecidesTheExitStatus(RemoteShellKind kind)
    {
        string line = ShellHistoryScrub.Prepend(kind, "cd '/var/log'");

        Assert.StartsWith(ShellHistoryScrub.For(kind), line);
        Assert.EndsWith("; cd '/var/log'", line);
    }

    /// <summary>没有命令就没有历史要摘:不能凭空多发一行(那本身就会在历史里留一条)。</summary>
    [TestMethod]
    public void WithoutACommand_NothingIsAdded()
    {
        Assert.AreEqual(string.Empty, ShellHistoryScrub.Prepend(RemoteShellKind.Bash, string.Empty));
        Assert.AreEqual("   ", ShellHistoryScrub.Prepend(RemoteShellKind.Zsh, "   "));
    }

    /// <summary>
    /// 按种类挑前缀。zsh 换成它自己那段 —— bash 那段在 zsh 上是两百多字符的空操作,还原样留在历史里;
    /// fish / 非 POSIX 一个字节都不接。
    /// </summary>
    /// <remarks>
    /// dash / ash 与探不出种类的仍接 bash 那段:登录 shell 是 sh、<c>.profile</c> 里 <c>exec bash</c>
    /// 的机器上,探针只看得到 sh,而那时 bash 那段是唯一真能摘掉历史的东西。
    /// </remarks>
    [TestMethod]
    [DataRow(RemoteShellKind.Bash, ShellHistoryScrub.Command)]
    [DataRow(RemoteShellKind.PosixSh, ShellHistoryScrub.Command)]
    [DataRow(RemoteShellKind.Unknown, ShellHistoryScrub.Command)]
    [DataRow(RemoteShellKind.Zsh, ShellHistoryScrub.ZshCommand)]
    [DataRow(RemoteShellKind.Fish, "")]
    [DataRow(RemoteShellKind.NonPosix, "")]
    public void EachShellGetsTheScrubItCanUse(RemoteShellKind kind, string expected)
    {
        Assert.AreEqual(expected, ShellHistoryScrub.For(kind));
        Assert.AreEqual(expected.Length > 0, ShellHistoryScrub.SupportedBy(kind));
        Assert.AreEqual(
            expected.Length > 0 ? $"{expected}; echo hi" : "echo hi",
            ShellHistoryScrub.Prepend(kind, "echo hi"));
    }

    /// <summary>
    /// zsh 那段:守卫在 <c>ZSH_VERSION</c> 上,靠 <c>HISTORY_IGNORE</c> 不让这一行写进历史文件,
    /// 并且是<b>追加</b> —— 用户自己的 <c>HISTORY_IGNORE</c> 作为一个分支留着,原来该拦的照拦。
    /// </summary>
    /// <remarks>
    /// 模式里写着记号,于是每一条接了这段的注入行都会被它自己拦住;已经有记号就不再追加,
    /// 同一个 shell 里连着注入几行也不会把模式越拼越长。shell 语义由 Docker 端到端用例
    /// (<c>ShellIntegrationDockerTests</c>,真 sshd + 真 zsh)验。
    /// </remarks>
    [TestMethod]
    public void TheZshScrub_GuardsOnZsh_AndAppendsToHistoryIgnore()
    {
        string zsh = ShellHistoryScrub.ZshCommand;

        Assert.StartsWith("test -n \"${ZSH_VERSION:-}\" && eval '", zsh);
        Assert.Contains($"*{ShellHistoryScrub.Marker}*", zsh);
        Assert.Contains("HISTORY_IGNORE=\"(${HISTORY_IGNORE:+$HISTORY_IGNORE|}", zsh);
        Assert.Contains($"[[ ${{HISTORY_IGNORE-}} == *{ShellHistoryScrub.Marker}* ]] ||", zsh);
        Assert.DoesNotContain("history -d", zsh, "zsh 没有 history -d");
        Assert.DoesNotContain("BASH_VERSION", zsh);
    }

    /// <summary>
    /// 两条不变量:非 bash 一律短路(zsh 没有 <c>history -d</c>,fish 连解析都过不去),
    /// 以及"删之前先认一认"的那个记号必须真的出现在这一行里。
    /// </summary>
    /// <remarks>
    /// 记号就是那个临时变量名本身。少了它,遇上配了 <c>HISTCONTROL=ignorespace</c> 的用户 ——
    /// 那时我们这行根本没进历史 —— 删掉的就是人家上一条真命令。
    /// </remarks>
    [TestMethod]
    public void ItGuardsOnBashAndCarriesItsOwnMarker()
    {
        Assert.Contains("test -n \"${BASH_VERSION:-}\"", ShellHistoryScrub.Command);
        Assert.Contains(ShellHistoryScrub.Marker, ShellHistoryScrub.Command);
        Assert.Contains("builtin history -d", ShellHistoryScrub.Command);
    }
}
