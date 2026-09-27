namespace VelaShell.Core.Ssh;

/// <summary>
/// 伪终端尺寸:字符网格的行列,外加整个网格的物理像素宽高。
/// </summary>
/// <remarks>
/// <para>
/// 像素是 SSH <c>window-change</c>(RFC 4254 §6.7)的另两个字段,远端内核把它们放进
/// <c>TIOCGWINSZ</c> 的 <c>ws_xpixel</c> / <c>ws_ypixel</c>。sixel、kitty 图形协议与按像素排版的 TUI
/// 靠它换算单元格尺寸(像素 ÷ 行列);写死 0 就等于告诉它们「不知道」。
/// </para>
/// <para>
/// 取值是<b>物理像素</b>:单元格尺寸(DIP)× 行列 × 显示缩放。只收行列的传输(ConPTY、插件协议)
/// 直接忽略这两项;<c>0</c> 表示「不知道」,各传输照常发 0。
/// </para>
/// </remarks>
/// <param name="Columns">字符列数。</param>
/// <param name="Rows">字符行数。</param>
/// <param name="PixelWidth">网格的物理像素宽度;<c>0</c> 表示不知道。</param>
/// <param name="PixelHeight">网格的物理像素高度;<c>0</c> 表示不知道。</param>
public readonly record struct PtySize(int Columns, int Rows, int PixelWidth = 0, int PixelHeight = 0);
