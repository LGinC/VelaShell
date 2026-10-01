using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Security;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("Todo2PixelRegression")]
public sealed class Todo2PixelRegressionTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Todo2PixelRegressionTests).Assembly);

    [TestMethod]
    public void FocusedSftpPng_RejectsSaturatedLimePixels()
    {
        OnUi(() =>
        {
            ThemeVariant previousTheme = Application.Current!.RequestedThemeVariant;
            try
            {
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                var viewModel = new ConnectionProfileViewModel
                {
                    Host = "files.example.com",
                    Port = 22,
                    Username = "root",
                    Password = SecureStringConvert.FromPlaintext("secret"),
                };
                viewModel.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
                var window = new ConnectionProfileView { DataContext = viewModel };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Button sftp = window.FindControl<Button>("SftpTab")
                    ?? throw new AssertFailedException("SftpTab not found.");
                Assert.IsTrue(sftp.Classes.Contains("proto-item"));
                Assert.IsTrue(sftp.Focus(NavigationMethod.Tab));
                Dispatcher.UIThread.RunJobs();
                Assert.IsTrue(sftp.IsFocused);
                using WriteableBitmap bitmap = window.CaptureRenderedFrame()
                    ?? throw new AssertFailedException("Headless renderer did not produce a focused frame.");
                // 采样窗取协议栏里 SFTP 那一项的实际边界,而不是写死坐标:
                // 协议从横排页签改成左侧竖排之后,写死的那块区域落到了标题栏与表单上。
                Point origin = sftp.TranslatePoint(default, window)
                    ?? throw new AssertFailedException("SftpTab is not in the window.");
                InspectFocusedProtocolItem(bitmap, new PixelRect(
                    (int)Math.Floor(origin.X), (int)Math.Floor(origin.Y),
                    (int)Math.Ceiling(sftp.Bounds.Width), (int)Math.Ceiling(sftp.Bounds.Height)));
                SaveOptionalFocusCapture(bitmap, "connection-profile-sftp-keyboard-focused-dark.png");
                window.Close();
            }
            finally
            {
                Application.Current.RequestedThemeVariant = previousTheme;
            }
        });
    }

    private static void InspectFocusedProtocolItem(WriteableBitmap bitmap, PixelRect sample)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        const int stride = 4;
        int bufferSize = checked(width * height * stride);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, bufferSize, width * stride);
            int limePixels = 0;
            int purplePixels = 0;
            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            var limeColors = new Dictionary<string, int>();
            var purpleColors = new Dictionary<string, int>();
            // 选中项的图标与名字是强调色,键盘焦点的描边也是:这一块里必须数得到紫色,
            // 而一个 Fluent 系统强调色(黄绿)像素都不能有。headless 渲染是 1:1 缩放,DIP 即像素。
            for (int y = Math.Max(0, sample.Y); y < sample.Bottom && y < height; y++)
            {
                for (int x = Math.Max(0, sample.X); x < sample.Right && x < width; x++)
                {
                    int offset = (y * width + x) * stride;
                    byte blue = Marshal.ReadByte(buffer, offset);
                    byte green = Marshal.ReadByte(buffer, offset + 1);
                    byte red = Marshal.ReadByte(buffer, offset + 2);
                    if (green > 180 && red > 100 && blue < 100)
                    {
                        limePixels++;
                        string color = $"#{red:X2}{green:X2}{blue:X2}";
                        limeColors[color] = limeColors.GetValueOrDefault(color) + 1;
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                    if (red > 80 && blue > 100 && green < 190)
                    {
                        purplePixels++;
                        string color = $"#{red:X2}{green:X2}{blue:X2}";
                        purpleColors[color] = purpleColors.GetValueOrDefault(color) + 1;
                    }
                }
            }

            Assert.AreEqual(0, limePixels,
                $"Focused SFTP frame contains saturated Fluent lime pixels at {minX}..{maxX}, {minY}..{maxY}. Colors: {string.Join(", ", limeColors.Keys)}");
            Assert.IsGreaterThan(0, purplePixels, "Focused SFTP capture lacks expected Vela purple/accent pixels.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SaveOptionalFocusCapture(WriteableBitmap bitmap, string fileName)
    {
        string? configured = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }
        string directory = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(RepositoryRoot, configured);
        Directory.CreateDirectory(directory);
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VelaShell.slnx")))
            {
                directory = directory.Parent;
            }
            return directory?.FullName ?? Directory.GetCurrentDirectory();
        }
    }

    private static void OnUi(Action action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
