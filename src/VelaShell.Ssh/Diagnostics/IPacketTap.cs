// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §9

namespace VelaShell.Ssh.Diagnostics;

/// <summary>
/// 报文旁路：每个收发的报文调一次（连接诊断面板、协议级排错、类似 <c>ssh -vvv</c> 的轨迹）。
/// </summary>
/// <remarks>
/// <para>
/// 〔velashell-docs/zh/ssh/spec/08 §9〕<b>默认不启用</b>（<c>SshConnectionOptions.PacketTap</c> 为 <see langword="null"/> 时只多一次判空）。
/// 回调跑在收发循环上，<b>必须很快、不能阻塞</b>；它抛的异常被吞掉 —— 旁路出错不该弄坏连接。
/// </para>
/// <para>
/// <b>载荷默认不给</b>；打开 <c>PacketTapIncludesPayload</c> 之后，通道数据里有什么就带出什么：
/// 终端里敲的口令（<c>sudo</c>）、传输的文件内容、转发的流量。<b>认证报文的载荷永远不给</b>（没有开关）——
/// 没有哪种排错值得把密码打进日志。
/// </para>
/// </remarks>
public interface IPacketTap
{
    /// <summary>看到一个报文。</summary>
    /// <param name="record">报文的元信息（与可能的载荷），只在这次调用期间有效。</param>
    void OnPacket(in PacketTapRecord record);
}
