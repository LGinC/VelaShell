using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 在动态码框上点取消 = 「不连了」:不报错、不再弹凭据框、不让自动重连过几秒又弹同一个框。
/// </summary>
[TestClass]
public sealed class KeyboardInteractiveCancelFlowTests
{
    [TestMethod]
    public async Task FirstConnect_Cancelled_RemovesTheTab_WithoutAnErrorOrAnotherCredentialPrompt()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new VelaSshAuthenticationCancelledException("cancelled"));
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator());
        int prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            return Task.FromResult<SessionProfile?>(p);
        };

        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(Profile());

        Assert.IsNull(tab);
        Assert.IsEmpty(vm.TerminalTabs);
        Assert.IsNull(vm.LastConnectionError);
        Assert.AreEqual(0, prompted, "取消不是认证失败,不该再弹凭据框重试");
        await workflow.Received(1).ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Reconnect_Cancelled_CountsAsAUserDisconnect()
    {
        (MainWindowViewModel vm, TerminalTabViewModel tab) = DisconnectedTab(
            new VelaSshAuthenticationCancelledException("cancelled"));

        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsTrue(tab.UserRequestedDisconnect, "否则自动重连过几秒又来弹同一个框");
        Assert.IsNull(vm.LastConnectionError);
        Assert.IsFalse(tab.HasConnectionError);
    }

    [TestMethod]
    public async Task Reconnect_Failed_IsStillAFailure()
    {
        (MainWindowViewModel vm, TerminalTabViewModel tab) = DisconnectedTab(
            new VelaSshAuthenticationException("denied"));

        await vm.ReconnectTabAsync(tab);

        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsFalse(tab.UserRequestedDisconnect);
        Assert.IsNotNull(vm.LastConnectionError);
    }

    private static SessionProfile Profile() =>
        new() { Name = "P", Host = "h", Port = 22, Username = "root", AuthMethod = AuthMethod.Password, Password = "pw" };

    private static (MainWindowViewModel Vm, TerminalTabViewModel Tab) DisconnectedTab(Exception failure)
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw failure);
        var vm = new MainWindowViewModel(workflow, Substitute.For<ISshConnectionService>(), () => FakeTerminal.Emulator());
        var tab = new TerminalTabViewModel(FakeTerminal.Emulator())
        {
            Profile = Profile(),
            ConnectionStatus = SessionStatus.Disconnected,
        };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        return (vm, tab);
    }
}
