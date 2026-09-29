# 「老设备」SSH 测试服务端 —— 连接配置里「允许老算法」与自定义算法清单的真机验证靶子。
#
# 把一台现代 OpenSSH 收窄成老交换机 / CentOS 6 那样:密钥交换只有 diffie-hellman-group14-sha1、
# 主机密钥只有 SHA-1 的 ssh-rsa、MAC 只有 hmac-sha1(加密留 aes128-ctr,那是默认清单里就有的)。
# 默认清单一个都谈不成;放开老算法、或在自定义清单里把这三个加上,才连得上。
# 账号 vela-legacy,口令 velapass。PerSourcePenalties 必须关,理由同 ssh-shells。
FROM alpine:3.21

RUN apk add --no-cache openssh \
    && ssh-keygen -A

RUN printf '%s\n' \
    'Port 2222' \
    'PermitRootLogin no' \
    'PasswordAuthentication yes' \
    'KbdInteractiveAuthentication no' \
    'HostKey /etc/ssh/ssh_host_rsa_key' \
    'KexAlgorithms diffie-hellman-group14-sha1' \
    'HostKeyAlgorithms ssh-rsa' \
    'Ciphers aes128-ctr' \
    'MACs hmac-sha1' \
    'PrintMotd no' \
    'PerSourcePenalties no' \
    'MaxStartups 100' \
    > /etc/ssh/sshd_config

RUN adduser -D -s /bin/sh vela-legacy \
    && echo 'vela-legacy:velapass' | chpasswd

EXPOSE 2222
CMD ["/usr/sbin/sshd", "-D", "-e"]
