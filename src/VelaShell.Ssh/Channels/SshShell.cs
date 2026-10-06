// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.2  pty-req
//   RFC 4254 §6.5  shell
//   RFC 4254 §6.7  window-change
//   行为规格:      velashell-docs/zh/ssh/spec/05-connection.md §5.3、§7.2

using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Channels;

/// <summary>打开交互式 shell 的参数。</summary>
public sealed record SshShellOptions : SshSessionRequestOptions
{
    /// <summary>建一组默认的 shell 参数。</summary>
    /// <remarks>
    /// 默认把 stderr 设成丢弃：<b>有 pty 时 stderr 会合并进 stdout</b>，
    /// 伪终端只有一条输出流，缓冲一条永远没数据的流毫无意义。
    /// </remarks>
    public SshShellOptions() =>
        Channel = SshChannelOptions.Default with { StderrMode = SshStderrMode.Discard };

    /// <summary>终端类型。</summary>
    public string TerminalType { get; init; } = "xterm-256color";

    /// <summary>初始尺寸。</summary>
    public SshTerminalSize Size { get; init; } = SshTerminalSize.Default;

    /// <summary>终端模式。</summary>
    public SshTerminalModes Modes { get; init; } = SshTerminalModes.Empty;

    /// <summary>
    /// 在伪终端里跑的命令（相当于 <c>ssh -t host 命令</c>）；<see langword="null"/>（默认）开登录 shell。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/05 §7.2〕给了命令时，<c>pty-req</c> 之后发的是 <c>exec</c> 而不是 <c>shell</c>：
    /// 要终端的命令（<c>sudo</c>、<c>top</c>、交互式的 TUI）一次跑完，不必开一整个登录 shell。
    /// 结果仍是 <see cref="SshShell"/> —— 有伪终端就只有一条输出流、尺寸变化发 <c>window-change</c>，
    /// 这正是这个类型与 <see cref="SshCommand"/> 分开的理由；命令跑完通道就关，退出码照常取。
    /// </para>
    /// <para>
    /// 与 <see cref="Session.SshConnectionExtensions.ExecuteAsync"/> 一样，命令是<b>一整条字符串、由远端的登录 shell 解释</b>：
    /// 拼进不可信的内容就是注入，库不能替使用者转义。
    /// </para>
    /// </remarks>
    public string? Command { get; init; }

    /// <summary>
    /// 按键时序混淆的节拍（OpenSSH 的 <c>ObscureKeystrokeTiming</c>，它的默认是 20 毫秒）；<see langword="null"/>（默认）不混淆。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/05 §7.3〕按键之间的时间间隔在网上看得见（每次按键一个报文），是公认的侧信道 —— 输口令、敲命令的节奏能推测出内容。
    /// 打开之后输入按固定节拍发，没有输入的节拍上发等长的 PING 当掩护，一直到最后一次按键之后的一段随机时间（0.5–1.5 秒）；闲着时一个报文都不发。
    /// </para>
    /// <para>
    /// 代价是带宽：打字时每秒约 1000 / 节拍 个报文（服务端还回同样多的 PONG），且每次按键最多晚一个节拍才发出去。
    /// 掩护要服务端认 PING（<c>ping@openssh.com</c>，OpenSSH 9.5 起）；不认时只攒批、不发掩护。粘贴（一次写进来很多）不受影响。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不在 1 毫秒到 1 秒之间。</exception>
    public TimeSpan? ObscureKeystrokeTiming
    {
        get;
        init => field = value is null || (value >= TimeSpan.FromMilliseconds(1) && value <= TimeSpan.FromSeconds(1))
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ObscureKeystrokeTiming), value, "节拍要在 1 毫秒到 1 秒之间；不混淆就给 null。");
    }

    /// <summary>默认参数。</summary>
    public static SshShellOptions Default { get; } = new();
}

/// <summary>一个正在运行的交互式 shell。</summary>
/// <remarks>
/// <para>
/// 与 <see cref="SshCommand"/> 的差别只有三处，但都关乎正确性：
/// </para>
/// <list type="number">
///   <item><c>exec</c> 换成 <c>pty-req</c> + <c>shell</c>。</item>
///   <item><b>有 pty 时 stderr 合并进 stdout</b> —— 伪终端只有一条输出流。
///   〔决策 velashell-docs/zh/ssh/spec/05 §7.2〕所以这个类型<b>干脆不暴露 <c>StandardError</c></b>，
///   免得使用者对着一条永远空的流等待。</item>
///   <item>尺寸变化发 <c>window-change</c>。</item>
/// </list>
/// 其余成员与 <see cref="SshCommand"/> 同名同义。
/// </remarks>
public sealed class SshShell : IAsyncDisposable
{
    internal SshShell(
        SshChannel channel,
        SshTerminalSize size,
        X11Forwarder? x11 = null,
        AgentForwarder? agent = null,
        SshForwardException? x11SetupFailure = null,
        SshForwardException? agentSetupFailure = null,
        KeystrokeObfuscator? obfuscator = null)
    {
        _obfuscator = obfuscator;
        Channel = channel;
        Size = size;
        X11 = x11;
        Agent = agent;
        X11SetupFailure = x11SetupFailure;
        AgentSetupFailure = agentSetupFailure;
    }

    private readonly KeystrokeObfuscator? _obfuscator;

    /// <summary>按键时序混淆在不在跑（<see cref="SshShellOptions.ObscureKeystrokeTiming"/>）。</summary>
    public bool IsObscuringKeystrokeTiming => _obfuscator is not null;

    /// <summary>按键时序混淆发过的掩护报文个数（诊断用；没开时为 0）。</summary>
    public int KeystrokeChaffSent => _obfuscator?.ChaffSent ?? 0;

    /// <summary>底层通道。</summary>
    public SshChannel Channel { get; }

    /// <summary>这个 shell 的 X11 转发；没请求过、或按 <see cref="ForwardFailureMode.Continue"/> 请求而没成时是 <see langword="null"/>。</summary>
    public X11Forwarder? X11 { get; }

    /// <summary>
    /// 按 <see cref="ForwardFailureMode.Continue"/> 请求的 X11（<see cref="X11ForwardOptions.FailureMode"/>）没成时的原因；
    /// 其余情况（成了、没请求、或者请求是 <see cref="ForwardFailureMode.Fail"/> —— 那种失败直接抛）都是 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 〔<c>velashell-docs/zh/ssh/spec/07</c> §7.5.8〕连接级开关打开的 X11 失败时「记日志，照常启动」。
    /// 本库不带日志器，所以原因以结构化形式交给调用方（同时计入转发的错误计数），
    /// 由调用方决定记到哪里、要不要提示使用者。
    /// </remarks>
    public SshForwardException? X11SetupFailure { get; }

    /// <summary>这个 shell 的 agent 转发；没请求过、或按 <see cref="ForwardFailureMode.Continue"/> 请求而没成时是 <see langword="null"/>。</summary>
    public AgentForwarder? Agent { get; }

    /// <summary>
    /// 按 <see cref="ForwardFailureMode.Continue"/> 请求的 agent 转发（<see cref="AgentForwardOptions.FailureMode"/>）
    /// 没成时的原因 —— 本机 agent 连不上，或者服务端拒绝；其余情况都是 <see langword="null"/>。
    /// </summary>
    /// <remarks>语义与 <see cref="X11SetupFailure"/> 相同。</remarks>
    public SshForwardException? AgentSetupFailure { get; }

    /// <summary>终端输出（<b>stdout 与 stderr 已经由伪终端合并</b>）。</summary>
    public PipeReader StandardOutput => Channel.StandardOutput;

    /// <summary>终端输入。</summary>
    public PipeWriter StandardInput => _obfuscator?.Writer ?? Channel.StandardInput;

    /// <summary>当前的终端尺寸。</summary>
    public SshTerminalSize Size { get; private set; }

    /// <summary>读下一件事（退出状态、关闭…）。</summary>
    public ValueTask<SshChannelEvent> ReadEventAsync(CancellationToken cancellationToken = default) =>
        Channel.ReadEventAsync(cancellationToken);

    /// <summary>终端尺寸变了。</summary>
    /// <remarks>
    /// 像素尺寸照样发过去 —— sixel、kitty 图形协议这类东西要靠它排版
    /// （velashell-docs/zh/ssh/spec/05 §5.3）。不知道就给 0，那也是一个有意义的回答。
    /// </remarks>
    public async ValueTask ResizeAsync(SshTerminalSize size, CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUInt32((uint)size.Columns);
        writer.WriteUInt32((uint)size.Rows);
        writer.WriteUInt32((uint)size.PixelWidth);
        writer.WriteUInt32((uint)size.PixelHeight);

        // RFC 4254 §6.7 明确要求 want_reply 为假。
        await Channel.SendRequestAsync(
            SshProtocolNames.RequestWindowChange, buffer.WrittenMemory, wantReply: false, cancellationToken)
            .ConfigureAwait(false);

        Size = size;
    }

    /// <summary>给远端进程发信号。</summary>
    /// <param name="signalName">信号名，<b>不带 <c>SIG</c> 前缀</b>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public ValueTask SendSignalAsync(string signalName, CancellationToken cancellationToken = default) =>
        Channel.SendSignalAsync(signalName, cancellationToken);

    /// <summary>发 BREAK（RFC 4335）：经 SSH 访问串口控制台服务器、网络设备的 console 时，靠它进 ROMMON / 引导菜单。</summary>
    /// <param name="length">
    /// BREAK 的长度；<see langword="null"/>（默认）发 0，让服务端用设备的默认长度。
    /// RFC 4335 §3 建议服务端把它限在 500 ms–3 s 之间。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端有没有执行（执行了任何一种 BREAK 都回 SUCCESS，没执行回 FAILURE —— RFC 4335 §3 要求必须回）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">长度为负，或超出 <c>uint32</c> 毫秒。</exception>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/05 §5.2〕要应答：用户按了「发送 Break」，界面要能说一句服务端没执行（不支持、或者不是终端会话）。
    /// </remarks>
    public ValueTask<bool> SendBreakAsync(TimeSpan? length = null, CancellationToken cancellationToken = default)
    {
        double milliseconds = length?.TotalMilliseconds ?? 0;
        if (milliseconds is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "BREAK 的长度要在 0 到 2^32-1 毫秒之间。");
        }

        byte[] payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)milliseconds);
        return Channel.SendRequestAsync(SshProtocolNames.RequestBreak, payload, wantReply: true, cancellationToken);
    }

    /// <summary>告诉远端输入到此为止。</summary>
    public ValueTask CompleteStandardInputAsync(CancellationToken cancellationToken = default) =>
        _obfuscator is { } obfuscator ? obfuscator.CompleteAsync(cancellationToken) : Channel.SendEofAsync(cancellationToken);

    /// <summary>等 shell 结束。</summary>
    public ValueTask<SshExitStatus> WaitAsync(CancellationToken cancellationToken = default) =>
        Channel.WaitForExitAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// 先摘转发的处理器再关通道：反过来的话，关通道那一刻服务端
    /// 可能还在往回开 x11 / agent 通道，而处理器已经没了。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_obfuscator is not null)
        {
            await _obfuscator.DisposeAsync().ConfigureAwait(false);
        }

        if (X11 is not null)
        {
            await X11.DisposeAsync().ConfigureAwait(false);
        }

        if (Agent is not null)
        {
            await Agent.DisposeAsync().ConfigureAwait(false);
        }

        await Channel.DisposeAsync().ConfigureAwait(false);
    }
}
