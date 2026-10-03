using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
public class KeyboardShortcutServiceTests
{
    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlShiftC_InTerminal_ReturnsCopy()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(
            KeyModifiers.Ctrl | KeyModifiers.Shift,
            KeyCode.C,
            ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.Copy, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlShiftV_InTerminal_ReturnsPaste()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(
            KeyModifiers.Ctrl | KeyModifiers.Shift,
            KeyCode.V,
            ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.Paste, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlC_InTerminal_ReturnsSendInterrupt()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.SendInterrupt, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdC_InTerminal_ReturnsCopy()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Meta, KeyCode.C, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.Copy, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdV_InTerminal_ReturnsPaste()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Meta, KeyCode.V, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.Paste, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdT_InGlobal_ReturnsNewTab()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Meta, KeyCode.T, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.NewTab, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CtrlC_InTerminal_ReturnsSendInterrupt()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.SendInterrupt, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdComma_InGlobal_ReturnsOpenSettings()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Meta, KeyCode.Comma, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.OpenSettings, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_UnmappedKey_ReturnsNone()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(KeyModifiers.Alt, KeyCode.T, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.None, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void IsMacOS_ReturnsConstructorValue()
    {
        var macService = new KeyboardShortcutService(isMacOS: true);
        var winService = new KeyboardShortcutService(isMacOS: false);

        Assert.IsTrue(macService.IsMacOS);
        Assert.IsFalse(winService.IsMacOS);
    }

    /// <summary>
    /// Ctrl 版本的全局键位(新建 / 关闭 / 切换标签、打开设置)归键位表管,这里一条都不映射:
    /// 否则用户在设置里把 Ctrl+W 解绑之后,焦点落在标签视图上时它照样关标签(#551)。
    /// </summary>
    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlGlobalShortcuts_AreLeftToTheKeymap()
    {
        var service = new KeyboardShortcutService(isMacOS: false);
        (KeyModifiers Modifiers, KeyCode Key)[] globals =
        [
            (KeyModifiers.Ctrl, KeyCode.T),
            (KeyModifiers.Ctrl, KeyCode.W),
            (KeyModifiers.Ctrl, KeyCode.Comma),
            (KeyModifiers.Ctrl, KeyCode.Tab),
            (KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.Tab),
        ];

        foreach ((KeyModifiers modifiers, KeyCode key) in globals)
        {
            Assert.AreEqual(ShortcutAction.None, service.Resolve(modifiers, key, ShortcutContext.Global), $"{modifiers}+{key}");
            Assert.AreEqual(ShortcutAction.None, service.Resolve(modifiers, key, ShortcutContext.Terminal), $"{modifiers}+{key}");
        }
    }

    /// <summary>macOS 的 Command 别名是固定的,终端上下文里同样生效(回落到全局映射)。</summary>
    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdAliases_AlsoWorkInTerminalContext()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        Assert.AreEqual(ShortcutAction.NewTab, service.Resolve(KeyModifiers.Meta, KeyCode.T, ShortcutContext.Terminal));
        Assert.AreEqual(ShortcutAction.CloseTab, service.Resolve(KeyModifiers.Meta, KeyCode.W, ShortcutContext.Terminal));
        Assert.AreEqual(ShortcutAction.None, service.Resolve(KeyModifiers.Ctrl, KeyCode.W, ShortcutContext.Terminal),
                        "Ctrl+W 在 macOS 上同样归键位表管。");
    }
}
