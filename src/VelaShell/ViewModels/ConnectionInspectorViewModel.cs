using System.Collections.ObjectModel;
using ReactiveUI;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.ViewModels;

/// <summary>
/// 连接对话框右侧栏的「连接检查」部分:连接串预览、逐步的测试结果、测试时发现的成员。
/// 内容全部来自插件的 <see cref="IWorkspaceConnectionInspector" />,长相全部归宿主 ——
/// 这里把 SDK 的结构化数据翻成界面能直接绑的形状(着色片段、状态图标、语气色)。
/// </summary>
public sealed class ConnectionInspectorViewModel : ReactiveObject
{
    /// <summary>当前连接类型有没有连接检查(没有时右侧栏只剩插件声明在那里的字段)。</summary>
    public bool IsAvailable
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // ---- 连接串预览 ----

    /// <summary>预览卡片标题(插件给,如「连接字符串」)。</summary>
    public string PreviewTitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>着色片段。</summary>
    public ObservableCollection<PreviewSpanViewModel> PreviewSpans { get; } = [];

    /// <summary>预览全文(复制、页脚预览用);没有预览时为空串。</summary>
    public string PreviewText
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasPreview));
        }
    } = string.Empty;

    /// <summary>预览卡片底部的说明。</summary>
    public string PreviewNote
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasPreviewNote));
        }
    } = string.Empty;

    /// <summary>有预览可画。</summary>
    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>有预览说明。</summary>
    public bool HasPreviewNote => PreviewNote.Length > 0;

    /// <summary>换一份预览(<see langword="null" /> = 清空)。</summary>
    /// <param name="preview">插件给的预览。</param>
    public void SetPreview(WorkspaceConnectionPreview? preview)
    {
        string text = preview?.Text ?? string.Empty;
        if (string.Equals(text, PreviewText, StringComparison.Ordinal)
            && string.Equals(preview?.Title ?? string.Empty, PreviewTitle, StringComparison.Ordinal))
        {
            return;
        }
        PreviewSpans.Clear();
        foreach (WorkspacePreviewSpan span in preview?.Spans ?? [])
        {
            PreviewSpans.Add(new PreviewSpanViewModel(span.Text, span.Role));
        }
        PreviewTitle = preview?.Title ?? string.Empty;
        PreviewNote = preview?.Note ?? string.Empty;
        PreviewText = text;
    }

    // ---- 测试结果 ----

    /// <summary>各步骤(进度来一条更新一条,按 <see cref="WorkspaceProbeStep.Key" /> 原地更新)。</summary>
    public ObservableCollection<ProbeStepViewModel> Steps { get; } = [];

    /// <summary>正在测试。</summary>
    public bool IsProbing
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowStepsPlaceholder));
        }
    }

    /// <summary>还没测过、也没在测:测试结果那一节显示一行说明,而不是一片空白。</summary>
    public bool ShowStepsPlaceholder => !IsProbing && Steps.Count == 0;

    /// <summary>端点列表的标题(插件给,如「发现的成员」)。</summary>
    public string EndpointsTitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>发现的端点。</summary>
    public ObservableCollection<ProbeEndpointViewModel> Endpoints { get; } = [];

    /// <summary>有端点可画(那一节才出现)。</summary>
    public bool HasEndpoints => Endpoints.Count > 0;

    /// <summary>最近一次测试的完整报告;没测过为 <see langword="null" />。</summary>
    public WorkspaceProbeReport? LastReport
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>开始一次测试:清掉上一次的结果。</summary>
    public void BeginProbe()
    {
        Steps.Clear();
        Endpoints.Clear();
        EndpointsTitle = string.Empty;
        LastReport = null;
        IsProbing = true;
        this.RaisePropertyChanged(nameof(HasEndpoints));
    }

    /// <summary>收到一条进度:同键的那一行原地更新,没有就追加。</summary>
    /// <param name="step">步骤。</param>
    public void Apply(WorkspaceProbeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        ProbeStepViewModel? existing = Steps.FirstOrDefault(row => string.Equals(row.Key, step.Key, StringComparison.Ordinal));
        if (existing is null)
        {
            Steps.Add(new ProbeStepViewModel(step));
        }
        else
        {
            existing.Update(step);
        }
        this.RaisePropertyChanged(nameof(ShowStepsPlaceholder));
    }

    /// <summary>测试结束:以报告为准补齐各步骤与端点。</summary>
    /// <param name="report">报告;老式探测没有报告时为 <see langword="null" />。</param>
    public void Complete(WorkspaceProbeReport? report)
    {
        IsProbing = false;
        LastReport = report;
        if (report is null)
        {
            this.RaisePropertyChanged(nameof(ShowStepsPlaceholder));
            return;
        }
        foreach (WorkspaceProbeStep step in report.Steps)
        {
            Apply(step);
        }
        // 报告里没有的步骤是进度里报过、最后又被插件丢掉的 —— 不留"进行中"的残影。
        foreach (ProbeStepViewModel orphan in Steps.Where(row => report.Steps.All(step => step.Key != row.Key)
                                                                   && row.State == WorkspaceProbeState.Running).ToList())
        {
            Steps.Remove(orphan);
        }
        Endpoints.Clear();
        foreach (WorkspaceProbeEndpoint endpoint in report.Endpoints)
        {
            Endpoints.Add(new ProbeEndpointViewModel(endpoint));
        }
        EndpointsTitle = report.EndpointsTitle ?? string.Empty;
        this.RaisePropertyChanged(nameof(HasEndpoints));
        this.RaisePropertyChanged(nameof(ShowStepsPlaceholder));
    }

    /// <summary>换了连接类型 / 表单清空:整块复位。</summary>
    public void Reset()
    {
        SetPreview(null);
        ClearResults();
    }

    /// <summary>只清测试结果(步骤与发现的端点),连接串预览留着 —— 换了变体时用。</summary>
    public void ClearResults()
    {
        Steps.Clear();
        Endpoints.Clear();
        EndpointsTitle = string.Empty;
        LastReport = null;
        IsProbing = false;
        this.RaisePropertyChanged(nameof(HasEndpoints));
        this.RaisePropertyChanged(nameof(ShowStepsPlaceholder));
    }
}

/// <summary>预览里的一段:文字 + 角色(视图按角色着色)。</summary>
/// <param name="Text">文字。</param>
/// <param name="Role">角色。</param>
public sealed record PreviewSpanViewModel(string Text, WorkspacePreviewRole Role)
{
    /// <summary>协议头 / 普通标点(三级字)。</summary>
    public bool IsMuted => Role is WorkspacePreviewRole.Scheme or WorkspacePreviewRole.Plain or WorkspacePreviewRole.Key;

    /// <summary>被遮住的机密。</summary>
    public bool IsSecret => Role == WorkspacePreviewRole.Secret;

    /// <summary>用户名。</summary>
    public bool IsUser => Role == WorkspacePreviewRole.User;

    /// <summary>主机。</summary>
    public bool IsHost => Role == WorkspacePreviewRole.Host;

    /// <summary>路径 / 库名。</summary>
    public bool IsPath => Role == WorkspacePreviewRole.Path;

    /// <summary>参数值。</summary>
    public bool IsValue => Role == WorkspacePreviewRole.Value;
}

/// <summary>测试结果的一行。</summary>
public sealed class ProbeStepViewModel : ReactiveObject
{
    /// <summary>由一条进度建。</summary>
    /// <param name="step">步骤。</param>
    public ProbeStepViewModel(WorkspaceProbeStep step)
    {
        Key = step.Key;
        Update(step);
    }

    /// <summary>稳定标识。</summary>
    public string Key { get; }

    /// <summary>标题。</summary>
    public string Title
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>说明(成员可达数、失败原因);没有为空串。</summary>
    public string Detail
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasDetail));
        }
    } = string.Empty;

    /// <summary>有说明。</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>耗时文字(<c>12 ms</c>);没有为空串。</summary>
    public string Elapsed
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>状态。</summary>
    public WorkspaceProbeState State
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsPassed));
            this.RaisePropertyChanged(nameof(IsWarning));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsRunning));
            this.RaisePropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>通过。</summary>
    public bool IsPassed => State == WorkspaceProbeState.Passed;

    /// <summary>有需要注意的地方。</summary>
    public bool IsWarning => State == WorkspaceProbeState.Warning;

    /// <summary>失败。</summary>
    public bool IsFailed => State == WorkspaceProbeState.Failed;

    /// <summary>进行中。</summary>
    public bool IsRunning => State == WorkspaceProbeState.Running;

    /// <summary>没轮到 / 被跳过。</summary>
    public bool IsIdle => State is WorkspaceProbeState.Pending or WorkspaceProbeState.Skipped;

    /// <summary>用一条新进度更新。</summary>
    /// <param name="step">步骤。</param>
    public void Update(WorkspaceProbeStep step)
    {
        Title = step.Title;
        Detail = step.Detail ?? string.Empty;
        Elapsed = step.ElapsedMs is { } ms ? $"{ms} ms" : string.Empty;
        State = step.State;
    }
}

/// <summary>「发现的成员」的一行。</summary>
/// <param name="Endpoint">端点。</param>
public sealed record ProbeEndpointViewModel(WorkspaceProbeEndpoint Endpoint)
{
    /// <summary>地址。</summary>
    public string Address => Endpoint.Address;

    /// <summary>角色。</summary>
    public string Role => Endpoint.Role ?? string.Empty;

    /// <summary>有角色。</summary>
    public bool HasRole => Role.Length > 0;

    /// <summary>右侧小字。</summary>
    public string Detail => Endpoint.Detail ?? string.Empty;

    /// <summary>成功色(主节点)。</summary>
    public bool IsSuccess => Endpoint.Tone == ProtocolTone.Success;

    /// <summary>信息色(从节点)。</summary>
    public bool IsInfo => Endpoint.Tone == ProtocolTone.Info;

    /// <summary>警告色。</summary>
    public bool IsWarning => Endpoint.Tone == ProtocolTone.Warning;

    /// <summary>危险色。</summary>
    public bool IsDanger => Endpoint.Tone == ProtocolTone.Danger;
}
