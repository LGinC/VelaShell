using System.Net;
using System.Net.Sockets;
using VelaShell.Core.XServer;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>本机显示的探测:Windows 上在 6000+N 监听的进程是不是当前用户会话里的(终端服务器上可能是别人的 X 服务端)。</summary>
[TestClass]
[TestCategory("XServer")]
public class XDisplayProbeTests
{
    [TestMethod]
    public void TcpListenerOfThisProcess_IsInThisSession_AndNoListenerIsNot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("按会话核对属主只在 Windows 上做(TCP 端口全机共享)");
            return;
        }
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int display = ((IPEndPoint)listener.LocalEndpoint).Port - XServerCommandLine.TcpPort(0);
        Assert.IsTrue(XDisplayProbe.IsTcpListenerInThisSession(display), "自己开的监听在当前会话里");

        listener.Stop();
        Assert.IsFalse(XDisplayProbe.IsTcpListenerInThisSession(display), "没人在听:不算自己的");
    }
}
