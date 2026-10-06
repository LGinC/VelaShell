// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §8

namespace VelaShell.Ssh.Session;

/// <summary>一次密钥重协商是怎么来的。</summary>
public enum SshRekeyTrigger
{
    /// <summary>说不清（零值）。</summary>
    Unknown = 0,

    /// <summary>对端发起。</summary>
    Peer,

    /// <summary>调用方显式调了 <see cref="SshConnection.StartRekeyAsync"/>。</summary>
    Requested,

    /// <summary>单向字节数到了策略的阈值（<see cref="SshRekeyPolicy.MaxBytes"/>）。</summary>
    Bytes,

    /// <summary>单向报文数到了策略的阈值（<see cref="SshRekeyPolicy.MaxPackets"/>）。</summary>
    Packets,

    /// <summary>距上次密钥交换的时长到了策略的阈值（<see cref="SshRekeyPolicy.MaxInterval"/>）。</summary>
    Interval,

    /// <summary>同一套密钥下的单向报文数到了与策略无关的硬线（序号回绕之前必须换钥）。</summary>
    PacketHardLimit,
}
