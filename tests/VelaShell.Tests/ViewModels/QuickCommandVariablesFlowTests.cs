using System.Text;
using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 含 <c>{{变量}}</c> 的快捷命令:先问、再替换、再发;取消就一个字节都不发。
/// </summary>
[TestClass]
[TestCategory("QuickCommands")]
public class QuickCommandVariablesFlowTests
{
    static QuickCommandVariablesFlowTests()
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

    [TestMethod]
    public void Placeholders_AreAskedFor_AndTheRenderedCommandIsSent()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected();
        string? askedName = null;
        QuickCommandTemplate? askedTemplate = null;
        vm.QuickCommandVariablePrompt = (name, template) =>
        {
            askedName = name;
            askedTemplate = template;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(
                new Dictionary<string, string> { ["pod"] = "web-1", ["ns"] = "prod" });
        };

        Send(vm, "查看日志", "kubectl logs -f {{pod}} -n {{ns=default}}");

        Assert.AreEqual("查看日志", askedName);
        Assert.HasCount(2, askedTemplate!.Variables);
        emulator.Received(1).WriteInput(Arg.Is<byte[]>(bytes =>
            Encoding.UTF8.GetString(bytes) == "kubectl logs -f web-1 -n prod"));
    }

    [TestMethod]
    public void CancellingThePrompt_SendsNothing()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected();
        bool focusRequested = false;
        vm.TerminalFocusRequested += (_, _) => focusRequested = true;
        vm.QuickCommandVariablePrompt = (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        Send(vm, "查看日志", "kubectl logs -f {{pod}}");

        emulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
        Assert.IsFalse(focusRequested);
    }

    [TestMethod]
    public void CommandsWithoutPlaceholders_AreSentWithoutAsking()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected();
        bool asked = false;
        vm.QuickCommandVariablePrompt = (_, _) =>
        {
            asked = true;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        };

        Send(vm, "状态", "docker inspect -f '{{.State.Status}}' web");

        Assert.IsFalse(asked, "Go 模板的 {{.State.Status}} 不是占位,不该弹框");
        emulator.Received(1).WriteInput(Arg.Is<byte[]>(bytes =>
            Encoding.UTF8.GetString(bytes) == "docker inspect -f '{{.State.Status}}' web"));
    }

    [TestMethod]
    public void TargetDisconnectedWhileAsking_IsSkipped()
    {
        (MainWindowViewModel vm, ITerminalEmulator emulator) = Connected();
        TerminalTabViewModel tab = vm.TerminalTabs.Single();
        var answer = new TaskCompletionSource<IReadOnlyDictionary<string, string>?>();
        vm.QuickCommandVariablePrompt = (_, _) => answer.Task;

        Send(vm, "查看日志", "kubectl logs -f {{pod}}");
        tab.ConnectionStatus = SessionStatus.Disconnected;
        answer.SetResult(new Dictionary<string, string> { ["pod"] = "web-1" });

        emulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
    }

    private static (MainWindowViewModel Vm, ITerminalEmulator Emulator) Connected()
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var vm = new MainWindowViewModel(quickCommands: new QuickCommandsViewModel(repository));
        ITerminalEmulator emulator = FakeTerminal.Emulator();
        var tab = new TerminalTabViewModel(emulator) { ConnectionStatus = SessionStatus.Connected };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        return (vm, emulator);
    }

    private static void Send(MainWindowViewModel vm, string name, string commandText) =>
        vm.Sidebar.QuickCommands!.SendCommand
            .Execute(new QuickCommandViewModel(new QuickCommand { Name = name, CommandText = commandText }))
            .Subscribe();
}
