namespace VelaShell.Core.Ssh;

/// <summary>
/// 静默注入的收尾:让被注入的那一行不留在远端 shell 的命令历史里。
/// </summary>
/// <remarks>
/// <para>
/// 注入的命令在屏幕上是隐形的(<c>SshTerminalBridge.SuppressEchoOnce</c> 把回显剥掉了),
/// 在历史里却不是:用户登进去按一下方向键,迎面就是一整行
/// <c>test -n "${BASH_VERSION:-}" &amp;&amp; eval '…'</c> —— 那不是他敲的,他也不知道那是什么,
/// 第一反应是「谁往我服务器上注了东西」。屏幕上隐形、历史里现形,这本身就是自相矛盾的。
/// </para>
/// <para>
/// <b>为什么前导空格不够。</b>注入一直带着一个前导空格,那是 <c>HISTCONTROL=ignorespace</c>
/// 的老办法 —— 可 <c>HISTCONTROL</c> 默认<b>是空的</b>,绝大多数机器上那个空格什么也没做。
/// 它仍然留着(配了的人本就该受益),但不能当成防线。
/// </para>
/// <para>
/// <b>怎么摘。</b>bash 在<b>读到</b>这一行时就把它记进了历史,所以命令跑起来的时候
/// 「最后一条」正是自己 —— <c>history -d</c> 把它删掉即可,而且要赶在
/// <c>PROMPT_COMMAND</c> 之前(有人在那儿挂 <c>history -a</c> 往文件里追加),
/// 所以这段是<b>前置</b>在注入行开头的,不是缀在末尾:
/// </para>
/// <list type="bullet">
/// <item>前置还顺带保住了退出码 —— 这一行最终的 <c>$?</c> 由用户自己那条命令决定,
/// 而不是被收尾动作抹成 0。提示符(starship / p10k)是会把它画出来的。</item>
/// <item>删之前先<b>认一认</b>:只有当最后一条历史里出现 <see cref="Marker" /> 才动手。
/// 少了这一步,遇上真配了 <c>ignorespace</c> 的用户 —— 那时我们这行<b>根本没进历史</b> ——
/// 删掉的就是人家上一条真命令。删错用户的历史比留下一行噪音严重得多。</item>
/// <item>标记就是那个临时变量名本身,因此它必然出现在被记下的那一行里,不必另外埋记号。</item>
/// </list>
/// <para>
/// <b>整段仍旧包在 <c>eval '…'</c> 里,由 <c>BASH_VERSION</c> 守卫。</b>理由与目录上报钩子
/// 逐字相同(见 <see cref="ShellIntegrationScript.Bash" />):shell 先把整行解析完
/// 再执行,裸写的 <c>case</c>/<c>${var//}</c> 会让 fish 在<b>解析阶段</b>就报错,那时守卫还没
/// 来得及短路。
/// </para>
/// <para>
/// <b>zsh 另有一段(<see cref="ZshCommand" />)。</b>zsh 没有 <c>history -d</c>,上面这段在它那里
/// 只是两百多字符的空操作,还原样留在历史里。探针认出是 zsh 时就换成 zsh 自己能做到的那一半 ——
/// 不让这一行写进历史文件(能做到哪一步、做不到哪一步,见那边)。按种类挑哪段由 <see cref="For" /> 决定。
/// </para>
/// </remarks>
public static class ShellHistoryScrub
{
    /// <summary>
    /// 历史里认自己用的记号。它同时是那个临时变量的名字,因此一定出现在被记下的那一行中。
    /// </summary>
    public const string Marker = "__vela_hist_scrub";

    /// <summary>
    /// 摘掉「最后一条历史」的那段 bash 代码(非 bash 一律短路,一个字节都不执行)。
    /// </summary>
    /// <remarks>
    /// <c>HISTTIMEFORMAT=</c> 这个前缀不能省:用户设了它之后 <c>history 1</c> 会在序号后面
    /// 多打一列时间戳,而下面要按「开头的数字」取序号。前缀只对这一次调用生效,不动用户的设置。
    /// 取序号用参数展开而不是 <c>set -- $line</c>:后者会对历史内容做通配展开,
    /// 一条含 <c>*</c> 的命令能把它变成一串文件名。
    /// </remarks>
    public const string Command =
        """
        test -n "${BASH_VERSION:-}" && eval '__vela_hist_scrub=$(HISTTIMEFORMAT= builtin history 1); __vela_hist_scrub=${__vela_hist_scrub#"${__vela_hist_scrub%%[![:space:]]*}"}; case "$__vela_hist_scrub" in *__vela_hist_scrub*) builtin history -d "${__vela_hist_scrub%%[![:digit:]]*}";; esac; unset __vela_hist_scrub'
        """;

    /// <summary>
    /// zsh 版:让注入行不写进<b>历史文件</b>(zsh 没有 <c>history -d</c>,本会话的历史表里摘不掉)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>本会话里那一条是摘不掉的。</b>zsh 读完一行、执行之前就已把它记进历史表,
    /// <c>HIST_IGNORE_SPACE</c> 与 <c>zshaddhistory</c> 钩子也都在那一刻判完 ——
    /// 这一行里再 <c>setopt</c>、再装钩子都来不及,<c>fc</c> 也不会把自己那一行换掉(zsh 5.9 实测)。
    /// 配了 <c>HIST_IGNORE_SPACE</c> 的人(oh-my-zsh 默认就开)靠注入行的那个前导空格,
    /// 历史表与历史文件里都不留;其余的人按方向键仍看得见这一行,只到这个会话结束为止。
    /// </para>
    /// <para>
    /// <b>不落盘是做得到的。</b><c>HISTORY_IGNORE</c> 是<b>写历史文件时</b>才逐行比对的,
    /// 所以这一行跑的时候把模式加上,轮到写盘时它已经生效:默认的退出时写盘
    /// (macOS 出厂的 <c>/etc/zshrc</c> 就是这种)、<c>APPEND_HISTORY</c>、<c>INC_APPEND_HISTORY_TIME</c>
    /// 三种都拦得住。漏网的是 <c>INC_APPEND_HISTORY</c> 与 <c>SHARE_HISTORY</c>:它们读完一行就立刻写盘,
    /// <b>第一条</b>注入行赶在模式生效之前已经写了进去,之后的注入行照样拦得住。
    /// </para>
    /// <para>
    /// <b>追加,不覆盖。</b>用户自己的 <c>HISTORY_IGNORE</c> 作为一个分支包进括号,原来该拦的照拦。
    /// 模式里已经有记号就不再追加 —— 同一个 shell 里会连着注入好几行(钩子、启动命令、认证后命令)。
    /// 模式本身就写着 <see cref="Marker" />,所以凡是接了这段前缀的行都会被它自己拦住,不必另埋记号。
    /// </para>
    /// <para>
    /// 守卫与 <c>eval '…'</c> 的写法同 bash 那段:探针认的是<b>登录</b> shell,交互 shell 万一是别的
    /// (<c>.zshrc</c> 里 <c>exec bash</c>),这段就是空操作,不在别的 shell 里留下一个不认识的变量。
    /// </para>
    /// </remarks>
    public const string ZshCommand =
        """
        test -n "${ZSH_VERSION:-}" && eval '[[ ${HISTORY_IGNORE-} == *__vela_hist_scrub* ]] || HISTORY_IGNORE="(${HISTORY_IGNORE:+$HISTORY_IGNORE|}*__vela_hist_scrub*)"'
        """;

    /// <summary>
    /// 该种 shell 该接哪一段前缀;空串 = 不接。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>fish 不接,接了是致命的。</b>前缀里的 <c>${BASH_VERSION:-}</c> 在 fish 里不是
    /// "求值得到空串",而是<b>解析期语法错误</b>(<c>Expected a variable name after this $</c>)——
    /// fish 先把整行解析完再执行,于是<b>整行连同后面真正要跑的命令一起死掉</b>。
    /// 也就是说:不挡这一下,fish 会话的目录上报脚本、初始目录 <c>cd</c>、认证后命令
    /// <b>一条都不会执行</b>,而注入窗口还会把那行报错藏起来 —— 表现就是"功能莫名其妙不工作"。
    /// </para>
    /// <para>
    /// <see cref="RemoteShellKind.NonPosix" />(cmd.exe / PowerShell)一并不接:那上面本就
    /// 不该出现 sh 代码(#305)。
    /// </para>
    /// <para>
    /// <b>只有认出是 zsh 才换成 <see cref="ZshCommand" />。</b>dash / ash / ksh(<see cref="RemoteShellKind.PosixSh" />)
    /// 与探不出种类的 <see cref="RemoteShellKind.Unknown" /> 仍接 bash 那段:登录 shell 是 <c>/bin/sh</c>、
    /// 在 <c>.profile</c> 里 <c>exec bash</c> 的机器不少见,探针在那里只看得到 sh,
    /// 这时 bash 那段是唯一真能摘掉历史的东西;对真的 dash / ash 它不过是被守卫短路的空操作。
    /// </para>
    /// </remarks>
    public static string For(RemoteShellKind kind) =>
        kind switch
        {
            RemoteShellKind.Fish or RemoteShellKind.NonPosix => string.Empty,
            RemoteShellKind.Zsh => ZshCommand,
            _ => Command
        };

    /// <summary>该种 shell 有没有可接的前缀(理由见 <see cref="For" />)。</summary>
    public static bool SupportedBy(RemoteShellKind kind) => For(kind).Length > 0;

    /// <summary>
    /// 把该种 shell 的那段前缀接在注入命令<b>前面</b>,返回可直接发给 PTY 的一整行。
    /// </summary>
    /// <param name="kind">对端 shell 种类;决定接哪一段,或者不接(见 <see cref="For" />)。</param>
    /// <param name="command">要静默执行的命令;空白则原样返回(没有命令就没有历史要摘)。</param>
    /// <returns>带前缀的整行,或原串。</returns>
    public static string Prepend(RemoteShellKind kind, string command) =>
        string.IsNullOrWhiteSpace(command) || For(kind) is not { Length: > 0 } scrub
            ? command
            : $"{scrub}; {command}";
}
