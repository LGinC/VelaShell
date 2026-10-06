// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §8

using VelaShell.Ssh.Crypto;

namespace VelaShell.Ssh.Session;

/// <summary>一次重协商做完了（<see cref="SshConnection.Rekeyed"/>）。</summary>
public sealed class SshRekeyEventArgs : EventArgs
{
    internal SshRekeyEventArgs(SshRekeyCause cause, TimeSpan duration, int count, SshNegotiatedAlgorithms algorithms)
    {
        Cause = cause;
        Duration = duration;
        Count = count;
        Algorithms = algorithms;
    }

    /// <summary>这一次是怎么来的（与 <see cref="SshConnection.LastRekey"/> 相同）。</summary>
    public SshRekeyCause Cause { get; }

    /// <summary>从收到对端的 <c>KEXINIT</c> 到新密钥装好用了多久（期间通道数据暂存）。</summary>
    public TimeSpan Duration { get; }

    /// <summary>这是第几次重协商（与 <see cref="SshConnection.RekeyCount"/> 相同）。</summary>
    public int Count { get; }

    /// <summary>这一次协商出来的算法。</summary>
    public SshNegotiatedAlgorithms Algorithms { get; }
}
