// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/00-overview.md §6

namespace VelaShell.Ssh.Crypto;

/// <summary>算法的类别（加密、MAC、压缩两个方向共用一份名单）。</summary>
public enum SshAlgorithmCategory
{
    /// <summary>密钥交换。</summary>
    KeyExchange,

    /// <summary>主机密钥（签名）算法。</summary>
    HostKey,

    /// <summary>加密。</summary>
    Encryption,

    /// <summary>MAC。</summary>
    Mac,

    /// <summary>压缩。</summary>
    Compression,
}
