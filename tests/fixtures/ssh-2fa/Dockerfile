# 两步验证 SSH 测试服务端 —— keyboard-interactive(2FA / OTP)的真机验证靶子。
#
# sshd 只开 keyboard-interactive,经 PAM 问,顺序与堡垒机上最常见的配法一致:
#   vela-otp    —— 先问口令(pam_unix,Password:),再问动态码(pam_google_authenticator,Verification code:)
#   vela-keyotp —— AuthenticationMethods publickey,keyboard-interactive:钥认证之后只问动态码
#   vela-strict —— 同 vela-otp,但 MaxAuthTries 1:多浪费一次尝试(比如把口令填进验证码那一轮)就被当场断开
# 口令一律 velapass;三个账号的 TOTP 种子相同(base32 JBSWY3DPEHPK3PXP),测试按 RFC 6238 现算。
# vela-keyotp 的钥是同目录的 id_ed25519(**仅供测试**,别拿去用在任何别的地方)。
#
# 不开 DISALLOW_REUSE 与 RATE_LIMIT:同一个 30 秒窗口里几条用例共用一个码,不该被当成重放或限流。
# PerSourcePenalties 必须关,理由同 ssh-shells(Docker NAT 下所有连接同源,noauth 罚分会攒满)。
FROM alpine:3.21

RUN apk add --no-cache openssh openssh-server-pam google-authenticator \
    && ssh-keygen -A

RUN printf '%s\n' \
    'Port 2222' \
    'PermitRootLogin no' \
    'PasswordAuthentication no' \
    'KbdInteractiveAuthentication yes' \
    'UsePAM yes' \
    'AuthenticationMethods keyboard-interactive' \
    'PrintMotd no' \
    'PerSourcePenalties no' \
    'MaxStartups 100' \
    'Subsystem sftp internal-sftp' \
    'Match User vela-strict' \
    '    MaxAuthTries 1' \
    'Match User vela-keyotp' \
    '    AuthenticationMethods publickey,keyboard-interactive' \
    > /etc/ssh/sshd_config

# pam_succeed_if 命中 vela-keyotp 时跳过下一行(口令):钥已经证明过身份,第二步只要动态码。
RUN set -eu; \
    ga="$(find / -name pam_google_authenticator.so -print -quit 2>/dev/null)"; \
    test -n "$ga"; \
    printf '%s\n' \
        'auth [success=1 default=ignore] pam_succeed_if.so quiet user = vela-keyotp' \
        'auth required pam_unix.so' \
        "auth required $ga" \
        'account required pam_unix.so' \
        'session required pam_unix.so' \
        > /etc/pam.d/sshd

COPY id_ed25519.pub /tmp/keyotp.pub
RUN set -eu; \
    for user in vela-otp vela-strict vela-keyotp; do \
        adduser -D -s /bin/sh "$user"; \
        echo "$user:velapass" | chpasswd; \
        secret="/home/$user/.google_authenticator"; \
        printf '%s\n' 'JBSWY3DPEHPK3PXP' '" WINDOW_SIZE 3' '" TOTP_AUTH' > "$secret"; \
        chown "$user:$user" "$secret"; \
        chmod 400 "$secret"; \
    done; \
    install -d -m 700 -o vela-keyotp -g vela-keyotp /home/vela-keyotp/.ssh; \
    install -m 600 -o vela-keyotp -g vela-keyotp /tmp/keyotp.pub /home/vela-keyotp/.ssh/authorized_keys; \
    rm /tmp/keyotp.pub

EXPOSE 2222
# openssh-server-pam 带的是编进了 PAM 的那一份 sshd。
CMD ["/usr/sbin/sshd.pam", "-D", "-e"]
