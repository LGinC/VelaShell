using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.XServer;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>标题栏的 X Server 按钮:停之前有 X 程序连着就先确认,说清会断开几个;没人连着直接停。</summary>
[TestClass]
[TestCategory("XServer")]
public sealed class XServerToggleViewModelTests
{
    static XServerToggleViewModelTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    /// <summary>提示不会自动消失(用例里用不到计时)。</summary>
    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static ILocalXServer RunningServer(int clients)
    {
        ILocalXServer server = Substitute.For<ILocalXServer>();
        server.IsSupported.Returns(true);
        server.State.Returns(XServerState.Running);
        server.CountConnectedClientsAsync().Returns(clients);
        return server;
    }

    [TestMethod]
    public async Task Stop_WithConnectedPrograms_AsksFirst_AndKeepsRunningWhenDeclined()
    {
        ILocalXServer server = RunningServer(clients: 3);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        List<int> asked = [];
        bool answer = false;
        XServerToggleViewModel vm = new(server, toasts) { ConfirmStopAsync = n => { asked.Add(n); return Task.FromResult(answer); } };

        await vm.ToggleCommand.Execute().FirstAsync();
        Assert.AreSequenceEqual([3], asked.ToArray(), "说清会断开几个");
        await server.DidNotReceive().StopAsync();

        answer = true;
        await vm.ToggleCommand.Execute().FirstAsync();
        await server.Received(1).StopAsync();
    }

    [TestMethod]
    public async Task Stop_WithNoProgramsConnected_DoesNotAsk()
    {
        ILocalXServer server = RunningServer(clients: 0);
        using ToastHostViewModel toasts = new((_, _) => new NoTimer());
        bool asked = false;
        XServerToggleViewModel vm = new(server, toasts) { ConfirmStopAsync = _ => { asked = true; return Task.FromResult(false); } };

        await vm.ToggleCommand.Execute().FirstAsync();
        Assert.IsFalse(asked);
        await server.Received(1).StopAsync();
    }
}
