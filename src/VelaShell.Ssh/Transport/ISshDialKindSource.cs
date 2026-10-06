// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/09-dialing.md §2.2

namespace VelaShell.Ssh.Transport;

/// <summary>本库自己的拨号器报出自己是哪一类，进跳信息（<see cref="Diagnostics.SshHopInfo.Kind"/>）。</summary>
/// <remarks>
/// 曾经是 <see cref="ISshTransportDialer"/> 的公开成员，逼每个实现者提前声明一个静态的种类 ——
/// 按每次拨号现选路的实现（这次直连、下次经代理）给不出真值，只好记「上一次」。
/// 现在只有库自己的拨号器报种类，使用者实现的一律记 <see cref="SshDialKind.Custom"/>：那是唯一不会说错的答案。
/// </remarks>
internal interface ISshDialKindSource
{
    /// <summary>这个拨号器属于哪一类。</summary>
    SshDialKind Kind { get; }
}
