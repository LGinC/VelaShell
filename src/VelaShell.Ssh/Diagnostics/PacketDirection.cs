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
