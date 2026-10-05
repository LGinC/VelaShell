// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测:对端给的文本进异常消息之前先清一遍（终端转义序列注入）
//
// 异常消息最后会被打到终端或界面上。版本标识串、DISCONNECT 的描述、拒绝开通道的理由、
// SFTP 的状态消息都来自对端 —— 很多时候来自一个还没被认证的对端。

using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Protocol;

[TestClass]
[TestCategory("Protocol")]
public sealed class PeerTextTests
{
    // ESC ] 52 改剪贴板、ESC [ 2 J 清屏、CR 盖掉前半行、BEL、C1 的 CSI、双向覆盖符。
    private const string Hostile = "正常的中文\u001b]52;c;cm0gLXJmIH4=\u0007\u001b[2J\r伪造的提示\u009b31m‮txt.exe";

    private static void AssertClean(string message)
    {
        foreach (char c in message)
        {
            Assert.IsFalse(
                c is < ' ' or >= '\u007F' and <= '\u009F' or >= '‪' and <= '‮',
                $"消息里不该有控制字符 U+{(int)c:X4}：{message}");
        }
    }

    [TestMethod]
    public void 控制字符与双向覆盖符被换掉_可打印的中文原样保留()
    {
        string cleaned = PeerText.Sanitize(Hostile);

        AssertClean(cleaned);
        Assert.Contains("正常的中文", cleaned);
        Assert.Contains("伪造的提示", cleaned);
    }

    [TestMethod]
    public void 超长的截断()
    {
        string cleaned = PeerText.Sanitize(new string('a', 10_000), maxLength: 100);
        Assert.AreEqual(101, cleaned.Length, "100 个字符加一个省略号");
    }

    [TestMethod]
    public void SFTP状态消息进异常之前被清_原话留在ServerMessage()
    {
        SftpException error = new(SftpStatusCode.Failure, Hostile, "/tmp/\u001b[2Jx", SftpOperation.Read);

        AssertClean(error.Message);
        Assert.AreEqual(Hostile, error.ServerMessage, "原话照样交出去（文档写明是不可信输入）");
    }

    [TestMethod]
    public void 拒绝开通道的理由进异常之前被清_原话留在PeerDescription()
    {
        var error = SshChannelException.FromOpenFailure(
            "session", (uint)SshChannelOpenFailureReason.ConnectFailed, Hostile);

        AssertClean(error.Message);
        Assert.AreEqual(Hostile, error.PeerDescription);
    }

    [TestMethod]
    public async Task 版本标识串里的转义序列不进异常消息()
    {
        (InMemoryDuplexStream clientStream, InMemoryDuplexStream serverStream) = InMemoryTransport.CreatePair();
        await using SshPacketTransport clientTransport = new(clientStream);
        await using SshPacketTransport serverTransport = new(serverStream);

        // 协议版本那一段里藏一个清屏序列 —— 那一段曾经原样拼进「对端只支持 SSH 协议 …」。
        await serverTransport.WriteLineAsync("SSH-1.\u001b[2J5-Evil");

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await SshVersionExchange.ExchangeAsync(clientTransport));

        Assert.AreEqual(SshFailureReason.VersionMismatch, error.Reason);
        AssertClean(error.Message);
    }

    [TestMethod]
    public void 多行文本留末尾_换行收成一个记号()
    {
        string cleaned = PeerText.SanitizeTail("第一行\r\n\r\n第二行\u001b[2J\n最后一行\n", maxLength: 100);

        Assert.AreEqual("第一行 ⏎ 第二行?[2J ⏎ 最后一行", cleaned);

        string tail = PeerText.SanitizeTail(new string('a', 10_000) + "\n出错在这里", maxLength: 20);
        Assert.EndsWith("出错在这里", tail, "出错的那一句通常在最后，截断时留末尾");
        Assert.StartsWith("…", tail);
        Assert.IsLessThanOrEqualTo(21 + 3, tail.Length);
    }

    [TestMethod]
    public void 命令失败的消息里stderr清过且截短_原文留在Result()
    {
        // stderr 是对端的输出：可以有几 MB，带着终端转义序列。
        string stderr = new string('x', 5 * 1024 * 1024) + "\n" + Hostile + "\n最后一句";
        SshCommandResult result = new(new SshExitStatus(1), "", stderr);

        SshCommandFailedException error = Assert.ThrowsExactly<SshCommandFailedException>(() => result.EnsureSuccess("make"));

        AssertClean(error.Message);
        Assert.IsLessThan(2048, error.Message.Length, "消息里只放摘要");
        Assert.Contains("最后一句", error.Message);
        Assert.Contains("退出码 1", error.Message);
        Assert.AreEqual(stderr, error.Result.StandardError, "原文完整地留在 Result 里");
    }

    [TestMethod]
    public void 对端给的信号名进消息之前被清()
    {
        SshCommandResult result = new(new SshExitStatus(null, ExitSignalName: Hostile), "", "");

        SshCommandFailedException error = Assert.ThrowsExactly<SshCommandFailedException>(() => result.EnsureSuccess());

        AssertClean(error.Message);
        Assert.AreEqual(Hostile, error.Result.ExitStatus.ExitSignalName, "原话照样留在结果里");
    }
}
