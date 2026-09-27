using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.PluginSdk.TerminalView;
using VelaShell.Services.Plugins;

namespace VelaShell.Tests.Services;

/// <summary>
/// 插件终端视图的 <see cref="IPluginTerminalView.Resized" />。
/// </summary>
/// <remarks>
/// 控件事件改成带像素的 <c>PtySize</c> 之后,SDK 的契约仍是 <c>Action&lt;int, int&gt;</c>(不动 SDK),
/// 中间隔着一层适配。适配最容易坏的是<b>退订</b>:每次现包一个 lambda 挂上去,<c>-=</c> 就永远摘不掉,
/// 插件关了面板还在收回调。
/// </remarks>
[TestClass]
[TestCategory("PluginTerminal")]
public sealed class PluginTerminalViewResizedTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginTerminalViewResizedTests).Assembly);

    [TestMethod]
    public void Resized_ReportsColumnsAndRows_AndStopsAfterUnsubscribe()
    {
        OnUi(() =>
        {
            using IPluginTerminalView view = new PluginTerminalViewApi(() => null)
                .Create(new TerminalViewOptions { FollowHostAppearance = false });
            var window = new Window { Width = 900, Height = 400, Content = (Control)view.Control };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                var reported = new List<(int Columns, int Rows)>();
                void OnResized(int columns, int rows) => reported.Add((columns, rows));
                view.Resized += OnResized;

                window.Width = 1300;
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                Assert.IsNotEmpty(reported, "窗口变宽后插件应当收到新尺寸。");
                Assert.AreEqual((view.Columns, view.Rows), reported[^1]);

                view.Resized -= OnResized;
                int before = reported.Count;
                window.Width = 700;
                Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();

                Assert.HasCount(before, reported, "退订之后不该再收到回调。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void OnUi(Action body) =>
        _session
            .Dispatch(
                () =>
                {
                    body();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
}
