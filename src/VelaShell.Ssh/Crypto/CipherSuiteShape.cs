// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6      Binary Packet Protocol
//   RFC 5647 §7.1    AES-GCM 的长度字段是明文 AAD、且不参与对齐
//   OpenSSH PROTOCOL.chacha20poly1305
//   OpenSSH PROTOCOL 的 *-etm@openssh.com
//   行为规格:        velashell-docs/zh/ssh/spec/01-transport-framing.md §2

namespace VelaShell.Ssh.Crypto;

/// <summary>
/// 一套密码套件在**分帧**上的形状。
/// </summary>
/// <remarks>
/// <para>
/// 不同套件在分帧上的差异只有这几个维度。把它们抽成数据，帧层就不必为每种算法
/// 写一遍「先读几个字节、先解密还是先验证」的分支 —— 那些分支正是
/// AEAD 与非 AEAD 混在一起时最容易出错的地方。
/// </para>
/// <para>
/// ⚠️ 形状由「加密算法 + MAC 算法」的**组合**决定，不是加密算法单独决定：AES-CTR 配 MtE 时长度字段进对齐、配 EtM 时不进。
/// 因此这个结构由套件的**组装函数**产出，而不是两个独立枚举的笛卡尔积。
/// </para>
/// <para>
/// 只留帧层与测试真正读的字段（TR-D4）：曾经还有「长度是否加密」「AAD 字节数」「是否 EtM」「先读几个字节」四项，
/// 没有任何代码读 —— 各套件的收包逻辑本来就自己知道这些，留着只是一份会和实现漂开的副本。
/// </para>
/// </remarks>
internal readonly record struct CipherSuiteShape
{
    /// <summary>AEAD tag 或 MAC 的字节数。<c>none</c> 套件为 0。</summary>
    public required int TagBytes { get; init; }

    /// <summary>填充对齐的块大小。<b>至少为 8</b>（RFC 4253 §6）。</summary>
    public required int BlockBytes { get; init; }

    /// <summary>
    /// 4 字节的长度字段是否计入填充对齐。
    /// </summary>
    /// <remarks>
    /// 普通套件为 <see langword="true"/>（对齐的是 <c>4 + 1 + payload + padding</c>）。
    /// <b>AEAD 套件为 <see langword="false"/></b> —— RFC 5647 与
    /// OpenSSH 的 chacha20-poly1305 都规定对齐的是 <c>1 + payload + padding</c>，
    /// 不含长度字段。
    /// <para>
    /// 这一条是 AEAD 实现最常见的错位来源：写错了在某些包长上才会暴露，
    /// 短包全对、长包忽然对不上。
    /// </para>
    /// </remarks>
    public required bool LengthInAlignment { get; init; }

    /// <summary>这套套件是否提供保密性（<c>none</c> 之外都是）。</summary>
    public required bool IsEncrypted { get; init; }

    /// <summary>不加密、不认证的握手期套件形状。</summary>
    public static CipherSuiteShape Plaintext { get; } = new()
    {
        TagBytes = 0,
        BlockBytes = 8,
        LengthInAlignment = true,
        IsEncrypted = false,
    };
}
