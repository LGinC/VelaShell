using VelaShell.Core.Credentials;

namespace VelaShell.Core.Data;

/// <summary>
/// 共享凭据(#550)的持久化边界。读出的密码与私钥口令已解密为明文,落盘时加密。
/// </summary>
public interface ISharedCredentialRepository
{
    /// <summary>
    /// 有凭据被保存或删除。界面据此刷新:会话树与连接配置页里显示的是凭据名,
    /// 删除凭据还会顺带改写引用它的连接(见 <c>SharedCredentialService</c>)。
    /// </summary>
    event EventHandler? Changed;

    /// <summary>返回全部共享凭据,按名称排序。</summary>
    Task<List<SharedCredential>> GetAllAsync();

    /// <summary>按 Id 取一条;不存在时返回 <c>null</c>。</summary>
    Task<SharedCredential?> GetAsync(Guid id);

    /// <summary>插入或更新(以 Id 为主键)。</summary>
    Task SaveAsync(SharedCredential credential);

    /// <summary>删除一条。不处理引用它的连接 —— 那是调用方的事,见 <c>SharedCredentialService.DeleteAsync</c>。</summary>
    Task DeleteAsync(Guid id);
}
