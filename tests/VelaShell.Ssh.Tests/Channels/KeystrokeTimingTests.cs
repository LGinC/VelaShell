// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §7.3

using System.Text;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Config;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;

namespace VelaShell.Ssh.Tests.Channels;

/// <summary>按键时序混淆：输入照样一字节不差地到；对端认 PING 时打字期间发掩护、停手一阵之后就不发了；不认时不发。</summary>
[TestClass]
[TestCategory("Channels")]
public sealed class KeystrokeTimingTests
{
    private static async Task<string> ReadEchoAsync(SshShell shell, int length, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        while (text.Length < length)
        {
            System.IO.Pipelines.ReadResult read = await shell.StandardOutput.ReadAsync(cancellationToken);
            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
            {
                text.Append(Encoding.ASCII.GetString(segment.Span));
            }
            shell.StandardOutput.AdvanceTo(read.Buffer.End);
        }
        return text.ToString();
    }

    private static async Task TypeAsync(SshShell shell, string text, CancellationToken cancellationToken)
    {
        foreach (char c in text)
        {
            await shell.StandardInput.WriteAsync(new[] { (byte)c }, cancellationToken);
            await Task.Delay(30, cancellationToken);
        }
    }

    /// <summary>
    /// 对端认 PING：一个字一个字敲，回显一字节不差；打字期间发了掩护 PING；停手超过掩护期（最长 1.5 秒）之后不再发。
    /// 一下子粘贴一大段照样完整到。
    /// </summary>
    [TestMethod]
    public async Task 打字期间发掩护_停手之后不再发()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript
        {
            EchoStandardInput = true,
            WaitForClientEof = true,
            PingOnStart = [9],
            AnswerPings = true,
        });
        for (int i = 0; i < 200 && !host.Connection.PeerSupportsPing; i++)
        {
            await Task.Delay(10, host.Token);
        }

        await using SshShell shell = await host.Connection.OpenShellAsync(
            new SshShellOptions { ObscureKeystrokeTiming = TimeSpan.FromMilliseconds(20) }, host.Token);
        Assert.IsTrue(shell.IsObscuringKeystrokeTiming);

        await TypeAsync(shell, "secret", host.Token);
        Assert.AreEqual("secret", await ReadEchoAsync(shell, 6, host.Token));
        await Task.Delay(200, host.Token);
        Assert.IsGreaterThan(0, shell.KeystrokeChaffSent, "打字期间要有掩护");

        await Task.Delay(1800, host.Token);
        int settled = shell.KeystrokeChaffSent;
        await Task.Delay(300, host.Token);
        Assert.AreEqual(settled, shell.KeystrokeChaffSent, "停手过了掩护期就不发了");

        string paste = new('p', 4000);
        await shell.StandardInput.WriteAsync(Encoding.ASCII.GetBytes(paste), host.Token);
        Assert.AreEqual(paste, await ReadEchoAsync(shell, paste.Length, host.Token));
    }

    /// <summary>对端不认 PING：只攒批，一个掩护都不发（对端不认的报文不发），输入照样到。</summary>
    [TestMethod]
    public async Task 对端不认PING就不发掩护()
    {
        await using TestSshServerHost host = await TestSshServerHost.StartAsync(new TestChannelScript { EchoStandardInput = true, WaitForClientEof = true });

        await using SshShell shell = await host.Connection.OpenShellAsync(
            new SshShellOptions { ObscureKeystrokeTiming = TimeSpan.FromMilliseconds(20) }, host.Token);

        await TypeAsync(shell, "ls", host.Token);
        Assert.AreEqual("ls", await ReadEchoAsync(shell, 2, host.Token));
        await Task.Delay(200, host.Token);
        Assert.AreEqual(0, shell.KeystrokeChaffSent);
        Assert.AreEqual(0, host.Channels.Observation.PingsReceived);
    }

    /// <summary>ssh_config 的 ObscureKeystrokeTiming：yes 是 20 毫秒，interval:N 是 N 毫秒，no 不开；节拍越界当场报。</summary>
    [TestMethod]
    public void 配置与参数()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host yes
                ObscureKeystrokeTiming yes
            Host custom
                ObscureKeystrokeTiming interval:80
            Host off
                ObscureKeystrokeTiming no
            """);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), SshConfigFile.Resolve(blocks, "yes").ApplyToShell().ObscureKeystrokeTiming);
        Assert.AreEqual(TimeSpan.FromMilliseconds(80), SshConfigFile.Resolve(blocks, "custom").ApplyToShell().ObscureKeystrokeTiming);
        Assert.IsNull(SshConfigFile.Resolve(blocks, "off").ApplyToShell().ObscureKeystrokeTiming);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshShellOptions { ObscureKeystrokeTiming = TimeSpan.Zero });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SshShellOptions { ObscureKeystrokeTiming = TimeSpan.FromSeconds(2) });
    }
}
