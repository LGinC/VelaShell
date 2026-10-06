// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §8

namespace VelaShell.Ssh.Session;

/// <summary>最近一次密钥重协商的起因（<see cref="SshConnection.LastRekey"/>）。</summary>
/// <param name="Trigger">怎么来的。</param>
/// <param name="Observed">
/// 到阈值时的观测值：字节数、报文数，或者距上次交换的毫秒数。对端发起与显式请求时为 0。
/// </param>
/// <param name="Threshold">对应的阈值（单位同 <paramref name="Observed"/>）。对端发起与显式请求时为 0。</param>
public sealed record SshRekeyCause(SshRekeyTrigger Trigger, long Observed = 0, long Threshold = 0);
