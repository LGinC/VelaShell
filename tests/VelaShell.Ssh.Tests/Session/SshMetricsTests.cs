// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/08-failures.md §7

using System.Diagnostics.Metrics;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Session;

/// <summary>
/// 连接与通道的度量（Meter <c>VelaShell.Ssh</c>）：经真的 <see cref="SshConnection.ConnectAsync"/> 连测试服务端，
/// 用 <see cref="MeterListener"/> 按 <c>host</c> 标签收 —— 仪表是全进程共享的，并行的别的用例也在记，
/// 每条用例用一个独有的主机名把自己的那几笔挑出来。
/// </summary>
[TestClass]
[TestCategory("Diagnostics")]
public sealed class SshMetricsTests
{
    public TestContext TestContext { get; set; } = null!;

    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>只收 <c>host</c> 标签等于给定值的那几笔。</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly string _host;
        private readonly List<Measurement> _measurements = [];

        public Recorder(string host)
        {
            _host = host;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SshMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
            _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Add(i, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
            _listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Dictionary<string, object?> map = [];
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                map[tag.Key] = tag.Value;
            }
            if (!Equals(map.GetValueOrDefault("host"), _host))
            {
                return;
            }
            lock (_measurements)
            {
                _measurements.Add(new Measurement(instrument.Name, value, map));
            }
        }

        public Measurement[] Of(string instrument, string? tag = null, string? value = null)
        {
            lock (_measurements)
            {
                return [.. _measurements.Where(m => m.Instrument == instrument && (tag is null || Equals(m.Tags.GetValueOrDefault(tag), value)))];
            }
        }

        public double Sum(string instrument, string? tag = null, string? value = null) =>
            Of(instrument, tag, value).Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }

    private static string UniqueHost() => $"metrics-{Guid.NewGuid():N}.example";

    private static SshConnectionOptions Options(string host, List<Task> servers, TestChannelScript script, string password, CancellationToken token) =>
        new($"joe@{host}:22")
        {
            Dialer = InMemoryTransport.CreateDialer((server, _, _) =>
            {
                servers.Add(Task.Run(() => ServeAsync(server, script, token), CancellationToken.None));
                return ValueTask.CompletedTask;
            }),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential(password)],
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

    private static async Task ServeAsync(Stream stream, TestChannelScript script, CancellationToken cancellationToken)
    {
        try
        {
            await using TestSshServer server = new(stream);
            TestSshServerHandshake handshake = await server.HandshakeAsync(cancellationToken);
            TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            if (!await auth.RunAsync(cancellationToken))
            {
                return;
            }
            TestChannelServer channels = new(server.Transport, script);
            await channels.RunAsync(cancellationToken);
        }
        catch (Exception)
        {
            // 客户端走了、用例拆场。
        }
    }

    /// <summary>
    /// 一条连接从连上到释放：建连耗时记成功、活着的连接数一加一减、线上字节与报文数与连接上的属性对得上；
    /// 通道一加一减、窗口取值记了；通道自己的字节计数是应用数据（服务端输出的那 6 个字节）。
    /// </summary>
    [TestMethod]
    public async Task 一条连接走完_连接与通道的度量都记上了()
    {
        string host = UniqueHost();
        using Recorder recorder = new(host);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];
        TestChannelScript script = new() { StandardOutput = Encoding.UTF8.GetBytes("tapped"), ExitCode = 0 };

        SshConnection connection = await SshConnection.ConnectAsync(
            Options(host, servers, script, "hunter2", lifetime.Token), TestContext.CancellationToken);
        SshChannel channel;
        await using (connection)
        {
            await using SshCommand command = await connection.ExecuteAsync("echo", cancellationToken: TestContext.CancellationToken);
            channel = command.Channel;
            SshCommandResult result = await command.ReadToEndAsync(TestContext.CancellationToken);
            Assert.AreEqual("tapped", result.StandardOutput);
            Assert.AreEqual(1d, recorder.Sum("velashell.ssh.connections.active"), "认证完成之后算一条活着的连接");
        }
        await lifetime.CancelAsync();
        await Task.WhenAll(servers);

        Measurement duration = recorder.Of("velashell.ssh.connect.duration").Single();
        Assert.AreEqual("Success", duration.Tags["outcome"]);
        Assert.AreEqual("Open", duration.Tags["phase"]);

        Assert.AreEqual(0d, recorder.Sum("velashell.ssh.connections.active"), "释放之后减回去");
        Assert.AreEqual(connection.BytesSent, (long)recorder.Sum("velashell.ssh.bytes", "direction", "sent"));
        Assert.AreEqual(connection.BytesReceived, (long)recorder.Sum("velashell.ssh.bytes", "direction", "received"));
        Assert.AreEqual(connection.PacketsSent, (long)recorder.Sum("velashell.ssh.packets", "direction", "sent"));
        Assert.AreEqual(connection.PacketsReceived, (long)recorder.Sum("velashell.ssh.packets", "direction", "received"));

        Assert.AreEqual(1d, recorder.Of("velashell.ssh.channels.active", "type", "session").Count(m => m.Value > 0));
        Assert.AreEqual(0d, recorder.Sum("velashell.ssh.channels.active", "type", "session"));
        Assert.IsNotEmpty(recorder.Of("velashell.ssh.channel.window", "type", "session"));

        Assert.AreEqual(6L, channel.BytesReceived, "通道上数的是应用数据，不含协议开销");
        Assert.AreEqual(0L, channel.BytesSent);
        Assert.IsGreaterThan(channel.BytesReceived, connection.BytesReceived);
    }

    /// <summary>认证失败：建连耗时记失败的原因与停在认证这一步；没连上的不算活着的连接。</summary>
    [TestMethod]
    public async Task 认证失败_记下失败的原因与停在哪一步()
    {
        string host = UniqueHost();
        using Recorder recorder = new(host);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];

        SshException failure = await Assert.ThrowsAsync<SshException>(async () => await SshConnection.ConnectAsync(
            Options(host, servers, new TestChannelScript(), "wrong", lifetime.Token), TestContext.CancellationToken));
        await lifetime.CancelAsync();
        await Task.WhenAll(servers);

        Measurement duration = recorder.Of("velashell.ssh.connect.duration").Single();
        Assert.AreEqual(failure.Reason.ToString(), duration.Tags["outcome"]);
        Assert.AreEqual("Authenticating", duration.Tags["phase"]);
        Assert.IsEmpty(recorder.Of("velashell.ssh.connections.active"));
    }

    /// <summary>
    /// 经跳板：跳板那一跳的连接也记在最终目标的名下（spec/08 §7「host 用逻辑目标」）。
    /// 跳板上开隧道被拒，目标那一跳在拨号这一步失败 —— 两笔建连耗时都是最终目标的。
    /// </summary>
    [TestMethod]
    public async Task 经跳板_跳板那一跳也记在最终目标名下()
    {
        string target = UniqueHost();
        using Recorder recorder = new(target);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];
        SshConnectionOptions jumpHost = Options("bastion.example", servers, new TestChannelScript(), "hunter2", lifetime.Token);

        SshConnectionOptions options = new($"joe@{target}:22")
        {
            Dialer = DialerChain.Jump(jumpHost),
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        await Assert.ThrowsAsync<SshException>(async () => await SshConnection.ConnectAsync(options, TestContext.CancellationToken));
        await lifetime.CancelAsync();
        await Task.WhenAll(servers);

        Measurement[] durations = recorder.Of("velashell.ssh.connect.duration");
        Assert.HasCount(2, durations);
        Assert.Contains(m => Equals(m.Tags["outcome"], "Success"), durations, "跳板那一跳连上了，记在最终目标名下");
        Assert.Contains(m => Equals(m.Tags["phase"], "Dialing") && !Equals(m.Tags["outcome"], "Success"), durations);
        Assert.AreEqual(0d, recorder.Sum("velashell.ssh.connections.active"), "跳板连接随拨号失败一起释放");
    }

    /// <summary>SFTP：每发一个请求记一次在途数；上传一个大于一块的文件，在途数至少到过 1。</summary>
    [TestMethod]
    public async Task Sftp_每个请求记一次在途数()
    {
        string host = UniqueHost();
        using Recorder recorder = new(host);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];
        TestSftpServer sftpServer = new();
        sftpServer.AddDirectory("/up");

        await using (SshConnection connection = await SshConnection.ConnectAsync(
            Options(host, servers, new TestChannelScript { SubsystemHandler = sftpServer.RunAsync }, "hunter2", lifetime.Token),
            TestContext.CancellationToken))
        {
            await using SftpFileSystem sftp = await SftpFileSystem.ConnectAsync(connection, cancellationToken: TestContext.CancellationToken);
            await sftp.WriteAllBytesAsync("/up/a.bin", new byte[256 * 1024], cancellationToken: TestContext.CancellationToken);
        }
        await lifetime.CancelAsync();
        await Task.WhenAll(servers);

        Measurement[] inFlight = recorder.Of("velashell.ssh.sftp.inflight");
        Assert.IsNotEmpty(inFlight);
        Assert.IsTrue(inFlight.All(m => m.Value >= 1), "记的是含这个请求自己在内的在途数");
        Assert.Contains(m => Equals(m.Tags.GetValueOrDefault("type"), "session"), recorder.Of("velashell.ssh.channels.active"));
    }
}
