using System.Net;
using System.Net.Sockets;
using VelaShell.Core.XServer;

namespace VelaShell.Infrastructure.XServer;

/// <summary>本机某个显示号上有没有 X 服务端在听。</summary>
/// <remarks>
/// 两处都要看:TCP <c>6000+N</c>(Windows 上的 X 服务端都走它),以及类 Unix 上的 <c>/tmp/.X11-unix/XN</c>
/// —— 桌面自己的 Xorg / XWayland 通常关着 TCP,只看端口会以为 :0 空着。
/// </remarks>
internal static class XDisplayProbe
{
    /// <summary>探测一个端口有没有人听的上限。环回上连不上是立刻被拒,这个数只防意外。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>TCP 与 Unix 套接字任何一处有人在听即为占用。</summary>
    public static async Task<bool> IsInUseAsync(int display, CancellationToken cancellationToken) =>
        await IsTcpListeningAsync(display, cancellationToken).ConfigureAwait(false)
        || await IsUnixSocketLiveAsync(display, cancellationToken).ConfigureAwait(false);

    /// <summary>环回上 <c>6000+N</c> 有没有人在听。</summary>
    public static async Task<bool> IsTcpListeningAsync(int display, CancellationToken cancellationToken)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ProbeTimeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, XServerCommandLine.TcpPort(display)), limit.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>类 Unix 上 <c>/tmp/.X11-unix/XN</c> 后面有没有进程在听(Linux 另看抽象命名空间里的同名套接字)。</summary>
    private static async Task<bool> IsUnixSocketLiveAsync(int display, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || !Socket.OSSupportsUnixDomainSockets)
        {
            return false;
        }
        string path = $"/tmp/.X11-unix/X{display}";
        return (OperatingSystem.IsLinux() && await CanConnectAsync("\0" + path, cancellationToken).ConfigureAwait(false))
               || (File.Exists(path) && await CanConnectAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 连得上就是有人在听。限时:Linux 上对 backlog 已满的 AF_UNIX 流套接字做阻塞 connect 会一直等 —— 原先同步 Connect、不设时限,
    /// 本机任何用户在 <c>\0/tmp/.X11-unix/X1</c> 上 listen(0) 并自己先连一条不 accept,显示号探测就挂死在那里,X Server 再也启动不了。
    /// 到时限还没连上按「有人占着」算(确实有人在听,只是不收)。
    /// </summary>
    private static async Task<bool> CanConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        using Socket probe = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ProbeTimeout);
        try
        {
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), limit.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }
}
