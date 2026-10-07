using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace VelaShell.Services.XServer;

/// <summary>
/// 请用户注意一个窗口(闪任务栏按钮),但不把它切到前台 —— X 程序要求激活自己、而那不是用户操作引起的时候用
/// (见 <see cref="VelaShell.XServer.XActivateRequest.UserInitiated" />)。Windows 上是 FlashWindowEx;别的平台什么也不做。
/// </summary>
internal static partial class WindowAttention
{
    public static void Request(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle)
        {
            return;
        }
        try
        {
            FlashWindowInfo info = new()
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                Window = handle.Handle,
                Flags = FlashTray | FlashUntilForeground,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private const uint FlashTray = 0x2, FlashUntilForeground = 0xC;   // FLASHW_TRAY、FLASHW_TIMERNOFG

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWindowInfo info);
}
