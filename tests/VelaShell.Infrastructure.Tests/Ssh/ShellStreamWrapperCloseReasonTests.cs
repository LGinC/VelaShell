using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Channels;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 终端流读到头时的原因取自库记下的通道关闭原因(W7),不再从读管道的结束方式反推。
/// 库那一侧(谁先发的 CLOSE、会话没了)由 <c>VelaShell.Ssh.Tests</c> 的通道用例覆盖,这里只管换算。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class ShellStreamWrapperCloseReasonTests
{
    [TestMethod]
    public void ChannelCloseReason_MapsToShellCloseReason()
    {
        // 会话没了才是自动重连要救的;远端 shell 自己退了(对端关、或只收到 EOF)不该再把用户连回去(#383)。
        Assert.AreEqual(ShellCloseReason.ConnectionLost, ShellStreamWrapper.FromChannel(SshChannelCloseReason.SessionClosed));
        Assert.AreEqual(ShellCloseReason.LocalTeardown, ShellStreamWrapper.FromChannel(SshChannelCloseReason.ClosedLocally));
        Assert.AreEqual(ShellCloseReason.RemoteExited, ShellStreamWrapper.FromChannel(SshChannelCloseReason.ClosedByPeer));
        Assert.AreEqual(ShellCloseReason.RemoteExited, ShellStreamWrapper.FromChannel(SshChannelCloseReason.Unknown));
    }
}
