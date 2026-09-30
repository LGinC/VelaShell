using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// chown(SFTP setstat 的 uid/gid)在<b>真实 OpenSSH 服务端</b>上的行为。单测里 <see cref="ISftpClientWrapper" />
/// 是替身,测不到的是服务端那一侧:
/// <list type="bullet">
///   <item>只改属组时,属主得按 stat 取回的值原样写回 —— uid 与 gid 共用一个标志位,写成 0 等于把文件交给 root,
///   普通用户会被拒;</item>
///   <item>普通用户把属主交给别人,服务端回权限不足,要冒成 <see cref="VelaSftpPermissionDeniedException" />
///   而不是静默成功。</item>
/// </list>
/// 放在 Core.Tests 是因为要用 <c>VelaSshClientWrapper.InnerConnection</c>(internal,只对本工程开放)。
/// </summary>
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
public class SftpOwnerIntegrationTests
{
    // 写 IPv4 字面量而不是 localhost:理由见 SftpSymlinkIntegrationTests。
    private const string TestHost = "127.0.0.1";
    private const int TestPort = 2222;
    private const string TestUser = "testuser";
    private const string TestPassword = "testpass";

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(60_000)]
    public async Task ChangeOwner_KeepsTheOtherIdAndSurfacesRefusals_AgainstRealOpenSsh()
    {
        RequireDockerAndSsh();

        string root = $"/tmp/vela-chown-{Guid.NewGuid():N}";
        string file = $"{root}/a.txt";
        VelaSshClientWrapper ssh = await ConnectAsync();
        try
        {
            await ssh.RunCommandAsync($"mkdir -p {root} && echo hi > {file}");
            string[] ids = (await ssh.RunCommandAsync("id -u; id -g"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int uid = int.Parse(ids[0], CultureInfo.InvariantCulture);
            int gid = int.Parse(ids[1], CultureInfo.InvariantCulture);
            Assert.AreNotEqual(0, uid, "测试账号必须是普通用户,否则「交给 root 被拒」那一步无从验证。");

            SshConnection inner = ssh.InnerConnection ?? throw new InvalidOperationException("SSH not connected.");
            await using var sftp = new VelaSftpClientWrapper(
                async ct => await SftpFileSystem.ConnectAsync(inner, cancellationToken: ct));
            await sftp.ConnectAsync(CancellationToken.None);

            // 只改属组(设成自己的主组 —— 普通用户唯一保证能设的那个):属主必须原样写回。
            // 写成 0 的话服务端会以权限不足拒绝,这一步就抛了。
            await sftp.ChangeOwnerAsync(file, null, gid);
            SftpEntry? entry = await sftp.GetEntryAsync(file);
            Assert.IsNotNull(entry);
            Assert.AreEqual(uid, entry.UserId);
            Assert.AreEqual(gid, entry.GroupId);

            // 两项一起给,不需要先 stat。
            await sftp.ChangeOwnerAsync(file, uid, gid);

            // 把属主交给 root:普通用户没这个权限,拒绝要冒出来,文件原样不动。
            await Assert.ThrowsAsync<VelaSftpPermissionDeniedException>(() => sftp.ChangeOwnerAsync(file, 0, null));
            string owner = await ssh.RunCommandAsync($"stat -c %u {file}");
            Assert.AreEqual(uid.ToString(CultureInfo.InvariantCulture), owner.Trim());
        }
        finally
        {
            try
            {
                await ssh.RunCommandAsync($"rm -rf {root}");
            }
            catch
            {
                // 清理尽力而为;容器本来就是一次性的。
            }
            await ssh.DisposeAsync();
        }
    }

    /// <summary>跳过记 Inconclusive 而不是"通过":全绿的报告里不能混着一行断言都没跑的用例。</summary>
    private static void RequireDockerAndSsh()
    {
        if (!IsDockerAvailable())
        {
            Assert.Inconclusive("Docker 不可用。运行 'docker compose -f docker-compose.test.yml up -d' 以启用。");
        }
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(TestHost, TestPort).Wait(TimeSpan.FromSeconds(3)))
            {
                Assert.Inconclusive($"SSH 测试服务器 {TestHost}:{TestPort} 不可达。");
            }
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            Assert.Inconclusive($"SSH 测试服务器 {TestHost}:{TestPort} 不可达:{ex.Message}");
        }
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null)
            {
                return false;
            }
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<VelaSshClientWrapper> ConnectAsync()
    {
        VelaSshClientWrapper client = new(
            ct => SshConnection.ConnectAsync(new SshConnectionOptions(TestUser, TestHost, TestPort)
            {
                Credentials = [new PasswordCredential(TestPassword)],
                // 测试容器的主机键每次重建都变:无条件信任,不写 known_hosts。
                HostKeyPolicy = new DangerousAcceptAnyHostKeyPolicy(),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            }, ct),
            TimeSpan.FromSeconds(10));

        await client.ConnectAsync(CancellationToken.None);
        return client;
    }
}
