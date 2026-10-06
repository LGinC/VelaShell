// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 8446   TLS 1.3
//   RFC 6066 §3  SNI
//   行为规格:  velashell-docs/zh/ssh/spec/09-dialing.md §4.4

using System.Net.Security;

namespace VelaShell.Ssh.Transport;

/// <summary>TLS 拨号器的参数（<see cref="DialerChain.Tls"/>）。</summary>
public sealed record SshTlsOptions
{
    /// <summary>默认参数：SNI 与证书名都用这一跳的主机名，证书按系统的规则严格校验。</summary>
    public static SshTlsOptions Default { get; } = new();

    /// <summary>
    /// SNI 与证书校验用的名字；<see langword="null"/>（默认）用这一跳的主机名。
    /// </summary>
    /// <remarks>按 IP 连、证书却签给域名时用它（服务端前面的 sslh / stunnel 也可能按 SNI 分流）。</remarks>
    public string? ServerName { get; init; }

    /// <summary>
    /// 自己校验服务端证书；<see langword="null"/>（默认）按系统的规则严格校验：证书链可信、名字对得上、在有效期内。
    /// </summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/09 §4.4〕<b>默认严格。</b>自行部署的 stunnel 用的是自签证书时，
    /// 在这里比对证书的指纹（钉住那一张），而不是一律放行 —— 一律放行的 TLS 挡不住中间人，
    /// 只是把 SSH 包了一层（SSH 自己的主机密钥校验照样在，但代理的口令这类走在 TLS 里的东西就暴露了）。
    /// </remarks>
    public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; init; }
}
