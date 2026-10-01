using Avalonia;
using Avalonia.Media;
using VelaShell.Controls.Controls;

namespace VelaShell.Controls.Tests;

/// <summary>取色器的零件:色值解析 / 格式化,面板与色相条的坐标换算。</summary>
[TestClass]
public class ColorPickerPartsTests
{
    [TestMethod]
    [DataRow("#E05252", (byte)0xE0, (byte)0x52, (byte)0x52)]
    [DataRow("e05252", (byte)0xE0, (byte)0x52, (byte)0x52)]
    [DataRow("  #e05252  ", (byte)0xE0, (byte)0x52, (byte)0x52)]
    [DataRow("#F80", (byte)0xFF, (byte)0x88, (byte)0x00)]
    public void TryParse_AcceptsTheHexFormsTheSettingsUse(string text, byte r, byte g, byte b)
    {
        Assert.IsTrue(ColorHex.TryParse(text, out Color color));
        Assert.AreEqual((r, g, b), (color.R, color.G, color.B));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("#12345")]
    [DataRow("#GG0000")]
    // 颜色名 Color.TryParse 认,取色器不认:框里敲个 "red" 应当是没填对,而不是悄悄变成一个颜色。
    [DataRow("red")]
    [DataRow("Transparent")]
    public void TryParse_RejectsAnythingThatIsNotPlainHex(string? text) =>
        Assert.IsFalse(ColorHex.TryParse(text, out _));

    [TestMethod]
    public void Format_IsUppercaseSixDigitsWithoutAlpha() =>
        // 终端四色只认六位:写出去带 alpha 的话,TerminalAppearanceMapper 解析失败,整套落回出厂色。
        Assert.AreEqual("#0A0B0C", ColorHex.Format(Color.FromArgb(0x80, 0x0A, 0x0B, 0x0C)));

    [TestMethod]
    public void ParseThenFormat_RoundTrips()
    {
        Assert.IsTrue(ColorHex.TryParse("#bd93f9", out Color color));
        Assert.AreEqual("#BD93F9", ColorHex.Format(color));
    }

    [TestMethod]
    public void SpectrumPad_MapsCornersToSaturationAndValue()
    {
        Size size = new(200, 100);
        Assert.AreEqual((0d, 1d), ColorSpectrumPad.FromPoint(new Point(0, 0), size), "左上 = 白");
        Assert.AreEqual((1d, 1d), ColorSpectrumPad.FromPoint(new Point(200, 0), size), "右上 = 纯色");
        Assert.AreEqual((0.5, 0.5), ColorSpectrumPad.FromPoint(new Point(100, 50), size));
        Assert.AreEqual((1d, 0d), ColorSpectrumPad.FromPoint(new Point(200, 100), size), "下沿 = 黑");
    }

    [TestMethod]
    public void SpectrumPad_ClampsPointsDraggedOutsideItsBounds() =>
        // 拖出面板之外(捕获着指针)要停在边上,而不是算出负的饱和度。
        Assert.AreEqual((1d, 0d), ColorSpectrumPad.FromPoint(new Point(500, 400), new Size(200, 100)));

    [TestMethod]
    public void SpectrumPad_CoercesOutOfRangeValues()
    {
        ColorSpectrumPad pad = new() { Saturation = 1.7, Value = -0.2 };
        Assert.AreEqual(1d, pad.Saturation);
        Assert.AreEqual(0d, pad.Value);
    }

    [TestMethod]
    public void HueStrip_MapsTheTrackBetweenTheThumbRadii()
    {
        // 高 14 → 滑块半径 7:圆心只在 [7, 宽 − 7] 之间走,两端的滑块不会被裁掉一半。
        Size size = new(214, 14);
        Assert.AreEqual(0d, HueStrip.FromX(7, size));
        Assert.AreEqual(180d, HueStrip.FromX(107, size), 0.001);
        Assert.AreEqual(360d, HueStrip.FromX(207, size));
        Assert.AreEqual(0d, HueStrip.FromX(-30, size), "左边那一小段就是 0");
        Assert.AreEqual(360d, HueStrip.FromX(400, size), "右边那一小段就是 360");
    }

    [TestMethod]
    public void HueStrip_CoercesIntoZeroToThreeSixty()
    {
        HueStrip strip = new() { Hue = 400 };
        Assert.AreEqual(360d, strip.Hue);
        strip.Hue = -5;
        Assert.AreEqual(0d, strip.Hue);
    }
}
