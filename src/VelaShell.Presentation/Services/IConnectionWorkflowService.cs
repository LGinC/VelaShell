using VelaShell.Core.Models;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Presentation.Services;

/// <summary>
/// 插件连接类型的连通性探针。
/// <para>
/// 插件连接(S3、Redis 之类)握手完全不是 SSH,拿 SSH 去试只会连出一个超时。
/// 但真正开一次插件会话要用到界面层才有的东西(SSH 隧道链路、凭据解密),
/// 所以这里只留一个可挂的委托,由界面层在启动时接线 —— 与
/// <c>PluginProtocolRegistry.ConnectionProposalHandler</c> 同一个套路。
/// </para>
/// <para>
/// 连接类型实现了 <see cref="IWorkspaceConnectionInspector" /> 时,探针交给它逐步检查并返回报告
/// (<paramref name="progress" /> 收逐步进度);否则按配置真的开一次插件会话随即关掉,返回 <see langword="null" />。
/// </para>
/// </summary>
/// <param name="profile">要测的连接配置(凭据已解密)。</param>
/// <param name="progress">逐步进度;可为 <see langword="null" />。</param>
/// <param name="cancellationToken">取消令牌。</param>
/// <returns>
/// 逐步报告;老式探测(开一次再关掉)成功时为 <see langword="null" />。
/// 老式探测失败抛异常,由调用方翻成界面上的原因;逐步检查的失败写在报告里,不抛。
/// </returns>
public delegate Task<WorkspaceProbeReport?> PluginConnectionProbe(
    SessionProfile profile,
    IProgress<WorkspaceProbeStep>? progress,
    CancellationToken cancellationToken);

/// <summary>连接工作流服务:统一管理会话配置的读取/保存、连接测试、建立与断开。</summary>
public interface IConnectionWorkflowService
{
    /// <summary>
    /// 插件连接类型的探针。界面层接线;没接线时插件配置的「测试」会明确说"测不了",
    /// 而不是拿 SSH 去撞插件端口撞出一个超时。
    /// </summary>
    PluginConnectionProbe? PluginProbe { get; set; }

    /// <summary>获取已保存的全部会话配置。</summary>
    Task<IReadOnlyList<SessionProfile>> GetSavedProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>保存会话配置,返回持久化后的结果。</summary>
    Task<SessionProfile> SaveProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>对指定配置执行连接测试,返回测试结果。</summary>
    Task<ConnectionTestResult> TestConnectionAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// 对指定配置执行连接测试,并把插件连接检查的逐步进度报给 <paramref name="progress" />
    /// (连接对话框的右侧栏据此逐行点亮「测试结果」)。非插件连接与上一个重载完全一样。
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(
        SessionProfile profile,
        IProgress<WorkspaceProbeStep>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>按指定配置建立 SSH 连接,返回已连接的会话。</summary>
    Task<SshSession> ConnectProfileAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>断开指定会话。</summary>
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
