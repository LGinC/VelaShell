using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VelaShell.Services.XServer;

/// <summary>
/// 系统此刻的锁定键状态(CapsLock、NumLock),在 X 窗口得到焦点时换进内置服务端(<see cref="VelaShell.XServer.X11Server.SetLockState" />):
/// 服务端起步时两个都关着,用户在别的程序里切过它也不知道 —— Windows 台式机通常开着 NumLock,小键盘在 X 里却是方向键。
/// </summary>
internal static partial class HostLockState
{
    /// <summary>
    /// 读不到(平台不支持)时为 null,沿用服务端现在的状态。Linux 上不在这里读:要连桌面的 X 显示,与键位表一起在后台读
    /// (<see cref="LinuxKeymap.ReadDesktop" />),这里给 null。
    /// </summary>
    public static (bool CapsLock, bool NumLock)? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ((GetKeyState(VkCapital) & 1) != 0, (GetKeyState(VkNumLock) & 1) != 0);
            }
            if (OperatingSystem.IsMacOS())
            {
                // Mac 键盘没有 NumLock:小键盘总是打数字,X 这边就当 NumLock 一直开着。
                return ((CGEventSourceFlagsState(CombinedSessionState) & AlphaShiftMask) != 0, true);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    private const int VkCapital = 0x14, VkNumLock = 0x90;
    private const int CombinedSessionState = 0;           // kCGEventSourceStateCombinedSessionState
    private const ulong AlphaShiftMask = 0x00010000;     // kCGEventFlagMaskAlphaShift

    [LibraryImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial short GetKeyState(int virtualKey);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [SupportedOSPlatform("macos")]
    private static partial ulong CGEventSourceFlagsState(int stateId);
}
