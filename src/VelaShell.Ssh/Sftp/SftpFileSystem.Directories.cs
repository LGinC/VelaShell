// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   draft-ietf-secsh-filexfer-02 §6、§7
//   OpenSSH PROTOCOL              SFTP 扩展章节
//   行为规格:                     velashell-docs/zh/ssh/spec/06-sftp.md 全部

using System.Runtime.CompilerServices;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Ssh.Sftp;

public sealed partial class SftpFileSystem
{
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

        Task<IReadOnlyList<SftpNameEntry>?> next = ReadDirectoryBatchAsync(handle, path, cancellationToken).AsTask();
        try
        {
            int emptyBatches = 0;
            while (true)
            {
                IReadOnlyList<SftpNameEntry>? batch = await next.ConfigureAwait(false);

                if (batch is null)
                {
                    yield break;   // STATUS = EOF：目录读完了
                }

                // 〔velashell-docs/zh/ssh/spec/06 §4.4〕预取：这一批到了就把下一个 READDIR 发出去 ——
                // 补链接、交给调用方的同时它已经在路上。曾经要等这一批处理完才发，大目录每批都白等一轮往返。
                next = ReadDirectoryBatchAsync(handle, path, cancellationToken).AsTask();

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
            // 调用方中途不要了（break、取消）：预取的那一个可能还在路上 —— 看一眼它的结局，不留未观察的异常。
            if (!next.IsCompleted)
            {
                _ = next.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else if (next.IsFaulted)
            {
                _ = next.Exception;
            }

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
            if (_options.IsFilteringDotEntries && entry.Name is "." or "..")
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
}
