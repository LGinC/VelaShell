using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Channels;

namespace VelaShell.Infrastructure.Tests.Ssh;

/// <summary>
/// 宿主的 <see cref="PtySize" /> 换成库的 <see cref="SshTerminalSize" />,即 <c>window-change</c> 的载荷。
/// 线上那一段(库把四个字段写进报文)由 <c>VelaShell.Ssh.Tests</c> 的通道用例覆盖,这里只管换算。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class ShellStreamWrapperResizeTests
{
    [TestMethod]
    public void PixelsAreCarriedIntoTheWindowChange()
    {
        SshTerminalSize size = ShellStreamWrapper.ToTerminalSize(new PtySize(200, 60, 1600, 1200));

        Assert.AreEqual(new SshTerminalSize(200, 60, 1600, 1200), size, "像素字段不能再在适配层被丢成 0。");
    }

    /// <summary>
    /// 负的像素按「不知道」发 0。库在构造时就拒绝负数(线上是 uint32),
    /// 而这条路是即发即忘的 —— 在这里抛出去,这次尺寸变化就无声无息地没了。
    /// </summary>
    [TestMethod]
    public void NegativePixels_AreSentAsUnknown()
    {
        SshTerminalSize size = ShellStreamWrapper.ToTerminalSize(new PtySize(80, 24, -1, -5));

        Assert.AreEqual(new SshTerminalSize(80, 24), size);
    }
}
