using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Presentation.Services;

/// <summary>连接测试的结果:是否成功,以及失败时的错误信息。</summary>
/// <param name="Success">连接测试是否成功。</param>
/// <param name="ErrorMessage">测试失败时的错误描述;成功时为 <see langword="null" />。</param>
public sealed record ConnectionTestResult(bool Success, string? ErrorMessage = null)
{
    /// <summary>
    /// 插件连接类型给出的逐步报告(实现了 <see cref="IWorkspaceConnectionInspector" /> 的工作台才有);
    /// 其余连接为 <see langword="null" />。连接对话框的右侧栏据此画「测试结果」与「发现的成员」。
    /// </summary>
    public WorkspaceProbeReport? Report { get; init; }
}
