using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Import;
using VelaShell.Presentation.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 「导入会话」对话框的界面契约:打开即自动出结果,默认不要求用户做任何选择;
/// 需要时切到「自己挑选会话」才展开逐条勾选。
/// </summary>
[TestClass]
[TestCategory("SessionImportUi")]
public sealed class SessionImportViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionImportViewUiTests).Assembly);

    [TestMethod]
    public void Opening_AutoScansAndPreparesEverything_WithoutAnyUserChoice()
    {
        _session.Dispatch(() =>
        {
            var window = new SessionImportView { DataContext = CreateViewModel(out SessionImportViewModel vm) };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 打开即扫完:两个来源各一张卡片,勾选与统计都已就绪。
            Assert.HasCount(2, vm.Sources);
            Assert.AreEqual(3, vm.TotalCount);
            Assert.AreEqual(2, vm.SelectedCount);   // 重复项自动跳过
            Assert.IsFalse(vm.IsBusy);

            // 自动模式下不摆出一堆勾选框——用户不必先弄懂怎么选。
            Assert.IsEmpty(VisibleCheckBoxes(window));

            Button import = PrimaryButton(window);
            Assert.IsTrue(import.IsEffectivelyEnabled, "扫描完成后导入按钮应可直接点击。");
            Assert.Contains("2", (string)import.Content!, "导入按钮要写清将导入几个会话。");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SwitchingToManualMode_RevealsPerSessionCheckboxes_AndFollowsTheUser()
    {
        _session.Dispatch(() =>
        {
            var window = new SessionImportView { DataContext = CreateViewModel(out SessionImportViewModel vm) };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.IsAdvanced = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 3 条会话 + 1 个「跳过已存在的会话」开关。
            Assert.HasCount(4, VisibleCheckBoxes(window));

            vm.Sources[0].Items[0].IsSelected = false;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(1, vm.SelectedCount);
            Assert.Contains("1", (string)PrimaryButton(window).Content!);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 标题栏与回放中心同一个样子:标题靠左、13px、不带图标,27×27 关闭键贴住右上角 ——
    /// 原先是 48 高头部那一套(14px 标题、24×24 圆角 ×)硬塞进 28,又挤又大。
    /// </summary>
    [TestMethod]
    public void TitleBar_FollowsTheDialogTitleBarSpec()
    {
        _session.Dispatch(() =>
        {
            var window = new SessionImportView { DataContext = CreateViewModel(out _) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                DialogTitleBarAssert.FollowsSpec(window);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>悬停关闭键的红底被卡片的圆角裁掉,不伸出窗口外。</summary>
    [TestMethod]
    public void CloseHover_StaysInsideTheRoundedCorner()
    {
        _session.Dispatch(() =>
        {
            var window = new SessionImportView { DataContext = CreateViewModel(out _) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                DialogTitleBarAssert.CloseHoverStaysInsideTheRoundedCorner(window);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>没找到来源(路径为空)的卡片在自定义模式下不能多出一截空行。</summary>
    /// <remarks>
    /// 同步 body:窗口打开即扫描,假服务同步完成。别写成 async lambda —— 会话没有 <c>Func&lt;Task&gt;</c> 重载,
    /// 那样拿到的是没人等的 <c>Task&lt;Task&gt;</c>,第一个 await 之后的断言全被吞掉(这条用例第一版就这么假绿过)。
    /// </remarks>
    [TestMethod]
    public void ASourceWithoutAPath_TakesNoExtraLine()
    {
        _session.Dispatch(() =>
        {
            ISessionImportService missing = NSubstitute.Substitute.For<ISessionImportService>();
            NSubstitute.SubstituteExtensions.Returns(missing.SourceKey, "WinSCP");
            NSubstitute.SubstituteExtensions.Returns(missing.DetectDefaultSource(), (string?)null);
            NSubstitute.SubstituteExtensions.Returns(
                missing.ScanAsync(NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<CancellationToken>()),
                Task.FromResult(new SessionImportScan { Source = string.Empty, Items = [] }));
            var vm = new SessionImportViewModel([new FakeImportService("Xshell", Session("web", "10.0.0.1")), missing]);
            var window = new SessionImportView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            vm.ToggleAdvancedCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.IsTrue(vm.Sources.All(s => s.IsExpanded), "前置:自定义模式下每张卡片都展开");
                string[] visiblePaths =
                [
                    .. window.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.IsEffectivelyVisible && t.Classes.Count == 0
                                    && t.FontFamily.ToString().Contains("Mono", StringComparison.OrdinalIgnoreCase)
                                    && vm.Sources.Any(s => ReferenceEquals(t.DataContext, s)))
                        .Select(t => t.Text ?? string.Empty)
                ];
                Assert.Contains(@"C:\Xshell\config.ini", visiblePaths, "有路径的来源照常显示");
                Assert.DoesNotContain(string.Empty, visiblePaths, "空的来源路径不该占一行");
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static List<CheckBox> VisibleCheckBoxes(Window window) =>
        [.. window.GetVisualDescendants().OfType<CheckBox>().Where(static box => box.IsEffectivelyVisible)];

    private static Button PrimaryButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(static b => b.Classes.Contains("dlg-primary"));

    private static SessionImportViewModel CreateViewModel(out SessionImportViewModel viewModel)
    {
        viewModel = new SessionImportViewModel(
        [
            new FakeImportService("Xshell", Session("web", "10.0.0.1", password: "p")),
            new FakeImportService("WinSCP",
                Session("db", "10.0.0.2"),
                Session("dup", "10.0.0.3", exists: true))
        ]);
        return viewModel;
    }

    private static ImportedSession Session(string name, string host, string? password = null, bool exists = false) =>
        new()
        {
            Name = name,
            Host = host,
            Port = 22,
            Username = "root",
            Protocol = "SSH",
            IsSupported = true,
            HasEncryptedPassword = password is not null,
            Password = password,
            AlreadyExists = exists
        };

    private sealed class FakeImportService(string key, params ImportedSession[] sessions) : ISessionImportService
    {
        public string SourceKey => key;

        public ImportBrowseKind BrowseKind => ImportBrowseKind.File;

        public string? DetectDefaultSource() => $@"C:\{key}\config.ini";

        public Task<SessionImportScan> ScanAsync(string? source, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionImportScan { Source = source ?? string.Empty, Items = sessions });

        public Task<SessionImportOutcome> ImportAsync(IReadOnlyList<ImportedSession> items, string groupName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionImportOutcome { Imported = items.Count, PasswordsRecovered = 0, GroupId = Guid.NewGuid() });
    }
}
