using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Docking;

/// <summary>
/// 独立文件标签的共同基类:「本地 + 远程」的 <see cref="SftpDocument" /> 与「远程 + 远程」的
/// <see cref="DualSftpDocument" />。
/// </summary>
/// <remarks>
/// 标签页外观(<c>SftpDockTabItem</c>)、关标签的收尾、退出前的排空都按这个基类处理 ——
/// 新增一种文件标签不必再到宿主里逐处补一个 <c>is</c> 分支,漏补的后果是关掉标签后连接没断。
/// </remarks>
public abstract class SftpDocumentBase : DockDocument, IDockViewProvider
{
    /// <summary>标签页与关闭流程共用的那一面视图模型。</summary>
    public abstract ISftpDocumentContent Content { get; }

    /// <summary>标签页色条与图标的强调色。</summary>
    public abstract IBrush ConnectionAccentBrush { get; }

    /// <summary>标签页上的协议图标。</summary>
    public abstract TabIcon? TabIcon { get; }

    /// <summary>悬停标题时的连接详情。</summary>
    public abstract string ConnectionTooltip { get; }

    /// <inheritdoc />
    public abstract Control CreateView();
}
