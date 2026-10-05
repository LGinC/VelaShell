// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §7             publickey 认证的签名输入
//   OpenSSH PROTOCOL        publickey-hostbound-v00@openssh.com
//   OpenSSH PROTOCOL.agent  session-bind@openssh.com
//   OpenSSH PROTOCOL.sshsig SSHSIG 的签名输入
//   行为规格:               velashell-docs/zh/ssh/spec/07-forwarding.md §7.2.1

using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Forwarding;

/// <summary>从远端要 agent 签的数据里认出「签来做什么」，给逐次确认用。</summary>
/// <remarks>
/// 只为<b>给人看</b>：认不出来就什么都不填，绝不因此拒签或改动转给 agent 的请求。
/// 远端给的每个字节都不可信 —— 能当真的只有签名本身绑死的那几项（见 <see cref="AgentSignatureRequest"/>）。
/// </remarks>
internal static class AgentSignedData
{
    /// <summary>单个字段的长度上限：用户名、服务名、主机密钥、会话标识都远小于它。</summary>
    private const int MaxFieldBytes = 16 * 1024;

    /// <summary>进界面的文本最多留多少字符。</summary>
    private const int MaxDisplayLength = 128;

    /// <summary>SSHSIG 签名输入的前导魔数（OpenSSH PROTOCOL.sshsig）。</summary>
    private static ReadOnlySpan<byte> SshSigMagic => "SSHSIG"u8;

    /// <summary>远端那一跳经 agent 通道声明过的一个会话：签名已经验过。</summary>
    internal sealed record SessionBinding(byte[] SessionId, SshPublicKey HostKey);

    /// <summary>读一条 <c>session-bind@openssh.com</c>，<b>验过签名</b>才交回。</summary>
    /// <param name="request">一条完整的 agent 报文（不含长度前缀）。</param>
    /// <returns>格式不对、主机密钥认不出、签名验不过时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// ⚠️ 不能指望本机 agent 替我们验：agent 不认这个扩展（Pageant、旧版 Windows agent）时一律回 FAILURE，
    /// 认它的 agent 回的 SUCCESS 我们也分不出是不是验过。不自己验的话，远端随手编一把「你信任的主机」的公钥，
    /// 确认框就会说「要登录 github.com」。
    /// </remarks>
    internal static SessionBinding? TryReadSessionBinding(ReadOnlySpan<byte> request)
    {
        if (!SshAgentClient.IsSessionDeclaration(request))
        {
            return null;
        }

        try
        {
            SshDataReader reader = new(request);
            reader.ReadByte();
            reader.ReadString(MaxFieldBytes);   // 扩展名，IsSessionDeclaration 已经核过
            byte[] hostKeyBlob = reader.ReadStringAsArray(MaxFieldBytes);
            byte[] sessionId = reader.ReadStringAsArray(MaxFieldBytes);
            byte[] signature = reader.ReadStringAsArray(MaxFieldBytes);
            reader.ReadBoolean();   // is_forwarding：给人看用不上

            SshPublicKey hostKey = SshPublicKey.Decode(hostKeyBlob);
            return sessionId.Length > 0 && SignedBy(hostKey, signature, sessionId)
                ? new SessionBinding(sessionId, hostKey)
                : null;
        }
        catch (SshWireFormatException)
        {
            return null;
        }
        catch (SshPublicKeyException)
        {
            return null;
        }
    }

    /// <summary>给一次签名请求填上能认出来的上下文。</summary>
    /// <param name="key">要用的钥。</param>
    /// <param name="comment">它在 agent 里的注释。</param>
    /// <param name="data">远端要签的数据。</param>
    /// <param name="bindings">这条 agent 通道上验过的会话声明。</param>
    internal static AgentSignatureRequest Describe(
        SshPublicKey key, string comment, ReadOnlySpan<byte> data, IReadOnlyList<SessionBinding> bindings)
    {
        AgentSignatureRequest request = new(key, comment);

        if (data.StartsWith(SshSigMagic))
        {
            return TryReadNamespace(data[SshSigMagic.Length..]) is { } ns
                ? request with { SignatureNamespace = ns }
                : request;
        }

        return TryReadUserAuthentication(key, data, bindings, request) ?? request;
    }

    /// <summary>SSHSIG：<c>"SSHSIG" ‖ string namespace ‖ string reserved ‖ string hash_algorithm ‖ string H(message)</c>。</summary>
    private static string? TryReadNamespace(ReadOnlySpan<byte> body)
    {
        try
        {
            SshDataReader reader = new(body);
            string ns = reader.ReadUtf8String(MaxFieldBytes, strict: true);
            return ns.Length == 0 ? null : PeerText.Sanitize(ns, MaxDisplayLength);
        }
        catch (SshWireFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// RFC 4252 §7 的签名输入：<c>string session_id ‖ byte 50 ‖ string user ‖ string service ‖ string "publickey"
    /// ‖ bool TRUE ‖ string algorithm ‖ string key</c>，hostbound 方法末尾再加 <c>string hostkey</c>。
    /// </summary>
    private static AgentSignatureRequest? TryReadUserAuthentication(
        SshPublicKey key, ReadOnlySpan<byte> data, IReadOnlyList<SessionBinding> bindings, AgentSignatureRequest request)
    {
        try
        {
            SshDataReader reader = new(data);
            byte[] sessionId = reader.ReadStringAsArray(MaxFieldBytes);
            if (sessionId.Length == 0 || reader.ReadMessageNumber() != SshMessageNumber.UserAuthRequest)
            {
                return null;
            }

            string user = reader.ReadUtf8String(MaxFieldBytes, strict: true);
            string service = reader.ReadUtf8String(MaxFieldBytes, strict: true);
            string method = reader.ReadUtf8String(MaxFieldBytes, strict: true);
            bool hostBound = method == SshProtocolNames.AuthPublicKeyHostBound;
            if (!hostBound && method != SshProtocolNames.AuthPublicKey)
            {
                return null;
            }

            if (!reader.ReadBoolean())
            {
                return null;   // 签名输入里这一位必须为真；为假的那种请求根本不带签名
            }

            reader.ReadUtf8String(MaxFieldBytes, strict: true);   // 签名算法：给人看用不上
            byte[] keyBlob = reader.ReadStringAsArray(MaxFieldBytes);

            // 登录请求里出示的钥必须就是要拿来签的这一把 —— 不是的话，这份签名哪儿也登录不了，
            // 摆出里面的用户名只会误导人。
            if (!keyBlob.AsSpan().SequenceEqual(key.Blob.Span))
            {
                return null;
            }

            SshPublicKey? destination = hostBound
                ? SshPublicKey.Decode(reader.ReadStringAsArray(MaxFieldBytes))
                : FindBinding(bindings, sessionId);
            reader.ExpectEnd("userauth 签名输入");

            return request with
            {
                UserName = PeerText.Sanitize(user, MaxDisplayLength),
                Service = PeerText.Sanitize(service, MaxDisplayLength),
                DestinationHostKey = destination,
            };
        }
        catch (SshWireFormatException)
        {
            return null;
        }
        catch (SshPublicKeyException)
        {
            return null;
        }
    }

    private static SshPublicKey? FindBinding(IReadOnlyList<SessionBinding> bindings, byte[] sessionId)
    {
        // 从后往前找：同一个会话标识只会出自同一次密钥交换，重复声明时哪一条都一样，取最近的。
        for (int i = bindings.Count - 1; i >= 0; i--)
        {
            if (bindings[i].SessionId.AsSpan().SequenceEqual(sessionId))
            {
                return bindings[i].HostKey;
            }
        }
        return null;
    }

    /// <summary>签名 blob 是这把主机密钥对 <paramref name="data"/> 的签名（算法取 blob 里写的那个，但必须是这把钥能用的）。</summary>
    private static bool SignedBy(SshPublicKey hostKey, byte[] signatureBlob, byte[] data)
    {
        string algorithm;
        try
        {
            SshDataReader reader = new(signatureBlob);
            algorithm = reader.ReadUtf8String(MaxFieldBytes, strict: true);
        }
        catch (SshWireFormatException)
        {
            return false;
        }

        return hostKey.SupportsSignatureAlgorithm(algorithm)
            && hostKey.VerifySignature(signatureBlob, data, algorithm);
    }
}
