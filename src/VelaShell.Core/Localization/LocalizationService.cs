using System.Globalization;
using System.Resources;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Localization;

/// <summary>基于资源程序集的本地化服务:自持目标文化,提供取词与运行时切换语言。</summary>
public class LocalizationService : ILocalizationService
{
    private readonly ResourceManager _resourceManager = new("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);

    // 服务自持目标文化,取词不依赖线程环境:CurrentUICulture 是线程级状态且随
    // ExecutionContext 流动 —— 在异步命令(设置保存)里改它会随上下文回卷丢失;
    // 而 UI 线程启动时被显式设置过文化后,DefaultThreadCurrentUICulture 也不再
    // 影响它。两条路都靠不住,曾表现为“保存后界面不换语言”。
    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    // 系统界面文化在构造时快照:SetLanguage 会改写环境文化,之后再读 CurrentUICulture
    // 拿到的是上一次选的语言 —— 从「日本語」切回「跟随系统」就会停在日语。
    private readonly CultureInfo _systemCulture = CultureInfo.CurrentUICulture;

    /// <summary>按键取当前文化下的本地化字符串;缺失或资源不可用时回退返回键本身。</summary>
    public string GetString(string key)
    {
        try
        {
            string? value = _resourceManager.GetString(key, _culture);
            return value ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
        catch (MissingSatelliteAssemblyException)
        {
            return key;
        }
    }

    /// <summary>当前语言的文化名称(如 zh-CN)。</summary>
    public string CurrentLanguage => _culture.Name;

    /// <summary>语言切换成功后触发,携带新的文化名称。</summary>
    public event Action<string>? LanguageChanged;

    /// <summary>
    /// 把系统界面文化折算成五种界面语言之一:沿父文化链找,中文按书写体系归到简 / 繁
    /// (zh-SG → zh-Hans → zh-CN,zh-HK / zh-MO → zh-Hant → zh-TW);一个都对不上
    /// (德语、法语、LANG=C 的不变文化……)就用英文。
    /// </summary>
    public static string ResolveSystemLanguage(CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);
        for (CultureInfo culture = systemCulture; culture.Name.Length > 0; culture = culture.Parent)
        {
            switch (culture.Name)
            {
                case "zh-Hans":
                case "zh":
                    return "zh-CN";
                case "zh-Hant":
                    return "zh-TW";
                case "ja":
                case "ko":
                case "en":
                    return culture.Name;
            }
        }
        return "en";
    }

    /// <summary>
    /// 切换当前语言(空串 = 跟随系统,见 <see cref="ResolveSystemLanguage" />):与现有文化相同则跳过,
    /// 否则更新并尽力同步环境文化后触发 <see cref="LanguageChanged"/>。
    /// </summary>
    public void SetLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var culture = new CultureInfo(
            string.IsNullOrWhiteSpace(language) ? ResolveSystemLanguage(_systemCulture) : language
        );
        if (culture.Name == _culture.Name)
        {
            return;
        }
        _culture = culture;

        // 环境文化仍尽力同步:Default* 覆盖未显式设置文化的线程(含此后新建的),
        // Current* 覆盖当前流;UI 线程的线程级文化由宿主在 Dispatcher 顶层回调里
        // 补设(见 App.axaml.cs),供 C# 侧 Strings.Get 与日期/数字格式化使用。
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        LanguageChanged?.Invoke(culture.Name);
    }
}
