using System.Text;
using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>RANDR(只读):一台覆盖整个根窗口的虚拟显示器,改配置的请求一律失败。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class RandRTests
{
    private static readonly X11ServerOptions Options = new() { ScreenWidth = 1920, ScreenHeight = 1080, Dpi = 96 };

    private static async Task<byte> RandRMajorAsync(XTestClient c)
    {
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("RANDR")).Pad());
        Assert.AreEqual(1, q.Bytes[8], "RANDR 应当存在");
        Assert.AreEqual(130, q.Bytes[11], "first-error(XFIXES 占 128、129)");
        return q.Bytes[9];
    }

    [TestMethod]
    public async Task QueryVersion报1点5()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);
        XMessage v = await c.RequestAsync(major, 0, b => b.U32(1).U32(6));
        Assert.AreEqual(1u, v.U32(8));
        Assert.AreEqual(5u, v.U32(12));
    }

    [TestMethod]
    public async Task 资源输出CRTC三者互相对得上()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);

        XMessage res = await c.RequestAsync(major, 25, b => b.U32(c.RootWindow));
        Assert.AreEqual(1, res.U16(16), "一个 CRTC");
        Assert.AreEqual(1, res.U16(18), "一个输出");
        Assert.AreEqual(1, res.U16(20), "一个模式");
        uint crtc = res.U32(32), output = res.U32(36), mode = res.U32(40);
        Assert.AreEqual(1920, res.U16(44));
        Assert.AreEqual(1080, res.U16(46));
        int nameLength = res.U16(22);
        Assert.AreEqual("1920x1080", Encoding.Latin1.GetString(res.Bytes, 72, nameLength));

        XMessage info = await c.RequestAsync(major, 9, b => b.U32(output).U32(0));
        Assert.AreEqual(0, info.Bytes[1], "Success");
        Assert.AreEqual(crtc, info.U32(12));
        Assert.AreEqual(508u, info.U32(16), "毫米宽 = 1920 × 25.4 / 96");
        Assert.AreEqual(0, info.Bytes[24], "Connected");
        Assert.AreEqual(mode, info.U32(40), "模式列表跟在 CRTC 列表后面");

        XMessage crtcInfo = await c.RequestAsync(major, 20, b => b.U32(crtc).U32(0));
        Assert.AreEqual(1920, crtcInfo.U16(16));
        Assert.AreEqual(mode, crtcInfo.U32(20));
        Assert.AreEqual(output, crtcInfo.U32(32));
    }

    [TestMethod]
    public async Task GetMonitors报一台主显示器()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);
        XMessage m = await c.RequestAsync(major, 42, b => b.U32(c.RootWindow).U8(1).U8(0).U8(0).U8(0));
        Assert.AreEqual(1u, m.U32(12), "nmonitors");
        Assert.AreEqual(1, m.Bytes[36], "primary");
        Assert.AreEqual(1920, m.U16(44));
        Assert.AreEqual(1080, m.U16(46));
    }

    /// <summary>没有回复的请求:等一个往返,确认它没有回错误。</summary>
    private static async Task AssertNoErrorAsync(XTestClient c, ushort sequence, string because)
    {
        await c.SyncAsync();
        try
        {
            XMessage error = await c.NextAsync(m => m.IsError && m.Sequence == sequence, timeoutMs: 100);
            Assert.Fail($"{because}:回了错误 {error.Bytes[1]}");
        }
        catch (OperationCanceledException)
        {
            // 没有错误。
        }
    }

    [TestMethod]
    public async Task SetScreenSize同尺寸成功_别的尺寸BadValue_SetCrtcGamma与SetOutputPrimary静默接受()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);
        XMessage res = await c.RequestAsync(major, 25, b => b.U32(c.RootWindow));
        uint crtc = res.U32(32), output = res.U32(36);

        ushort same = await c.SendAsync(major, 7, b => b.U32(c.RootWindow).U16(1920).U16(1080).U32(508).U32(286));
        await AssertNoErrorAsync(c, same, "同尺寸的 SetScreenSize");
        XMessage other = await c.RequestAsync(major, 7, b => b.U32(c.RootWindow).U16(1024).U16(768).U32(270).U32(203));
        Assert.IsTrue(other.IsError);
        Assert.AreEqual(2, other.Bytes[1], "范围之外是 BadValue(原先一律 BadAccess,Xlib 默认处理会让程序退出)");

        ushort[] ramp = [.. Enumerable.Range(0, 256).Select(k => (ushort)(k * 0x101))];
        ushort gamma = await c.SendAsync(major, 24, b =>
        {
            b.U32(crtc).U16(256).U16(0);
            for (int channel = 0; channel < 3; channel++)
            {
                foreach (ushort v in ramp)
                {
                    b.U16(v);
                }
            }
        });
        await AssertNoErrorAsync(c, gamma, "SetCrtcGamma(调色温的程序)");
        XMessage shortRamp = await c.RequestAsync(major, 24, b => b.U32(crtc).U16(2).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0));
        Assert.AreEqual(2, shortRamp.Bytes[1], "长度不是 GetCrtcGammaSize 报的:BadValue");

        ushort primary = await c.SendAsync(major, 30, b => b.U32(c.RootWindow).U32(output));
        await AssertNoErrorAsync(c, primary, "SetOutputPrimary(桌面会话启动时设)");
        XMessage badOutput = await c.RequestAsync(major, 30, b => b.U32(c.RootWindow).U32(0x12345));
        Assert.AreEqual(130, badOutput.Bytes[1], "BadOutput");
    }

    [TestMethod]
    public async Task 拔掉一台显示器后其余的输出ID不变_8K144的点时钟不回绕_DPI变化发ScreenChangeNotify()
    {
        X11ServerOptions options = new()
        {
            ScreenWidth = 5760,
            ScreenHeight = 1080,
            Dpi = 96,
            Monitors =
            [
                new XMonitor(0, 0, 1920, 1080) { Name = "DP-1", Primary = true },
                new XMonitor(1920, 0, 1920, 1080) { Name = "DP-2" },
                new XMonitor(3840, 0, 1920, 1080) { Name = "HDMI-1" },
            ],
        };
        await using X11Server server = new(options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);

        async Task<Dictionary<string, uint>> OutputsAsync()
        {
            XMessage res = await c.RequestAsync(major, 25, b => b.U32(c.RootWindow));
            int crtcs = res.U16(16), outputs = res.U16(18);
            Dictionary<string, uint> byName = [];
            for (int i = 0; i < outputs; i++)
            {
                uint id = res.U32(32 + (4 * crtcs) + (4 * i));
                XMessage info = await c.RequestAsync(major, 9, b => b.U32(id).U32(0));
                byName[Encoding.Latin1.GetString(info.Bytes, 44, info.U16(34))] = id;
            }
            return byName;
        }

        Dictionary<string, uint> before = await OutputsAsync();
        server.SetScreenLayout(3840, 4320,
        [
            new XMonitor(0, 0, 1920, 1080) { Name = "DP-1", Primary = true },
            new XMonitor(0, 1080, 7680, 4320) { Name = "HDMI-1", RefreshRate = 144 },   // DP-2 拔掉,HDMI-1 换成 8K@144
        ]);
        await c.SyncAsync();
        Dictionary<string, uint> after = await OutputsAsync();
        Assert.HasCount(2, after);
        Assert.AreEqual(before["DP-1"], after["DP-1"]);
        Assert.AreEqual(before["HDMI-1"], after["HDMI-1"], "原先按下标分配,拔掉 DP-2 后 HDMI-1 换成了它的 ID");

        XMessage res = await c.RequestAsync(major, 25, b => b.U32(c.RootWindow));
        int modes = res.U16(20), modeStart = 32 + (4 * res.U16(16)) + (4 * res.U16(18));
        uint clock = Enumerable.Range(0, modes).Where(k => res.U16(modeStart + (32 * k) + 4) == 7680)
            .Select(k => res.U32(modeStart + (32 * k) + 8)).Single();
        Assert.AreEqual(uint.MaxValue, clock, "7680 × 4320 × 144 超出 32 位:取最大值,不回绕成一个很小的数");

        XMessage q = await c.RequestAsync(98, 0, b => b.U16(5).U16(0).Bytes(Encoding.Latin1.GetBytes("RANDR")).Pad());
        await c.SendAsync(major, 4, b => b.U32(c.RootWindow).U16(1).U16(0));   // SelectInput:ScreenChange
        await c.SyncAsync();
        server.SetDisplayScale(192, 2);
        XMessage change = await c.NextEventAsync(q.Bytes[10]);
        Assert.AreEqual(ToMillimeters(3840, 192), change.U16(28), "毫米宽按新的 DPI 算");

        static int ToMillimeters(int pixels, int dpi) => (int)Math.Round(pixels * 25.4 / dpi);
    }

    [TestMethod]
    public async Task XINERAMA的GetScreenSize与QueryScreens同一个次序()
    {
        X11ServerOptions options = new()
        {
            ScreenWidth = 3200,
            ScreenHeight = 1080,
            Monitors = [new XMonitor(0, 0, 1920, 1080) { Name = "A" }, new XMonitor(1920, 0, 1280, 1024) { Name = "B", Primary = true }],
        };
        await using X11Server server = new(options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        XMessage q = await c.RequestAsync(98, 0, b => b.U16(8).U16(0).Bytes(Encoding.Latin1.GetBytes("XINERAMA")).Pad());
        byte xinerama = q.Bytes[9];
        XMessage screens = await c.RequestAsync(xinerama, 5);
        Assert.AreEqual(1280, screens.U16(36), "QueryScreens:主显示器排第一");
        XMessage size = await c.RequestAsync(xinerama, 3, b => b.U32(c.RootWindow).U32(0));
        Assert.AreEqual(1280u, size.U32(8), "GetScreenSize 的 0 号也是主显示器(原先按原下标给了 A)");
    }

    [TestMethod]
    public async Task 错误的输出ID报BadOutput_改配置回Failed()
    {
        await using X11Server server = new(Options);
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        byte major = await RandRMajorAsync(c);

        XMessage bad = await c.RequestAsync(major, 9, b => b.U32(0x12345).U32(0));
        Assert.IsTrue(bad.IsError);
        Assert.AreEqual(130, bad.Bytes[1], "BadOutput = first-error + 0");

        XMessage set = await c.RequestAsync(major, 21, b => b.U32(0x40).U32(0).U32(0).I16(0).I16(0).U32(0).U16(1).U16(0));
        Assert.IsTrue(set.IsReply);
        Assert.AreEqual(3, set.Bytes[1], "Failed");
    }
}
