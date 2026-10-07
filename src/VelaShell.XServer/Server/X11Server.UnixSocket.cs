// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 8 节「Connection Setup」(传输与协议无关);
//   本地传输的约定:显示号 N 的服务端监听 /tmp/.X11-unix/XN(Linux 另有抽象命名空间里的同名套接字,
//   Xlib / XCB 对 DISPLAY=:N 先试它),与 X.Org 的 Xtrans 行为一致。
//
//   本机客户端(DISPLAY=:N)走这里,比 TCP 快,也不必开端口。连进来的一律算本机连接;是不是「运行服务端的这个用户」:
//   套接字文件在 Listen 之前就改成 0600,连得上的只有属主(与 root);抽象命名空间没有文件权限,按 SO_PEERCRED 的 uid 核对。

using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    private readonly List<(Socket Socket, string? File)> _unixListeners = [];

    /// <summary>本进程的有效 uid(Linux;只有那里取得到对端的 uid,见 <see cref="PeerUidOf" />)。</summary>
    private static readonly uint? ProcessUid = OperatingSystem.IsLinux() ? GetEffectiveUid() : null;

    /// <summary>Unix 套接字的路径:选项给了就用它,否则 Windows 以外默认 /tmp/.X11-unix/X{N};空字符串 = 不监听。</summary>
    private string? UnixSocketPath => _options.UnixSocketPath switch
    {
        "" => null,
        { } path => path,
        null when OperatingSystem.IsWindows() => null,
        null => $"/tmp/.X11-unix/X{_options.DisplayNumber}",
    };

    private void StartUnixListeners(CancellationToken cancellationToken)
    {
        if (UnixSocketPath is not { } path || !Socket.OSSupportsUnixDomainSockets)
        {
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            // 抽象命名空间:不落文件,进程退出自动消失;Xlib / XCB 对 :N 先试它。
            TryListen("\0" + path, file: null, cancellationToken);
        }
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows() && directory == "/tmp/.X11-unix")
                {
                    // 与 X.Org 一致:目录人人可写、带粘滞位,别的用户的服务端也能在这里建自己的套接字。
                    File.SetUnixFileMode(directory, (UnixFileMode)0x3FF);   // 八进制 1777
                }
            }
            if (File.Exists(path))
            {
                // 有人在听(桌面自己的 Xorg 通常不开 TCP,TCP 那一侧的占用检查看不出它):不碰,这条传输不开。
                // 没人应答才是上次没收拾干净的残留,删掉重建。
                if (IsUnixSocketLive(path))
                {
                    Log($"Unix socket {path} is in use by another X server; not listening on it");
                    return;
                }
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Unix socket {path} unavailable: {ex.Message}");
            return;
        }
        TryListen(path, path, cancellationToken);
    }

    /// <summary>
    /// 这个 Unix 套接字文件后面有没有进程在听。限时 300 毫秒:Linux 上对 backlog 已满的 AF_UNIX 流套接字做阻塞 connect 会一直等 ——
    /// 原先同步 Connect、不设时限,本机任何用户布置一个 backlog 满的套接字就能让 StartAsync 永远卡在这里。到时限按「有人占着」算。
    /// </summary>
    internal static bool IsUnixSocketLive(string path)
    {
        using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using CancellationTokenSource limit = new(TimeSpan.FromMilliseconds(300));
        try
        {
            probe.ConnectAsync(new UnixDomainSocketEndPoint(path), limit.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain)
        {
            return true;   // 有人在听、backlog 满了(Linux 上非阻塞 connect 回 EAGAIN):占着
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private void TryListen(string endpoint, string? file, CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        bool ownerOnly = false;
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(endpoint));
            // 在 Listen 之前把套接字文件改成只有属主能读写:连接要对套接字文件有写权限,于是连得上的只有这个用户(与 root),
            // 也就不必再要 cookie。还没 Listen,改权限之前没有人连得进来。改不了(文件系统不支持)就照常要 cookie。
            if (file is not null && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    ownerOnly = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Unix socket {file}: cannot restrict permissions ({ex.Message}); clients need the cookie");
                }
            }
            socket.Listen(64);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            Log($"Unix socket {endpoint.TrimStart('\0')} unavailable: {ex.SocketErrorCode}");
            return;
        }
        _unixListeners.Add((socket, file));
        _ = AcceptUnixLoopAsync(socket, ownerOnly, cancellationToken);
    }

    private async Task AcceptUnixLoopAsync(Socket listener, bool ownerOnly, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            TrackConnection(ServeUnixAsync(connection, ownerOnly, cancellationToken));
        }
    }

    /// <param name="connection">接进来的连接。</param>
    /// <param name="ownerOnly">它连的是只有属主能连的套接字文件(见 <see cref="TryListen" />)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ServeUnixAsync(Socket connection, bool ownerOnly, CancellationToken cancellationToken)
    {
        uint? peerUid = PeerUidOf(connection);
        bool localUser = ownerOnly || (peerUid is { } uid && uid == ProcessUid);
        await using NetworkStream stream = new(connection, ownsSocket: true);
        if (!localUser && peerUid is not null && _options.AuthorizationCookie is null)
        {
            return;   // 别的用户、又没配 cookie:授权必然失败,accept 时就关掉,不让它占握手的名额
        }
        try
        {
            await ServeCoreAsync(stream, new Peer(IsLocal: true, SameHost: true, peerUid, localUser, Authenticated: false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 服务端正在收工。
        }
    }

    /// <summary>
    /// Linux 上经 SO_PEERCRED 取连接对端的 uid(struct ucred:pid、uid、gid 各 4 字节);别的平台或取不到时为 null。
    /// </summary>
    private static uint? PeerUidOf(Socket connection)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        Span<byte> credentials = stackalloc byte[12];
        try
        {
            int length = connection.GetRawSocketOption(1, 17, credentials);   // SOL_SOCKET、SO_PEERCRED
            return length >= 8 ? BitConverter.ToUInt32(credentials[4..]) : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();

    private void StopUnixListeners()
    {
        foreach ((Socket socket, string? file) in _unixListeners)
        {
            socket.Dispose();
            if (file is not null)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // 删不掉就留着;下次启动会先删。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        _unixListeners.Clear();
    }
}
