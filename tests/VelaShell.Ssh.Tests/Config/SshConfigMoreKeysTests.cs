// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/09-dialing.md §7

using System.Security.Cryptography;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Config;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.Config;

/// <summary>记号展开、CertificateFile、ConnectionAttempts，以及会话级的 SetEnv / SendEnv / RemoteCommand。</summary>
[TestClass]
[TestCategory("Config")]
public sealed class SshConfigMoreKeysTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Keys", "Fixtures", name);

    /// <summary>补上的记号：%p %n %j %k %l %L %C；不认识的原样留着。</summary>
    [TestMethod]
    public void 路径里的记号都展开()
    {
        SshHostConfig config = SshConfigFile.Resolve(SshConfigFile.Parse("""
            Host alias
                HostName real.example
                User deploy
                Port 2200
                ProxyJump bastion
                HostKeyAlias hk
                IdentityFile /keys/%n-%h-%p-%r-%j-%k-%q
                CertificateFile /certs/%C
                IdentityFile /keys/%l/%L
            """), "alias");

        string local = System.Net.Dns.GetHostName();
        IReadOnlyList<string> identities = config.ExpandIdentityFiles();
        Assert.AreEqual("/keys/alias-real.example-2200-deploy-bastion-hk-%q", identities[0]);
        Assert.AreEqual($"/keys/{local}/{local.Split('.')[0]}", identities[1]);

#pragma warning disable CA5350 // %C 由 ssh_config(5) 规定就是 SHA-1
        string hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(local + "real.example" + "2200" + "deploy")));
#pragma warning restore CA5350
        Assert.AreSequenceEqual([$"/certs/{hash}"], config.ExpandCertificateFiles().ToArray());
    }

    /// <summary>
    /// 钥旁边的 -cert.pub 自动配上（ssh 的默认行为），证书排在裸钥前面；CertificateFile 写了一张证的不是这些钥的，报给 IdentityFileSkipped。
    /// 样本是真 ssh-keygen 生成与签发的。
    /// </summary>
    [TestMethod]
    public async Task 证书与私钥自动配对_证书排在前面()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse($"""
            Host cert
                IdentityFile "{Fixture("cert-ed25519")}"
                CertificateFile "{Fixture("cert-rsa-cert.pub")}"
            """);
        List<string> skipped = [];

        SshConnectionOptions options = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "cert", new SshConfigConnectOptions
        {
            IdentityFileSkipped = (path, _) => skipped.Add(Path.GetFileName(path)),
        });

        PublicKeyCredential[] keys = [.. options.Credentials.OfType<PublicKeyCredential>()];
        Assert.HasCount(2, keys);
        Assert.IsInstanceOfType<SshCertificateSigner>(keys[0].Signer, "证书排在前面");
        Assert.IsInstanceOfType<InMemorySshSigner>(keys[1].Signer);
        Assert.AreSequenceEqual(["cert-rsa-cert.pub"], skipped.ToArray(), "这张证书证的钥没读出来，报给调用方");
    }

    /// <summary>ConnectionAttempts：拨号器包一层，失败了隔一会儿再试，第三次连上；只有一次时不包。</summary>
    [TestMethod]
    public async Task ConnectionAttempts拨号失败时再试()
    {
        IReadOnlyList<SshConfigBlock> blocks = SshConfigFile.Parse("""
            Host flaky
                ConnectionAttempts 3
            Host once
                HostName once.example
            """);
        SshConnectionOptions flaky = await SshConfigFile.CreateConnectionOptionsAsync(blocks, "flaky");
        Assert.AreEqual(3, ((RetryingDialer)flaky.Dialer).Attempts);
        Assert.IsNotInstanceOfType<RetryingDialer>((await SshConfigFile.CreateConnectionOptionsAsync(blocks, "once")).Dialer);

        int calls = 0;
        RetryingDialer dialer = new(new FlakyDialer(() => ++calls), 3);
        TimeSpan interval = RetryingDialer.Interval;
        RetryingDialer.Interval = TimeSpan.FromMilliseconds(10);
        try
        {
            await using Stream stream = await dialer.DialAsync(SshDialTarget.Direct("h", 22));
            Assert.AreEqual(3, calls);

            calls = -10;
            await Assert.ThrowsAsync<SshConnectException>(async () => await dialer.DialAsync(SshDialTarget.Direct("h", 22)));
            Assert.AreEqual(-7, calls, "试满三次就照实失败");
        }
        finally
        {
            RetryingDialer.Interval = interval;
        }
    }

    private sealed class FlakyDialer(Func<int> attempt) : ISshTransportDialer
    {
        public ValueTask<Stream> DialAsync(SshDialTarget target, CancellationToken cancellationToken = default) =>
            attempt() == 3
                ? ValueTask.FromResult<Stream>(new MemoryStream())
                : throw new SshConnectException(SshFailureReason.TcpRefused, SshPhase.Dialing, "连不上");
    }

    /// <summary>SetEnv（多个、先出现的赢）+ SendEnv 选中的本机变量；RemoteCommand 成了伪终端里的命令；模板里显式给的不动。</summary>
    [TestMethod]
    public void 会话的环境变量与远端命令()
    {
        string variable = $"VELA_SENDENV_{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        Environment.SetEnvironmentVariable(variable, "from-local");
        try
        {
            SshHostConfig config = SshConfigFile.Resolve(SshConfigFile.Parse($"""
                Host h
                    SetEnv LANG=C.UTF-8 EDITOR=vim
                    SetEnv LANG=ignored
                    SendEnv VELA_SENDENV_* -LC_*
                    RemoteCommand tmux new -A -s main
                """), "h");

            SshShellOptions shell = config.ApplyToShell();
            Assert.AreEqual("C.UTF-8", shell.Environment["LANG"], "先出现的赢");
            Assert.AreEqual("vim", shell.Environment["EDITOR"]);
            Assert.AreEqual("from-local", shell.Environment[variable]);
            Assert.AreEqual("tmux new -A -s main", shell.Command);

            SshShellOptions explicitTemplate = config.ApplyToShell(new SshShellOptions
            {
                Environment = new Dictionary<string, string> { ["ONLY"] = "me" },
                Command = "htop",
            });
            Assert.AreSequenceEqual(["ONLY"], explicitTemplate.Environment.Keys.ToArray());
            Assert.AreEqual("htop", explicitTemplate.Command);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
