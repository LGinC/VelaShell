// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4254 §6.10  exit-status / exit-signal
//   RFC 4250 §4.2   信号名
//   行为规格:       velashell-docs/zh/ssh/spec/05-connection.md §1、§5.4

namespace VelaShell.Ssh.Channels;

/// <summary>通道上发生的一件事。</summary>
/// <remarks>
/// <para>
/// 数据走 <c>PipeReader</c>，**其余一切走这条事件流** ——
/// 退出状态、半关闭、关闭、对端发来的通道请求。
/// </para>
/// <para>
/// 把它们做成一条有序的流而不是若干个回调或事件，
/// 是因为顺序本身携带信息：<c>exit-status</c> 在 <c>Eof</c> 之前还是之后到，
/// 决定了「命令还没输出完就退出了」这类判断能不能做。
/// </para>
/// </remarks>
public abstract record SshChannelEvent
{
    /// <summary>对端不再发送数据（<c>CHANNEL_EOF</c>）。</summary>
    /// <remarks>
    /// <b>这不是「通道结束」。</b>EOF 是单向的半关闭：
    /// 对端发了 EOF 之后我们仍然可以继续往它那边发数据
    /// （<c>ssh host 'cat &gt; f' &lt; big</c> 正是这个形状）。
    /// </remarks>
    public sealed record Eof : SshChannelEvent;

    /// <summary>远端进程正常退出。</summary>
    /// <param name="Code">退出码。</param>
    public sealed record ExitStatus(int Code) : SshChannelEvent;

    /// <summary>远端进程被信号杀死。</summary>
    /// <param name="SignalName">信号名，<b>不带 <c>SIG</c> 前缀</b>（<c>"TERM"</c> 而非 <c>"SIGTERM"</c>）。</param>
    /// <param name="CoreDumped">是否产生了核心转储。</param>
    /// <param name="ErrorMessage">对端给的错误说明（<b>不可信文本</b>）。</param>
    /// <remarks>
    /// 〔决策 velashell-docs/zh/ssh/spec/05 §5.4〕<b>不把信号编成 128+n 这样的伪退出码。</b>
    /// 那是 shell 的约定，不是 SSH 的；伪造它会让「进程返回 137」
    /// 和「进程被 KILL」在调用方眼里无法区分。
    /// </remarks>
    public sealed record ExitSignal(string SignalName, bool CoreDumped, string ErrorMessage) : SshChannelEvent;

    /// <summary>通道已关闭，不会再有任何事件。</summary>
    /// <param name="Reason">为什么关的。</param>
    public sealed record Closed(SshChannelCloseReason Reason) : SshChannelEvent;

    /// <summary>服务端告诉客户端能不能在本地做 ^S / ^Q 流控（<c>xon-xoff</c>，RFC 4254 §6.8）。</summary>
    /// <param name="ClientCanDo">
    /// <see langword="true"/>：客户端可以在本地处理 ^S / ^Q（暂停 / 继续显示），不必把它们发给远端；
    /// <see langword="false"/>：照常当普通按键发出去。客户端也可以不理会这条消息（RFC 允许）。
    /// </param>
    /// <remarks>最近一次的值另见 <see cref="SshChannel.ClientMayDoFlowControl"/>。</remarks>
    public sealed record FlowControl(bool ClientCanDo) : SshChannelEvent;

    /// <summary>对端发来一个我们没有专门处理的通道请求。</summary>
    /// <param name="RequestType">请求类型。</param>
    /// <param name="Payload">类型相关的数据（<b>未解析，不可信</b>）。</param>
    /// <remarks>
    /// 库已经按协议回过应答了（<c>want_reply</c> 为真时回 <c>CHANNEL_FAILURE</c>）——
    /// 这条事件只是让使用者**看得见**，不需要也不应该再去应答。
    /// </remarks>
    public sealed record PeerRequest(string RequestType, ReadOnlyMemory<byte> Payload) : SshChannelEvent;
}

/// <summary>通道为什么关闭。</summary>
public enum SshChannelCloseReason
{
    /// <summary>
    /// 零值，本库不产出 —— 只在 <c>default</c> 时出现，读到它说明这个值没被设置过。
    /// </summary>
    /// <remarks>
    /// 〔AGENTS 4.3〕枚举的零值必须是安全的那个。曾经零值是 <c>Normal</c>（「双向 CLOSE 正常走完」），
    /// 库却从不产出它：一个没赋值的 <c>default</c> 读起来就像「正常关闭」。
    /// </remarks>
    Unknown,

    /// <summary>对端先发的 <c>CHANNEL_CLOSE</c>。</summary>
    ClosedByPeer,

    /// <summary>本端主动关的。</summary>
    ClosedLocally,

    /// <summary>
    /// 会话没了，通道跟着没。
    /// </summary>
    /// <remarks>
    /// 这种情况下**退出状态多半没有收到** —— 调用方的 <c>ExitCode</c> 会是
    /// <see langword="null"/>，那不是 bug 而是事实：进程到底怎么结束的，我们不知道。
    /// </remarks>
    SessionClosed,
}

