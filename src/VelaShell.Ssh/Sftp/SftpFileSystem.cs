// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6、§7
//   OpenSSH PROTOCOL              SFTP 扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md 全部

using System.Buffers;
using System.Runtime.CompilerServices;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Sftp;

/// <summary>建立 SFTP 时的参数。</summary>
public sealed record SftpOptions
{
    /// <summary>底层通道的参数。</summary>
    public SshChannelOptions Channel { get; init; } = SshChannelOptions.Default with
    {
        // SFTP 是吞吐型负载，窗口给大一些；stderr 上只会来服务端的诊断噪音。
        WindowPolicy = SshWindowPolicy.Adaptive(2 * 1024 * 1024, 64 * 1024 * 1024),
        StderrMode = SshStderrMode.Discard,
    };

    /// <summary>在途请求数上限。</summary>
    /// <remarks>
    /// 在途请求数 × 块大小就是 SFTP 层的「窗口」。
    /// 和通道窗口一样，太小会在高 RTT 链路上直接封死吞吐。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">小于 1。</exception>
    public int MaxInFlight
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxInFlight), value, "在途请求数至少为 1。");
    } = 64;

    /// <summary>
    /// 块大小；<c>0</c> 表示按服务端宣告的 <c>limits@openssh.com</c> 定。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">为负。</exception>
    public int BlockSize
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(BlockSize), value, "块大小不能为负（0 表示按服务端宣告的定）。");
    }

    /// <summary>在途请求数是否按「深度有没有成为瓶颈」自动伸缩。</summary>
    /// <remarks>
    /// 关掉它可以得到确定性的内存占用（在途数 × 块大小），
    /// 代价是高 RTT 链路上吞吐被 <c>深度 × 块大小 / RTT</c> 封死。
    /// </remarks>
    public bool AdaptivePipelineDepth { get; init; } = true;

    /// <summary>自适应时在途请求数的上限；不能小于 <see cref="MaxInFlight"/>（连接时核对）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">小于 1。</exception>
    public int MaxPipelineDepth
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxPipelineDepth), value, "在途请求数的上限至少为 1。");
    } = 256;

    /// <summary>列目录时过滤掉 <c>.</c> 与 <c>..</c>。</summary>
    /// <remarks>它们**会**出现在服务端返回的结果里。</remarks>
    public bool FilterDotEntries { get; init; } = true;

    /// <summary>服务端的文件名用什么编码；<see langword="null"/>（默认）为 UTF-8。</summary>
    /// <remarks>
    /// <para>
    /// SFTP v3 没规定文件名编码。默认按 UTF-8，<b>解不开的字节无损往返</b>：列出来的名字拿回去开、删、改名，
    /// 到服务端的还是原来那串字节（界面上显示成替换字符）—— 见 velashell-docs/zh/ssh/spec/06 §4.7。
    /// </para>
    /// <para>
    /// 服务端用 GBK、Shift-JIS 之类的老编码时，在这里给那个编码（宿主通常复用这个会话的终端编码），名字才显示得对。
    /// 那种编码下非法的字节不保证往返。
    /// </para>
    /// </remarks>
    public System.Text.Encoding? FileNameEncoding { get; init; }

    /// <summary>
    /// 握手（<c>INIT</c> → <c>VERSION</c>、查 limits、取工作目录）最多等多久；到点以 <see cref="SshFailureReason.Timeout"/> 失败。
    /// </summary>
    /// <remarks>
    /// sftp-server 是经登录 shell 起的：启动文件卡住（等输入、挂在一个网络盘上）时它永远不会回 VERSION。
    /// 曾经只靠调用方的令牌，没给就一直挂着。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public TimeSpan HandshakeTimeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout), value, "握手时限必须为正。");
    } = TimeSpan.FromSeconds(30);

    /// <summary>关闭一个文件流时，等在途写入确认与 <c>CLOSE</c> 应答各最多等多久。</summary>
    /// <remarks>
    /// 服务端不再应答（卡死、链路半断）时，释放曾经一直等下去 —— 关一个标签页、取消一次上传都会挂住。
    /// 到点了照样发 <c>CLOSE</c>、还句柄额度，写入没确认完的话报带续传点的中断。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">不为正。</exception>
    public TimeSpan CloseTimeout
    {
        get;
        init => field = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(CloseTimeout), value, "关闭的时限必须为正。");
    } = TimeSpan.FromSeconds(30);

    /// <summary>默认参数。</summary>
    public static SftpOptions Default { get; } = new();

    /// <summary>跨字段的核对：在开通道<b>之前</b>做，不自洽就抛，不留下一条开了没人关的通道。</summary>
    /// <exception cref="ArgumentException">参数不自洽。</exception>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Channel, nameof(Channel));
        if (MaxPipelineDepth < MaxInFlight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxPipelineDepth), MaxPipelineDepth, $"在途请求数的上限不能小于起始值 MaxInFlight（{MaxInFlight}）。");
        }

        // 接收窗口至少装得下一整个 SFTP 报文（长度前缀 + 最长的报文）：报文收齐之前收包循环一个字节都不消费，
        // 而窗口只随消费回补 —— 窗口比报文小，就是对端等窗口、我们等报文，谁也动不了，也没有任何报错。
        // 通道的默认窗口（256 KiB）恰好装不下一块 256 KiB 的 DATA 应答。
        int frame = 4 + SftpProtocol.MaxMessageLength;
        if (Channel.WindowPolicy.MinimumBytes < frame)
        {
            throw new ArgumentException(
                $"SFTP 通道的接收窗口至少要 {frame} 字节（一整个 SFTP 报文），现在是 {Channel.WindowPolicy.MinimumBytes} 字节。",
                nameof(Channel));
        }
    }
}

/// <summary>面向使用者的 SFTP 客户端。</summary>
public sealed class SftpFileSystem : IAsyncDisposable
{
    private readonly SshChannel _channel;
    private readonly SftpRequestPipeline _pipeline;
    private readonly SftpOptions _options;

    /// <summary>文件名与路径怎么编解码（见 <see cref="SftpOptions.FileNameEncoding"/>）。</summary>
    private readonly SftpNameCodec _names;

    // 列目录可以在几个枚举里并发跑 —— 走 Interlocked。
    private int _malformedEntriesSkipped;

    /// <summary>
    /// 服务端宣告了 <c>max-open-handles</c> 时的句柄额度：每个开着的文件、目录占一个，关了还回来。
    /// 没宣告时为 <see langword="null"/>（不限）。
    /// </summary>
    private SemaphoreSlim? _handleSlots;
    private bool _disposed;

    private SftpFileSystem(
        SshChannel channel, SftpRequestPipeline pipeline, SftpOptions options, SftpCapabilities capabilities)
    {
        _channel = channel;
        _pipeline = pipeline;
        _options = options;
        _names = SftpNameCodec.For(options.FileNameEncoding);
        Capabilities = capabilities;
        WorkingDirectory = ".";
    }

    /// <summary>这台服务端支持什么。</summary>
    public SftpCapabilities Capabilities { get; }

    /// <summary>连上时的工作目录（通常就是家目录）。</summary>
    public string WorkingDirectory { get; private set; }

    /// <summary>实际使用的块大小。</summary>
    public int BlockSize { get; private set; }

    /// <summary>这条 SFTP 会话还能用吗。</summary>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/06 §5.4〕为假时之后的每个请求都会失败，<b>这个对象不会自己恢复</b> —— 丢掉它、
    /// 在同一条连接上再 <see cref="ConnectAsync"/> 一个。为假的原因：sftp-server 退出了、服务端按 <c>ChannelTimeout</c>
    /// 关掉了闲置的通道、收到了畸形帧、整条连接断了，或者已经释放。原因在 <see cref="Closed"/> 里。
    /// </para>
    /// <para>
    /// 曾经没有这个信号：宿主只能看「对象还在不在」，通道死了之后这个会话的文件面板一直坏着，直到整条 SSH 连接重连。
    /// </para>
    /// </remarks>
    public bool IsConnected => !_pipeline.IsFaulted;

    /// <summary>这条 SFTP 会话结束时完成，结果是结束的原因（释放时是 <see cref="SftpUnavailableException"/>）。</summary>
    /// <remarks>
    /// 以<b>成功</b>完成、结果是原因，而不是以异常完成：没人等它时也不会变成未观察的任务异常。
    /// </remarks>
    public Task<Exception> Closed => _pipeline.Closed;

    /// <summary>在一条会话上起 SFTP。</summary>
    /// <exception cref="SftpUnavailableException">服务端没有 sftp 子系统，或版本太低。</exception>
    /// <exception cref="SshChannelException">
    /// session 通道都没开成（<see cref="SshFailureReason.ChannelOpenFailed"/>：服务端 <c>MaxSessions</c> 满了、
    /// 管理上禁止，或者本端的通道数 / 窗口预算用尽）—— 原样抛出，可以稍后重试。
    /// </exception>
    public static async ValueTask<SftpFileSystem> ConnectAsync(
        SshConnection connection,
        SftpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SftpOptions effective = options ?? SftpOptions.Default;

        // 〔velashell-docs/zh/ssh/spec/06 §一〕参数先核对、再开通道：曾经是通道开了才在建流水线时抛，
        // 那条 sftp 通道就一直挂在连接上，没人关。
        effective.Validate();

        SshChannel channel;
        try
        {
            channel = await connection
                .OpenSubsystemAsync(SshProtocolNames.SubsystemSftp, effective.Channel, cancellationToken).ConfigureAwait(false);
        }
        catch (SshChannelException ex) when (ex.Reason == SshFailureReason.ChannelRequestRejected)
        {
            // 只有「通道开了、subsystem 请求被拒」才是没有 sftp 子系统。通道都没开成（服务端 MaxSessions 满、
            // 管理上禁止、本端通道数或窗口预算用尽）原样抛出：曾经一律改写成「sshd_config 缺 Subsystem」，
            // 用户去改一个本来没问题的配置，也丢了「稍后可以重试」这个信息。
            //
            // 〔决策 velashell-docs/zh/ssh/spec/06 §一〕**不自动回退到 `exec sftp-server`。**
            // 回退等于在管理员明确禁用 subsystem 的情况下绕过他的配置。
            throw new SftpUnavailableException(
                "服务端没有提供 sftp 子系统。常见原因是 sshd_config 里缺少或注释掉了 " +
                "`Subsystem sftp ...` 那一行。（本库不会自动改用 `exec sftp-server` 绕开它 —— " +
                "那等于绕过管理员的配置。）", ex);
        }

        SftpRequestPipeline pipeline;
        try
        {
            pipeline = new(channel, effective.MaxInFlight, effective.AdaptivePipelineDepth, effective.MaxPipelineDepth);
        }
        catch (Exception)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        pipeline.Start();

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effective.HandshakeTimeout);
        try
        {
            SftpCapabilities capabilities = await HandshakeAsync(pipeline, deadline.Token).ConfigureAwait(false);
            SftpFileSystem fileSystem = new(channel, pipeline, effective, capabilities);

            await fileSystem.InitializeAsync(deadline.Token).ConfigureAwait(false);
            return fileSystem;
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new SftpUnavailableException(
                SshFailureReason.Timeout,
                $"SFTP 握手在 {effective.HandshakeTimeout.TotalSeconds:0} 秒内没有完成：sftp-server 没有回应。" +
                "常见原因是服务端登录 shell 的启动文件卡住了（等输入、挂在一个连不上的网络盘上）。", ex);
        }
        catch (Exception)
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<SftpCapabilities> HandshakeAsync(
        SftpRequestPipeline pipeline, CancellationToken cancellationToken)
    {
        Task<SftpResponse> versionTask = pipeline.WaitForVersionAsync();

        ArrayBufferWriter<byte> init = new();
        SftpWire.WriteInit(init);
        await pipeline.SendRawAsync(init.WrittenMemory, cancellationToken).ConfigureAwait(false);

        using SftpResponse response = await versionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        (uint version, IReadOnlyDictionary<string, byte[]> extensions) = SftpWire.ReadVersion(response.Payload);

        if (version < SftpProtocol.Version)
        {
            // v0–v2 与 v3 的差异过大（没有 ATTRS 的部分字段、没有 handle 语义保证）。
            throw new SftpUnavailableException(
                $"服务端只支持 SFTP v{version}，本库要求至少 v{SftpProtocol.Version}。");
        }

        // 版本更高就降到 3 —— 我们按 v3 工作，这是 OpenSSH 的实际口径。
        return new SftpCapabilities(version, extensions);
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        // 服务端宣告了上限就按它来，而不是写死。
        if (Capabilities.HasLimits)
        {
            try
            {
                Capabilities.Limits = await QueryLimitsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshException)
            {
                // 宣告了却查不出来 —— 用保守默认继续，不因此连不上。
            }
        }

        BlockSize = ChooseBlockSize(_options.BlockSize, Capabilities.Limits);

        // 〔velashell-docs/zh/ssh/spec/06 §7.1〕宣告了同时能开几个句柄，就按它排队：曾经读了不用，
        // 并发传输撞上服务端的句柄上限时只会得到一个随机的「操作失败」。
        if (Capabilities.Limits.MaxOpenHandles > 0)
        {
            int slots = (int)Math.Min(Capabilities.Limits.MaxOpenHandles, int.MaxValue);
            _handleSlots = new SemaphoreSlim(slots, slots);
        }

        // 〔决策 velashell-docs/zh/ssh/spec/06 §4.6〕连上就对 "." 做一次 REALPATH。
        // 这是唯一可靠的「用户家目录在哪」的答案 —— 比拼 /home/{user} 靠谱得多。
        try
        {
            WorkingDirectory = await GetRealPathAsync(".", cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            // 少数服务端会拒绝对 "." 做 REALPATH。那不该让整个连接失败。
            WorkingDirectory = ".";
        }
    }

    /// <summary>
    /// 块大小 = min(想要的, 服务端的读写上限, 服务端的报文上限减去请求头, 本端肯收的最大数据块)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>服务端给 0 是「没说」，不是「一个字节」。</b>曾经按字面取，块大小就成了 1 ——
    /// 一个 1 MB 的文件要一百万个请求。
    /// </para>
    /// <para>
    /// 读上限也要算进来：比它长的 <c>READ</c> 会被截短，而顺序读把短读当成「中间有洞」，
    /// 每一块都从头再来，预读永远建不起来。使用者指定的块大小同样要服从这些上限 ——
    /// 超长的 <c>WRITE</c> 会被拒，OpenSSH 甚至直接断开 SFTP 会话。
    /// </para>
    /// </remarks>
    internal static int ChooseBlockSize(int requested, SftpLimits limits)
    {
        long serverCap = long.MaxValue;
        if (limits.MaxWriteLength > 0)
        {
            serverCap = Math.Min(serverCap, (long)Math.Min(limits.MaxWriteLength, int.MaxValue));
        }
        if (limits.MaxReadLength > 0)
        {
            serverCap = Math.Min(serverCap, (long)Math.Min(limits.MaxReadLength, int.MaxValue));
        }
        if (limits.MaxPacketLength > SftpProtocol.RequestHeaderAllowance)
        {
            serverCap = Math.Min(
                serverCap, (long)Math.Min(limits.MaxPacketLength, int.MaxValue) - SftpProtocol.RequestHeaderAllowance);
        }

        long size = requested > 0
            ? requested
            : serverCap == long.MaxValue ? SftpProtocol.DefaultBlockSize : serverCap;

        return (int)Math.Clamp(Math.Min(size, serverCap), 1, SftpProtocol.MaxBlockSize);
    }

    private async ValueTask<SftpLimits> QueryLimitsAsync(CancellationToken cancellationToken)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteExtended(output, id, SftpExtensionNames.Limits, []),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path: null, SftpOperation.QueryLimits, SftpMessageType.ExtendedReply);
        return SftpWire.ReadLimits(response.Payload);
    }

    // ------------------------------------------------------------ 路径

    /// <summary>把路径规范化成绝对路径。</summary>
    public async ValueTask<string> GetRealPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.RealPath, id, path, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.RealPath, SftpMessageType.Name);

        IReadOnlyList<SftpNameEntry> entries = SftpWire.ReadName(response.Payload, _names);
        if (entries.Count != 1)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"REALPATH 应当返回恰好 1 项，实际返回了 {entries.Count} 项。");
        }

        return entries[0].Name;
    }

    // ------------------------------------------------------------ 属性

    /// <summary>取属性，<b>跟随</b>符号链接。</summary>
    public ValueTask<SftpFileAttributes> GetAttributesAsync(
        string path, CancellationToken cancellationToken = default) =>
        StatAsync(path, SftpMessageType.Stat, cancellationToken);

    /// <summary>取属性，<b>不跟随</b>符号链接 —— 描述链接本身。</summary>
    public ValueTask<SftpFileAttributes> GetLinkAttributesAsync(
        string path, CancellationToken cancellationToken = default) =>
        StatAsync(path, SftpMessageType.LStat, cancellationToken);

    private async ValueTask<SftpFileAttributes> StatAsync(
        string path, SftpMessageType type, CancellationToken cancellationToken)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, type, id, path, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.GetAttributes, SftpMessageType.Attrs);
        return SftpWire.ReadAttrs(response.Payload);
    }

    /// <summary>文件或目录存在吗。</summary>
    public async ValueTask<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await GetLinkAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SftpException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    /// <summary>设属性。</summary>
    public async ValueTask SetAttributesAsync(
        string path, SftpFileAttributes attributes, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteSetStat(output, id, path, attributes, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.SetAttributes, SftpMessageType.Status);
    }

    /// <summary>改权限。</summary>
    public ValueTask SetPermissionsAsync(
        string path, uint permissions, CancellationToken cancellationToken = default) =>
        SetAttributesAsync(path, SftpFileAttributes.WithPermissions(permissions), cancellationToken);

    /// <summary>改最后修改时间。</summary>
    /// <remarks>
    /// <c>atime</c> 与 <c>mtime</c> <b>共用一个标志位</b>，只给一个会把另一个抹成 1970 年。
    /// 所以这里<b>先把当前的 atime 取回来</b>再一并写回。
    /// </remarks>
    public async ValueTask SetLastWriteTimeAsync(
        string path, DateTimeOffset modifyTime, CancellationToken cancellationToken = default)
    {
        SftpFileAttributes current = await GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);

        DateTimeOffset accessTime = current.HasTimes
            ? current.LastAccessTime
            : modifyTime;

        await SetAttributesAsync(
            path, SftpFileAttributes.WithTimes(accessTime, modifyTime), cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 目录

    /// <summary>建目录。</summary>
    public async ValueTask CreateDirectoryAsync(
        string path,
        uint permissions = SftpProtocol.DefaultDirectoryPermissions,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteMkDir(output, id, path, SftpFileAttributes.WithPermissions(permissions), _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.CreateDirectory, SftpMessageType.Status);
    }

    /// <summary>删空目录。</summary>
    public async ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.RmDir, id, path, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.RemoveDirectory, SftpMessageType.Status);
    }

    /// <summary>删文件。</summary>
    public async ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.Remove, id, path, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.Remove, SftpMessageType.Status);
    }

    /// <summary>列目录。</summary>
    /// <remarks>
    /// <para>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §八〕<b>用 <c>LSTAT</c> 的口径列，链接项再补一次跟随的
    /// <c>STAT</c> 与 <c>READLINK</c>。</b>
    /// </para>
    /// <para>
    /// 直接用跟随的 <c>STAT</c> 会让「这是个链接」这个事实彻底消失 ——
    /// 于是删除一个指向目录的链接，会变成递归删除目标目录里的东西。那是数据事故。
    /// </para>
    /// <para>
    /// 链接项的补充请求是<b>并发</b>发出的：<c>/usr/lib</c> 那种几百个 <c>.so</c> 链接的目录，
    /// 串行补就是几百轮往返，并发补只是一轮。
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<SftpDirectoryEntry> EnumerateDirectoryAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        await AcquireHandleSlotAsync(cancellationToken).ConfigureAwait(false);
        byte[] handle;
        try
        {
            handle = await OpenDirectoryHandleAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ReleaseHandleSlot();
            throw;
        }

        try
        {
            int emptyBatches = 0;
            while (true)
            {
                IReadOnlyList<SftpNameEntry>? batch =
                    await ReadDirectoryBatchAsync(handle, path, cancellationToken).ConfigureAwait(false);

                if (batch is null)
                {
                    yield break;   // STATUS = EOF：目录读完了
                }

                // 〔velashell-docs/zh/ssh/spec/06 §4.4〕READDIR 要么给至少一项、要么回 EOF。一直给空批的服务端会让这里
                // 永远转下去（曾经就是这样，直到调用方取消）—— 连着空了这么多批就判协议错误。
                if (batch.Count == 0)
                {
                    if (++emptyBatches >= MaxConsecutiveEmptyBatches)
                    {
                        throw new SshProtocolException(
                            SshPhase.Open,
                            $"列目录 {PeerText.Sanitize(path)} 时服务端连着 {emptyBatches} 次回了空的一批，既没有项也没有 EOF。");
                    }
                    continue;
                }
                emptyBatches = 0;

                SftpDirectoryEntry[] resolved =
                    await ResolveBatchAsync(path, batch, cancellationToken).ConfigureAwait(false);

                foreach (SftpDirectoryEntry entry in resolved)
                {
                    yield return entry;
                }
            }
        }
        finally
        {
            await CloseHandleQuietlyAsync(handle).ConfigureAwait(false);
            ReleaseHandleSlot();
        }
    }

    /// <summary>READDIR 连着回多少次空批就不再等（见 <see cref="EnumerateDirectoryAsync"/>）。</summary>
    internal const int MaxConsecutiveEmptyBatches = 16;

    private async ValueTask<byte[]> OpenDirectoryHandleAsync(string path, CancellationToken cancellationToken)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.OpenDir, id, path, _names),
            onLateResponse: CloseLateHandle,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.OpenDirectory, SftpMessageType.Handle);
        return SftpWire.ReadHandle(response.Payload);
    }

    /// <returns>这一批；<see langword="null"/> 表示目录读完了。</returns>
    private async ValueTask<IReadOnlyList<SftpNameEntry>?> ReadDirectoryBatchAsync(
        byte[] handle, string path, CancellationToken cancellationToken)
    {
        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.ReadDir, id, handle),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.TryGetStatus(out SftpStatusCode code, out string message))
        {
            // EOF 是「读完了」，不是错误。
            if (code == SftpStatusCode.EndOfFile)
            {
                return null;
            }
            throw new SftpException(code, message, path, SftpOperation.ReadDirectory);
        }

        response.ExpectType(path, SftpOperation.ReadDirectory, SftpMessageType.Name);
        return SftpWire.ReadName(response.Payload, _names);
    }

    private async ValueTask<SftpDirectoryEntry[]> ResolveBatchAsync(
        string directory, IReadOnlyList<SftpNameEntry> batch, CancellationToken cancellationToken)
    {
        List<SftpNameEntry> kept = [];
        foreach (SftpNameEntry entry in batch)
        {
            // `.` 与 `..` **会**出现在服务端的结果里。
            if (_options.FilterDotEntries && entry.Name is "." or "..")
            {
                continue;
            }

            // 〔velashell-docs/zh/ssh/spec/06 §4.4〕目录项只能是一个名字。服务端回 `../x`、`a/b` 或空名字时，
            // 拼出来的 FullPath 指向这个目录以外的地方（或者就是这个目录本身）—— 照着它递归复制、删除，
            // 动的就是别处的东西。这种项一律丢掉，不交给调用方。
            if (!IsPlainName(entry.Name))
            {
                Interlocked.Increment(ref _malformedEntriesSkipped);
                continue;
            }
            kept.Add(entry);
        }

        // 链接项的补充请求并发发出 —— 它们在同一条通道上流水线，
        // 串行补的话几百个链接就是几百轮往返。
        Task<SftpDirectoryEntry>[] tasks =
        [
            .. kept.Select(entry => ResolveEntryAsync(directory, entry, cancellationToken).AsTask()),
        ];

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async ValueTask<SftpDirectoryEntry> ResolveEntryAsync(
        string directory, SftpNameEntry entry, CancellationToken cancellationToken)
    {
        string fullPath = CombinePath(directory, entry.Name);

        // 〔velashell-docs/zh/ssh/spec/06 §4.4〕READDIR 没给权限位，就分不出是目录、链接还是文件 —— 曾经一律当成文件，
        // 宿主进不了这样的目录。补一次不跟随链接的 stat（悄悄版本：还是拿不到就照旧）。
        if (!entry.Attributes.HasPermissions
            && await LinkStatQuietlyAsync(fullPath, cancellationToken).ConfigureAwait(false) is { HasPermissions: true } better)
        {
            entry = entry with { Attributes = better };
        }

        return await CompleteEntryAsync(entry.Name, fullPath, entry.Attributes, entry.LongName, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 由不跟随链接的属性补成完整的条目：不是链接就是它自己；是链接就并发补上目标与跟随后的属性。
    /// </summary>
    private async ValueTask<SftpDirectoryEntry> CompleteEntryAsync(
        string name, string fullPath, SftpFileAttributes linkAttributes, string longName, CancellationToken cancellationToken)
    {
        if (!linkAttributes.IsSymbolicLink)
        {
            return new SftpDirectoryEntry(
                name, fullPath, linkAttributes,
                IsSymbolicLink: false, LinkTarget: null, IsBrokenLink: false, longName);
        }

        // 链接：目标与跟随后的属性一起要。并发发出 —— 它们在同一条通道上流水线，串行就是多两轮往返。
        Task<string?> targetTask = ReadLinkQuietlyAsync(fullPath, cancellationToken);
        Task<SftpFileAttributes?> followedTask = StatQuietlyAsync(fullPath, cancellationToken);

        await Task.WhenAll(targetTask, followedTask).ConfigureAwait(false);

        string? target = await targetTask.ConfigureAwait(false);
        SftpFileAttributes? followed = await followedTask.ConfigureAwait(false);

        // 断链：**保留链接自身的属性**，IsDirectory 为 false。
        // 返回 null 是不对的 —— 链接本身是存在的，删除它不能先报「找不到」。
        return new SftpDirectoryEntry(
            name,
            fullPath,
            followed ?? linkAttributes,
            IsSymbolicLink: true,
            LinkTarget: target,
            IsBrokenLink: followed is null,
            longName);
    }

    /// <summary>取一个路径的完整条目（与列目录给出的一样：链接保留「是链接」这个事实，并补上目标与跟随后的属性）。</summary>
    /// <returns>路径不存在时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 〔velashell-docs/zh/ssh/spec/06 §八〕先不跟随地 stat；是链接才并发补 READLINK 与跟随的 stat —— 普通文件一轮往返，链接两轮。
    /// 曾经没有这个方法，宿主自己复制了一份：串行三轮往返、只吞 SFTP 异常，还用本机的 <c>Path.GetFileName</c> 取名字 ——
    /// Windows 上把远端名字里合法的 <c>\</c> 当成了分隔符。
    /// </para>
    /// <para><see cref="SftpDirectoryEntry.Name"/> 是路径最后一个 <c>/</c> 之后的那一段（SFTP 的分隔符永远是 <c>/</c>）。</para>
    /// </remarks>
    public async ValueTask<SftpDirectoryEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        SftpFileAttributes linkAttributes;
        try
        {
            linkAttributes = await GetLinkAttributesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException ex) when (ex.IsNotFound)
        {
            return null;
        }

        return await CompleteEntryAsync(NameOf(path), path, linkAttributes, longName: "", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>路径的最后一段（按 <c>/</c> 分；末尾的 <c>/</c> 不算；根就是 <c>/</c>）。</summary>
    internal static string NameOf(string path)
    {
        string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        if (trimmed.Length == 0)
        {
            return "/";
        }

        int slash = trimmed.LastIndexOf('/');
        return slash < 0 || trimmed.Length == 1 ? trimmed : trimmed[(slash + 1)..];
    }

    // 下面两个「悄悄」版本吞的是<b>这一条应答</b>的问题：服务端拒了（SftpException），
    // 或者这一条应答长得不对（SshProtocolException，比如 READLINK 回了两项）——
    // 一个怪链接不该让整个目录列不出来。流水线本身坏了就不吞：那不是这一项的事。

    private async Task<string?> ReadLinkQuietlyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadSymbolicLinkAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            return null;
        }
        catch (SshProtocolException) when (!_pipeline.IsFaulted)
        {
            return null;
        }
    }

    private async Task<SftpFileAttributes?> LinkStatQuietlyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await GetLinkAttributesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            return null;
        }
        catch (SshProtocolException) when (!_pipeline.IsFaulted)
        {
            return null;
        }
    }

    private async Task<SftpFileAttributes?> StatQuietlyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (SftpException)
        {
            return null;
        }
        catch (SshProtocolException) when (!_pipeline.IsFaulted)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ 文件

    /// <summary>打开一个文件用于读。</summary>
    public ValueTask<SftpFileStream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
        OpenAsync(path, SftpOpenModes.Read, cancellationToken: cancellationToken);

    /// <summary>打开一个文件用于写（不存在则创建，存在则截断）。</summary>
    public ValueTask<SftpFileStream> OpenWriteAsync(
        string path,
        uint permissions = SftpProtocol.DefaultFilePermissions,
        SftpWriteMode writeMode = SftpWriteMode.Pipelined,
        CancellationToken cancellationToken = default) =>
        OpenAsync(path,
            SftpOpenModes.Write | SftpOpenModes.Create | SftpOpenModes.Truncate,
            SftpFileAttributes.WithPermissions(permissions),
            writeMode, cancellationToken);

    /// <summary>打开一个文件用于续写（从给定偏移继续）。</summary>
    /// <remarks>
    /// <para>
    /// 配合 <see cref="SftpFileStream.DurableLength"/> 或
    /// <see cref="SftpTransferInterruptedException.DurableLength"/> 用，
    /// 就是精确的断点续传。
    /// </para>
    /// <para>
    /// 返回的流把 <c>[0, offset)</c> 算作已确认（服务端的文件比 <paramref name="offset"/> 短时只算到文件末尾）——
    /// 那一段是上一次传输确认过的。不这样算的话，续传途中再断一次，
    /// <see cref="SftpFileStream.DurableLength"/> 报的是 0，下一次续传就从头来过。
    /// </para>
    /// </remarks>
    public async ValueTask<SftpFileStream> OpenAppendAsync(
        string path,
        long offset,
        uint permissions = SftpProtocol.DefaultFilePermissions,
        CancellationToken cancellationToken = default)
    {
        // 先查参数再开：开了之后才发现偏移不对，那个句柄就没人关了。
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        SftpFileStream stream = await OpenAsync(
            path,
            SftpOpenModes.Write | SftpOpenModes.Create,
            SftpFileAttributes.WithPermissions(permissions),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        stream.Position = offset;
        stream.AssumeDurablePrefix(stream.LengthKnown ? Math.Min(offset, stream.Length) : offset);
        return stream;
    }

    /// <summary>用任意方式打开文件。</summary>
    /// <param name="path">路径。</param>
    /// <param name="flags">打开方式。流能不能读、能不能写就由它决定（<see cref="SftpOpenModes.Read"/> / <see cref="SftpOpenModes.Write"/>）。</param>
    /// <param name="attributes">
    /// 创建文件时的属性；带 <see cref="SftpOpenModes.Create"/> 而没给权限时补上 <c>0644</c>
    /// （<see cref="SftpProtocol.DefaultFilePermissions"/>）。
    /// </param>
    /// <param name="writeMode">写入方式。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentException">
    /// 打开方式自相矛盾：既不读也不写；<see cref="SftpOpenModes.Truncate"/> / <see cref="SftpOpenModes.Exclusive"/>
    /// 没有配 <see cref="SftpOpenModes.Create"/>（draft-02 §6.3 要求两者一起用）。
    /// </exception>
    public async ValueTask<SftpFileStream> OpenAsync(
        string path,
        SftpOpenModes flags,
        SftpFileAttributes attributes = default,
        SftpWriteMode writeMode = SftpWriteMode.Pipelined,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        bool canRead = (flags & SftpOpenModes.Read) != 0;
        bool canWrite = (flags & (SftpOpenModes.Write | SftpOpenModes.Append)) != 0;

        // 〔velashell-docs/zh/ssh/spec/06 §4.1〕不需要服务端就能查的，在发 OPEN 之前查。
        if (!canRead && !canWrite)
        {
            throw new ArgumentException("打开方式里既没有读也没有写。", nameof(flags));
        }
        if ((flags & (SftpOpenModes.Truncate | SftpOpenModes.Exclusive)) != 0 && (flags & SftpOpenModes.Create) == 0)
        {
            throw new ArgumentException(
                "TRUNC / EXCL 必须与 CREAT 一起用（draft-02 §6.3）。只想截短一个已有的文件：以写方式打开，再 SetLengthAsync(0)。",
                nameof(flags));
        }

        // 创建时传明确的权限：不传的话服务端用它自己的默认值（受 umask 影响），结果不可预测。
        // 曾经只有 OpenWriteAsync / OpenAppendAsync 传，宿主的 Create / CreateNew / OpenOrCreate 走的这条都没传。
        if ((flags & SftpOpenModes.Create) != 0 && !attributes.HasPermissions)
        {
            attributes = attributes with
            {
                Flags = attributes.Flags | SftpAttributeFields.Permissions,
                Permissions = SftpProtocol.DefaultFilePermissions,
            };
        }

        await AcquireHandleSlotAsync(cancellationToken).ConfigureAwait(false);

        byte[] handle;
        try
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) => SftpWire.WriteOpen(output, id, path, flags, attributes, _names),
                onLateResponse: CloseLateHandle,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            response.ThrowIfError(path, SftpOperation.Open, SftpMessageType.Handle);
            handle = SftpWire.ReadHandle(response.Payload);
        }
        catch (Exception)
        {
            // 没开成（或取消之后迟到的句柄由 CloseLateHandle 关）：额度现在就还。
            ReleaseHandleSlot();
            throw;
        }

        // 截断打开的，长度就是 0；别的（读、续写、不截断的写）要问一次 ——
        // 曾经只有读才问，续写打开的流 Length 一直报 0，Seek(0, End) 回到了文件开头。
        long length = 0;
        bool lengthKnown = (flags & SftpOpenModes.Truncate) != 0;
        if (!lengthKnown)
        {
            try
            {
                using SftpResponse stat = await _pipeline.SendAsync(
                    (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.FStat, id, handle),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                stat.ThrowIfError(path, SftpOperation.GetAttributes, SftpMessageType.Attrs);
                SftpFileAttributes current = SftpWire.ReadAttrs(stat.Payload);
                if (current.HasSize)
                {
                    length = (long)current.Size;
                    lengthKnown = true;
                }
            }
            catch (SftpException)
            {
                // 拿不到长度不影响读写 —— 只是 Length 与 Seek(SeekOrigin.End) 会不准。
            }
            catch (Exception)
            {
                // ⚠️ 取消、流水线断了、应答不合格式：句柄已经开在服务端了，
                //    还没交给流，这里不关就没人关 —— 每失败一次漏一个，直到 max-open-handles 用光。
                await CloseHandleQuietlyAsync(handle).ConfigureAwait(false);
                ReleaseHandleSlot();
                throw;
            }
        }

        return new SftpFileStream(
            _pipeline, handle, path, canRead, canWrite, length, BlockSize, writeMode,
            maxInFlightWrites: StreamWindow, maxReadAhead: StreamWindow)
        {
            LengthKnown = lengthKnown,
            OnHandleClosed = ReleaseHandleSlot,
            CloseTimeout = _options.CloseTimeout,
        };
    }

    /// <summary>单个流自己的在途上限。</summary>
    /// <remarks>
    /// <para>
    /// 开着自适应时取深度的<b>上限</b>，不取起始值：让管线的在途额度成为唯一的限流点。
    /// 「深度是不是瓶颈」的信号只在管线额度被用光时才记得到 ——
    /// 流自带一个与管线起始深度一样大的上限的话，它总是先卡住，管线额度永远空着，深度永远不长。
    /// </para>
    /// <para>
    /// 曾经就是这样：单文件上传、下载（最常见的用法）的窗口钉死在 64 × 块大小，
    /// 服务端没宣告 limits 时是 2 MiB，200 ms RTT 下约 10 MB/s —— 正是自适应深度想消灭的那个上限。
    /// 只有多个流并发时深度才会长。
    /// </para>
    /// </remarks>
    private int StreamWindow => _options.AdaptivePipelineDepth ? _options.MaxPipelineDepth : _options.MaxInFlight;

    /// <summary>流水线的深度被调大过几次（诊断与测试用）。</summary>
    internal int PipelineDepthIncreases => _pipeline.DepthIncreases;

    /// <summary>列目录时丢掉了几个名字不合法的项（空名字、含 <c>/</c> 或 NUL；诊断与测试用）。</summary>
    internal int MalformedEntriesSkipped => Volatile.Read(ref _malformedEntriesSkipped);

    /// <summary>单个写入流最多有多少字节「已经发出、还没被服务端确认」。</summary>
    /// <remarks>
    /// <para>
    /// 流水线写的完成顺序不保证与偏移顺序一致：断线时远端文件的长度只是「已确认的最高偏移」，
    /// 它之前最多这么多字节可能还是空洞。只凭远端长度续传的话，要从长度往回退这么多再比对。
    /// </para>
    /// <para>
    /// 开着自适应深度时它按深度的<b>上限</b>算 —— 断线那一刻深度长到了多少，事后无从得知。
    /// 能拿到断线那条流的 <see cref="SftpFileStream.DurableLength"/>（已连续确认的偏移）时用它，不必回退。
    /// </para>
    /// </remarks>
    public long MaxUnconfirmedWriteBytes => (long)StreamWindow * BlockSize;

    /// <summary>把整个文件读成字节。</summary>
    public async ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        await using SftpFileStream stream = await OpenReadAsync(path, cancellationToken).ConfigureAwait(false);

        // 初始容量按服务端报的长度估，但**封顶** —— 那是对端给的数：一个谎报 2 GiB 的小文件
        // 不该让我们先分配 2 GiB。真实数据多了，缓冲自己会长。
        const int maxInitialCapacity = 1024 * 1024;
        ArrayBufferWriter<byte> output = new((int)Math.Clamp(stream.Length, 1, maxInitialCapacity));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockSize);

        try
        {
            // 走顺序读：它带预读（spec/06 §5.5）。
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, BlockSize), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                output.Write(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.WrittenSpan.ToArray();
    }

    /// <summary>把字节写成一个文件（覆盖）。</summary>
    public async ValueTask WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> content,
        uint permissions = SftpProtocol.DefaultFilePermissions,
        CancellationToken cancellationToken = default)
    {
        await using SftpFileStream stream =
            await OpenWriteAsync(path, permissions, SftpWriteMode.Pipelined, cancellationToken).ConfigureAwait(false);

        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 移动与链接

    /// <summary>重命名或移动。</summary>
    /// <param name="sourcePath">源。</param>
    /// <param name="destinationPath">目标。</param>
    /// <param name="overwrite">
    /// 目标已存在时是否覆盖。<b>需要服务端支持 <c>posix-rename@openssh.com</c></b>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SftpException">
    /// 要求覆盖但服务端不支持原子重命名。
    /// </exception>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/06 §7.2〕<b>不静默降级。</b>
    /// <c>posix-rename</c> 原子覆盖，普通 <c>RENAME</c> 在目标存在时失败 ——
    /// 语义不同。悄悄换一个，上层就无从知道自己拿到的是哪一种。
    /// 用 <see cref="Capabilities"/> 事先问清楚。
    /// </remarks>
    public async ValueTask RenameAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(sourcePath);
        ValidatePath(destinationPath);

        if (overwrite && !Capabilities.HasPosixRename)
        {
            // 服务端没说话 —— 原话留空，本库的说明放进消息（ServerMessage 只装服务端的原话）。
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                sourcePath,
                SftpOperation.PosixRename,
                detail: "这台服务端没有 posix-rename@openssh.com，做不到原子覆盖式重命名。" +
                        "可以先删除目标再重命名，但那不是原子的 —— 中途失败会两个都没有");
        }

        if (overwrite)
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) =>
                {
                    ArrayBufferWriter<byte> inner = new();
                    SshDataWriter writer = new(inner);
                    _names.Write(ref writer, sourcePath);
                    _names.Write(ref writer, destinationPath);
                    SftpWire.WriteExtended(output, id, SftpExtensionNames.PosixRename, inner.WrittenSpan);
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            response.ThrowIfError(sourcePath, SftpOperation.PosixRename, SftpMessageType.Status);
            return;
        }

        using SftpResponse plain = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteRename(output, id, sourcePath, destinationPath, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        plain.ThrowIfError(sourcePath, SftpOperation.Rename, SftpMessageType.Status);
    }

    /// <summary>读符号链接指向哪里。</summary>
    public async ValueTask<string> ReadSymbolicLinkAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ValidatePath(path);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WritePathRequest(output, SftpMessageType.ReadLink, id, path, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(path, SftpOperation.ReadLink, SftpMessageType.Name);

        IReadOnlyList<SftpNameEntry> entries = SftpWire.ReadName(response.Payload, _names);
        if (entries.Count != 1)
        {
            throw new SshProtocolException(
                SshPhase.Open, $"READLINK 应当返回恰好 1 项，实际返回了 {entries.Count} 项。");
        }

        return entries[0].Name;
    }

    /// <summary>建符号链接。</summary>
    /// <param name="linkPath">在哪里<b>创建</b>链接。</param>
    /// <param name="targetPath">链接<b>指向</b>哪里。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 参数顺序上的坑在 <see cref="SftpWire.WriteSymLink"/> 里说明了 ——
    /// 这里按人话的顺序（先在哪建、再指向哪），发出去时按 OpenSSH 的顺序。
    /// </remarks>
    public async ValueTask CreateSymbolicLinkAsync(
        string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ValidatePath(linkPath);
        ValidatePath(targetPath);

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) => SftpWire.WriteSymLink(output, id, targetPath, linkPath, _names),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(linkPath, SftpOperation.CreateSymbolicLink, SftpMessageType.Status);
    }

    /// <summary>建硬链接（需要 <c>hardlink@openssh.com</c>）。</summary>
    public async ValueTask CreateHardLinkAsync(
        string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ValidatePath(linkPath);
        ValidatePath(targetPath);

        if (!Capabilities.HasHardLink)
        {
            throw new SftpException(
                SftpStatusCode.OperationUnsupported,
                serverMessage: "",
                linkPath,
                SftpOperation.CreateHardLink,
                detail: "这台服务端没有 hardlink@openssh.com");
        }

        using SftpResponse response = await _pipeline.SendAsync(
            (output, id) =>
            {
                ArrayBufferWriter<byte> inner = new();
                SshDataWriter writer = new(inner);
                _names.Write(ref writer, targetPath);
                _names.Write(ref writer, linkPath);
                SftpWire.WriteExtended(output, id, SftpExtensionNames.HardLink, inner.WrittenSpan);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        response.ThrowIfError(linkPath, SftpOperation.CreateHardLink, SftpMessageType.Status);
    }

    // ------------------------------------------------------------ 内部

    /// <summary>等一个句柄额度（服务端宣告了 <c>max-open-handles</c> 时）。</summary>
    private Task AcquireHandleSlotAsync(CancellationToken cancellationToken) =>
        _handleSlots?.WaitAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>还一个句柄额度。</summary>
    private void ReleaseHandleSlot() => _handleSlots?.Release();

    /// <summary>当前空着的句柄额度（测试用）；不限时为 <see langword="null"/>。</summary>
    internal int? FreeHandleSlots => _handleSlots?.CurrentCount;

    /// <summary>取消之后迟到的 <c>HANDLE</c> 应答 —— 句柄不关就泄漏在服务端。</summary>
    private void CloseLateHandle(SftpResponse response)
    {
        if (response.Type != SftpMessageType.Handle)
        {
            return;
        }

        byte[] handle;
        try
        {
            handle = SftpWire.ReadHandle(response.Payload);
        }
        catch (Exception)
        {
            return;
        }

        // 不等应答：这是善后，不是业务路径。
        _ = CloseHandleQuietlyAsync(handle).AsTask();
    }

    private async ValueTask CloseHandleQuietlyAsync(byte[] handle)
    {
        try
        {
            using SftpResponse response = await _pipeline.SendAsync(
                (output, id) => SftpWire.WriteHandleRequest(output, SftpMessageType.Close, id, handle))
                .ConfigureAwait(false);
            _ = response;
        }
        catch (Exception)
        {
            // 通道已经没了的话服务端会自己回收句柄。
        }
    }

    /// <summary>目录项的名字是不是一个单纯的名字：非空、不含 <c>/</c> 与 NUL。</summary>
    internal static bool IsPlainName(string name) =>
        name.Length > 0 && !name.Contains('/', StringComparison.Ordinal) && !name.Contains('\0', StringComparison.Ordinal);

    /// <summary>拼路径。<b>SFTP 的路径分隔符永远是 <c>/</c></b>，与本机平台无关。</summary>
    internal static string CombinePath(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory) || directory == ".")
        {
            return name;
        }
        return directory.EndsWith('/') ? directory + name : $"{directory}/{name}";
    }

    private static void ValidatePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // 含 NUL 的路径**在本地就拒掉**，不发给服务端：
        // 它在不同服务端上的行为从「截断」到「拒绝」都有，全是意外。
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("路径里不能含有 NUL 字符。", nameof(path));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _pipeline.DisposeAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
