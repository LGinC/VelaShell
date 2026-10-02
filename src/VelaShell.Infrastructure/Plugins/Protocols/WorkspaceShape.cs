using VelaShell.Core.Models;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Infrastructure.Plugins.Protocols;

/// <summary>
/// 一种工作台连接类型在**当前取值下**的形态:描述符上的默认值,被适用的
/// <see cref="WorkspaceVariant" /> 逐项覆盖之后的结果。
/// <para>
/// 变体是 SDK 1.3.1 就有的契约:同一个页签按某个字段(MongoDB 的「主机列表 / SRV / 连接字符串」)
/// 切换端口、"主机"一栏的含义与能力位。连接对话框、匿名判定与隧道判定都得按**同一份**形态走 ——
/// 只在对话框里认变体,用户选了 SRV(不提供隧道)却仍会被建一条隧道。所以收成一个类型,三处共用。
/// </para>
/// </summary>
/// <param name="DefaultPort">默认端口。</param>
/// <param name="HostLabel">"主机"一栏的标签;<see langword="null" /> 用宿主默认。</param>
/// <param name="HostPlaceholder">"主机"一栏的占位提示。</param>
/// <param name="UsernameLabel">"用户名"一栏的标签。</param>
/// <param name="PasswordLabel">"密码"一栏的标签。</param>
/// <param name="Features">能力位(变体给了就整体替换,不按位合并)。</param>
public sealed record WorkspaceShape(
    int DefaultPort,
    string? HostLabel,
    string? HostPlaceholder,
    string? UsernameLabel,
    string? PasswordLabel,
    WorkspaceFeatures Features)
{
    /// <summary>按表单当前取值求形态。</summary>
    /// <param name="descriptor">连接类型描述。</param>
    /// <param name="lookup">按键取当前值;键不存在时返回 <see langword="null" />。</param>
    /// <returns>形态。</returns>
    public static WorkspaceShape Of(WorkspaceDescriptor descriptor, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(lookup);
        WorkspaceVariant? variant = descriptor.ResolveVariant(lookup);
        return new(
            variant?.DefaultPort ?? descriptor.DefaultPort,
            variant?.HostLabel ?? descriptor.HostLabel,
            variant?.HostPlaceholder ?? descriptor.HostPlaceholder,
            variant?.UsernameLabel ?? descriptor.UsernameLabel,
            variant?.PasswordLabel ?? descriptor.PasswordLabel,
            variant?.Features ?? descriptor.Features);
    }

    /// <summary>按一条已保存的配置求形态(缺的字段取声明的默认值,与打开会话时的设置口径一致)。</summary>
    /// <param name="descriptor">连接类型描述。</param>
    /// <param name="profile">连接配置。</param>
    /// <returns>形态。</returns>
    public static WorkspaceShape Of(WorkspaceDescriptor descriptor, SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Of(descriptor, key =>
            profile.PluginSettings?.GetValueOrDefault(key)
            ?? descriptor.Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.Ordinal))?.DefaultValue);
    }
}
