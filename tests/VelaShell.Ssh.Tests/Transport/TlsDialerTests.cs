// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: RFC 8446(TLS 1.3);RFC 6066 §3(SNI);velashell-docs/zh/ssh/spec/09-dialing.md §4.4

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Tests.TestKit;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Transport;

/// <summary>
/// TLS 拨号器：套在内层连到的那个端点上；默认按系统规则严格校验证书，钉住自签证书的指纹才放行；
/// 握手失败（证书不可信、对端不说 TLS）报 <see cref="SshFailureReason.TlsFailed"/>。服务端一侧是 BCL 的 <see cref="SslStream"/>。
/// </summary>
[TestClass]
[TestCategory("Transport")]
public sealed class TlsDialerTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>一张自签证书（服务端用），签给 <c>tls.example</c>。</summary>
    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=tls.example", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("tls.example");
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // 带私钥导出再读回：Windows 上 SslStream 不认临时密钥。
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    /// <summary>内存里的「服务端」：在接到的流上做 TLS 服务端握手，然后把收到的回送；看到的 SNI 记下来。</summary>
    private static ISshTransportDialer EchoServer(X509Certificate2 certificate, List<string> seenServerNames) =>
        InMemoryTransport.CreateDialer((server, _, _) =>
        {
            _ = Task.Run(async () =>
            {
                await using SslStream tls = new(server);
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
                {
                    return;   // 客户端不信任证书时会中途断开
                }
                lock (seenServerNames)
                {
                    seenServerNames.Add(tls.TargetHostName);
                }
                byte[] buffer = new byte[256];
                int read;
                while ((read = await tls.ReadAsync(buffer)) > 0)
                {
                    await tls.WriteAsync(buffer.AsMemory(0, read));
                }
            }, CancellationToken.None);
            return ValueTask.CompletedTask;
        });

    /// <summary>钉住这张证书（比对 SHA-256 指纹）的校验回调。</summary>
    private static RemoteCertificateValidationCallback Pin(X509Certificate2 certificate)
    {
        string pinned = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        return (_, cert, _, _) =>
            cert is not null && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), pinned, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>钉住了证书指纹：握手成功，之后是透明的字节流；SNI 用这一跳的主机名。</summary>
    [TestMethod]
    public async Task 钉住证书指纹时握手成功且透明转发()
    {
        using X509Certificate2 certificate = CreateServerCertificate();
        List<string> seen = [];
        ISshTransportDialer dialer = DialerChain.Tls(
            new SshTlsOptions { RemoteCertificateValidation = Pin(certificate) },
            via: EchoServer(certificate, seen));

        await using Stream stream = await dialer.DialAsync(SshDialTarget.Direct("tls.example", 443), TestContext.CancellationToken);
        await stream.WriteAsync("SSH-2.0-probe\r\n"u8.ToArray(), TestContext.CancellationToken);
        byte[] echoed = new byte[15];
        await stream.ReadExactlyAsync(echoed, TestContext.CancellationToken);

        Assert.AreEqual("SSH-2.0-probe\r\n", Encoding.ASCII.GetString(echoed));
        Assert.AreSequenceEqual(["tls.example"], seen);
    }

    /// <summary>SNI 可以改：按 IP 连、证书签给域名时用。</summary>
    [TestMethod]
    public async Task ServerName改写SNI()
    {
        using X509Certificate2 certificate = CreateServerCertificate();
        List<string> seen = [];
        ISshTransportDialer dialer = DialerChain.Tls(
            new SshTlsOptions { ServerName = "tls.example", RemoteCertificateValidation = Pin(certificate) },
            via: EchoServer(certificate, seen));

        await using Stream stream = await dialer.DialAsync(SshDialTarget.Direct("192.0.2.10", 443), TestContext.CancellationToken);
        await stream.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken);
        await stream.ReadExactlyAsync(new byte[1], TestContext.CancellationToken);

        Assert.AreSequenceEqual(["tls.example"], seen);
    }

    /// <summary>SSH 套在 TLS 里（服务端前面是 sslh / stunnel 的形状）：握手、认证、跑命令一路走通。</summary>
    [TestMethod]
    public async Task SSH套在TLS里一路走通()
    {
        using X509Certificate2 certificate = CreateServerCertificate();
        using CancellationTokenSource serverLifetime = new(TimeSpan.FromSeconds(30));
        List<Task> servers = [];
        ISshTransportDialer overTls = DialerChain.Tls(
            new SshTlsOptions { RemoteCertificateValidation = Pin(certificate) },
            via: InMemoryTransport.CreateDialer((server, _, _) =>
            {
                servers.Add(Task.Run(() => ServeSshOverTlsAsync(server, certificate, serverLifetime.Token), CancellationToken.None));
                return ValueTask.CompletedTask;
            }));

        await using (SshConnection connection = await SshConnection.ConnectAsync(new SshConnectionOptions("joe@tls.example:443")
        {
            Dialer = overTls,
            HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
            Credentials = [new PasswordCredential("hunter2")],
        }, TestContext.CancellationToken))
        {
            SshCommandResult result = await connection.RunAsync("echo", cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual("over tls", result.StandardOutput);
        }

        await serverLifetime.CancelAsync();
        await Task.WhenAll(servers);
    }

    private static async Task ServeSshOverTlsAsync(Stream stream, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        try
        {
            await using SslStream tls = new(stream);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellationToken);
            await using TestSshServer server = new(tls);
            TestSshServerHandshake handshake = await server.HandshakeAsync(cancellationToken);
            TestAuthServer auth = new(server.Transport, handshake.ExchangeHash, new TestAuthPolicy { AcceptPassword = "hunter2" });
            await auth.RunAsync(cancellationToken);
            TestChannelServer channels = new(server.Transport, new TestChannelScript { StandardOutput = Encoding.UTF8.GetBytes("over tls"), ExitCode = 0 });
            await channels.RunAsync(cancellationToken);
        }
        catch (Exception)
        {
            // 客户端走了、用例拆场 —— 服务端这一侧的收尾不是被测对象。
        }
    }

    /// <summary>默认严格：自签证书不被信任，报 TlsFailed（拨号阶段），而不是放行。</summary>
    [TestMethod]
    public async Task 默认严格校验_自签证书报TlsFailed()
    {
        using X509Certificate2 certificate = CreateServerCertificate();
        ISshTransportDialer dialer = DialerChain.Tls(via: EchoServer(certificate, []));

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await dialer.DialAsync(SshDialTarget.Direct("tls.example", 443), TestContext.CancellationToken));

        Assert.AreEqual(SshFailureReason.TlsFailed, error.Reason);
        Assert.AreEqual(SshPhase.Dialing, error.Phase);
        Assert.Contains("tls.example:443", error.Message);
        Assert.IsFalse(error.Hops.Single().Succeeded);
    }

    /// <summary>对端说的不是 TLS（直接就是 SSH 标识串）：报 TlsFailed，而不是一个看不懂的 IO 异常。</summary>
    [TestMethod]
    public async Task 对端不说TLS时报TlsFailed()
    {
        ISshTransportDialer plain = InMemoryTransport.CreateDialer(async (server, _, ct) =>
        {
            await server.WriteAsync("SSH-2.0-OpenSSH_10.0\r\n"u8.ToArray(), ct);
            server.Dispose();
        });

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await DialerChain.Tls(via: plain).DialAsync(SshDialTarget.Direct("plain.example", 22), TestContext.CancellationToken));

        Assert.AreEqual(SshFailureReason.TlsFailed, error.Reason);
    }

    /// <summary>到不了这一跳：内层的失败原样往外走，不被说成 TLS 的问题。</summary>
    [TestMethod]
    public async Task 内层连不上时原样报内层的失败()
    {
        ISshTransportDialer refusing = InMemoryTransport.CreateDialer((_, target, _) =>
            throw new SshConnectException(SshFailureReason.TcpRefused, SshPhase.Dialing, $"连不上 {target.EndPoint}"));

        SshConnectException error = await Assert.ThrowsExactlyAsync<SshConnectException>(
            async () => await DialerChain.Tls(via: refusing).DialAsync(SshDialTarget.Direct("down.example", 443), TestContext.CancellationToken));

        Assert.AreEqual(SshFailureReason.TcpRefused, error.Reason);
    }
}
