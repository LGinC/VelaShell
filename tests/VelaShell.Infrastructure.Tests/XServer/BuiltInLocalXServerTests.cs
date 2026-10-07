using System.Net;
using System.Net.Sockets;
using System.Text;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.XServer;
using VelaShell.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>
/// 内置 X 服务端:生命周期(宿主的附着 / 脱离)、SSH 转发拿到的连接器真能接进服务端、什么情况下不接管。
/// 显示号探测注入,监听的是 :10 起的号,避开本机常见的 :0。
/// </summary>
[TestClass]
[TestCategory("XServer")]
public class BuiltInLocalXServerTests
{
    private static ISettingsService Settings(XServerOptions options)
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { XServer = options });
        return settings;
    }

    /// <summary>0–9 当作被占用,自动模式挑到 :10。</summary>
    private static Task<bool> LowDisplaysBusy(int display, CancellationToken _) => Task.FromResult(display < 10);

    private static BuiltInLocalXServer Create(XServerOptions options, RecordingHost? host, bool otherDisplay = false) =>
        new(Settings(options), () => host, LowDisplaysBusy, _ => Task.FromResult(otherDisplay));

    [TestMethod]
    public async Task Start_AttachesHost_ThenStop_DetachesIt()
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);
        List<XServerState> states = [];
        server.StateChanged += (_, _) => states.Add(server.State);

        XServerStartResult result = await server.StartAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(XServerState.Running, server.State);
        Assert.AreEqual(10, server.DisplayNumber);
        Assert.AreEqual("localhost:10.0", server.Display);
        Assert.IsNotNull(host.Attached, "启动时先把服务端交给宿主");

        await server.StopAsync();

        Assert.AreEqual(XServerState.Stopped, server.State);
        Assert.AreEqual(1, host.Detaches);
        Assert.AreSequenceEqual([XServerState.Starting, XServerState.Running, XServerState.Stopped], states);
    }

    /// <summary>设置里选的键盘布局在附着之前交给宿主(空串 = 跟随系统)。</summary>
    [TestMethod]
    [DataRow("de")]
    [DataRow("")]
    public async Task Start_HandsTheChosenKeyboardLayoutToTheHostBeforeAttaching(string layout)
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions { KeyboardLayout = layout }, host);

        Assert.IsTrue((await server.StartAsync()).Success);

        Assert.AreEqual(layout, host.LayoutAtAttach);
    }

    [TestMethod]
    public async Task Start_WithoutHost_FailsWithoutListening()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host: null);

        XServerStartResult result = await server.StartAsync();

        Assert.IsFalse(result.Success);
        Assert.IsFalse(string.IsNullOrEmpty(result.Error));
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    [TestMethod]
    public async Task Start_ConfiguredDisplayInUse_Fails()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { DisplayNumber = 3 }, new RecordingHost());

        XServerStartResult result = await server.StartAsync();

        Assert.IsFalse(result.Success);
        Assert.Contains("3", result.Error);
    }

    /// <summary>SSH 拿到的连接器接进的是服务端本身:走完 X 的连接建立,拿到 Success。</summary>
    [TestMethod]
    public async Task ResolveForwarding_AutoStarts_AndConnectorReachesTheServer()
    {
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.AreEqual("localhost:10.0", resolution.Display);
        Assert.IsNotNull(resolution.Connector);
        await using Stream stream = await resolution.Connector("user@host:22", CancellationToken.None);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "Success —— 经连接器来的连接按本机连接放行");
    }

    /// <summary>
    /// 回归:SSH 会话比服务端活得久。标题栏上停掉再开之后,会话早先拿到的连接器要接进新的服务端 ——
    /// 以前它记住的是旧实例,每条 x11 通道都接进已释放的服务端,远端只看到 Failed to open display。
    /// </summary>
    [TestMethod]
    public async Task Connector_AfterRestart_ReachesTheNewServer_AndWhileStopped_Throws()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost());
        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
        Assert.IsNotNull(resolution.Connector);

        await server.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolution.Connector("user@host:22", CancellationToken.None));

        Assert.IsTrue((await server.StartAsync()).Success);
        await using Stream stream = await resolution.Connector("user@host:22", CancellationToken.None);
        await stream.WriteAsync(new byte[] { (byte)'l', 0, 11, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, head[0], "重启之后旧连接器接进的是新服务端");
    }

    /// <summary>X 连接建立:可以带授权(MIT-MAGIC-COOKIE-1),返回回复的第一个字节(1 = Success,0 = Failed)。</summary>
    private static async Task<byte> HandshakeAsync(Stream stream, byte[]? cookie = null)
    {
        byte[] name = cookie is null ? [] : Encoding.ASCII.GetBytes("MIT-MAGIC-COOKIE-1");
        byte[] data = cookie ?? [];
        List<byte> hello = [(byte)'l', 0, 11, 0, 0, 0, (byte)name.Length, 0, (byte)data.Length, 0, 0, 0];
        hello.AddRange(name);
        hello.AddRange(new byte[((name.Length + 3) & ~3) - name.Length]);
        hello.AddRange(data);
        hello.AddRange(new byte[((data.Length + 3) & ~3) - data.Length]);
        await stream.WriteAsync(hello.ToArray());
        await stream.FlushAsync();
        byte[] head = new byte[8];
        await stream.ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return head[0];
    }

    /// <summary>
    /// 每次启动生成 cookie:环回 TCP 不带就拒(本机别的进程、别的用户都连得到那个端口),带上就放行;
    /// cookie 写进 .Xauthority 给本机 X 程序用,停下时撤出;SSH 的连接器不要 cookie。
    /// </summary>
    [TestMethod]
    public async Task Start_RequiresTheCookieOnTcp_PublishesItToXauthority_AndRetractsItOnStop()
    {
        string xauthority = Path.Combine(Path.GetTempPath(), $"vx-xauth-{Guid.NewGuid():N}");
        try
        {
            await using BuiltInLocalXServer server = new(Settings(new XServerOptions()), () => new RecordingHost(), LowDisplaysBusy,
                _ => Task.FromResult(false), xauthority);
            Assert.IsTrue((await server.StartAsync()).Success);

            XAuthorityFile.Entry entry = XAuthorityFile.Parse(File.ReadAllBytes(xauthority))!.Single();
            Assert.AreEqual(XAuthorityFile.FamilyLocal, entry.Family);
            Assert.AreEqual(Dns.GetHostName(), Encoding.ASCII.GetString(entry.Address));
            Assert.AreEqual("10", entry.Number);
            Assert.HasCount(16, entry.Data);

            using (TcpClient anonymous = new())
            {
                await anonymous.ConnectAsync(IPAddress.Loopback, 6010);
                Assert.AreEqual(0, await HandshakeAsync(anonymous.GetStream()), "环回 TCP 不带 cookie:Failed");
            }
            using (TcpClient authorized = new())
            {
                await authorized.ConnectAsync(IPAddress.Loopback, 6010);
                Assert.AreEqual(1, await HandshakeAsync(authorized.GetStream(), entry.Data), "带上 .Xauthority 里的 cookie:Success");
            }
            XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();
            await using (Stream channel = await resolution.Connector!("user@host:22", CancellationToken.None))
            {
                Assert.AreEqual(1, await HandshakeAsync(channel), "SSH 的连接器:转发层核对过假 cookie,不再要");
            }

            await server.StopAsync();
            Assert.IsEmpty(XAuthorityFile.Parse(File.ReadAllBytes(xauthority))!, "停下时撤出");
        }
        finally
        {
            File.Delete(xauthority);
        }
    }

    /// <summary>
    /// 自动选号时,探测说空着的号在开起来那一刻被占了(探测与绑定之间别的程序抢先了,或者别的服务端持着 /tmp/.X{N}-lock):
    /// 换下一个空闲的号再试,原先直接报「显示号被占用」。
    /// </summary>
    [TestMethod]
    public async Task Start_AutomaticDisplayTakenBetweenProbeAndBind_TriesTheNextOne()
    {
        using TcpListener squatter = new(IPAddress.Loopback, 6010);   // 探测(注入的)看不见它
        squatter.Start();
        RecordingHost host = new();
        await using BuiltInLocalXServer server = Create(new XServerOptions(), host);
        List<XServerState> states = [];
        server.StateChanged += (_, _) => states.Add(server.State);

        XServerStartResult result = await server.StartAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(11, server.DisplayNumber, ":10 开不起来,换到 :11");
        Assert.AreEqual(XServerState.Running, states[^1]);
        Assert.DoesNotContain(XServerState.Stopped, states, "换号期间一直是 Starting");
        await server.StopAsync();
    }

    /// <summary>
    /// macOS 换了网络主机名常跟着变,Xlib 按连接那一刻的主机名在 .Xauthority 里找:主机名变了就按新名字重登、撤掉旧的那一条
    /// (原先一直是启动时的名字,之后本机 X 程序一律被拒)。
    /// </summary>
    [TestMethod]
    public async Task HostNameChange_RegistersTheCookieUnderTheNewName_AndStopRetractsIt()
    {
        string xauthority = Path.Combine(Path.GetTempPath(), $"vx-xauth-{Guid.NewGuid():N}");
        string hostName = "box-a";
        try
        {
            await using BuiltInLocalXServer server = new(Settings(new XServerOptions()), () => new RecordingHost(), LowDisplaysBusy,
                _ => Task.FromResult(false), xauthority, () => hostName);
            Assert.IsTrue((await server.StartAsync()).Success);
            Assert.AreEqual("box-a", Encoding.ASCII.GetString(XAuthorityFile.Parse(File.ReadAllBytes(xauthority))!.Single().Address));

            await server.RepublishCookieAsync();   // 主机名没变:什么也不做
            hostName = "box-b";
            await server.RepublishCookieAsync();
            XAuthorityFile.Entry entry = XAuthorityFile.Parse(File.ReadAllBytes(xauthority))!.Single();
            Assert.AreEqual("box-b", Encoding.ASCII.GetString(entry.Address), "按新名字登记,旧的那条撤掉");
            Assert.AreEqual("10", entry.Number);

            await server.StopAsync();
            Assert.IsEmpty(XAuthorityFile.Parse(File.ReadAllBytes(xauthority))!, "停下时撤出的是新名字的那一条");
        }
        finally
        {
            File.Delete(xauthority);
        }
    }

    [TestMethod]
    public async Task ResolveForwarding_AutoStartOff_DoesNotTakeOver()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { AutoStartForX11Forwarding = false }, new RecordingHost());

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.IsNull(resolution.Display);
        Assert.IsNull(resolution.Connector);
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    /// <summary>本机已经有别的 X 显示在用(Windows 上 :0 有人听,其它平台设了 DISPLAY):不插手。</summary>
    [TestMethod]
    public async Task ResolveForwarding_OtherDisplayInUse_DoesNotTakeOver()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions(), new RecordingHost(), otherDisplay: true);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.IsNull(resolution.Display);
        Assert.AreEqual(XServerState.Stopped, server.State);
    }

    [TestMethod]
    public async Task ResolveForwarding_WhenRunning_ReturnsDisplayAndConnectorEvenIfAutoStartOff()
    {
        await using BuiltInLocalXServer server = Create(new XServerOptions { AutoStartForX11Forwarding = false }, new RecordingHost());
        Assert.IsTrue((await server.StartAsync()).Success);

        XServerDisplayResolution resolution = await server.ResolveForwardingDisplayAsync();

        Assert.AreEqual("localhost:10.0", resolution.Display);
        Assert.IsNotNull(resolution.Connector);
    }

    /// <summary>记录附着 / 脱离的宿主;窗口回调一概不管。</summary>
    internal sealed class RecordingHost : IEmbeddedXServerHost
    {
        public X11Server? Attached { get; private set; }

        public int Detaches { get; private set; }

        public Task AttachAsync(X11Server server, CancellationToken cancellationToken)
        {
            Attached = server;
            LayoutAtAttach = KeyboardLayout;
            return Task.CompletedTask;
        }

        public string? KeyboardLayout { get; private set; }

        /// <summary>附着那一刻宿主手里的键盘布局(要在附着之前就交给宿主)。</summary>
        public string? LayoutAtAttach { get; private set; }

        public void UseKeyboardLayout(string layout) => KeyboardLayout = layout;

        public void Detach() => Detaches++;

        public void TopLevelMapped(XTopLevelWindow window)
        {
        }

        public void TopLevelUnmapped(XTopLevelWindow window)
        {
        }

        public void TopLevelChanged(XTopLevelWindow window, XTopLevelChanges changes)
        {
        }

        public void TopLevelDamaged(XTopLevelWindow window, IReadOnlyList<XRect> damage)
        {
        }

        public void CursorChanged(XTopLevelWindow? window, XCursor cursor)
        {
        }

        public void BellRequested(int volume)
        {
        }

        public void ClipboardChanged(string text)
        {
        }
    }
}
