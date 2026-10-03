using VelaShell.Core.Models;

namespace VelaShell.Core.Credentials;

/// <summary>
/// 凭据来源:按 <see cref="CredentialReference" /> 取回一套认证材料。
/// </summary>
/// <remarks>
/// <para>
/// 它管「秘密放在哪」,不管「秘密怎么加密」—— 后者是 <c>ISecretProtector</c> 的事,两者不共用接口
/// (velashell-docs《凭据管理器集成设计》§1)。
/// </para>
/// <para>
/// 目前只有本机共享凭据一种来源;外部密码管理器作为更多来源接进同一个解析器。
/// </para>
/// </remarks>
public interface ICredentialProvider
{
    /// <summary>来源 id,与 <see cref="CredentialReference.ProviderId" /> 对应。落盘在引用里,定下后不可改。</summary>
    string Id { get; }

    /// <summary>取回引用指向的那套认证材料。</summary>
    /// <param name="reference">引用;其 <see cref="CredentialReference.ProviderId" /> 等于 <see cref="Id" />。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>认证材料;可能不完整,完整性由解析器按连接类型判断。</returns>
    /// <exception cref="CredentialProviderException">条目不存在,或来源此刻不可用。</exception>
    Task<ResolvedCredential> ResolveAsync(CredentialReference reference, CancellationToken cancellationToken = default);
}

/// <summary>凭据来源交出的一套认证材料。</summary>
/// <param name="DisplayName">条目的显示名(只用于提示文案)。</param>
/// <param name="Username">条目里的用户名;空表示由连接配置提供。</param>
/// <param name="AuthMethod">认证方式。</param>
/// <param name="Password">密码(密码认证)。</param>
/// <param name="PrivateKeyPath">私钥文件路径(私钥与证书认证)。</param>
/// <param name="PrivateKeyPassphrase">私钥口令。</param>
/// <param name="CertificatePath">OpenSSH 用户证书文件路径(证书认证)。</param>
public sealed record ResolvedCredential(
    string DisplayName,
    string? Username,
    AuthMethod AuthMethod,
    string? Password,
    string? PrivateKeyPath = null,
    string? PrivateKeyPassphrase = null,
    string? CertificatePath = null);
