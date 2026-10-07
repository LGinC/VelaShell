using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// 把内置 X 服务端的授权 cookie 写进(停下时撤出)用户的 <c>.Xauthority</c>:本机别的 X 程序经
/// <c>DISPLAY=localhost:N</c> / <c>:N</c> 连它时,Xlib 自己从这里取 cookie 带上 —— 与 <c>xauth add :N . cookie</c> 效果相同。
/// </summary>
/// <remarks>
/// <para>
/// 文件是一串定长前缀的二进制记录,全部大端:<c>family</c>(2 字节),然后地址、显示号(ASCII)、授权协议名、授权数据,
/// 各自是「2 字节长度 + 内容」。本机显示的记录族是 <see cref="FamilyLocal" />、地址是本机主机名 —— Xlib 连本机
/// (包括环回 TCP)时就按主机名找。
/// </para>
/// <para>
/// <b>这个文件里装的是能打开用户各个显示的钥匙</b>,改的时候格外小心:整份解析不了就不动它(截断的、别的格式的);
/// 按 xauth 的约定先建 <c>文件名-c</c>、再建 <c>文件名-l</c> 当锁(别的 xauth 正在改就等一会儿,等不到就放弃);新内容写进同目录的临时文件、
/// 权限 0600,再整个换过去 —— 写到一半崩了,原文件也还是完整的。任何一步失败只记日志,不影响服务端运行。
/// </para>
/// </remarks>
internal static class XAuthorityFile
{
    /// <summary>本机族:地址字段里是主机名。</summary>
    public const ushort FamilyLocal = 256;

    public const string MitMagicCookie1 = "MIT-MAGIC-COOKIE-1";

    /// <summary>
    /// 锁文件比这更老就当作上次没收拾干净的残留。原先 10 秒:家目录在慢的 NFS 上时,别的程序正拿着的锁可能被当成残留删掉,
    /// 两边同时改写、丢掉对方的记录。拿不到锁的代价只是这一次 cookie 没登记(记一行日志),宁可等不到。
    /// </summary>
    private static readonly TimeSpan StaleLock = TimeSpan.FromSeconds(60);

    /// <summary>一条记录。</summary>
    internal sealed record Entry(ushort Family, byte[] Address, string Number, string Name, byte[] Data)
    {
        public bool IsFor(string hostName, int display) =>
            Family == FamilyLocal && Name == MitMagicCookie1 && Number == display.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && string.Equals(Encoding.ASCII.GetString(Address), hostName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>XAUTHORITY</c> 优先,否则 <c>~/.Xauthority</c>(Windows 上是用户目录;Git Bash / MSYS2 的 HOME 默认就是它)。</summary>
    public static string? DefaultPath
    {
        get
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("XAUTHORITY");
            if (!string.IsNullOrEmpty(fromEnvironment))
            {
                return fromEnvironment;
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home.Length == 0 ? null : Path.Combine(home, ".Xauthority");
        }
    }

    /// <summary>登记这个显示的 cookie(同一主机名、同一显示号的旧记录一并替换,与 <c>xauth add</c> 一致)。</summary>
    public static bool Add(string path, string hostName, int display, byte[] cookie) =>
        Update(path, entries =>
        {
            entries.RemoveAll(e => e.IsFor(hostName, display));
            entries.Add(new Entry(FamilyLocal, Encoding.ASCII.GetBytes(hostName),
                display.ToString(System.Globalization.CultureInfo.InvariantCulture), MitMagicCookie1, cookie));
            return true;
        });

    /// <summary>撤掉自己登记的那一条(cookie 也对得上才撤:同一显示号后来被别的服务端登记了就不碰)。</summary>
    public static bool Remove(string path, string hostName, int display, byte[] cookie) =>
        Update(path, entries => entries.RemoveAll(e => e.IsFor(hostName, display) && e.Data.AsSpan().SequenceEqual(cookie)) > 0);

    /// <summary>解析一份文件的内容;有任何一条不完整就返回 null(不认识的内容不能改写,否则会把用户别的钥匙弄丢)。</summary>
    public static List<Entry>? Parse(ReadOnlySpan<byte> content)
    {
        List<Entry> entries = [];
        while (!content.IsEmpty)
        {
            if (content.Length < 2)
            {
                return null;
            }
            ushort family = BinaryPrimitives.ReadUInt16BigEndian(content);
            content = content[2..];
            if (!TryField(ref content, out byte[] address) || !TryField(ref content, out byte[] number)
                || !TryField(ref content, out byte[] name) || !TryField(ref content, out byte[] data))
            {
                return null;
            }
            entries.Add(new Entry(family, address, Encoding.ASCII.GetString(number), Encoding.ASCII.GetString(name), data));
        }
        return entries;

        static bool TryField(ref ReadOnlySpan<byte> content, out byte[] field)
        {
            field = [];
            if (content.Length < 2)
            {
                return false;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(content);
            if (content.Length < 2 + length)
            {
                return false;
            }
            field = content.Slice(2, length).ToArray();
            content = content[(2 + length)..];
            return true;
        }
    }

    public static byte[] Serialize(IEnumerable<Entry> entries)
    {
        using MemoryStream output = new();
        Span<byte> u16 = stackalloc byte[2];
        foreach (Entry entry in entries)
        {
            BinaryPrimitives.WriteUInt16BigEndian(u16, entry.Family);
            output.Write(u16);
            foreach (byte[] field in (byte[][])[entry.Address, Encoding.ASCII.GetBytes(entry.Number), Encoding.ASCII.GetBytes(entry.Name), entry.Data])
            {
                BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)field.Length);
                output.Write(u16);
                output.Write(field);
            }
        }
        return output.ToArray();
    }

    /// <summary>在锁里读、改、整个换掉。<paramref name="change" /> 返回 false 表示没改动,不写文件。</summary>
    private static bool Update(string path, Func<List<Entry>, bool> change)
    {
        string lockPath = path + "-c", linkPath = path + "-l";
        if (!TryLock(lockPath, linkPath))
        {
            Trace.WriteLine($"[XServer] {path} is locked by another program; the cookie was not updated");
            return false;
        }
        try
        {
            byte[] content = File.Exists(path) ? File.ReadAllBytes(path) : [];
            if (Parse(content) is not { } entries)
            {
                Trace.WriteLine($"[XServer] {path} is not a valid Xauthority file; leaving it alone");
                return false;
            }
            if (!change(entries))
            {
                return true;
            }
            string temporary = path + "-n";
            File.Delete(temporary);   // 上次没收拾干净的残留:权限只在新建时生效,得先删掉
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600:别的用户读不到钥匙
            }
            using (FileStream stream = new(temporary, options))
            {
                stream.Write(Serialize(entries));
            }
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[XServer] cannot update {path}: {ex.Message}");
            return false;
        }
        finally
        {
            foreach (string held in (string[])[linkPath, lockPath])   // 先 -l 后 -c(与上锁的次序相反)
            {
                try
                {
                    File.Delete(held);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.WriteLine($"[XServer] cannot remove {held}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 按 xauth 的约定上锁(Xau 的 XauLockAuth:先建 <c>文件名-c</c>,再由它得到 <c>文件名-l</c>,两个都成了才算拿到锁)。
    /// 原先只建 <c>-c</c>:xauth 那一侧以 <c>-l</c> 是否建得成为准的话,两边会同时以为自己拿着锁,与 xauth 只是部分互斥。
    /// 这里两个都以「不存在才建得成」的方式建;<c>-l</c> 建不成就放掉自己的 <c>-c</c>,等一会儿再试。别人拿着就等,
    /// 比 <see cref="StaleLock" /> 还老的当作残留清掉(<c>-c</c> 老了连同 <c>-l</c> 一起清,与 XauLockAuth 的 dead 参数同义)。
    /// </summary>
    private static bool TryLock(string lockPath, string linkPath)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                CreateExclusive(lockPath);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                if (IsStale(lockPath))
                {
                    TryDelete(linkPath);
                    TryDelete(lockPath);
                    continue;
                }
                Thread.Sleep(50);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"[XServer] cannot lock {lockPath}: {ex.Message}");
                return false;
            }
            try
            {
                CreateExclusive(linkPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // -l 在别人手里(或者是上次没收拾干净的):放掉自己的 -c,不挡着别人收尾。
                bool stale = File.Exists(linkPath) && IsStale(linkPath);
                if (stale)
                {
                    TryDelete(linkPath);
                }
                TryDelete(lockPath);
                if (!stale)
                {
                    Thread.Sleep(50);
                }
            }
        }
        return false;

        static void CreateExclusive(string path)
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
            }
        }

        static bool IsStale(string path) => DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > StaleLock;

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
