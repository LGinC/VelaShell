// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The X Resize and Rotate Extension (RandR), Version 1.5 —— §4「Protocol Types」(MODEINFO、MONITORINFO)、
//   §5「Extension Initialization」(QueryVersion)、§6「Events」(RRScreenChangeNotify、RRNotify)、
//   §7.1「Protocol Requests added with version 1.0」(GetScreenInfo、SelectInput)、§7.2「…version 1.2」
//   (GetScreenSizeRange、GetScreenResources、GetOutputInfo、输出属性、GetCrtcInfo、Gamma)、§7.3「…version 1.3」
//   (GetScreenResourcesCurrent、GetCrtcTransform、GetPanning、GetOutputPrimary)、§7.4「…version 1.4」
//   (GetProviders)、§7.5「…version 1.5」(GetMonitors)、附录「Protocol Encoding」
//
//   只读:每台显示器(见 X11Server.Monitors)一个 CRTC、一个输出、一个模式。布局由宿主经 SetScreenLayout 决定,
//   客户端改配置的请求回 Failed 或 BadAccess;SetScreenSize 给当前尺寸、SetCrtcGamma、SetOutputPrimary 接受但不生效 ——
//   它们回 BadAccess 时,用 Xlib 默认错误处理的程序(调色温的、桌面会话)会因此退出。布局与 DPI 变化时按 SelectInput 的掩码发事件。
//   CRTC / 输出的 ID 按显示器的名字沿用(X11Server.Monitors.cs),插拔一台不会让别的显示器换 ID。

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Server;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer;

public sealed partial class X11Server
{
    // 服务端自己的资源 ID,落在任何客户端的 resource-base 之外(同根窗口、默认颜色表)。每类 16 个。
    private const uint RandRCrtcBase = 0x60, RandROutputBase = 0x80, RandRModeBase = 0xA0;

    private const byte RandRStatusSuccess = 0, RandRStatusFailed = 3;
    private const ushort RandRRotate0 = 1;
    private const ushort RandRGammaSize = 256;

    /// <summary>SelectInput 的登记:(客户端, 窗口) → 掩码(1 ScreenChange、2 CrtcChange、4 OutputChange,其余接受但不发)。</summary>
    private readonly Dictionary<(XClient Client, XWindow Window), ushort> _randrSelections = [];

    /// <summary>去重后的模式表:下标 k 的模式 ID 是 RandRModeBase + k。</summary>
    private readonly List<(int Width, int Height, int Refresh)> _randrModes = [];

    private void RebuildRandRModes()
    {
        _randrModes.Clear();
        foreach (XMonitor m in _monitors)
        {
            (int, int, int) mode = (m.Width, m.Height, m.RefreshRate);
            if (!_randrModes.Contains(mode))
            {
                _randrModes.Add(mode);
            }
        }
    }

    private uint ModeIdOf(XMonitor m) => RandRModeBase + (uint)_randrModes.IndexOf((m.Width, m.Height, m.RefreshRate));

    private uint CrtcIdOf(int monitor) => RandRCrtcBase + (uint)_monitorSlots[monitor];

    private uint OutputIdOf(int monitor) => RandROutputBase + (uint)_monitorSlots[monitor];

    /// <summary>CRTC / 输出 ID → 显示器下标;不存在时抛对应的 RANDR 错误(BadOutput = +0,BadCrtc = +1)。</summary>
    private int MonitorOf(uint id, uint idBase, int errorOffset) =>
        MonitorIndexOf(id, idBase) is >= 0 and var index ? index : throw new XProtocolError((XErrorCode)(RandRErrorBase + errorOffset), id);

    private void RandR(XClient c, XRequestReader r)
    {
        int width = Root.Width, height = Root.Height;
        (int mmW, int mmH) = ScreenMillimeters();
        uint time = _layoutTime;
        switch (r.Data)
        {
            case 0:   // QueryVersion
                {
                    uint major = r.U32(), minor = r.U32();
                    (uint maj, uint min) = major > 1 || (major == 1 && minor >= 5) ? (1u, 5u) : (major, minor);
                    c.Reply(0, w => w.U32(maj).U32(min).Zero(16));
                    break;
                }
            case 2:   // SetScreenConfig:只读,回 Failed
                c.Reply(RandRStatusFailed, w => w.U32(time).U32(time).U32(RootWindowId).U16(0).Zero(10));
                break;
            case 4:   // SelectInput
                {
                    XWindow window = Window(r.U32());
                    ushort mask = r.U16();
                    if (mask == 0)
                    {
                        _randrSelections.Remove((c, window));
                    }
                    else
                    {
                        _randrSelections[(c, window)] = mask;
                    }
                    break;
                }
            case 5:   // GetScreenInfo(1.0):一个尺寸(当前)、一个刷新率(主显示器的)
                {
                    _ = Window(r.U32());
                    ushort rate = (ushort)_monitors[PrimaryMonitorIndex()].RefreshRate;
                    c.Reply((byte)RandRRotate0, w => w
                        .U32(RootWindowId).U32(time).U32(time)
                        .U16(1).U16(0).U16(RandRRotate0).U16(rate).U16(2).Zero(2)
                        .U16((ushort)width).U16((ushort)height).U16((ushort)mmW).U16((ushort)mmH)
                        .U16(1).U16(rate));
                    break;
                }
            case 6:   // GetScreenSizeRange:只有当前尺寸
                _ = Window(r.U32());
                c.Reply(0, w => w.U16((ushort)width).U16((ushort)height).U16((ushort)width).U16((ushort)height).Zero(16));
                break;
            case 8:   // GetScreenResources
            case 25:  // GetScreenResourcesCurrent
                {
                    _ = Window(r.U32());
                    byte[][] names = [.. _randrModes.Select(m => XWire.Latin1.GetBytes($"{m.Width}x{m.Height}"))];
                    int nameBytes = names.Sum(n => n.Length);
                    int count = _monitors.Count;
                    c.Reply(0, w =>
                    {
                        w.U32(time).U32(time).U16((ushort)count).U16((ushort)count).U16((ushort)_randrModes.Count).U16((ushort)nameBytes).Zero(8);
                        for (int i = 0; i < count; i++)
                        {
                            w.U32(CrtcIdOf(i));
                        }
                        for (int i = 0; i < count; i++)
                        {
                            w.U32(OutputIdOf(i));
                        }
                        for (int k = 0; k < _randrModes.Count; k++)
                        {
                            WriteModeInfo(w, RandRModeBase + (uint)k, _randrModes[k], names[k].Length);
                        }
                        foreach (byte[] name in names)
                        {
                            w.Bytes(name);
                        }
                        w.Pad4();
                    });
                    break;
                }
            case 9:   // GetOutputInfo
                {
                    int i = MonitorOf(r.U32(), RandROutputBase, 0);
                    XMonitor m = _monitors[i];
                    (int ow, int oh) = MonitorMillimeters(m);
                    byte[] name = XWire.Latin1.GetBytes(m.Name);
                    uint mode = ModeIdOf(m);
                    c.Reply(RandRStatusSuccess, w => w
                        .U32(time).U32(CrtcIdOf(i)).U32((uint)ow).U32((uint)oh)
                        .U8(0).U8(0)                                  // Connected、SubPixelUnknown
                        .U16(1).U16(1).U16(1).U16(0).U16((ushort)name.Length)
                        .U32(CrtcIdOf(i)).U32(mode).Bytes(name).Pad4());
                    break;
                }
            case 10:  // ListOutputProperties:没有输出属性
                MonitorOf(r.U32(), RandROutputBase, 0);
                c.Reply(0, w => w.U16(0).Zero(22));
                break;
            case 11:  // QueryOutputProperty
                {
                    MonitorOf(r.U32(), RandROutputBase, 0);
                    throw new XProtocolError(XErrorCode.Name, r.U32());
                }
            case 15:  // GetOutputProperty:属性不存在 → type None、format 0
                MonitorOf(r.U32(), RandROutputBase, 0);
                c.Reply(0, w => w.U32(0).U32(0).U32(0).Zero(12));
                break;
            case 20:  // GetCrtcInfo
                {
                    int i = MonitorOf(r.U32(), RandRCrtcBase, 1);
                    XMonitor m = _monitors[i];
                    uint mode = ModeIdOf(m);
                    c.Reply(RandRStatusSuccess, w => w
                        .U32(time).I16(m.X).I16(m.Y).U16((ushort)m.Width).U16((ushort)m.Height)
                        .U32(mode).U16(RandRRotate0).U16(RandRRotate0).U16(1).U16(1)
                        .U32(OutputIdOf(i)).U32(OutputIdOf(i)));
                    break;
                }
            case 21:  // SetCrtcConfig:只读,回 Failed
            case 29:  // SetPanning
                c.Reply(RandRStatusFailed, w => w.U32(time).Zero(20));
                break;
            case 22:  // GetCrtcGammaSize:报 256 级(不能改,见 SetCrtcGamma)
                MonitorOf(r.U32(), RandRCrtcBase, 1);
                c.Reply(0, w => w.U16(RandRGammaSize).Zero(22));
                break;
            case 23:  // GetCrtcGamma:线性斜坡,红绿蓝相同
                MonitorOf(r.U32(), RandRCrtcBase, 1);
                c.Reply(0, w =>
                {
                    w.U16(RandRGammaSize).Zero(22);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        for (int k = 0; k < RandRGammaSize; k++)
                        {
                            w.U16((ushort)(k * 0x101));
                        }
                    }
                    w.Pad4();
                });
                break;
            case 27:  // GetCrtcTransform:单位变换,无滤镜
                MonitorOf(r.U32(), RandRCrtcBase, 1);
                c.Reply(0, w =>
                {
                    WriteIdentityTransform(w);
                    w.Bool(false).Zero(3);
                    WriteIdentityTransform(w);
                    w.Zero(4).U16(0).U16(0).U16(0).U16(0);
                });
                break;
            case 28:  // GetPanning:不平移
                MonitorOf(r.U32(), RandRCrtcBase, 1);
                c.Reply(RandRStatusSuccess, w => w.U32(time).Zero(24));
                break;
            case 31:  // GetOutputPrimary
                _ = Window(r.U32());
                c.Reply(0, w => w.U32(OutputIdOf(PrimaryMonitorIndex())).Zero(20));
                break;
            case 32:  // GetProviders:没有 provider
                _ = Window(r.U32());
                c.Reply(0, w => w.U32(time).U16(0).Zero(18));
                break;
            case 42:  // GetMonitors
                {
                    _ = Window(r.U32());
                    uint[] names = [.. _monitors.Select(m => Intern(m.Name))];
                    int count = _monitors.Count;
                    c.Reply(0, w =>
                    {
                        w.U32(time).U32((uint)count).U32((uint)count).Zero(12);
                        for (int i = 0; i < count; i++)
                        {
                            XMonitor m = _monitors[i];
                            (int mw, int mh) = MonitorMillimeters(m);
                            w.U32(names[i]).Bool(m.Primary).Bool(true).U16(1)
                                .I16(m.X).I16(m.Y).U16((ushort)m.Width).U16((ushort)m.Height).U32((uint)mw).U32((uint)mh)
                                .U32(OutputIdOf(i));
                        }
                    });
                    break;
                }
            case 7:   // SetScreenSize:范围(GetScreenSizeRange)只有当前尺寸 —— 同尺寸照样成功,别的 BadValue(毫米数须非零)
                {
                    _ = Window(r.U32());
                    ushort w = r.U16(), h = r.U16();
                    uint mmWidth = r.U32(), mmHeight = r.U32();
                    if (w != width || h != height || mmWidth == 0 || mmHeight == 0)
                    {
                        throw new XProtocolError(XErrorCode.Value, w != width ? w : h != height ? h : 0u);
                    }
                    break;
                }
            case 24:  // SetCrtcGamma:接受但不生效(gamma 由宿主的系统管);长度须是 GetCrtcGammaSize 报的
                {
                    MonitorOf(r.U32(), RandRCrtcBase, 1);
                    ushort size = r.U16();
                    if (size != RandRGammaSize)
                    {
                        throw new XProtocolError(XErrorCode.Value, size);
                    }
                    break;
                }
            case 30:  // SetOutputPrimary:接受但不生效(主显示器由宿主定);output 须是 None 或现有的输出
                {
                    _ = Window(r.U32());
                    uint output = r.U32();
                    if (output != 0)
                    {
                        MonitorOf(output, RandROutputBase, 0);
                    }
                    break;
                }
            case 12:  // ConfigureOutputProperty
            case 13:  // ChangeOutputProperty
            case 14:  // DeleteOutputProperty
            case 16:  // CreateMode
            case 17:  // DestroyMode
            case 18:  // AddOutputMode
            case 19:  // DeleteOutputMode
            case 26:  // SetCrtcTransform
            case 43:  // SetMonitor
            case 44:  // DeleteMonitor
                throw new XProtocolError(XErrorCode.Access);
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    /// <summary>客户端断开:摘掉它的 RRSelectInput 登记。</summary>
    private void CleanupRandR(XClient client) => RemoveRandRSelections(key => ReferenceEquals(key.Client, client));

    /// <summary>窗口销毁:摘掉选在它上面的 RRSelectInput 登记(否则销毁的窗口会一直收到、也一直被引用着)。</summary>
    private void CleanupRandR(XWindow window) => RemoveRandRSelections(key => ReferenceEquals(key.Window, window));

    private void RemoveRandRSelections(Func<(XClient Client, XWindow Window), bool> match)
    {
        if (_randrSelections.Count == 0)
        {
            return;
        }
        foreach ((XClient, XWindow) key in _randrSelections.Keys.Where(match).ToArray())
        {
            _randrSelections.Remove(key);
        }
    }

    /// <summary>
    /// 布局变了:按各客户端 SelectInput 的掩码发 ScreenChangeNotify、CrtcChange、OutputChange。<paramref name="screenOnly" /> 时
    /// (只是 DPI 变了,毫米数跟着变)只发 ScreenChangeNotify。
    /// </summary>
    private void NotifyRandRChange(bool screenOnly = false)
    {
        if (_randrSelections.Count == 0)
        {
            return;
        }
        (int mmW, int mmH) = ScreenMillimeters();
        int width = Root.Width, height = Root.Height;
        uint time = _layoutTime;
        foreach (((XClient client, XWindow window), ushort mask) in _randrSelections)
        {
            if (client.Closed)
            {
                continue;
            }
            if ((mask & 1) != 0)
            {
                client.Event(RandREventBase, (byte)RandRRotate0, w => w
                    .U32(time).U32(time).U32(RootWindowId).U32(window.Id).U16(0).U16(0)
                    .U16((ushort)width).U16((ushort)height).U16((ushort)mmW).U16((ushort)mmH));
            }
            for (int i = 0; i < _monitors.Count && !screenOnly; i++)
            {
                XMonitor m = _monitors[i];
                uint crtc = CrtcIdOf(i), output = OutputIdOf(i), mode = ModeIdOf(m);
                if ((mask & 2) != 0)
                {
                    client.Event(RandREventBase + 1, 0, w => w   // CrtcChange
                        .U32(time).U32(window.Id).U32(crtc).U32(mode).U16(RandRRotate0).Zero(2)
                        .I16(m.X).I16(m.Y).U16((ushort)m.Width).U16((ushort)m.Height));
                }
                if ((mask & 4) != 0)
                {
                    client.Event(RandREventBase + 1, 1, w => w   // OutputChange
                        .U32(time).U32(time).U32(window.Id).U32(output).U32(crtc).U32(mode)
                        .U16(RandRRotate0).U8(0).U8(0));
                }
            }
        }
    }

    /// <summary>
    /// MODEINFO:行总长 = 宽、帧总行数 = 高,点时钟按刷新率反推(刷新率 = 点时钟 / (htotal × vtotal))。点时钟只有 32 位:
    /// 8K@144 之类算出来超过 4 GHz 时取最大值(原先截断回绕,报出来的刷新率乱七八糟)。
    /// </summary>
    private static void WriteModeInfo(XWriter w, uint id, (int Width, int Height, int Refresh) mode, int nameLength)
    {
        (int width, int height, int refresh) = mode;
        uint clock = (uint)Math.Min(uint.MaxValue, (long)width * height * Math.Max(0, refresh));
        w.U32(id).U16((ushort)width).U16((ushort)height).U32(clock)
            .U16((ushort)width).U16((ushort)width).U16((ushort)width).U16(0)
            .U16((ushort)height).U16((ushort)height).U16((ushort)height)
            .U16((ushort)nameLength).U32(0);
    }

    /// <summary>3×3 的 16.16 定点单位矩阵。</summary>
    private static void WriteIdentityTransform(XWriter w)
    {
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                w.U32(row == col ? 0x10000u : 0u);
            }
        }
    }
}
