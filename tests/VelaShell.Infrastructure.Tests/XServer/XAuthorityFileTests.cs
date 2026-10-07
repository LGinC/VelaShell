using System.Text;
using VelaShell.Infrastructure.XServer;

namespace VelaShell.Infrastructure.Tests.XServer;

/// <summary>内置 X 服务端往 .Xauthority 里登记 / 撤出 cookie:只动自己那一条,认不出的文件不碰,别人锁着就不写。</summary>
[TestClass]
[TestCategory("XServer")]
public class XAuthorityFileTests
{
    private string _path = "";

    [TestInitialize]
    public void Setup() => _path = Path.Combine(Path.GetTempPath(), $"vx-xauth-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string file in (string[])[_path, _path + "-c", _path + "-l", _path + "-n"])
        {
            File.Delete(file);
        }
    }

    private static XAuthorityFile.Entry Other(string host, string number, byte[] data) =>
        new(XAuthorityFile.FamilyLocal, Encoding.ASCII.GetBytes(host), number, XAuthorityFile.MitMagicCookie1, data);

    private List<XAuthorityFile.Entry> Read() => XAuthorityFile.Parse(File.ReadAllBytes(_path))!;

    [TestMethod]
    public void Add_CreatesTheFile_KeepsOtherEntries_ReplacesTheSameDisplay_AndRemoveTakesOnlyOurs()
    {
        byte[] desktop = [7, 7, 7], stale = [1], ours = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        File.WriteAllBytes(_path, XAuthorityFile.Serialize([Other("box", "0", desktop), Other("box", "10", stale)]));

        Assert.IsTrue(XAuthorityFile.Add(_path, "box", 10, ours));
        List<XAuthorityFile.Entry> entries = Read();
        Assert.HasCount(2, entries, "显示 0 的留着,显示 10 的旧记录被替换");
        Assert.AreSequenceEqual(desktop, entries.Single(e => e.Number == "0").Data);
        Assert.AreSequenceEqual(ours, entries.Single(e => e.Number == "10").Data);
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path), "0600");
        }

        Assert.IsTrue(XAuthorityFile.Remove(_path, "box", 10, [9, 9]), "cookie 对不上:什么也不撤");
        Assert.HasCount(2, Read());
        Assert.IsTrue(XAuthorityFile.Remove(_path, "box", 10, ours));
        Assert.AreEqual("0", Read().Single().Number, "只撤自己那一条");
        Assert.IsFalse(File.Exists(_path + "-c"), "锁放掉了");
    }

    [TestMethod]
    public void Add_LeavesAnUnparsableFileAlone()
    {
        byte[] garbage = [0x01, 0x00, 0x00, 0x05, (byte)'x'];   // 地址说有 5 字节,只有 1 字节
        File.WriteAllBytes(_path, garbage);

        Assert.IsFalse(XAuthorityFile.Add(_path, "box", 10, [1, 2, 3]));
        Assert.AreSequenceEqual(garbage, File.ReadAllBytes(_path), "认不出的文件原样不动");
    }

    [TestMethod]
    public void Add_WhileAnotherProgramHoldsTheLock_GivesUp()
    {
        File.WriteAllBytes(_path + "-c", []);

        Assert.IsFalse(XAuthorityFile.Add(_path, "box", 10, [1, 2, 3]));
        Assert.IsFalse(File.Exists(_path), "没写");
        Assert.IsTrue(File.Exists(_path + "-c"), "别人的锁不删");
    }

    [TestMethod]
    public void Add_StaleLock_IsCleared()
    {
        File.WriteAllBytes(_path + "-c", []);
        File.SetLastWriteTimeUtc(_path + "-c", DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllBytes(_path + "-l", []);
        File.SetLastWriteTimeUtc(_path + "-l", DateTime.UtcNow.AddMinutes(-5));

        Assert.IsTrue(XAuthorityFile.Add(_path, "box", 10, [1, 2, 3]));
        Assert.HasCount(1, Read());
        Assert.IsFalse(File.Exists(_path + "-c") || File.Exists(_path + "-l"), "两个锁文件都放掉了");
    }

    /// <summary>xauth(Xau 的 XauLockAuth)以 -c 与 -l 两个文件上锁:只有 -l 在别人手里时同样要等,不能只看 -c。</summary>
    [TestMethod]
    public void Add_WhileAnotherProgramHoldsTheLinkLock_GivesUp_AndReleasesItsOwnCreateLock()
    {
        File.WriteAllBytes(_path + "-l", []);

        Assert.IsFalse(XAuthorityFile.Add(_path, "box", 10, [1, 2, 3]), "原先只建 -c,-l 在别人手里也照写");
        Assert.IsFalse(File.Exists(_path), "没写");
        Assert.IsTrue(File.Exists(_path + "-l"), "别人的锁不删");
        Assert.IsFalse(File.Exists(_path + "-c"), "自己建的 -c 放掉了,不挡着别人收尾");
    }

    /// <summary>慢的 NFS 家目录上别的程序拿锁可能要好几秒:一分钟之内的锁不当残留删掉(原先 10 秒就删)。</summary>
    [TestMethod]
    public void Add_LockHeldForHalfAMinute_IsNotBroken()
    {
        File.WriteAllBytes(_path + "-c", []);
        File.SetLastWriteTimeUtc(_path + "-c", DateTime.UtcNow.AddSeconds(-30));

        Assert.IsFalse(XAuthorityFile.Add(_path, "box", 10, [1, 2, 3]));
        Assert.IsTrue(File.Exists(_path + "-c"), "别人的锁不删");
    }
}
