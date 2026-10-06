// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §9

namespace VelaShell.Ssh.Diagnostics;

/// <summary>报文的方向。</summary>
public enum PacketDirection
{
    /// <summary>收到的。</summary>
    Inbound,

    /// <summary>发出的。</summary>
    Outbound,
}

/// <summary>旁路看到的一个报文（明文载荷层：解密之后、解压之后；发出的在压缩与加密之前）。</summary>
/// <remarks>
/// 只在 <see cref="IPacketTap.OnPacket"/> 的调用期间有效：<see cref="Payload"/> 借的是传输的缓冲，回调返回之后就会被覆盖。要留就复制。
/// </remarks>
public readonly ref struct PacketTapRecord
{
    internal PacketTapRecord(
        PacketDirection direction, byte messageNumber, int length, uint sequenceNumber, uint? channelNumber, ReadOnlySpan<byte> payload)
    {
        Direction = direction;
        MessageNumber = messageNumber;
        Length = length;
        SequenceNumber = sequenceNumber;
        ChannelNumber = channelNumber;
        Payload = payload;
    }

    /// <summary>方向。</summary>
    public PacketDirection Direction { get; }

    /// <summary>消息编号（载荷的第一个字节）。</summary>
    public byte MessageNumber { get; }

    /// <summary>载荷长度（含消息编号）。</summary>
    public int Length { get; }

    /// <summary>这个方向上的报文序号（严格 KEX 下每次 NEWKEYS 归零）。</summary>
    public uint SequenceNumber { get; }

    /// <summary>通道消息（91–100）的接收方通道号；别的消息为 <see langword="null"/>。</summary>
    public uint? ChannelNumber { get; }

    /// <summary>
    /// 载荷（含消息编号）。<b>默认为空</b>：要给得显式打开 <c>SshConnectionOptions.PacketTapIncludesPayload</c>；
    /// 认证报文（50–79）的载荷无论如何都不给。
    /// </summary>
    public ReadOnlySpan<byte> Payload { get; }
}

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
