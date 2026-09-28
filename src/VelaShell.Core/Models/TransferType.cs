namespace VelaShell.Core.Models;

/// <summary>传输任务的方向类型:上传、下载、同服务器复制,或两台远端之间的中转。</summary>
public enum TransferType
{
    /// <summary>上传:将本地文件传输到远端。</summary>
    Upload,
    /// <summary>下载:将远端文件传输到本地。</summary>
    Download,
    /// <summary>远端复制:在同一服务器上将文件/目录复制到另一路径。</summary>
    Copy,
    /// <summary>
    /// 远端中转:把另一个会话上的文件经本机流式搬到本会话(双栏远程文档)。
    /// 字节只在内存里过一下,不落本地磁盘。
    /// </summary>
    Relay
}
