using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 基于 ~/.ssh 目录的密钥管理:以 *.pub 公钥文件枚举密钥对,
/// 类型与 SHA256 指纹从公钥 blob 解析(与 OpenSSH `ssh-keygen -lf` 口径一致)。
/// </summary>
/// <param name="sshDirectory">密钥目录;<see langword="null" /> 为 ~/.ssh。</param>
/// <param name="connectAgent">连本机 agent;<see langword="null" /> 走与「SSH Agent」认证相同的端点与 3 秒上限。</param>
public sealed class SshKeyService(
    string? sshDirectory = null,
    Func<CancellationToken, ValueTask<SshAgentClient>>? connectAgent = null) : ISshKeyService
{
    private readonly Func<CancellationToken, ValueTask<SshAgentClient>> _connectAgent =
        connectAgent ?? SshConnectionAssembler.ConnectLocalAgentAsync;

    private readonly string _sshDirectory = sshDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>枚举 ~/.ssh 目录下的密钥对,按公钥文件解析类型与指纹后返回。</summary>
    public Task<List<SshKeyInfo>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var keys = new List<SshKeyInfo>();
            if (!Directory.Exists(_sshDirectory))
            {
                return keys;
            }
            foreach (string pubFile in Directory.EnumerateFiles(_sshDirectory, "*.pub").OrderBy(f => f))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileNameWithoutExtension(pubFile);
                string privatePath = Path.Combine(_sshDirectory, name);
                SshKeyInfo? info = TryParsePublicKey(name, privatePath, pubFile);
                if (info is not null)
                {
                    keys.Add(info);
                }
            }
            return keys;
        }, cancellationToken);
    }

    /// <summary>
    /// 将外部私钥及其同名公钥复制到 <c>~/.ssh</c> 导入;目标同名已存在时返回 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>先验后抄,失败回滚。</b>旧实现是"能抄就抄":源私钥不存在时 <c>if (File.Exists(…))</c>
    /// 直接跳过复制,却照样返回一条 <c>Unknown</c> 条目 —— 界面显示"已导入 xxx",
    /// 而 <c>~/.ssh</c> 里什么都没多。挑中一个随便的文本文件同样一路成功。
    /// </para>
    /// <para>
    /// <b>没有 .pub 就从私钥文件里读出公钥、写一份。</b><see cref="ListKeysAsync" /> 是按 <c>*.pub</c> 枚举的,
    /// 只抄私钥的话列表里一条都看不到。OpenSSH 容器的公钥段与 PuTTY <c>.ppk</c> 的 <c>Public-Lines</c> 是明文,
    /// 私钥加了密也读得出(<see cref="SshPrivateKeyFile.TryReadPublicKey" />);只有加密的 PKCS#8 这类
    /// 公钥也在密文里的,才要求连 <c>.pub</c> 一起选。曾经一律要求 <c>.pub</c>,而 PuTTY 用户手里通常只有一个 <c>.ppk</c>。
    /// </para>
    /// <para>
    /// <b>认格式交给库。</b>曾经只看首行是不是 <c>-----BEGIN … PRIVATE KEY</c>,库能读的 <c>.ppk</c> 被当成「不是私钥」挡在门外。
    /// </para>
    /// <para>
    /// <b>私钥权限。</b>生成路径一直会设 0600,导入路径以前不设 —— 而 OpenSSH 对
    /// 组/其他可读的私钥直接拒用(<c>UNPROTECTED PRIVATE KEY FILE</c>)。
    /// </para>
    /// </remarks>
    /// <param name="sourcePrivateKeyPath">源私钥路径(选中 <c>.pub</c> 时自动换成同名私钥)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入后的密钥;同名已存在时为 <see langword="null" />。</returns>
    /// <exception cref="FileNotFoundException">源私钥不存在,或 <c>.pub</c> 不存在而私钥文件里又读不出公钥。</exception>
    /// <exception cref="InvalidDataException">源文件不是私钥,或公钥无法解析。</exception>
    public async Task<SshKeyInfo?> ImportKeyAsync(string sourcePrivateKeyPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_sshDirectory);
        string name = Path.GetFileName(sourcePrivateKeyPath);
        if (name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
            sourcePrivateKeyPath = sourcePrivateKeyPath[..^4];
        }
        string sourcePub = sourcePrivateKeyPath + ".pub";
        string targetPrivate = Path.Combine(_sshDirectory, name);
        string targetPub = targetPrivate + ".pub";
        if (File.Exists(targetPrivate) || File.Exists(targetPub))
        {
            return null;
        }

        // ——— 先验:任何一条不过就当场退出,此时 ~/.ssh 一个字节都没动过 ———
        if (!File.Exists(sourcePrivateKeyPath))
        {
            throw new FileNotFoundException(
                Strings.Format("KeySvc_ImportPrivateKeyMissing", sourcePrivateKeyPath), sourcePrivateKeyPath);
        }
        if (await ReadPrivateKeyTextAsync(sourcePrivateKeyPath, cancellationToken).ConfigureAwait(false) is not { } privateText)
        {
            throw new InvalidDataException(Strings.Format("KeySvc_ImportNotAPrivateKey", name));
        }
        string? derivedPublicLine = null;
        if (!File.Exists(sourcePub))
        {
            if (!SshPrivateKeyFile.TryReadPublicKey(privateText, out SshPublicKey? publicKey))
            {
                throw new FileNotFoundException(
                    Strings.Format("KeySvc_ImportPublicKeyMissing", Path.GetFileName(sourcePub)), sourcePub);
            }
            derivedPublicLine = publicKey.ToOpenSshFormat(name);
        }

        // ——— 再抄:两份一起,任一失败就把已抄的清掉,不留半套 ———
        try
        {
            File.Copy(sourcePrivateKeyPath, targetPrivate);
            ApplyPrivateKeyPermissions(targetPrivate);
            if (derivedPublicLine is null)
            {
                File.Copy(sourcePub, targetPub);
            }
            else
            {
                File.WriteAllText(targetPub, derivedPublicLine + Environment.NewLine);
            }
        }
        catch
        {
            TryDelete(targetPrivate);
            TryDelete(targetPub);
            throw;
        }

        // ——— 最后按真实解析结果回报。解析不出来说明 .pub 不是公钥,一并回滚 ———
        List<SshKeyInfo> keys = await ListKeysAsync(cancellationToken).ConfigureAwait(false);
        if (keys.FirstOrDefault(k => k.Name == name) is { } imported)
        {
            return imported;
        }
        TryDelete(targetPrivate);
        TryDelete(targetPub);
        throw new InvalidDataException(Strings.Format("KeySvc_ImportBadPublicKey", Path.GetFileName(sourcePub)));
    }

    /// <summary>私钥文件最大多少字节:最大的 RSA 私钥也不过十几 KiB,再大的不会是私钥。</summary>
    private const long MaxPrivateKeyFileBytes = 1024 * 1024;

    /// <summary>
    /// 读出私钥文件的文本;认不出是私钥(或读不了)时为 <see langword="null" />。够用来挡住"选错文件"。
    /// </summary>
    /// <remarks>
    /// 格式由库认(<see cref="SshPrivateKeyFile.DetectFormat" />:OpenSSH、PKCS#1、PKCS#8、SEC1、PuTTY <c>.ppk</c>),
    /// 不做完整解析 —— 私钥可能加了密,真解析要口令。这足以把 README、id_rsa.pub、截图挡在门外。
    /// </remarks>
    private static async Task<string?> ReadPrivateKeyTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path).Length > MaxPrivateKeyFileBytes)
            {
                return null;
            }
            string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return SshPrivateKeyFile.DetectFormat(text) == SshPrivateKeyFormat.Unknown ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>把私钥收成仅属主可读写。OpenSSH 对更宽的权限直接拒用。</summary>
    private static void ApplyPrivateKeyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 回滚是尽力而为:清不掉也不该把原始失败原因盖掉。
        }
    }

    /// <summary>
    /// 在 ~/.ssh 目录生成指定名称的密钥对(默认 Ed25519),并写出 OpenSSH 格式的私钥与公钥。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>私钥写 OpenSSH 格式</b>(<c>-----BEGIN OPENSSH PRIVATE KEY-----</c>)。
    /// 这一条曾经是硬性的:上一版底层库的解析器只认这一种,<c>ExportRSAPrivateKeyPem()</c> 产出的
    /// PKCS#1(<c>-----BEGIN RSA PRIVATE KEY-----</c>)与 PKCS#8 都会被判 "Unsupported format"
    /// 而当作无可用凭据【跳过】—— 认证遂以 "These methods were skipped: publickey" 失败,用户表现为
    /// 用本应用生成的密钥怎么都登不上。换到 VelaShell.Ssh 之后这三种格式都原生读得出
    /// (见 <c>LegacyPrivateKeyFormatTests</c>),但生成端仍只写 OpenSSH 格式:Ed25519 在 BCL 里
    /// 根本没有 PEM 导出,而这也正是今天 <c>ssh-keygen</c> 的产物,拷到别处照样能用。
    /// </para>
    /// <para>
    /// <b>生成与写出都交给 SSH 库</b>(<see cref="InMemorySshSigner" /> 的 <c>Generate*</c> 与
    /// <see cref="SshPrivateKeyFile.Format" />)。曾经宿主手写了一份 OpenSSH 私钥容器、拿 BouncyCastle 现造 Ed25519,
    /// 中间导出的 <c>RSAParameters</c> / <c>ECParameters</c> 也不清零;库的写出侧用完即清。
    /// </para>
    /// <para>
    /// <b>默认给 Ed25519,而不是 RSA。</b>OpenSSH 自 6.5(2014)起支持,`ssh-keygen` 自 9.5(2023)
    /// 起也已默认给它:私钥 32 字节、生成几乎不耗时(RSA 4096 要秒级),强度还不比 RSA 4096 差。
    /// 留着 <see cref="SshKeyAlgorithm.Rsa" /> 只是为了对付把 <c>ssh-rsa</c> 写死进白名单的老堡垒机。
    /// </para>
    /// </remarks>
    public Task<SshKeyInfo> GenerateKeyAsync(
        string name,
        SshKeyAlgorithm algorithm = SshKeyAlgorithm.Ed25519,
        int bits = 0,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(Strings.Get("KeySvc_InvalidName"), nameof(name));
            }
            Directory.CreateDirectory(_sshDirectory);
            string privatePath = Path.Combine(_sshDirectory, name);
            string publicPath = privatePath + ".pub";
            if (File.Exists(privatePath) || File.Exists(publicPath))
            {
                throw new IOException(Strings.Format("KeySvc_AlreadyExists", name));
            }
            string comment = $"velashell@{Environment.MachineName}";
            // ECDSA 只有 256 / 384 / 521 三条曲线,别的位数由库抛 ArgumentOutOfRangeException ——
            // SSH 给别的曲线连个能写进公钥行的名字都没有。RSA 宿主默认 4096,比 ssh-keygen 的 3072 保守一档。
            using InMemorySshSigner key = algorithm switch
            {
                SshKeyAlgorithm.Ed25519 => InMemorySshSigner.GenerateEd25519(),
                SshKeyAlgorithm.Ecdsa => InMemorySshSigner.GenerateEcdsa(bits > 0 ? bits : 256),
                SshKeyAlgorithm.Rsa => InMemorySshSigner.GenerateRsa(bits > 0 ? bits : 4096),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null)
            };

            File.WriteAllText(privatePath, SshPrivateKeyFile.Format(key, comment: comment));
            ApplyPrivateKeyPermissions(privatePath);
            SshPublicKey publicKey = key.PublicKey;
            string publicLine = publicKey.ToOpenSshFormat(comment);
            File.WriteAllText(publicPath, publicLine + Environment.NewLine);
            return new SshKeyInfo(name, DescribeType(publicKey), publicKey.Sha256Fingerprint, privatePath, publicLine);
        }, cancellationToken);
    }

    /// <summary>删除指定名称密钥对的私钥与公钥文件(存在则删除)。</summary>
    public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            string privatePath = Path.Combine(_sshDirectory, name);
            string publicPath = privatePath + ".pub";
            if (File.Exists(privatePath))
            {
                File.Delete(privatePath);
            }
            if (File.Exists(publicPath))
            {
                File.Delete(publicPath);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 公钥行按 <c>类型 base64 注释</c> 拼出来,与 <c>ssh-add -L</c> 的输出同一格式 ——
    /// 连接配置里「只转发指定密钥」存的就是它。
    /// </remarks>
    public async Task<List<SshKeyInfo>> ListAgentKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SshAgentClient agent = await _connectAgent(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SshAgentIdentity> identities =
                await agent.ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

            List<SshKeyInfo> keys = [];
            foreach (SshAgentIdentity identity in identities)
            {
                SshPublicKey key = identity.PublicKey;
                keys.Add(new SshKeyInfo(
                    identity.Comment, DescribeType(key), key.Sha256Fingerprint, "", key.ToOpenSshFormat(identity.Comment)));
            }
            return keys;
        }
        catch (Exception ex) when (ex is SshAgentException or IOException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // agent 没在跑(Windows 上服务默认是停着的)是常态,不是错误。
            return [];
        }
    }

    /// <remarks>
    /// 解析、指纹、位数都交给 SSH 库的 <see cref="SshPublicKey" /> —— 宿主不另写一份 blob 解析。
    /// 库认不出的类型(早已弃用的 DSA 之类)不列出:列出来也连不上。
    /// </remarks>
    private static SshKeyInfo? TryParsePublicKey(string name, string privatePath, string pubFile)
    {
        try
        {
            string line = File.ReadAllText(pubFile).Trim();
            return SshPublicKey.TryParse(line, out SshPublicKey? key)
                ? new(name, DescribeType(key), key.Sha256Fingerprint, privatePath, line)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>给人看的类型与位数,如 <c>RSA 4096</c>、<c>ED25519</c>、<c>ECDSA 256</c>。证书照原样显示它的类型串。</summary>
    private static string DescribeType(SshPublicKey key) =>
        key.IsCertificate
            ? key.KeyType
            : key.PlainKeyType switch
            {
                "ssh-rsa" => $"RSA {key.KeyBits}",
                "ssh-ed25519" => "ED25519",
                _ when key.PlainKeyType.StartsWith("ecdsa-", StringComparison.Ordinal) => $"ECDSA {key.KeyBits}",
                _ => key.PlainKeyType,
            };
}
