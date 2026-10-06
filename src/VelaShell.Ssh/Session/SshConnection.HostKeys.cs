// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   OpenSSH PROTOCOL  hostkeys-00@openssh.com / hostkeys-prove-00@openssh.com(UpdateHostKeys)
//   行为规格:         velashell-docs/zh/ssh/spec/05-connection.md §6.4

using System.Buffers;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Session;

public sealed partial class SshConnection
{
    /// <summary>一次宣告里最多看多少把钥：多出来的不看（正常的服务端不过三五把）。</summary>
    private const int MaxAnnouncedHostKeys = 16;

    /// <summary>这条连接只做一次轮换：服务端重复宣告的不理。</summary>
    private int _hostKeysAnnounced;

    /// <summary>最近一次主机密钥轮换的结果；还没收到宣告（或者策略不做轮换）时为 <see langword="null"/>。</summary>
    public SshHostKeyUpdate? LastHostKeyUpdate { get; private set; }

    /// <summary>后台的轮换跑完（测试用）。</summary>
    internal Task HostKeyRotation { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// 〔velashell-docs/zh/ssh/spec/05 §6.4〕服务端宣告了它的全部主机密钥（<c>string</c> 公钥 blob，重复若干个）。
    /// 在接收循环上只解析、记下；证明与写 <c>known_hosts</c> 放到后台 —— 那要发全局请求、等应答。
    /// </summary>
    private void OnHostKeysAnnounced(ref SshDataReader reader)
    {
        if (RekeyContext?.HostKeyPolicy is not IHostKeyRotationPolicy { AllowHostKeyUpdates: true } rotation
            || Interlocked.Exchange(ref _hostKeysAnnounced, 1) != 0)
        {
            return;
        }

        List<byte[]> blobs = [];
        try
        {
            while (!reader.IsEmpty && blobs.Count < MaxAnnouncedHostKeys)
            {
                blobs.Add(reader.ReadStringAsArray(MaxFieldBytes));
            }
        }
        catch (SshWireFormatException)
        {
            LastHostKeyUpdate = new SshHostKeyUpdate([], "服务端的主机密钥宣告格式不对，没理会。");
            return;
        }

        SshRekeyContext context = RekeyContext;
        HostKeyRotation = Task.Run(() => RotateHostKeysAsync(rotation, context.Host, context.Port, blobs, _lifetime.Token));
    }

    private async Task RotateHostKeysAsync(
        IHostKeyRotationPolicy rotation, string host, int port, List<byte[]> blobs, CancellationToken cancellationToken)
    {
        try
        {
            LastHostKeyUpdate = await TryRotateAsync(rotation, host, port, blobs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 连接在收工。
        }
        catch (Exception ex) when (ex is SshException or IOException or UnauthorizedAccessException)
        {
            // 后台尽力而为：轮换没做成不影响这条连接。
            LastHostKeyUpdate = new SshHostKeyUpdate([], $"主机密钥轮换没做成：{ex.Message}");
        }
    }

    private async Task<SshHostKeyUpdate> TryRotateAsync(
        IHostKeyRotationPolicy rotation, string host, int port, List<byte[]> blobs, CancellationToken cancellationToken)
    {
        // 〔决策〕CA 管的主机（出示的是证书）不做：它的信任来自 CA，不来自 known_hosts 里的某一把钥。
        if (HostKey.IsCertificate)
        {
            return new SshHostKeyUpdate([], "这台主机出示的是证书（CA 管理），不做轮换。");
        }

        List<SshPublicKey> offered = [];
        foreach (byte[] blob in blobs)
        {
            try
            {
                SshPublicKey key = SshPublicKey.Decode(blob);
                if (!key.IsCertificate && key.SignatureAlgorithms.Count > 0
                    && !offered.Any(k => k.Blob.Span.SequenceEqual(key.Blob.Span)))
                {
                    offered.Add(key);
                }
            }
            catch (SshPublicKeyException)
            {
                // 本库认不得的类型：不管它。
            }
        }

        // 〔决策〕宣告里得有这次连接用的那把钥，而且它得是 known_hosts 里记着的普通钥 —— 否则不知道该替谁记（TOFU 里刚接受、
        // 没记下来的，CA 担保的，pinned 指纹的，都不算）。
        if (!offered.Any(k => k.Blob.Span.SequenceEqual(HostKey.Blob.Span)))
        {
            return new SshHostKeyUpdate([], "服务端的宣告里没有这次连接用的主机密钥，没理会。");
        }
        IReadOnlyList<SshPublicKey> known = await rotation.GetKnownHostKeysAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (!known.Any(k => k.Blob.Span.SequenceEqual(HostKey.Blob.Span)))
        {
            return new SshHostKeyUpdate([], "这次连接用的主机密钥没有作为普通钥记在 known_hosts 里，不做轮换。");
        }

        SshPublicKey[] fresh = [.. offered.Where(o => !known.Any(k => k.Blob.Span.SequenceEqual(o.Blob.Span)))];
        if (fresh.Length == 0)
        {
            return new SshHostKeyUpdate([], null);
        }

        // 请服务端证明持有这几把：每把一个签名，签的是 string "hostkeys-prove-00@openssh.com" ‖ string session_id ‖ string 公钥 blob。
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        foreach (SshPublicKey key in fresh)
        {
            writer.WriteString(key.Blob.Span);
        }
        SshGlobalRequestReply reply = await SendGlobalRequestAsync(
            SshProtocolNames.RequestHostKeysProve, request.WrittenMemory, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!reply.Success)
        {
            return new SshHostKeyUpdate([], "服务端拒绝证明它宣告的主机密钥。");
        }

        SshDataReader signatures = new(new ReadOnlySequence<byte>(reply.Payload));
        foreach (SshPublicKey key in fresh)
        {
            byte[] signature;
            try
            {
                signature = signatures.ReadStringAsArray(MaxFieldBytes);
            }
            catch (SshWireFormatException)
            {
                return new SshHostKeyUpdate([], "服务端的证明少了签名，一把都不记。");
            }

            // 〔决策〕一把签不过就一把都不记：同一个应答里有假的，其余的也不可信。RSA 只认 SHA-2 的签名。
            if (!VerifiesHostKeyProof(key, signature))
            {
                return new SshHostKeyUpdate([], $"服务端对 {key.KeyType} {key.Sha256Fingerprint} 的证明签名验不过，一把都不记。");
            }
        }

        await rotation.RecordHostKeysAsync(host, port, fresh, cancellationToken).ConfigureAwait(false);
        return new SshHostKeyUpdate(fresh, null);
    }

    /// <summary>验一把钥的持有证明。</summary>
    private bool VerifiesHostKeyProof(SshPublicKey key, byte[] signature)
    {
        string algorithm;
        try
        {
            algorithm = new SshDataReader(new ReadOnlySequence<byte>(signature)).ReadUtf8String(64);
        }
        catch (SshWireFormatException)
        {
            return false;
        }
        if (algorithm == SshAlgorithmNames.SshRsa || !key.SignatureAlgorithms.Contains(algorithm, StringComparer.Ordinal))
        {
            return false;
        }

        ArrayBufferWriter<byte> signed = new();
        SshDataWriter writer = new(signed);
        writer.WriteUtf8String(SshProtocolNames.RequestHostKeysProve);
        writer.WriteString(SessionId.Span);
        writer.WriteString(key.Blob.Span);
        return key.VerifySignature(signature, signed.WrittenSpan, algorithm);
    }
}
