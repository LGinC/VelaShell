using Avalonia.Controls;

namespace VelaShell.Views;

/// <summary>「远程 + 远程」双栏文件标签的视图:两栏各是一个完整的远程文件浏览器,中间可拖动分隔。</summary>
public partial class DualSftpDocumentView : UserControl
{
    /// <summary>初始化双栏远程文档视图。</summary>
    public DualSftpDocumentView() => InitializeComponent();
}
