using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Controls.Controls;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Recording;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 回放中心的标题栏:导出 / 刷新 / 清理 / 自动录制与主窗口的全局功能图标一样收在标题栏里;
/// 自动录制开着时图标转强调色;双击标题栏里的按钮不会顺手把窗口最大化。
/// </summary>
[TestClass]
[TestCategory("RecorderUI")]
public sealed class RecordingPlayerTitleBarUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RecordingPlayerTitleBarUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void TheActions_LiveInTheTitleBar_AsIconButtons()
    {
        OnUi(() =>
        {
            RecordingPlayerView window = Show(autoRecord: false);
            try
            {
                StackPanel actions = Named<StackPanel>(window, "TitleActions");
                Assert.Contains(b => b.Classes.Contains("window-titlebar"), actions.GetVisualAncestors().OfType<Border>());
                Button[] buttons = [.. actions.Children.OfType<Button>()];
                CollectionAssert.AreEqual(
                    new[] { "导出录制", "刷新", "清理", "自动录制: 已关闭" },
                    buttons.Select(AutomationProperties.GetName).ToArray(),
                    "四个动作都在,顺序与原先那排文字按钮一致");
                object theme = window.FindResource("VelaTitleActionButtonTheme")!;
                foreach (Button button in buttons)
                {
                    Assert.AreSame(theme, button.Theme, "与主窗口的全局功能图标同一个主题");
                    Assert.IsGreaterThan(0, button.Bounds.Width);
                    Assert.IsNotNull(ToolTip.GetTip(button), "纯图标按钮必须有悬停提示");
                }
                SaveFrame(window, "recording-player-titlebar.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AutoRecord_TurnsTheIconAccentWhenOn()
    {
        OnUi(() =>
        {
            RecordingPlayerView off = Show(autoRecord: false);
            RecordingPlayerView on = Show(autoRecord: true);
            try
            {
                Assert.AreEqual(Token(off, "VelaTextMuted"), ColorOf(VisibleIcon(Named<Button>(off, "AutoRecordButton")).Foreground));
                Assert.AreEqual(Token(on, "VelaAccent"), ColorOf(VisibleIcon(Named<Button>(on, "AutoRecordButton")).Foreground));
            }
            finally
            {
                off.Close();
                on.Close();
            }
        });

        static LucideIcon VisibleIcon(Button button) =>
            button.GetVisualDescendants().OfType<LucideIcon>().Single(i => i.IsVisible);
    }

    [TestMethod]
    public void DoubleClickingAButton_DoesNotMaximize_ButTheBlankTitleBarDoes()
    {
        OnUi(() =>
        {
            RecordingPlayerView window = Show(autoRecord: false);
            try
            {
                Button refresh = Named<StackPanel>(window, "TitleActions").Children.OfType<Button>().ElementAt(1);
                DoubleClick(window, refresh.TranslatePoint(new Point(refresh.Bounds.Width / 2, refresh.Bounds.Height / 2), window)!.Value);
                Assert.AreEqual(WindowState.Normal, window.WindowState, "连点两下刷新不该把窗口最大化");

                // 标题左边、标题文字右边的空白处
                Border titleBar = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("window-titlebar"));
                Point blank = titleBar.TranslatePoint(new Point(titleBar.Bounds.Width / 2, titleBar.Bounds.Height / 2), window)!.Value;
                DoubleClick(window, blank);
                Assert.AreEqual(WindowState.Maximized, window.WindowState, "双击标题栏空白处照常最大化");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void DoubleClick(Window window, Point point)
    {
        for (int i = 0; i < 2; i++)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static RecordingPlayerView Show(bool autoRecord)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
        _localization.SetLanguage("zh-CN");

        ISessionRecordingStore store = Substitute.For<ISessionRecordingStore>();
        store.ListRecordingsAsync(Arg.Any<CancellationToken>()).Returns([]);
        ISettingsService settings = Substitute.For<ISettingsService>();
        var appSettings = new AppSettings();
        appSettings.Security.RecordProductionSessions = autoRecord;
        settings.GetSettingsAsync().Returns(appSettings);
        var viewModel = new RecordingPlayerViewModel(store, settings);
        viewModel.InitializeAsync().GetAwaiter().GetResult();

        var window = new RecordingPlayerView { DataContext = viewModel, Width = 1208, Height = 828 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    /// <summary>设了 <c>VELASHELL_VISUAL_QA_DIR</c> 时把这一帧存成 PNG,供人眼验收(同资源监视的用例)。</summary>
    private static void SaveFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using Avalonia.Media.Imaging.WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>令牌在这扇窗当前主题下的颜色(比颜色而不是比画刷对象:主题字典与强调色覆盖给的画刷实例不同)。</summary>
    private static Avalonia.Media.Color Token(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out object? value) ? ColorOf(value) : throw new AssertFailedException($"令牌 {key} 不存在");

    private static Avalonia.Media.Color ColorOf(object? value) => value switch
    {
        Avalonia.Media.ISolidColorBrush brush => brush.Color,
        Avalonia.Media.Color color => color,
        _ => throw new AssertFailedException($"不是纯色画刷:{value?.GetType().Name ?? "null"}"),
    };

    private static void OnUi(Action body) =>
        _session.Dispatch(
            () =>
            {
                body();
                return Task.CompletedTask;
            },
            CancellationToken.None
        ).GetAwaiter().GetResult();
}
