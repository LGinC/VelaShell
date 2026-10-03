using System.Text.Json;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 自定义键位的载入规整(#551):老配置没有这一节,手改的配置可能写出 null;
/// 「空串 = 解绑」必须原样留着,它与「没改过」是两回事。
/// </summary>
[TestClass]
[TestCategory("Settings")]
public class ShortcutOptionsNormalizeTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [TestMethod]
    public void LegacyConfig_WithoutSection_HasNoOverrides()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("""{"language":"en"}""", Json)!;

        settings.Normalize();

        Assert.IsNotNull(settings.Shortcuts);
        Assert.IsEmpty(settings.Shortcuts.Overrides);
    }

    [TestMethod]
    public void ExplicitNulls_AreReplaced()
    {
        AppSettings withoutSection = JsonSerializer.Deserialize<AppSettings>("""{"shortcuts":null}""", Json)!;
        AppSettings withoutTable = JsonSerializer.Deserialize<AppSettings>("""{"shortcuts":{"overrides":null}}""", Json)!;

        withoutSection.Normalize();
        withoutTable.Normalize();

        Assert.IsEmpty(withoutSection.Shortcuts.Overrides);
        Assert.IsEmpty(withoutTable.Shortcuts.Overrides);
    }

    /// <summary>值为 null 的条目既不是解绑也不是手势,丢掉回到出厂键位;空串(解绑)与手势原样保留。</summary>
    [TestMethod]
    public void NullEntries_AreDropped_UnbindingIsKept()
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(
            """{"shortcuts":{"overrides":{"app.palette":"Ctrl+Shift+P","session.close":"","view.sidebar":null}}}""", Json)!;

        settings.Normalize();

        Assert.HasCount(2, settings.Shortcuts.Overrides);
        Assert.AreEqual("Ctrl+Shift+P", settings.Shortcuts.Overrides["app.palette"]);
        Assert.AreEqual("", settings.Shortcuts.Overrides["session.close"]);
        Assert.IsFalse(settings.Shortcuts.Overrides.ContainsKey("view.sidebar"));
    }

    [TestMethod]
    public void Overrides_RoundTripThroughJson()
    {
        AppSettings settings = new();
        settings.Shortcuts.Overrides = new() { ["app.palette"] = "Ctrl+Shift+P", ["session.close"] = "" };

        AppSettings back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
        back.Normalize();

        Assert.AreEqual("Ctrl+Shift+P", back.Shortcuts.Overrides["app.palette"]);
        Assert.AreEqual("", back.Shortcuts.Overrides["session.close"]);
    }
}
