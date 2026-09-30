namespace VelaShell.ViewModels;

/// <summary>
/// 属性弹窗点「确定」后带回的变更:只带改了的项,没改的为 null。
/// </summary>
/// <param name="Mode">新的三位八进制权限(按十进制书写,如 755)。</param>
/// <param name="Owner">新的属主(用户名或数字 UID)。</param>
/// <param name="Group">新的属组(组名或数字 GID)。</param>
public sealed record FilePropertiesChange(short? Mode, string? Owner, string? Group)
{
    /// <summary>什么都没改。</summary>
    public bool IsEmpty => Mode is null && Owner is null && Group is null;
}
