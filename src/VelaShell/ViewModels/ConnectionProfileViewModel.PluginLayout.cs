using System.Collections.ObjectModel;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>插件字段的一节(节标题 + 这一节的字段)。</summary>
/// <param name="title">节标题。</param>
/// <param name="fields">字段(按声明顺序)。</param>
public sealed class PluginFieldSectionViewModel(string title, IReadOnlyList<PluginProtocolFieldViewModel> fields) : ReactiveObject
{
    /// <summary>节标题。</summary>
    public string Title { get; } = title;

    /// <summary>字段。</summary>
    public IReadOnlyList<PluginProtocolFieldViewModel> Fields { get; } = fields;

    /// <summary>这一节此刻有没有可见的字段(全藏起来时连标题一起收起)。</summary>
    public bool HasVisibleFields
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>按各字段的可见性重算 <see cref="HasVisibleFields" />。</summary>
    internal void Refresh() => HasVisibleFields = Fields.Any(static f => f.IsRowVisible);
}

/// <summary>
/// 插件字段的版式(分节、并入宿主的节、右侧栏)、连接类型的变体,以及右侧栏的连接检查。
/// <para>
/// 形状由插件声明(<see cref="ProtocolSettingField.Section" /> / <see cref="ProtocolSettingField.Width" /> /
/// <see cref="ProtocolSettingField.Presentation" /> / <see cref="ProtocolSettingField.Placement" />),
/// 画法全部归宿主:插件交的是数据,不是控件 —— 对话框里的口令框、别的连接类型的表单,它一样也碰不到。
/// </para>
/// </summary>
public partial class ConnectionProfileViewModel
{
    /// <summary>右侧栏宽(设计稿 10 的 Side)。</summary>
    internal const double AsideWidth = 320;

    /// <summary>对话框的基础宽度(卡片 760 + 外边距)。</summary>
    internal const double BaseDialogWidth = 792;

    /// <summary>插件描述直接转来的表单(未套变体);变体每次都从它重算。</summary>
    private PluginConnectionForm? _pluginBaseForm;

    /// <summary>连接串预览的去抖定时器:一次输入一个字段会连发好几个属性变化。</summary>
    private DispatcherTimer? _previewTimer;

    /// <summary>进行中的连接检查;再点一次测试或关掉对话框时取消。</summary>
    private CancellationTokenSource? _probeCts;

    /// <summary>右侧栏的连接检查(连接串预览、逐步测试结果、发现的成员)。</summary>
    public ConnectionInspectorViewModel Inspector { get; } = new();

    /// <summary>复制连接串(预览卡片右上角)。</summary>
    public ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> CopyPreviewCommand =>
        field ??= ReactiveCommand.CreateFromTask(async () =>
        {
            if (CopyToClipboard is { } copy && Inspector.PreviewText is { Length: > 0 } text)
            {
                await copy(text).ConfigureAwait(true);
            }
        });

    /// <summary>并进宿主「基本」那一节的插件字段(环境标记之类)。</summary>
    public ObservableCollection<PluginProtocolFieldViewModel> PluginBasicFields { get; } = [];

    /// <summary>并进「连接目标」、排在主机那一行**之上**的插件字段(连接类型的变体键:它决定主机那一行长什么样)。</summary>
    public ObservableCollection<PluginProtocolFieldViewModel> PluginTargetLeadFields { get; } = [];

    /// <summary>并进「连接目标」、排在主机那一行之后的插件字段(副本集成员、副本集名…)。</summary>
    public ObservableCollection<PluginProtocolFieldViewModel> PluginTargetFields { get; } = [];

    /// <summary>并进「身份验证」的插件字段(认证机制、认证库),排在用户名 / 口令之前 —— 先选机制,再填账号。</summary>
    public ObservableCollection<PluginProtocolFieldViewModel> PluginAuthFields { get; } = [];

    /// <summary>插件自己的各节(含没声明节的那些字段组成的「<i>连接类型</i> 设置」)。</summary>
    public ObservableCollection<PluginFieldSectionViewModel> PluginFieldSections { get; } = [];

    /// <summary>「高级」页的插件字段。</summary>
    public ObservableCollection<PluginProtocolFieldViewModel> PluginAdvancedFields { get; } = [];

    /// <summary>右侧栏里的插件字段(按节)。</summary>
    public ObservableCollection<PluginFieldSectionViewModel> PluginAsideSections { get; } = [];

    /// <summary>主机那一行之上有没有插件字段。</summary>
    public bool HasPluginTargetLeadFields => PluginTargetLeadFields.Count > 0;

    /// <summary>主机那一行之后有没有插件字段。</summary>
    public bool HasPluginTargetFields => PluginTargetFields.Count > 0;

    /// <summary>「身份验证」里有没有插件字段。</summary>
    public bool HasPluginAuthFields => PluginAuthFields.Count > 0;

    /// <summary>「基本」那一节挪到表单最上面(有插件字段并进去时;否则它照旧在末尾,叫「整理」)。</summary>
    public bool ShowBasicOnTop => IsPluginSelected && PluginBasicFields.Count > 0;

    /// <summary>末尾的「整理」那一节(基本挪到上面时它就不在末尾了)。</summary>
    public bool ShowOrganizeAtBottom => !ShowBasicOnTop;

    /// <summary>右侧栏在不在:连接类型有连接检查,或插件把字段放进了右侧栏。</summary>
    public bool HasPluginAside => IsPluginSelected && (Inspector.IsAvailable || PluginAsideSections.Count > 0);

    /// <summary>对话框宽度:有右侧栏时加宽一栏,而不是把表单挤窄 —— 表单区的宽度是按 SSH 的四段认证方式定的。</summary>
    public double DialogWidth => HasPluginAside ? BaseDialogWidth + AsideWidth : BaseDialogWidth;

    /// <summary>
    /// 「跳板主机」那一行(SSH 的 ProxyJump)。插件自己声明了 SSH 隧道字段时收起 ——
    /// 工作台连接走的是那个字段,两个"跳板"摆在一起,用户只会不知道该填哪个。
    /// </summary>
    public bool ShowJumpHost => !(IsPluginSelected && PluginFields.Any(static f => f.Field.Kind == ProtocolSettingKind.SshSession));

    /// <summary>插件协议的用户名与口令并排(它们没有 SSH 那一组认证方式要摆在用户名旁边)。</summary>
    public bool CredentialsSideBySide => IsPluginSelected && ShowPasswordField;

    /// <summary>用户名与口令上下排(内建协议)。</summary>
    public bool CredentialsStacked => !CredentialsSideBySide;

    /// <summary>上下排时单独一行的口令框(并排时口令画在用户名旁边)。</summary>
    public bool ShowStackedPassword => ShowPasswordField && CredentialsStacked;

    /// <summary>主机那一行旁边的角色徽章(测试连接后由连接检查给出,如 PRIMARY);没有为空串。</summary>
    public string HostRoleBadge
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasHostRoleBadge));
        }
    } = string.Empty;

    /// <summary>主机那一行旁边有没有角色徽章。</summary>
    public bool HasHostRoleBadge => HostRoleBadge.Length > 0;

    /// <summary>角色徽章是成功色(主节点)。</summary>
    public bool IsHostRoleSuccess
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>测试通过时反馈条上的那句话:有插件的结论(<c>连接成功 · 38 ms</c>)就用它。</summary>
    public string TestSuccessText => Inspector.LastReport?.Summary is { Length: > 0 } summary
        ? summary
        : Strings.Get("Profile_TestSuccess");

    /// <summary>
    /// 按插件声明把字段分进各个容器。字段视图模型只建一份,几个容器拿的是同一批实例 ——
    /// 值、可见性、校验都只有一处真相。
    /// </summary>
    private void RebuildPluginLayout()
    {
        PluginBasicFields.Clear();
        PluginTargetLeadFields.Clear();
        PluginTargetFields.Clear();
        PluginAuthFields.Clear();
        PluginFieldSections.Clear();
        PluginAdvancedFields.Clear();
        PluginAsideSections.Clear();
        string? variantKey = _pluginBaseForm?.Workspace?.VariantKey;
        var sections = new List<(string Title, List<PluginProtocolFieldViewModel> Fields)>();
        var asides = new List<(string Title, List<PluginProtocolFieldViewModel> Fields)>();
        var general = new List<PluginProtocolFieldViewModel>();

        static List<PluginProtocolFieldViewModel> SectionOf(List<(string Title, List<PluginProtocolFieldViewModel> Fields)> list, string title)
        {
            foreach ((string existing, List<PluginProtocolFieldViewModel> fields) in list)
            {
                if (string.Equals(existing, title, StringComparison.Ordinal))
                {
                    return fields;
                }
            }
            var created = new List<PluginProtocolFieldViewModel>();
            list.Add((title, created));
            return created;
        }

        foreach (PluginProtocolFieldViewModel field in PluginFields)
        {
            field.DefaultPortProvider = () => _pluginForm?.DefaultPort ?? Port;
            if (field.IsAside)
            {
                SectionOf(asides, field.Section is { Length: > 0 } title && !title.StartsWith("vela:", StringComparison.Ordinal)
                    ? title
                    : PluginSectionTitle).Add(field);
                continue;
            }
            if (field.IsAdvanced)
            {
                PluginAdvancedFields.Add(field);
                continue;
            }
            switch (field.Section)
            {
                case ProtocolSettingSection.Basic:
                    PluginBasicFields.Add(field);
                    break;
                case ProtocolSettingSection.Target:
                    (string.Equals(field.Key, variantKey, StringComparison.Ordinal) ? PluginTargetLeadFields : PluginTargetFields).Add(field);
                    break;
                case ProtocolSettingSection.Authentication:
                    PluginAuthFields.Add(field);
                    break;
                case { Length: > 0 } title when !title.StartsWith("vela:", StringComparison.Ordinal):
                    SectionOf(sections, title).Add(field);
                    break;
                default:
                    // 没声明节(或是宿主还不认识的保留节):照旧进「<连接类型> 设置」。
                    general.Add(field);
                    break;
            }
        }
        foreach ((string title, List<PluginProtocolFieldViewModel> fields) in sections)
        {
            PluginFieldSections.Add(new PluginFieldSectionViewModel(title, fields));
        }
        if (general.Count > 0)
        {
            PluginFieldSections.Add(new PluginFieldSectionViewModel(PluginSectionTitle, general));
        }
        foreach ((string title, List<PluginProtocolFieldViewModel> fields) in asides)
        {
            PluginAsideSections.Add(new PluginFieldSectionViewModel(title, fields));
        }
        Inspector.IsAvailable = IsPluginSelected && _pluginBaseForm?.Inspector is not null;
        RefreshSectionVisibility();
        RaiseLayoutChanged();
    }

    /// <summary>各节的"有没有可见字段"跟着字段可见性走。</summary>
    private void RefreshSectionVisibility()
    {
        foreach (PluginFieldSectionViewModel section in PluginFieldSections.Concat(PluginAsideSections))
        {
            section.Refresh();
        }
    }

    private void RaiseLayoutChanged()
    {
        this.RaisePropertyChanged(nameof(HasPluginTargetLeadFields));
        this.RaisePropertyChanged(nameof(HasPluginTargetFields));
        this.RaisePropertyChanged(nameof(HasPluginAuthFields));
        this.RaisePropertyChanged(nameof(ShowBasicOnTop));
        this.RaisePropertyChanged(nameof(ShowOrganizeAtBottom));
        this.RaisePropertyChanged(nameof(HasPluginAside));
        this.RaisePropertyChanged(nameof(DialogWidth));
        this.RaisePropertyChanged(nameof(ShowJumpHost));
        this.RaisePropertyChanged(nameof(CredentialsSideBySide));
        this.RaisePropertyChanged(nameof(CredentialsStacked));
        this.RaisePropertyChanged(nameof(ShowStackedPassword));
    }

    /// <summary>
    /// 按变体键的当前值重算表单形态(三格标签、端口栏、凭据栏、匿名与隧道能力)。
    /// <paramref name="followPort" /> 为真时端口跟着变体的默认端口走 —— 但只在用户没手填过端口时
    /// (端口还等于切换前那个形态的默认值),与切换页签同一套判定。
    /// </summary>
    private void ApplyVariantShape(bool followPort)
    {
        if (_pluginBaseForm is not { } baseForm)
        {
            return;
        }
        int previousDefault = _pluginForm?.DefaultPort ?? baseForm.DefaultPort;
        _pluginForm = baseForm.ApplyVariant(PluginFieldValue);
        if (followPort && _pluginForm.DefaultPort > 0 && Port == previousDefault && previousDefault != _pluginForm.DefaultPort)
        {
            Port = _pluginForm.DefaultPort;
        }
        RaisePluginLabelsChanged();
        this.RaisePropertyChanged(nameof(CredentialsSideBySide));
        this.RaisePropertyChanged(nameof(CredentialsStacked));
        this.RaisePropertyChanged(nameof(ShowStackedPassword));
    }

    /// <summary>插件字段的值变了:变体键要重算形态,任何字段都可能改变连接串。</summary>
    private void OnPluginFieldValueChanged(PluginProtocolFieldViewModel field)
    {
        if (_pluginBaseForm?.Workspace?.VariantKey is { } variantKey
            && string.Equals(field.Key, variantKey, StringComparison.Ordinal))
        {
            ApplyVariantShape(followPort: true);
            // 上一次测试量的是另一种连接形态:步骤、发现的成员与主机行上的角色标记一并作废,进行中的那次也取消。
            CancelProbe();
            Inspector.ClearResults();
            ClearRoleBadges();
            LastTestSucceeded = null;
        }
        SchedulePreview();
    }

    /// <summary>预览去抖:一次输入往往连发好几个属性变化,没必要每个都去问一遍插件。</summary>
    private void SchedulePreview()
    {
        if (!Inspector.IsAvailable)
        {
            return;
        }
        if (_previewTimer is null)
        {
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _previewTimer.Tick += (_, _) =>
            {
                _previewTimer.Stop();
                RefreshPreview();
            };
        }
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>立刻向插件要一次预览(插件抛异常时当作没有预览,见 <see cref="PluginWorkspaceLauncher.Preview" />)。</summary>
    internal void RefreshPreview()
    {
        if (!Inspector.IsAvailable || _pluginBaseForm is not { Inspector: { } inspector, Workspace: { } descriptor })
        {
            Inspector.SetPreview(null);
        }
        else
        {
            Inspector.SetPreview(PluginWorkspaceLauncher.Preview(inspector, descriptor, BuildProfile()));
        }
        this.RaisePropertyChanged(nameof(EndpointPreview));
    }

    /// <summary>
    /// 有连接检查的测试连接:逐步进度画进右侧栏,结束后按报告给主机行与主机列表标上角色。
    /// </summary>
    private async Task TestWithInspectorAsync(IConnectionWorkflowService service)
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        Inspector.BeginProbe();
        ClearRoleBadges();
        ConnectionTestResult result;
        try
        {
            result = await service.TestConnectionAsync(BuildProfile(), new UiProgress(Inspector.Apply), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Inspector.Complete(null);
            return;
        }
        if (cts.IsCancellationRequested)
        {
            return;
        }
        Inspector.Complete(result.Report);
        ApplyRoleBadges(result.Report?.Endpoints ?? []);
        LastTestSucceeded = result.Success;
        ErrorMessage = result.ErrorMessage;
        this.RaisePropertyChanged(nameof(TestSuccessText));
    }

    private void ClearRoleBadges() => ApplyRoleBadges([]);

    /// <summary>主机那一行与各主机列表行:地址对得上发现的端点,就标上它的角色。</summary>
    private void ApplyRoleBadges(IReadOnlyList<WorkspaceProbeEndpoint> endpoints)
    {
        string host = _host.Trim();
        string address = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]:{_port}" : $"{host}:{_port}";
        WorkspaceProbeEndpoint? match = endpoints.FirstOrDefault(endpoint => PluginHostListViewModel.SameAddress(endpoint.Address, address));
        HostRoleBadge = match?.Role ?? string.Empty;
        IsHostRoleSuccess = match?.Tone == ProtocolTone.Success;
        foreach (PluginProtocolFieldViewModel field in PluginFields)
        {
            field.HostList?.ApplyBadges(endpoints);
        }
    }

    /// <summary>取消进行中的连接检查(对话框关闭时)。</summary>
    private void CancelProbe()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = null;
        _previewTimer?.Stop();
    }

    /// <summary>
    /// 把进度送回界面线程。<see cref="Progress{T}" /> 认的是构造时的同步上下文,而测试里与插件的线程池续体上
    /// 都不一定有;这里直接认 Avalonia 的界面线程。
    /// </summary>
    private sealed class UiProgress(Action<WorkspaceProbeStep> apply) : IProgress<WorkspaceProbeStep>
    {
        public void Report(WorkspaceProbeStep value)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                apply(value);
            }
            else
            {
                Dispatcher.UIThread.Post(() => apply(value));
            }
        }
    }
}
