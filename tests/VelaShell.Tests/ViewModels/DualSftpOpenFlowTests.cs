using Avalonia.Headless;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 「在双栏 SFTP 中打开」的连接流程:两条都连上才建文档,任一条没连上就整体回滚;
/// 关标签时两条一起断开。
/// </summary>
[TestClass]
[TestCategory("Sftp")]
public class DualSftpOpenFlowTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DualSftpOpenFlowTests).Assembly);

    [TestMethod]
    public void BothConnect_OpensOneDualDocument_AndClosingItDisconnectsBoth()
    {
        _session.Dispatch(async () =>
        {
            (MainWindowViewModel vm, IConnectionWorkflowService workflow, SessionProfile left, SessionProfile right, SshSession leftSession, SshSession rightSession) = Create();

            DualSftpDocument? document = await vm.OpenDualSftpDocumentAsync(left, right);

            Assert.IsNotNull(document);
            Assert.AreSame(document, vm.Layout.AllDocuments().OfType<DualSftpDocument>().Single());
            Assert.IsEmpty(vm.Layout.AllDocuments().OfType<ConnectingDocument>(), "连上之后占位标签要换成真文档。");
            Assert.AreEqual("left-box", document.ViewModel.LeftFiles.ServerDisplayName, "先选的那条在左栏。");
            Assert.AreEqual("right-box", document.ViewModel.RightFiles.ServerDisplayName);
            await document.ViewModel.InitialLoadTask;

            vm.Layout.CloseDocument(document);
            await vm.GetStandaloneSftpCloseTask(document);

            await workflow.Received(1).DisconnectAsync(leftSession.SessionId, Arg.Any<CancellationToken>());
            await workflow.Received(1).DisconnectAsync(rightSession.SessionId, Arg.Any<CancellationToken>());
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SecondFails_RollsBackTheFirst_AndOpensNothing()
    {
        _session.Dispatch(async () =>
        {
            (MainWindowViewModel vm, IConnectionWorkflowService workflow, SessionProfile left, SessionProfile right, SshSession leftSession, _) = Create();
            workflow.ConnectProfileAsync(right, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("host unreachable"));

            DualSftpDocument? document = await vm.OpenDualSftpDocumentAsync(left, right);

            Assert.IsNull(document);
            Assert.IsEmpty(vm.Layout.AllDocuments().OfType<DualSftpDocument>());
            await workflow.Received(1).DisconnectAsync(leftSession.SessionId, Arg.Any<CancellationToken>());
            ConnectingDocument placeholder = vm.Layout.AllDocuments().OfType<ConnectingDocument>().Single();
            Assert.IsTrue(placeholder.HasError, "失败要留在占位标签的失败卡片上。");
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SecondCancelled_RollsBackTheFirst_AndRemovesThePlaceholder()
    {
        _session.Dispatch(async () =>
        {
            (MainWindowViewModel vm, IConnectionWorkflowService workflow, SessionProfile left, SessionProfile right, SshSession leftSession, _) = Create();
            workflow.ConnectProfileAsync(right, Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

            DualSftpDocument? document = await vm.OpenDualSftpDocumentAsync(left, right);

            Assert.IsNull(document);
            await workflow.Received(1).DisconnectAsync(leftSession.SessionId, Arg.Any<CancellationToken>());
            Assert.IsEmpty(vm.Layout.AllDocuments().OfType<ConnectingDocument>(), "取消是「不连了」,不留失败卡片。");
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void TheSameProfileTwice_IsRejected()
    {
        _session.Dispatch(async () =>
        {
            (MainWindowViewModel vm, _, SessionProfile left, _, _, _) = Create();
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => vm.OpenDualSftpDocumentAsync(left, left));
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void APluginFileProtocolWithStreamedUploads_OpensAsAReceivingPane()
    {
        _session.Dispatch(async () =>
        {
            IProtocolFileSystem bucket = Substitute.For<IProtocolFileSystem, IProtocolStreamUpload>();
            (MainWindowViewModel vm, _, SessionProfile left, SessionProfile s3) = CreateWithPlugin(registry => registry.Register(PluginId, S3Descriptor(), bucket));

            DualSftpDocument? document = await vm.OpenDualSftpDocumentAsync(left, s3);

            Assert.IsNotNull(document);
            Assert.IsTrue(document.ViewModel.RightFiles.AcceptsStreamedUploads);
            Assert.IsNotNull(document.ViewModel.RightFiles.InvokeProtocolAction, "插件栏要带上协议的右键动作。");
            await document.ViewModel.InitialLoadTask;
            await document.ViewModel.CloseAsync();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void APluginFileProtocolWithoutStreamedUploads_OpensAsASourceOnlyPane()
    {
        _session.Dispatch(async () =>
        {
            IProtocolFileSystem bucket = Substitute.For<IProtocolFileSystem>();
            (MainWindowViewModel vm, _, SessionProfile left, SessionProfile s3) = CreateWithPlugin(registry => registry.Register(PluginId, S3Descriptor(), bucket));

            DualSftpDocument? document = await vm.OpenDualSftpDocumentAsync(left, s3);

            Assert.IsNotNull(document, "不能接收的插件照样能进双栏,只是只能作为源。");
            Assert.IsFalse(document.ViewModel.RightFiles.AcceptsStreamedUploads);
            Assert.IsFalse(await document.ViewModel.CopyLeftToRightCommand.CanExecute.FirstAsync());
            Assert.IsTrue(await document.ViewModel.CopyRightToLeftCommand.CanExecute.FirstAsync());
            await document.ViewModel.InitialLoadTask;
            await document.ViewModel.CloseAsync();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ATerminalPluginProtocol_IsRefused_AndTheOtherSideIsRolledBack()
    {
        _session.Dispatch(async () =>
        {
            IProtocolTerminal telnet = Substitute.For<IProtocolTerminal>();
            (MainWindowViewModel vm, IConnectionWorkflowService workflow, SessionProfile left, SessionProfile plugin) =
                CreateWithPlugin(registry => registry.Register(PluginId, S3Descriptor() with { DisplayName = "Telnet" }, telnet));

            DualSftpDocument? document = await vm.OpenDualSftpDocumentAsync(left, plugin);

            Assert.IsNull(document);
            Assert.IsEmpty(vm.Layout.AllDocuments().OfType<DualSftpDocument>());
            await workflow.ReceivedWithAnyArgs(1).DisconnectAsync(default, default);
            ConnectingDocument placeholder = vm.Layout.AllDocuments().OfType<ConnectingDocument>().Single();
            Assert.IsTrue(placeholder.HasError);
            Assert.Contains("Telnet", placeholder.ErrorMessage);
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SshAndAPluginFileProtocol_AreBothAllowedIntoTheDualPane()
    {
        _session.Dispatch(() =>
        {
            (MainWindowViewModel vm, _, SessionProfile left, SessionProfile s3) = CreateWithPlugin(registry => registry.Register(PluginId, S3Descriptor(), Substitute.For<IProtocolFileSystem>()));

            Assert.IsTrue(vm.CanOpenInDualSftp(left));
            Assert.IsTrue(vm.CanOpenInDualSftp(s3));
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private const string PluginId = "acme.s3";

    private static ProtocolDescriptor S3Descriptor() => new()
    {
        Id = PluginId,
        DisplayName = "S3",
        DefaultPort = 443,
        Features = ProtocolFeatures.AnonymousAccess,
        Actions = [new("share", "Copy share link", ProtocolActionScope.File)],
    };

    /// <summary>一条 SSH 连接 + 一条插件协议连接(插件用真的注册表与会话服务,协议实现是替身)。</summary>
    private static (MainWindowViewModel, IConnectionWorkflowService, SessionProfile, SessionProfile) CreateWithPlugin(
        Action<PluginProtocolRegistry> register)
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("/");
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        var registry = new PluginProtocolRegistry();
        register(registry);
        var plugins = new PluginProtocolFileService(registry);
        SessionProfile left = Profile("left-box");
        workflow.ConnectProfileAsync(left, Arg.Any<CancellationToken>()).Returns(Session(left));
        var s3 = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "bucket",
            Host = "s3.example.com",
            Port = 443,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = PluginId,
        };
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator(),
            sftpService: sftp, pluginProtocolService: plugins, protocolRegistry: registry);
        return (vm, workflow, left, s3);
    }

    private static (MainWindowViewModel, IConnectionWorkflowService, SessionProfile, SessionProfile, SshSession, SshSession) Create()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.GetWorkingDirectoryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("/home/u");
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));
        SessionProfile left = Profile("left-box");
        SessionProfile right = Profile("right-box");
        SshSession leftSession = Session(left);
        SshSession rightSession = Session(right);
        workflow.ConnectProfileAsync(left, Arg.Any<CancellationToken>()).Returns(leftSession);
        workflow.ConnectProfileAsync(right, Arg.Any<CancellationToken>()).Returns(rightSession);
        var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator(), sftpService: sftp);
        return (vm, workflow, left, right, leftSession, rightSession);
    }

    private static SessionProfile Profile(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = $"{name}.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
        ConnectionType = ConnectionType.SSH,
    };

    private static SshSession Session(SessionProfile profile) => new()
    {
        SessionId = Guid.NewGuid(),
        ConnectionInfo = new()
        {
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            AuthMethod = profile.AuthMethod,
        },
        Status = SessionStatus.Connected,
    };
}
