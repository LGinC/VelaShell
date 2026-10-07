using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VelaShell.XServer.Tests.Server;

/// <summary>Unix 套接字监听:本机客户端经 DISPLAY=:N 连进来。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed partial class UnixSocketTests
{
    [TestMethod]
    public void 占用探测有时限_backlog满了的套接字不会把启动卡死()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个系统不支持 Unix 套接字");
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        UnixDomainSocketEndPoint endpoint = new(path);
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endpoint);
        listener.Listen(0);
        List<Socket> fillers = [];
        try
        {
            for (int i = 0; i < 4; i++)   // 连上不 accept,把 backlog 塞满(Linux 上之后的阻塞 connect 会一直等)
            {
                Socket filler = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
                fillers.Add(filler);
                try
                {
                    filler.Connect(endpoint);
                }
                catch (SocketException)
                {
                }
            }
            Stopwatch watch = Stopwatch.StartNew();
            bool live = X11Server.IsUnixSocketLive(path);
            Assert.IsLessThan(2000L, watch.ElapsedMilliseconds, "原先同步 connect、不设时限,Linux 上挂死在这里");
            if (OperatingSystem.IsLinux())
            {
                Assert.IsTrue(live, "有人在听、只是 backlog 满了:算占着,不去删它的套接字文件");
            }
        }
        finally
        {
            fillers.ForEach(f => f.Dispose());
            listener.Dispose();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 经Unix套接字完成连接建立()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个系统不支持 Unix 套接字");
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        await server.StartAsync();
        Assert.AreEqual(0, server.Port, "没开 TCP");

        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await using NetworkStream stream = new(socket);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "Success");
        Assert.AreEqual(11, head[2], "协议主版本");

        await server.DisposeAsync();
        Assert.IsFalse(File.Exists(path), "收工时删掉套接字文件");
    }

    [TestMethod]
    public async Task 套接字文件只有属主能读写_配了cookie时同一个用户不带也能连()
    {
        if (!Socket.OSSupportsUnixDomainSockets || OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows 上的 AF_UNIX 没有 Unix 文件权限");
            return;
        }
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path, AuthorizationCookie = new byte[16] });
        await server.StartAsync();
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path), "0600:别的用户连不进来");

        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await using NetworkStream stream = new(socket);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "同一个用户经套接字文件连进来,不必带 cookie");
    }

    [TestMethod]
    public async Task 套接字文件后面有别的服务端在听时不删不抢_这个显示号不能用_TCP监听一并撤回()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            Assert.Inconclusive("这个系统不支持 Unix 套接字");
        }
        // 桌面自己的 Xorg 通常不开 TCP:只看 TCP 端口会以为 :0 空着,把它的套接字文件删了 —— 整个桌面的新程序都连不上。
        // 原先只是不开这条传输、照样用 TCP 开起来,Display 还是 :N —— 用 :N 的本机程序连到的是那一个。
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        using Socket other = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        other.Bind(new UnixDomainSocketEndPoint(path));
        other.Listen(8);   // 服务端探测时连进来的那一条不会被 accept;backlog 给小了,macOS 上后面的连接会被拒
        try
        {
            int display = FreeDisplayNumber();
            await using (X11Server server = new(new X11ServerOptions { DisplayNumber = display, UnixSocketPath = path }))
            {
                SocketException taken = await Assert.ThrowsExactlyAsync<SocketException>(() => server.StartAsync());
                Assert.AreEqual(SocketError.AddressAlreadyInUse, taken.SocketErrorCode);
                Assert.AreEqual(0, server.Port, "TCP 已经开起来的监听撤回了");
                Assert.IsNull(server.Display);
            }
            using (TcpListener reuse = new(System.Net.IPAddress.Loopback, 6000 + display))
            {
                reuse.Start();   // 端口放出来了
            }
            Assert.IsTrue(File.Exists(path), "别人的套接字文件还在");
            using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
            using Socket accepted = await other.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(accepted, "连过去的还是原来那个服务端");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task 抽象名被别人bind了_这个显示号不能用_套接字文件也不建()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("抽象命名空间只有 Linux 有");
            return;
        }
        // 攻击者先 bind 抽象名、不 listen:原先我们的抽象名开不了只记一行,套接字文件与 TCP 照样开、Display 照样是 :N;
        // 它随后再 listen,Xlib / XCB 对 :N 先试抽象名,cookie 就交到了它手上。
        string path = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}.sock");
        using Socket squatter = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        squatter.Bind(new UnixDomainSocketEndPoint("\0" + path));

        await using X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path });
        SocketException taken = await Assert.ThrowsExactlyAsync<SocketException>(() => server.StartAsync());
        Assert.AreEqual(SocketError.AddressAlreadyInUse, taken.SocketErrorCode);
        Assert.IsFalse(File.Exists(path), "套接字文件也没建");
        Assert.IsNull(server.Display);
    }

    [TestMethod]
    public async Task 放套接字文件的目录是符号链接或属于别的用户时不开套接字文件()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("目录属主只在 Linux 与 macOS 上核对");
            return;
        }
        // 目录的属主能把里面的条目改名、删掉(粘滞位拦不住属主):别人的目录里,它随时能把我们的 0600 套接字换成自己的。
        string real = Path.Combine(Path.GetTempPath(), $"vx-{Guid.NewGuid():N}");
        string link = real + "-link";
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real);
        try
        {
            Assert.IsNull(X11Server.SocketDirectoryProblem(real), "自己的目录可信");
            Assert.IsNotNull(X11Server.SocketDirectoryProblem(link), "符号链接不可信:指向哪里由它的属主说了算");
            string path = Path.Combine(link, "X0");
            await using (X11Server server = new(new X11ServerOptions { ListenTcp = false, UnixSocketPath = path }))
            {
                await server.StartAsync();
                Assert.IsFalse(File.Exists(path), "不在不可信的目录里开套接字文件");
            }

            if (GetEffectiveUid() == 0)   // 改属主要 root(CI 的容器里是)
            {
                Assert.AreEqual(0, Chown(real, 12345, 12345));
                Assert.IsNotNull(X11Server.SocketDirectoryProblem(real), "属于别的用户");
                File.SetUnixFileMode(real, (UnixFileMode)0x3FF);   // 1777 也一样:粘滞位拦不住目录的属主
                Assert.IsNotNull(X11Server.SocketDirectoryProblem(real), "属于别的用户,带粘滞位也不行");
            }
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(real, recursive: true);
        }
    }

    [TestMethod]
    public void 取得到对端uid时以uid为准_取不到才看套接字文件的权限()
    {
        // 9p / drvfs(WSL 挂进来的 Windows 盘)上 chmod 0600 不报错却不生效:「只有属主连得上」不成立,别的用户也连得进来。
        Assert.IsFalse(X11Server.IsLocalUser(ownerOnly: true, peerUid: 4242, self: 1000), "原先取「或」:别的用户被当成本用户、不要 cookie");
        Assert.IsTrue(X11Server.IsLocalUser(ownerOnly: false, peerUid: 1000, self: 1000));
        Assert.IsTrue(X11Server.IsLocalUser(ownerOnly: true, peerUid: null, self: 1000), "取不到 uid(Windows):看套接字文件的权限");
        Assert.IsFalse(X11Server.IsLocalUser(ownerOnly: false, peerUid: null, self: null));
    }

    /// <summary>6000 + N 此刻没人占着的显示号(TCP 监听要用)。</summary>
    private static int FreeDisplayNumber()
    {
        for (int n = Random.Shared.Next(1000, 9000); ; n++)
        {
            try
            {
                using TcpListener probe = new(System.Net.IPAddress.Loopback, 6000 + n);
                probe.Start();
                return n;
            }
            catch (SocketException)
            {
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();

    [LibraryImport("libc", EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Chown(string path, uint owner, uint group);
}
