using System.Text.Json.Serialization;
using VelaShell.Core.Models;

namespace VelaShell.Core.Credentials;

/// <summary>
/// 一条共享凭据(#550):多条连接共用的一套用户名 + 密码 / 私钥 / 证书。
/// 连接配置经 <see cref="CredentialReference" /> 引用它,改这一处,所有引用它的连接下次连接即用上新值。
/// </summary>
/// <remarks>
/// <para>
/// 字段与 <see cref="SessionProfile" /> 上那一组认证字段一一对应,语义也相同,
/// 只有 <see cref="Username" /> 多了一层:连接配置自己填了用户名时以配置为准,
/// 没填才用这里的 —— 同一套密钥常常要配不同的登录名(<c>ec2-user</c> / <c>ubuntu</c>)。
/// </para>
/// <para>
/// 密码与私钥口令落盘前经 <c>ISecretProtector</c> 加密,与连接配置里的密码同一强度;
/// 云同步没设端到端口令时这两项不上传。
/// </para>
/// </remarks>
public sealed class SharedCredential
{
    /// <summary>全局唯一标识,创建时自动生成。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>显示名称(如「机房交换机 admin」)。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>登录用户名;空串表示由引用它的连接各自提供。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>认证方式;FTP 与插件协议只能用 <see cref="AuthMethod.Password" />。</summary>
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    /// <summary>登录密码(密码认证)。</summary>
    public string? Password { get; set; }

    /// <summary>私钥文件路径(私钥与证书认证)。</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>私钥口令;私钥未加密时为空。</summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>OpenSSH 用户证书文件路径(证书认证)。</summary>
    public string? CertificatePath { get; set; }

    /// <summary>备注(用途、适用范围);null = 没有备注。</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// 这条凭据的认证材料是否齐全(用户名不算 —— 它可以由连接各自提供)。
    /// </summary>
    /// <remarks>
    /// 不齐全的凭据是正当状态:云同步没开端到端口令时,拉到本机的凭据只有名称与用户名,
    /// 密码要在本机补一次。
    /// </remarks>
    [JsonIgnore]
    public bool HasSecret => AuthMethod switch
    {
        AuthMethod.Password => !string.IsNullOrEmpty(Password),
        AuthMethod.PrivateKey => !string.IsNullOrWhiteSpace(PrivateKeyPath),
        AuthMethod.Certificate => !string.IsNullOrWhiteSpace(PrivateKeyPath) && !string.IsNullOrWhiteSpace(CertificatePath),
        _ => true
    };

    /// <summary>返回深拷贝(所有字段都是不可变值,逐字段拷贝即可)。</summary>
    /// <returns>与本实例等值的新实例。</returns>
    public SharedCredential Clone() =>
        new()
        {
            Id = Id,
            Name = Name,
            Username = Username,
            AuthMethod = AuthMethod,
            Password = Password,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassphrase = PrivateKeyPassphrase,
            CertificatePath = CertificatePath,
            Notes = Notes
        };
}
