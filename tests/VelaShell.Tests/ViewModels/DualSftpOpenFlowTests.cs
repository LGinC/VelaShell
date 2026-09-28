using Avalonia.Headless;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
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
