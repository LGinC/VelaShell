using VelaShell.Core.Models;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 工作台的连接检查(<see cref="IWorkspaceConnectionInspector" />)在启动器里的接线:
/// 测试连接收到的请求必须与"点连接"那一刻完全一样,预览的草稿必须拿不到口令与机密字段,
/// 插件写坏的检查不能把对话框带崩。
/// </summary>
[TestClass]
public sealed class WorkspaceConnectionInspectorTests
{
    private const string TypeId = "acme.db";

    private static WorkspaceDescriptor Descriptor() => new()
    {
        Id = TypeId,
        DisplayName = "Acme DB",
        DefaultPort = 5000,
        Fields =
        [
            new() { Key = "db", Label = "DB", DefaultValue = "main" },
            new() { Key = "token", Label = "Token", IsSecret = true }
        ]
    };

    private static SessionProfile Profile() => new()
    {
        Name = "acme",
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = TypeId,
        Host = "db.internal",
        Port = 5001,
        Username = "ops",
        Password = "pw",
        PluginSettings = new() { ["db"] = "shop" },
        PluginSecrets = new() { ["token"] = "t0ken" }
    };

    private sealed class Inspector(Func<WorkspaceConnectRequest, WorkspaceProbeReport>? probe = null) : IWorkspaceProvider, IWorkspaceConnectionInspector
    {
        public WorkspaceConnectRequest? Probed { get; private set; }

        public WorkspaceConnectRequest? Drafted { get; private set; }

        public Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public WorkspaceConnectionPreview? Preview(WorkspaceConnectRequest draft)
        {
            Drafted = draft;
            return draft.Host == "boom" ? throw new InvalidOperationException("bad preview") : new() { Title = "t", Spans = [new(draft.Host)] };
        }

        public Task<WorkspaceProbeReport> ProbeAsync(
            WorkspaceConnectRequest request,
            IProgress<WorkspaceProbeStep>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Probed = request;
            progress?.Report(new("tcp", "TCP", WorkspaceProbeState.Running));
            return Task.FromResult(probe?.Invoke(request) ?? new WorkspaceProbeReport { Succeeded = true });
        }
    }

    private sealed class Plain : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [TestMethod]
    public async Task Probe_GetsTheSameRequestAsConnect_IncludingTheTunnelEndpoint()
    {
        var registry = new PluginProtocolRegistry();
        var inspector = new Inspector();
        using IDisposable handle = registry.RegisterWorkspace(TypeId, Descriptor(), inspector);
        var launcher = new PluginWorkspaceLauncher(registry);
        var steps = new List<WorkspaceProbeStep>();

        WorkspaceProbeReport? report = await launcher.ProbeAsync(
            Profile(), new WorkspaceEndpoint("127.0.0.1", 51872, "db.internal", 5001, "bastion"), new SyncProgress(steps.Add));

        Assert.IsNotNull(report);
        Assert.IsTrue(report.Succeeded);
        WorkspaceConnectRequest request = inspector.Probed!;
        Assert.AreEqual("127.0.0.1", request.Host, "through a tunnel the plugin connects to the local forward");
        Assert.AreEqual(51872, request.Port);
        Assert.AreEqual("db.internal", request.Tunnel?.TargetHost);
        Assert.AreEqual("pw", request.Password, "a probe connects for real, so it carries the one-time credentials like Open does");
        Assert.AreEqual("shop", request.Settings["db"]);
        Assert.AreEqual("t0ken", request.Settings["token"]);
        Assert.AreEqual("tcp", steps.Single().Key);
    }

    [TestMethod]
    public async Task Probe_IsNullWithoutAnInspector_AndAFailedReportWhenItThrows()
    {
        var registry = new PluginProtocolRegistry();
        using (registry.RegisterWorkspace(TypeId, Descriptor(), new Plain()))
        {
            Assert.IsNull(await new PluginWorkspaceLauncher(registry).ProbeAsync(Profile(), null, null),
                "no inspector: the caller falls back to opening and closing a session");
        }
        using (registry.RegisterWorkspace(TypeId, Descriptor(), new Inspector(_ => throw new InvalidOperationException("driver exploded"))))
        {
            WorkspaceProbeReport? report = await new PluginWorkspaceLauncher(registry).ProbeAsync(Profile(), null, null);
            Assert.IsNotNull(report);
            Assert.IsFalse(report.Succeeded);
            Assert.AreEqual("driver exploded", report.Summary);
        }
    }

    [TestMethod]
    public void Preview_DraftHasNoPasswordNoSecretsButKeepsDefaults()
    {
        var inspector = new Inspector();
        SessionProfile draft = Profile();
        draft.PluginSettings = null;

        WorkspaceConnectionPreview? preview = PluginWorkspaceLauncher.Preview(inspector, Descriptor(), draft);

        Assert.AreEqual("db.internal", preview?.Text);
        Assert.AreEqual(string.Empty, inspector.Drafted!.Password);
        Assert.IsFalse(inspector.Drafted.Settings.ContainsKey("token"), "secret fields never reach a preview");
        Assert.AreEqual("main", inspector.Drafted.Settings["db"], "missing fields take their declared default, as on connect");
        Assert.IsNull(inspector.Drafted.Tunnel);

        draft.Host = "boom";
        Assert.IsNull(PluginWorkspaceLauncher.Preview(inspector, Descriptor(), draft), "a throwing preview means no preview, not a crash");
    }

    private sealed class SyncProgress(Action<WorkspaceProbeStep> report) : IProgress<WorkspaceProbeStep>
    {
        public void Report(WorkspaceProbeStep value) => report(value);
    }
}
