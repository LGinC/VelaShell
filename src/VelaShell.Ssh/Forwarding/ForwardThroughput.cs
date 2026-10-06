// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/07-forwarding.md §5.1

namespace VelaShell.Ssh.Forwarding;

/// <summary>一个转发器此刻的吞吐（应用字节 / 秒，本机视角）。</summary>
/// <param name="SentPerSecond">本机送进隧道的速率。</param>
/// <param name="ReceivedPerSecond">从隧道收回本机的速率。</param>
/// <remarks>取最近三个整秒的平均（不含正在走的这一秒）：比瞬时值稳，面板上不会一跳一跳。</remarks>
public readonly record struct ForwardThroughput(double SentPerSecond, double ReceivedPerSecond);
