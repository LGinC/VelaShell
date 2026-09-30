using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using VelaShell.Core.Resources;

namespace VelaShell.Converters;

/// <summary>UTC 时间 → 本地时间文本(云同步版本历史)。</summary>
public sealed class UtcToLocalTextConverter : IValueConverter
{
    /// <summary>可复用的单例实例,供 XAML 绑定直接引用。</summary>
    public static readonly UtcToLocalTextConverter Instance = new();

    /// <summary>把 UTC 时间转为本地时区的 "yyyy-MM-dd HH:mm:ss" 文本;空值或最小值返回空串。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime utc && utc != DateTime.MinValue
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "";

    /// <summary>单向展示转换器,不支持回写,始终返回 <see cref="BindingOperations.DoNothing" /> 静默忽略。</summary>
    // 单向展示转换器:Run.Text 等绑定在控件卸载等时机会尝试回写,
    // 返回 DoNothing 静默忽略,不能抛异常(会冒泡成运行时错误)。
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>版本历史里的设备名;没记下设备名的旧版本显示「未知设备」(按当前界面语言)。</summary>
/// <remarks>
/// 原先写的是 <c>TargetNullValue=未知设备</c>:那是写死在 XAML 里的中文,绑定属性里又没法套 {loc:Localize}。
/// </remarks>
public sealed class DeviceNameConverter : IValueConverter
{
    /// <summary>可复用的单例实例,供 XAML 绑定直接引用。</summary>
    public static readonly DeviceNameConverter Instance = new();

    /// <summary>有设备名原样返回,空值或空串返回本地化的「未知设备」。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } name ? name : Strings.Get("SetSync_UnknownDevice");

    /// <summary>单向展示转换器,不支持回写,始终返回 <see cref="BindingOperations.DoNothing" /> 静默忽略。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>长版本号(Gist revision SHA)截为前 10 位展示。</summary>
public sealed class ShortShaConverter : IValueConverter
{
    /// <summary>可复用的单例实例,供 XAML 绑定直接引用。</summary>
    public static readonly ShortShaConverter Instance = new();

    /// <summary>将长版本号截取为前 10 位;非字符串或已足够短时原样返回。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string sha && sha.Length > 10 ? sha[..10] : value ?? "";

    /// <summary>单向展示转换器,不支持回写,始终返回 <see cref="BindingOperations.DoNothing" /> 静默忽略。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
