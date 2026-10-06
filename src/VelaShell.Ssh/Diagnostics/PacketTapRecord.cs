// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/08-failures.md §9

namespace VelaShell.Ssh.Diagnostics;

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
    /// 载荷（含消息编号）。<b>默认为空</b>：要给得显式打开 <c>SshConnectionOptions.AllowPacketTapPayload</c>；
    /// 认证报文（50–79）的载荷无论如何都不给。
    /// </summary>
    public ReadOnlySpan<byte> Payload { get; }
}
