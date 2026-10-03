using VelaShell.Core.Models;

namespace VelaShell.Core.Credentials;

/// <summary>
/// 连接链路取凭据的唯一入口:把一条可能引用了凭据的连接配置,变成这一次连接实际要用的配置。
/// </summary>
/// <remarks>
/// <para>
/// <b>连接那一刻才解析,结果不回写。</b>解析出来的明文只活在这一次连接用的副本里,
/// 不进会话树缓存、不进 <c>tab.Profile</c>、更到不了仓储 —— 改了凭据,下一次连接(包括重连)
/// 就用上新值,不必等哪里刷新。
/// </para>
/// <para>
/// 五个构建点都要经过它:SSH 工作流(含跳板的每一跳)、FTP、插件文件协议、插件终端、插件工作台。
/// </para>
/// </remarks>
public interface ICredentialResolver
{
    /// <summary>
    /// 返回这一次连接要用的配置。
    /// </summary>
    /// <param name="profile">连接配置;不会被修改。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 没有引用凭据,或带着本次在登录框里手输的凭据(见 <see cref="CredentialMaterial.HasInline" />)且自己有用户名时,
    /// 原样返回 <paramref name="profile" />;否则返回一份填好用户名与认证材料的副本 ——
    /// 手输过材料的,只补用户名,材料仍用手输的那份。
    /// </returns>
    /// <exception cref="CredentialProviderException">
    /// 凭据取不到或不完整。消息面向用户;调用方据此退回登录框让用户手输。
    /// </exception>
    Task<SessionProfile> ResolveAsync(SessionProfile profile, CancellationToken cancellationToken = default);
}
