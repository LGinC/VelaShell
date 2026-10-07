using VelaShell.XServer.Tests.TestKit;

namespace VelaShell.XServer.Tests.Server;

/// <summary>结构请求:DestroySubwindows、CirculateWindow、ReparentWindow、ConfigureWindow 的事件、次序与改道。</summary>
[TestClass]
[TestCategory("X11Server")]
public sealed class WindowStructureTests
{
    private const uint SubstructureNotifyMask = 0x80000;
    private const byte DestroyNotify = 17;

    /// <summary>在 <paramref name="parent" /> 下建一个子窗口(InputOutput,背景色 <paramref name="background" />)。</summary>
    private static async Task<uint> CreateChildAsync(XTestClient c, uint parent, short x, short y, ushort width, ushort height,
        uint background = 0xFFFFFF, uint eventMask = 0)
    {
        uint id = c.NewId();
        await c.SendAsync(1, 24, b => b.U32(id).U32(parent).I16(x).I16(y).U16(width).U16(height).U16(0).U16(1).U32(0)
            .U32(0x802).U32(background).U32(eventMask));
        return id;
    }

    private static Task<ushort> SelectInputAsync(XTestClient c, uint window, uint mask) =>
        c.SendAsync(2, 0, b => b.U32(window).U32(0x800).U32(mask));

    [TestMethod]
    public async Task DestroySubwindows按堆叠次序从下到上销毁()
    {
        await using X11Server server = new();
        await using XTestClient c = await XTestClient.ConnectAsync(server);
        uint parent = await CreateChildAsync(c, c.RootWindow, 0, 0, 100, 100);
        uint bottom = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        uint middle = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        uint top = await CreateChildAsync(c, parent, 0, 0, 10, 10);
        await SelectInputAsync(c, parent, SubstructureNotifyMask);
        await c.SendAsync(5, 0, b => b.U32(parent));   // DestroySubwindows
        uint[] order = [(await c.NextEventAsync(DestroyNotify)).U32(8), (await c.NextEventAsync(DestroyNotify)).U32(8),
            (await c.NextEventAsync(DestroyNotify)).U32(8)];
        CollectionAssert.AreEqual(new[] { bottom, middle, top }, order, "协议「DestroySubwindows」:从下到上(原先从上到下)");
    }
}
