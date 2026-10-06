using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Tunnels;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Tests;

/// <summary>隧道错误的提示按原因码给,不去解析库的句子。</summary>
[TestClass]
public class TunnelErrorDescriptionTests
{
    /// <summary>服务端拒绝转发(原因码 1)与连不上目标(原因码 2)各有各的提示。</summary>
    [TestMethod]
    public void 按开通道失败的原因码给提示()
    {
        Assert.AreEqual(
            Strings.Get("TunnelSvc_ForwardProhibited"),
            TunnelService.DescribeForwardError(new SshChannelException(
                SshFailureReason.ChannelOpenFailed, "x", SshChannelOpenFailureReason.AdministrativelyProhibited)));

        // ConnectFailed 曾经没有映射,直接显示库的原文。
        Assert.AreEqual(
            Strings.Get("TunnelSvc_TargetRefused"),
            TunnelService.DescribeForwardError(new InvalidOperationException("外层", new SshChannelException(
                SshFailureReason.ChannelOpenFailed, "x", SshChannelOpenFailureReason.ConnectFailed))));
    }

    /// <summary>消息里碰巧有「administratively prohibited」不算数 —— 曾经宿主会去匹配这串字。</summary>
    [TestMethod]
    public void 不去解析消息里的句子()
    {
        string description = TunnelService.DescribeForwardError(
            new InvalidOperationException("administratively prohibited (by a string match)"));

        Assert.AreNotEqual(Strings.Get("TunnelSvc_ForwardProhibited"), description);
    }
}
