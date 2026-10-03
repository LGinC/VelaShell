// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.1;velashell-docs/zh/ssh/spec/08-failures.md §3
//
// 本机 agent 没在跑是常态（Windows 上服务默认停着）。这一组钉住两件事：
// 连不上要**有时限**地报出来，而且原因码要说清是「没在跑」—— 宿主据此给出本地化的提示。

using System.Diagnostics;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;

namespace VelaShell.Ssh.Tests.Keys;

[TestClass]
[TestCategory("Keys")]
public sealed class AgentConnectTests
{
    [TestMethod]
    public async Task 端点为空时报没在跑()
    {
        // 等同于 SSH_AUTH_SOCK 没设。
        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            async () => await SshAgentClient.ConnectAsync(""));

        Assert.AreEqual(SshFailureReason.AgentNotRunning, error.Reason);
    }

    [TestMethod]
    public async Task 套接字不存在时报没在跑()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"velashell-no-agent-{Guid.NewGuid():N}.sock");

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            async () => await SshAgentClient.ConnectAsync(missing));

        Assert.AreEqual(SshFailureReason.AgentNotRunning, error.Reason, error.Message);
    }

    [TestMethod]
    public async Task 命名管道不存在时在时限内报没在跑而不是一直等()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("只在 Windows 上走命名管道。");
        }

        // 不带时限的管道连接会一直重试到管道出现 —— 曾经 agent 转发那一路就是这样，
        // 服务没起时远端的 ssh / git 一直挂到 shell 关掉。
        string pipe = $@"\\.\pipe\velashell-no-agent-{Guid.NewGuid():N}";
        Stopwatch elapsed = Stopwatch.StartNew();

        SshAgentException error = await Assert.ThrowsExactlyAsync<SshAgentException>(
            () => SshAgentClient.ConnectAsync(pipe).AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.AreEqual(SshFailureReason.AgentNotRunning, error.Reason, error.Message);
        Assert.IsLessThan(SshAgentClient.PipeConnectTimeout + TimeSpan.FromSeconds(5), elapsed.Elapsed);
    }
}
