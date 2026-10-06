// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH ssh_config(5)  各项的语义(只取行为描述)
//   行为规格:              velashell-docs/zh/ssh/spec/09-dialing.md §7

using System.Globalization;
using System.Text;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Forwarding;

namespace VelaShell.Ssh.Config;

/// <summary>把 <c>ssh_config</c> 解完之后，某一台主机最终生效的设置。</summary>
public sealed class SshHostConfig
{
    // ⚠️ IDE0028 会建议把它简化成 []。**不能听** —— 那会把
    //    OrdinalIgnoreCase 丢掉，而 ssh_config 的键是不区分大小写的
    //    （`HostName` 与 `hostname` 是同一个键）。
    private readonly Dictionary<string, List<string>> _settings = [with(StringComparer.OrdinalIgnoreCase)];

    /// <param name="host">查的是哪个名字。</param>
    /// <param name="originalHost">使用者输入的那个名字（<c>HostName</c> 里的 <c>%h</c> 换成它）；缺省就是 <paramref name="host"/>。</param>
    internal SshHostConfig(string host, string? originalHost = null)
    {
        QueriedHost = host;
        _originalHost = originalHost ?? host;
    }

    /// <summary>使用者输入的那个名字（<c>HostName</c> 里的 <c>%h</c> 换成它）。</summary>
    private readonly string _originalHost;

    /// <summary>当初查的是哪个名字。</summary>
    public string QueriedHost { get; }

    /// <summary>真正要连的主机（<c>HostName</c>，没有就是 <see cref="QueriedHost"/>）。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/09 §七〕<c>HostName</c> 里的 <c>%h</c> 换成使用者输入的名字、<c>%%</c> 换成 <c>%</c>
    /// （<c>Host *.prod</c> 配 <c>HostName %h.example.com</c> 是常见写法）。曾经原样交出去：建连拿字面量
    /// <c>%h.example.com</c> 去连，<c>DnsFailure</c>；<c>IdentityFile</c> 等处代入的 <c>%h</c> 也是这个没展开的值。
    /// </remarks>
    public string HostName => First("HostName") is { } configured ? ExpandHostTokens(configured, _originalHost) : QueriedHost;

    /// <summary>展开 <c>HostName</c> 认的两个记号：<c>%h</c> 与 <c>%%</c>；别的原样留着。</summary>
    private static string ExpandHostTokens(string value, string originalHost)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder result = new(value.Length + originalHost.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 1 < value.Length && value[i + 1] is 'h' or '%')
            {
                result.Append(value[++i] == 'h' ? originalHost : "%");
            }
            else
            {
                result.Append(value[i]);
            }
        }
        return result.ToString();
    }

    /// <summary>端口；没配、或者配的不是 1–65535 之间的整数时是 22。</summary>
    /// <remarks>
    /// 〔FW-E14〕配得不对的端口在建连时（<see cref="SshConfigFile.CreateConnectionOptionsAsync"/>）当成配置错误报出来；
    /// 这里只交出一个能用的值。曾经 <c>Port -1</c> / <c>Port 99999</c> 原样交出去，建连时抛的是 BCL 的参数异常，
    /// 宿主导入时又自己夹了一次。
    /// </remarks>
    public int Port => TryParsePort(First("Port"), out int port) ? port : 22;

    /// <summary>是不是 1–65535 之间的整数（不带正负号、不带空白）。</summary>
    internal static bool TryParsePort(string? text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>用户名。</summary>
    public string? User => First("User");

    /// <summary>私钥文件（<c>IdentityFile</c> 可以出现多次，按顺序），配置里的原文。</summary>
    /// <remarks>要拿来读文件，用 <see cref="ExpandIdentityFiles"/>。</remarks>
    public IReadOnlyList<string> IdentityFiles => All("IdentityFile");

    /// <summary>
    /// 展开好的私钥文件路径（按出现的顺序）：<c>~</c> 与 <c>%d</c> <c>%u</c> <c>%h</c> <c>%r</c> <c>%%</c> 照这台主机与用户展开，
    /// 与连接时读私钥用的是同一套；<c>IdentityFile none</c> 不算。
    /// </summary>
    /// <param name="remoteUser">登录用户（<c>%r</c>）；不给就用 <see cref="User"/>。</param>
    /// <remarks>
    /// 文件存不存在不管，相对路径也原样留着 —— 那是读的时候的事。
    /// 曾经展开是 internal 的，宿主导入 <c>ssh_config</c> 时只好自己再写一份（而且不认 <c>%h</c> / <c>%r</c>）。
    /// </remarks>
    public IReadOnlyList<string> ExpandIdentityFiles(string? remoteUser = null) =>
    [
        .. IdentityFiles
            .Where(static raw => !string.Equals(raw.Trim().Trim('"'), "none", StringComparison.OrdinalIgnoreCase))
            .Select(raw => SshConfigFile.ExpandPath(raw, HostName, remoteUser ?? User))
            .OfType<string>(),
    ];

    /// <summary>跳板（<c>ProxyJump</c>）。</summary>
    public string? ProxyJump => First("ProxyJump");

    /// <summary><c>ProxyCommand</c>：经一个外部程序连接（<c>none</c> 表示不用）。</summary>
    public string? ProxyCommand => First("ProxyCommand");

    /// <summary><c>ConnectTimeout</c>（秒）；没配为 <see langword="null"/>。</summary>
    public int? ConnectTimeoutSeconds =>
        int.TryParse(First("ConnectTimeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value > 0
            ? value
            : null;

    /// <summary><c>ForwardX11</c>。</summary>
    public bool ForwardX11 => IsYes(First("ForwardX11"));

    /// <summary><c>ForwardX11Trusted</c>。</summary>
    public bool ForwardX11Trusted => IsYes(First("ForwardX11Trusted"));

    /// <summary><c>ForwardX11Timeout</c>：X11 转发的有效期；没写或写不对为 <see langword="null"/>（用默认）。</summary>
    /// <remarks>
    /// ssh_config 的时间格式：数字后跟 <c>s</c> / <c>m</c> / <c>h</c> / <c>d</c> / <c>w</c>（大小写均可），
    /// 不带单位为秒，几段相加（<c>1h30m</c>）；<c>0</c> 为不过期（<see cref="TimeSpan.Zero"/>）。
    /// </remarks>
    public TimeSpan? ForwardX11Timeout =>
        TryParseTimeSpec(First("ForwardX11Timeout"), out TimeSpan value) ? value : null;

    /// <summary>解析 ssh_config 的时间格式（见 <see cref="ForwardX11Timeout"/>）。</summary>
    /// <remarks>写不对（空、带别的字符、单位不认识、溢出）就返回 <see langword="false"/> —— 不猜。</remarks>
    internal static bool TryParseTimeSpec(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string spec = text.Trim();
        long totalSeconds = 0;
        int i = 0;

        while (i < spec.Length)
        {
            int start = i;
            while (i < spec.Length && spec[i] is >= '0' and <= '9')
            {
                i++;
            }

            if (i == start
                || !long.TryParse(spec.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out long number))
            {
                return false;
            }

            long unit = 1;
            if (i < spec.Length)
            {
                unit = char.ToLowerInvariant(spec[i]) switch
                {
                    's' => 1,
                    'm' => 60,
                    'h' => 60 * 60,
                    'd' => 24 * 60 * 60,
                    'w' => 7 * 24 * 60 * 60,
                    _ => 0,
                };

                if (unit == 0)
                {
                    return false;
                }
                i++;
            }

            try
            {
                totalSeconds = checked(totalSeconds + (number * unit));
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (totalSeconds > (long)TimeSpan.MaxValue.TotalSeconds)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    /// <summary><c>StrictHostKeyChecking</c> 的原文（<c>yes</c> / <c>no</c> / <c>ask</c> / <c>accept-new</c>）。</summary>
    public string? StrictHostKeyChecking => First("StrictHostKeyChecking");

    /// <summary><c>UserKnownHostsFile</c>。</summary>
    public string? UserKnownHostsFile => First("UserKnownHostsFile");

    /// <summary>是否只用显式给出的密钥（<c>IdentitiesOnly yes</c>）。</summary>
    public bool IdentitiesOnly => IsYes(First("IdentitiesOnly"));

    /// <summary><c>ForwardAgent</c> 开着没有：<c>yes</c>、给了 agent 套接字路径、给了设着的 <c>$环境变量</c> 都算开。</summary>
    public bool ForwardAgent => TryGetForwardedAgent(out _);

    /// <summary>按 <c>ForwardAgent</c> 的值决定要不要转发、转发哪个 agent。</summary>
    /// <param name="endpoint">要转发的 agent；<see langword="null"/> 是默认的那个（<c>SSH_AUTH_SOCK</c> / Windows 的 OpenSSH agent 管道）。</param>
    /// <returns>要转发时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/09 §7〕ssh_config(5) 的写法有四种：<c>yes</c> / <c>no</c>（大小写均可）、
    /// 一个 agent 套接字的路径（展开 <c>~</c> 与 <c>%d %u %h %r</c>）、以 <c>$</c> 开头的环境变量名（变量的值是路径）。
    /// 曾经只认 <c>yes</c>：写了路径的配置被当成 <c>no</c>，转发悄悄没开。
    /// </para>
    /// <para>
    /// 环境变量没设或为空时不转发 —— 没有一个可转发的 agent，宣告出去也只是让远端白连一次。
    /// </para>
    /// </remarks>
    internal bool TryGetForwardedAgent(out string? endpoint)
    {
        endpoint = null;
        string? value = First("ForwardAgent")?.Trim();

        if (string.IsNullOrEmpty(value) || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.StartsWith('$'))
        {
            string? fromEnvironment = value.Length > 1 ? Environment.GetEnvironmentVariable(value[1..]) : null;
            endpoint = string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
            return endpoint is not null;
        }

        endpoint = SshConfigFile.ExpandPath(value, HostName, User);
        return endpoint is not null;
    }

    /// <summary><c>Ciphers</c>：加密算法清单（OpenSSH 的 <c>+ - ^</c> 写法，见 <see cref="Crypto.SshAlgorithmSpec"/>）。</summary>
    public string? Ciphers => First("Ciphers");

    /// <summary><c>KexAlgorithms</c>：密钥交换算法清单（写法同上）。</summary>
    public string? KexAlgorithms => First("KexAlgorithms");

    /// <summary><c>MACs</c>：MAC 算法清单（写法同上）。</summary>
    public string? Macs => First("MACs");

    /// <summary><c>HostKeyAlgorithms</c>：主机密钥算法清单（写法同上）。</summary>
    public string? HostKeyAlgorithms => First("HostKeyAlgorithms");

    /// <summary><c>PubkeyAcceptedAlgorithms</c>（旧名 <c>PubkeyAcceptedKeyTypes</c>）：公钥认证用哪些签名算法（写法同上）。</summary>
    public string? PubkeyAcceptedAlgorithms => First("PubkeyAcceptedAlgorithms") ?? First("PubkeyAcceptedKeyTypes");

    /// <summary><c>Compression</c>。</summary>
    public bool Compression => IsYes(First("Compression"));

    /// <summary><c>ServerAliveInterval</c>（秒，<c>0</c> = 关）。</summary>
    public int ServerAliveInterval =>
        int.TryParse(
            First("ServerAliveInterval"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    /// <summary><c>ServerAliveCountMax</c>。</summary>
    public int ServerAliveCountMax =>
        int.TryParse(
            First("ServerAliveCountMax"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 3;

    /// <summary>取某个键的第一个值。</summary>
    /// <remarks>
    /// <b>「第一个」是对的，不是「最后一个」。</b>
    /// <c>ssh_config</c> 的规则是<b>先出现的赢</b> —— 与大多数配置格式相反。
    /// 这也是为什么 OpenSSH 的样例里 <c>Host *</c> 总是放在文件末尾。
    /// </remarks>
    public string? First(string key) =>
        _settings.TryGetValue(key, out List<string>? values) && values.Count > 0 ? values[0] : null;

    /// <summary>取某个键的全部值，按出现顺序。</summary>
    public IReadOnlyList<string> All(string key) =>
        _settings.TryGetValue(key, out List<string>? values) ? values : [];

    internal void Add(string key, string value)
    {
        if (!_settings.TryGetValue(key, out List<string>? values))
        {
            values = [];
            _settings[key] = values;
        }
        values.Add(value);
    }

    private static bool IsYes(string? value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把配置里的<b>会话</b>项（<c>ForwardAgent</c>、<c>ForwardX11</c>、<c>ForwardX11Trusted</c>、<c>ForwardX11Timeout</c>）套到 shell 参数上。
    /// </summary>
    /// <param name="template">起点；<see langword="null"/> 用默认 shell 参数。</param>
    /// <remarks>
    /// <para>
    /// 它们不是连接参数：同一条连接上的不同会话可以各开各的转发。
    /// 模板里已经显式设了的不会被覆盖。
    /// </para>
    /// <para>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕<c>ForwardX11 yes</c> 与 <c>ForwardAgent</c> 产生的选项都按
    /// <see cref="ForwardFailureMode.Continue"/> 请求：本机没有显示、没有 <c>xauth</c>、本机 agent 没在跑、
    /// 服务端拒绝时 shell 照常启动，原因见 <see cref="SshShell.X11SetupFailure"/> / <see cref="SshShell.AgentSetupFailure"/> ——
    /// 一份存量配置不该让所有会话都起不来。模板里调用方自己给的选项保持原样（显式的，失败就抛）。
    /// </para>
    /// </remarks>
    public SshShellOptions ApplyToShell(SshShellOptions? template = null)
    {
        SshShellOptions options = template ?? SshShellOptions.Default;

        if (options.AgentForwarding is null && TryGetForwardedAgent(out string? agentEndpoint))
        {
            options = options with
            {
                AgentForwarding = new AgentForwardOptions
                {
                    AgentEndpoint = agentEndpoint,
                    FailureMode = ForwardFailureMode.Continue,
                },
            };
        }

        if (ForwardX11 && options.X11Forwarding is null)
        {
            options = options with
            {
                X11Forwarding = new X11ForwardOptions
                {
                    Trusted = ForwardX11Trusted,
                    FailureMode = ForwardFailureMode.Continue,
                    Timeout = ForwardX11Timeout ?? X11ForwardOptions.Default.Timeout,
                },
            };
        }

        return options;
    }
}
