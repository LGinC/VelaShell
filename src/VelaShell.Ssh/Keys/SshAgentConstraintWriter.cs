// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 9987 §5.2.7      Key Constraints(有效期、逐次确认、扩展约束)
//   OpenSSH PROTOCOL.agent §2  restrict-destination-v00@openssh.com
//   行为规格:            velashell-docs/zh/ssh/spec/07-forwarding.md §7.3、§7.3.2

using System.Buffers;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Keys;

/// <summary>把 <see cref="SshAgentKeyConstraints"/> 写成 <c>SSH_AGENTC_ADD_ID_CONSTRAINED</c> 末尾的约束。</summary>
internal static class SshAgentConstraintWriter
{
    /// <summary>按有效期、逐次确认、目的地约束的次序写出约束（只在 <c>25</c> 里出现）。</summary>
    public static void Write(SshDataWriter writer, SshAgentKeyConstraints constraints)
    {
        if (constraints.Lifetime is { } lifetime)
        {
            writer.WriteByte(SshAgentMessage.ConstrainLifetime);
            writer.WriteUInt32((uint)Math.Clamp(Math.Ceiling(lifetime.TotalSeconds), 1, uint.MaxValue));
        }
        if (constraints.IsConfirmationRequired)
        {
            writer.WriteByte(SshAgentMessage.ConstrainConfirm);
        }
        if (constraints.AllowedHops is { } hops)
        {
            // 〔spec/07 §7.3.2〕所有的跳装在同一条扩展约束里。
            writer.WriteByte(SshAgentMessage.ConstrainExtension);
            writer.WriteUtf8String(SshAgentMessage.RestrictDestinationExtension);
            writer.WriteString(EncodeHops(hops));
        }
    }

    /// <summary>跳列表：一条条「跳」首尾相接，每条一个 string，没有条数字段。</summary>
    internal static byte[] EncodeHops(IReadOnlyList<SshAgentHop> hops)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        foreach (SshAgentHop hop in hops)
        {
            writer.WriteString(EncodeHop(hop));
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>一跳：string 起点 ‖ string 终点 ‖ string 保留（空）。</summary>
    private static byte[] EncodeHop(SshAgentHop hop)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteString(EncodeHost(userName: "", hop.Via));   // 起点的用户名必须为空
        writer.WriteString(EncodeHost(hop.UserName ?? "", hop.Destination));
        writer.WriteString([]);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 主机描述：string 用户名 ‖ string 主机名 ‖ string 保留（空）‖ 若干组「string 公钥 blob ‖ boolean 是否 CA」，没有条数字段。
    /// 从本机出发是空的主机描述（三个空 string、没有钥，共 12 字节）。
    /// </summary>
    private static byte[] EncodeHost(string userName, SshAgentHopHost? host)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String(userName);
        writer.WriteUtf8String(host?.Name ?? "");
        writer.WriteString([]);
        if (host is not null)
        {
            foreach (SshPublicKey key in host.HostKeys)
            {
                writer.WriteString(key.Blob.Span);
                writer.WriteBoolean(false);
            }
            foreach (SshPublicKey authority in host.CertificateAuthorities)
            {
                writer.WriteString(authority.Blob.Span);
                writer.WriteBoolean(true);
            }
        }
        return buffer.WrittenSpan.ToArray();
    }
}
