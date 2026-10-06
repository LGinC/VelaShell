# 面向 FIPS 的后量子混合密钥交换靶子:mlkem768nistp256-sha256 / mlkem1024nistp384-sha384(RFC 10042)。
#
# 用 AlmaLinux 10.2:它的 OpenSSH 9.9p1 带着 RHEL 10.2 的下游补丁,两种都有。上游 OpenSSH 到 10.6p1 才有
# mlkem768nistp256-sha256(默认不开),mlkem1024nistp384-sha384 还没有 —— 没有这台,线上格式只能自己证明自己。
#
# 密钥交换清单照「开了 FIPS 模式的服务端」来配:没有 X25519、没有 sntrup761,只剩两种 nistp 混合与普通 ECDH。
# 客户端的默认清单、FIPS 清单各谈成哪一种,都在这台上看;强制单一算法时两种各自谈得成。
# sshd_config.d 里的文件按名字排序、先出现的设置生效,10- 排在系统加密策略的那份前面。
#
# 同一个容器里另起两个 sshd(命令行的 -p / -o 压过配置文件),供「服务端给的清单不同时谈成哪一种」用:
#   2222 —— 上面那份「FIPS 模式」清单(两种 nistp 混合 + 普通 ECDH)
#   2223 —— 只给 mlkem1024nistp384-sha384(外加 ecdh-sha2-nistp384)
#   2224 —— 照 RHEL 10.2 的 DEFAULT 策略:三种 ML-KEM 混合(X25519 那种在前)+ curve25519 + 普通 ECDH
#
# 账号 vela-pq,口令 velapass(与 ssh-shells 等靶子一致)。
# PerSourcePenalties 必须关,理由同 ssh-shells(Docker NAT 下所有连接同源,罚分会攒满)。
FROM almalinux:10.2

RUN dnf -y install openssh-server openssh-clients passwd \
    && dnf clean all \
    && ssh-keygen -A \
    && useradd -m vela-pq \
    && echo 'vela-pq:velapass' | chpasswd \
    && rm -f /run/nologin /var/run/nologin

RUN printf '%s\n' \
    'Port 2222' \
    'PermitRootLogin no' \
    'PasswordAuthentication yes' \
    'KbdInteractiveAuthentication no' \
    'UsePAM yes' \
    'PrintMotd no' \
    'PerSourcePenalties no' \
    'MaxStartups 100' \
    'KexAlgorithms mlkem768nistp256-sha256,mlkem1024nistp384-sha384,ecdh-sha2-nistp256,ecdh-sha2-nistp384,ecdh-sha2-nistp521' \
    'LogLevel DEBUG1' \
    > /etc/ssh/sshd_config.d/10-velashell-pq.conf

EXPOSE 2222 2223 2224
CMD ["/bin/sh", "-c", "/usr/sbin/sshd -p 2223 -o PidFile=/run/sshd-2223.pid -o KexAlgorithms=mlkem1024nistp384-sha384,ecdh-sha2-nistp384 && /usr/sbin/sshd -p 2224 -o PidFile=/run/sshd-2224.pid -o KexAlgorithms=mlkem768x25519-sha256,mlkem768nistp256-sha256,mlkem1024nistp384-sha384,curve25519-sha256,ecdh-sha2-nistp256,ecdh-sha2-nistp384,ecdh-sha2-nistp521 && exec /usr/sbin/sshd -D -e"]
