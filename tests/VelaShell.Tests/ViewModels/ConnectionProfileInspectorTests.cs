using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Controls;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接对话框的插件版式(分节、并入宿主的节、右侧栏)、工作台变体与连接检查(连接串预览、逐步测试结果)。
/// </summary>
[TestClass]
public sealed class ConnectionProfileInspectorTests
{
    private const string TypeId = "test.mongo";

    /// <summary>照 MongoDB 插件的形状造一个连接类型:每种新版式各占一个字段。</summary>
    private static WorkspaceDescriptor Descriptor() => new()
    {
        Id = TypeId,
        DisplayName = "MongoDB",
        DefaultPort = 27017,
        Features = WorkspaceFeatures.AnonymousAccess | WorkspaceFeatures.SshTunnel,
        VariantKey = "topology",
        Variants =
        [
            new() { Value = "srv", HostLabel = "SRV 域名", Features = WorkspaceFeatures.AnonymousAccess | WorkspaceFeatures.NoEndpoint }
        ],
        Fields =
        [
            new()
            {
                Key = "topology", Label = "服务器", Kind = ProtocolSettingKind.Choice, DefaultValue = "hosts",
                Section = ProtocolSettingSection.Target, Presentation = ProtocolSettingPresentation.Segmented,
                Choices = [new("hosts", "主机列表"), new("srv", "SRV 记录")]
            },
            new()
            {
                Key = "hosts", Label = "其余成员", Kind = ProtocolSettingKind.HostList, Section = ProtocolSettingSection.Target,
                VisibleWhen = new("topology", "hosts")
            },
            new()
            {
                Key = "environment", Label = "环境标记", Kind = ProtocolSettingKind.Choice, DefaultValue = "dev",
                Section = ProtocolSettingSection.Basic, Presentation = ProtocolSettingPresentation.Chips,
                Choices = [new("dev", "开发") { Tone = ProtocolTone.Success }, new("prod", "生产") { Tone = ProtocolTone.Danger }]
            },
            new()
            {
                Key = "mechanism", Label = "机制", Kind = ProtocolSettingKind.Choice, DefaultValue = "SCRAM",
                Section = ProtocolSettingSection.Authentication, Width = ProtocolFieldWidth.Half,
                Choices = [new("SCRAM", "SCRAM")]
            },
            new()
            {
                Key = "tls", Label = "TLS", Kind = ProtocolSettingKind.Boolean, DefaultValue = "false",
                Section = "安全通道", Width = ProtocolFieldWidth.Half, Presentation = ProtocolSettingPresentation.Card
            },
            new()
            {
                Key = "jump", Label = "SSH 跳板", Kind = ProtocolSettingKind.SshSession,
                Section = "安全通道", Width = ProtocolFieldWidth.Half, Presentation = ProtocolSettingPresentation.Card
            },
            new() { Key = "plain", Label = "Plain" },
            new() { Key = "appName", Label = "appName", IsAdvanced = true, DefaultValue = "velashell" },
            new()
            {
                Key = "readOnly", Label = "只读", Kind = ProtocolSettingKind.Boolean, DefaultValue = "false",
                Section = "安全策略", Placement = ProtocolFieldPlacement.Aside
            },
            new() { Key = "secret", Label = "Secret", IsSecret = true }
        ]
    };

    /// <summary>连接检查替身:预览把草稿原样拼出来(看得见口令有没有漏进去);测试按配置好的报告回。</summary>
    private sealed class InspectingProvider : IWorkspaceProvider, IWorkspaceConnectionInspector
    {
        public WorkspaceConnectRequest? LastDraft { get; private set; }

        public Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public WorkspaceConnectionPreview? Preview(WorkspaceConnectRequest draft)
        {
            LastDraft = draft;
            return draft.Host.Length == 0
                ? null
                : new WorkspaceConnectionPreview
                {
                    Title = "连接字符串",
                    Spans =
                    [
                        new("x://", WorkspacePreviewRole.Scheme),
                        new(draft.Username, WorkspacePreviewRole.User),
                        new("@", WorkspacePreviewRole.Plain),
                        new($"{draft.Host}:{draft.Port}", WorkspacePreviewRole.Host),
                        new($"?pw={draft.Password}&secret={draft.Settings.GetValueOrDefault("secret") ?? "-"}", WorkspacePreviewRole.Value)
                    ],
                    Note = "密码不会写进连接串"
                };
        }

        public Task<WorkspaceProbeReport> ProbeAsync(
            WorkspaceConnectRequest request,
            IProgress<WorkspaceProbeStep>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static async Task<(ConnectionProfileViewModel Vm, InspectingProvider Provider, IDisposable Handle)> OpenAsync(
        IConnectionWorkflowService? workflow = null)
    {
        var registry = new PluginProtocolRegistry();
        var provider = new InspectingProvider();
        IDisposable handle = registry.RegisterWorkspace(TypeId, Descriptor(), provider);
        var vm = new ConnectionProfileViewModel(connectionWorkflowService: workflow, protocolRegistry: registry)
        {
            Host = "10.0.0.1",
            Username = "ops"
        };
        await vm.SelectPluginProtocolCommand.Execute(TypeId).FirstAsync();
        return (vm, provider, handle);
    }

    [TestMethod]
    public async Task Fields_AreSortedIntoTheHostSectionsTheirPluginDeclared()
    {
        (ConnectionProfileViewModel vm, _, IDisposable handle) = await OpenAsync();
        using (handle)
        {
            CollectionAssert.AreEqual(new[] { "environment" }, vm.PluginBasicFields.Select(static f => f.Key).ToArray());
            CollectionAssert.AreEqual(new[] { "topology" }, vm.PluginTargetLeadFields.Select(static f => f.Key).ToArray(),
                "the variant key sits above the host row: it decides what that row looks like");
            CollectionAssert.AreEqual(new[] { "hosts" }, vm.PluginTargetFields.Select(static f => f.Key).ToArray());
            CollectionAssert.AreEqual(new[] { "mechanism" }, vm.PluginAuthFields.Select(static f => f.Key).ToArray());
            Assert.AreEqual(2, vm.PluginFieldSections.Count);
            Assert.AreEqual(vm.PluginSectionTitle, vm.PluginFieldSections[1].Title);
            Assert.AreEqual("安全通道", vm.PluginFieldSections[0].Title);
            CollectionAssert.AreEqual(new[] { "tls", "jump" }, vm.PluginFieldSections[0].Fields.Select(static f => f.Key).ToArray());
            CollectionAssert.AreEqual(new[] { "plain", "secret" }, vm.PluginFieldSections[1].Fields.Select(static f => f.Key).ToArray(),
                "fields without a section keep going to the default plugin section");
            CollectionAssert.AreEqual(new[] { "appName" }, vm.PluginAdvancedFields.Select(static f => f.Key).ToArray());
            Assert.AreEqual("安全策略", vm.PluginAsideSections.Single().Title);
            Assert.AreEqual("readOnly", vm.PluginAsideSections.Single().Fields.Single().Key);

            Assert.IsTrue(vm.ShowBasicOnTop, "a plugin field in Basic moves the organise section to the top");
            Assert.IsTrue(vm.HasPluginAside);
            Assert.AreEqual(ConnectionProfileViewModel.BaseDialogWidth + ConnectionProfileViewModel.AsideWidth, vm.DialogWidth);
            Assert.IsFalse(vm.ShowJumpHost, "the plugin's own SSH field replaces the host's ProxyJump row");
            Assert.IsTrue(vm.CredentialsSideBySide);
            Assert.AreEqual("1", vm.AdvancedBadge, "aside fields never count towards the Advanced badge");
            Assert.IsTrue(vm.PluginAsideSections.Single().Fields.Single().IsRowVisible, "the aside panel shows on every page");
            vm.SelectedSection = ConnectionProfileSection.Advanced;
            Assert.IsTrue(vm.PluginAsideSections.Single().Fields.Single().IsRowVisible);
        }
    }

    [TestMethod]
    public async Task Layout_UnitsAndPresentations_FollowTheDeclaration()
    {
        (ConnectionProfileViewModel vm, _, IDisposable handle) = await OpenAsync();
        using (handle)
        {
            PluginProtocolFieldViewModel topology = vm.PluginTargetLeadFields.Single();
            Assert.IsTrue(topology.IsSegmented);
            Assert.IsFalse(topology.IsChoice, "a segmented choice is not also drawn as a dropdown");
            Assert.IsFalse(topology.ShowsLabel);

            PluginProtocolFieldViewModel environment = vm.PluginBasicFields.Single();
            Assert.IsTrue(environment.IsChips);
            Assert.IsTrue(environment.Options[1].IsDanger);
            environment.Options[1].SelectCommand.Execute().Subscribe();
            Assert.AreEqual("prod", environment.Text);
            Assert.IsTrue(environment.Options[1].IsSelected);
            Assert.IsFalse(environment.Options[0].IsSelected);

            PluginProtocolFieldViewModel tls = vm.PluginFieldSections[0].Fields[0];
            Assert.IsTrue(tls.IsToggleCard);
            Assert.IsFalse(tls.IsToggle);
            Assert.AreEqual(3, tls.FlowUnits);
            Assert.AreEqual(FieldFlowPanel.FullUnits, vm.PluginTargetFields.Single().FlowUnits);

            PluginProtocolFieldViewModel jump = vm.PluginFieldSections[0].Fields[1];
            Assert.IsTrue(jump.IsSshCard);
            Assert.IsFalse(jump.SshEnabled);
            Assert.IsTrue(jump.SshCardSessions.All(static s => s.Id.Length > 0), "the card's dropdown has no 'direct' entry — the switch says that");
            Assert.AreEqual(jump.SshCardSessions.Count > 0, jump.HasSshCardSessions);
            Assert.AreEqual(!jump.HasSshCardSessions, jump.ShowNoSshSessions, "with nothing to pick, the card says why instead of a switch that snaps back");
            Assert.IsFalse(tls.ShowNoSshSessions);
        }
    }

    [TestMethod]
    public async Task VariantKey_ReshapesTheHostRow_AndBack()
    {
        (ConnectionProfileViewModel vm, _, IDisposable handle) = await OpenAsync();
        using (handle)
        {
            Assert.IsTrue(vm.ShowPortField);
            PluginProtocolFieldViewModel topology = vm.PluginTargetLeadFields.Single();

            topology.Text = "srv";
            Assert.IsFalse(vm.ShowPortField, "the SRV variant declares NoEndpoint");
            Assert.AreEqual("SRV 域名", vm.HostLabel);
            Assert.IsFalse(vm.PluginTargetFields.Single().IsRowVisible, "the host list belongs to the host-list topology only");

            topology.Text = "hosts";
            Assert.IsTrue(vm.ShowPortField);
            Assert.AreNotEqual("SRV 域名", vm.HostLabel);
        }
    }

    [TestMethod]
    public async Task Preview_NeverSeesThePasswordOrSecretFields()
    {
        (ConnectionProfileViewModel vm, InspectingProvider provider, IDisposable handle) = await OpenAsync();
        using (handle)
        {
            vm.Password = SecureStringConvert.FromPlaintext("s3cret");
            vm.PluginFieldSections[1].Fields.Single(static f => f.Key == "secret").Text = "token";

            vm.RefreshPreview();

            Assert.IsTrue(vm.Inspector.HasPreview);
            Assert.AreEqual("x://ops@10.0.0.1:27017?pw=&secret=-", vm.Inspector.PreviewText);
            Assert.AreEqual(string.Empty, provider.LastDraft?.Password);
            Assert.IsFalse(provider.LastDraft!.Settings.ContainsKey("secret"));
            Assert.AreEqual("连接字符串", vm.Inspector.PreviewTitle);
            Assert.IsTrue(vm.Inspector.PreviewSpans.Single(static s => s.Text == "ops").IsUser);
            Assert.AreEqual(vm.Inspector.PreviewText, vm.EndpointPreview, "the footer shows the plugin's connection string");
        }
    }

    [TestMethod]
    public async Task TestConnection_FillsTheAsidePanel_AndBadgesTheRows()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        var report = new WorkspaceProbeReport
        {
            Succeeded = true,
            Summary = "连接成功 · 38 ms",
            Steps = [new("tcp", "TCP 连接", WorkspaceProbeState.Passed, "2 / 2", 2), new("perm", "权限检查", WorkspaceProbeState.Warning, "只读")],
            EndpointsTitle = "发现的成员",
            Endpoints =
            [
                new("10.0.0.1:27017", "PRIMARY", ProtocolTone.Success),
                new("10.0.0.2:27017", "SECONDARY", ProtocolTone.Info, "延迟 0.8 s")
            ]
        };
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<IProgress<WorkspaceProbeStep>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<IProgress<WorkspaceProbeStep>?>()?.Report(new("tcp", "TCP 连接", WorkspaceProbeState.Running));
                return Task.FromResult(new ConnectionTestResult(true) { Report = report });
            });
        (ConnectionProfileViewModel vm, _, IDisposable handle) = await OpenAsync(workflow);
        using (handle)
        {
            PluginHostListViewModel hosts = vm.PluginTargetFields.Single().HostList!;
            hosts.Add();
            hosts.Rows[0].Host = "10.0.0.2";
            hosts.Rows[0].Port = 27017;

            await vm.TestConnectionCommand.Execute().FirstAsync();

            Assert.IsTrue(vm.ShowTestSuccess);
            Assert.AreEqual("连接成功 · 38 ms", vm.TestSuccessText);
            CollectionAssert.AreEqual(new[] { "tcp", "perm" }, vm.Inspector.Steps.Select(static s => s.Key).ToArray(),
                "the progress row is updated in place by key, not duplicated");
            Assert.IsTrue(vm.Inspector.Steps[0].IsPassed);
            Assert.AreEqual("2 ms", vm.Inspector.Steps[0].Elapsed);
            Assert.IsTrue(vm.Inspector.Steps[1].IsWarning);
            Assert.AreEqual("发现的成员", vm.Inspector.EndpointsTitle);
            Assert.AreEqual(2, vm.Inspector.Endpoints.Count);
            Assert.AreEqual("PRIMARY", vm.HostRoleBadge);
            Assert.IsTrue(vm.IsHostRoleSuccess);
            Assert.AreEqual("SECONDARY", hosts.Rows[0].Badge);
            Assert.IsTrue(hosts.Rows[0].IsBadgeInfo);
            await workflow.DidNotReceive().TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());

            // 换了变体就是换了连接形态:上一次的测试结果与角色标记不能挂在新形态旁边。
            vm.PluginTargetLeadFields.Single().Text = "srv";
            Assert.IsEmpty(vm.Inspector.Steps);
            Assert.IsEmpty(vm.Inspector.Endpoints);
            Assert.AreEqual(string.Empty, vm.HostRoleBadge);
            Assert.IsFalse(hosts.Rows[0].HasBadge);
            Assert.IsFalse(vm.ShowTestSuccess);
            Assert.IsTrue(vm.Inspector.ShowStepsPlaceholder);
        }
    }

    [TestMethod]
    public async Task SwitchingBackToSsh_DropsTheAsidePanel()
    {
        (ConnectionProfileViewModel vm, _, IDisposable handle) = await OpenAsync();
        using (handle)
        {
            vm.RefreshPreview();
            Assert.IsTrue(vm.HasPluginAside);

            await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();

            Assert.IsFalse(vm.HasPluginAside);
            Assert.AreEqual(ConnectionProfileViewModel.BaseDialogWidth, vm.DialogWidth);
            Assert.IsFalse(vm.Inspector.HasPreview);
            Assert.IsTrue(vm.ShowJumpHost);
            Assert.IsFalse(vm.ShowBasicOnTop);
            Assert.IsTrue(vm.ShowOrganizeAtBottom);
        }
    }

    [TestMethod]
    public void HostList_RoundTripsTheCommaSeparatedValue()
    {
        var field = new PluginProtocolFieldViewModel(
            new ProtocolSettingField { Key = "hosts", Label = "Hosts", Kind = ProtocolSettingKind.HostList },
            "10.0.0.2:27017, db3.example.com:27018 ,[::1]:27019")
        {
            DefaultPortProvider = static () => 27017
        };
        PluginHostListViewModel list = field.HostList!;

        CollectionAssert.AreEqual(new[] { "10.0.0.2", "db3.example.com", "::1" }, list.Rows.Select(static r => r.Host).ToArray());
        CollectionAssert.AreEqual(new[] { 27017, 27018, 27019 }, list.Rows.Select(static r => r.Port).ToArray());

        list.Rows[1].Port = 27020;
        Assert.AreEqual("10.0.0.2:27017,db3.example.com:27020,[::1]:27019", field.Text);

        list.Add();
        Assert.AreEqual(4, list.Rows.Count);
        Assert.AreEqual(27017, list.Rows[3].Port, "a new row takes the current shape's default port");
        Assert.AreEqual("10.0.0.2:27017,db3.example.com:27020,[::1]:27019", field.Text, "an empty row is not written out as ':27017'");

        list.Rows[0].RemoveCommand.Execute().Subscribe();
        Assert.AreEqual("db3.example.com:27020,[::1]:27019", field.Text);

        field.Text = "a:1";
        Assert.AreEqual("a", list.Rows.Single().Host, "an outside change to the value rebuilds the rows");
    }

    [TestMethod]
    public void FieldFlowPanel_SplitsARowIntoSixths()
    {
        // 三个三分之一与一个整行左右两端对齐:列宽 = 份数 ×(行宽 + 间距)÷ 6 − 间距。
        double third = FieldFlowPanel.WidthOf(2, 600, 12);
        Assert.AreEqual(600, (third * 3) + (12 * 2), 0.001);
        double half = FieldFlowPanel.WidthOf(3, 600, 12);
        Assert.AreEqual(600, (half * 2) + 12, 0.001);
        Assert.AreEqual(600, FieldFlowPanel.WidthOf(FieldFlowPanel.FullUnits, 600, 12), 0.001);
    }

    [TestMethod]
    public void WorkspaceShape_AppliesTheMatchingVariantOnly()
    {
        WorkspaceDescriptor descriptor = Descriptor();
        var srv = WorkspaceShape.Of(descriptor, key => key == "topology" ? "srv" : null);
        Assert.IsTrue(srv.Features.HasFlag(WorkspaceFeatures.NoEndpoint));
        Assert.IsFalse(srv.Features.HasFlag(WorkspaceFeatures.SshTunnel), "a variant's features replace the descriptor's, they are not merged");
        Assert.AreEqual(27017, srv.DefaultPort);

        var saved = WorkspaceShape.Of(descriptor, new SessionProfile { PluginSettings = [] });
        Assert.IsTrue(saved.Features.HasFlag(WorkspaceFeatures.SshTunnel), "a missing key takes the declared default (hosts)");
    }
}
