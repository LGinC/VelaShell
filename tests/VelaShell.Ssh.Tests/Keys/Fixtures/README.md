# 加密私钥样本

这里的私钥**全部由真的 `ssh-keygen`（OpenSSH 10.5p1）生成**，口令统一是：

```
correct horse battery staple
```

## 为什么不自己拼

本仓库其余的私钥用例（`PrivateKeyFileTests`）是**按格式现拼**出来的，
因为拼的过程本身就在验证我们对格式的理解。加密私钥这里反过来，必须用外部产物：

加密侧要是也由我们写，它和解密侧会**一起错** —— `bcrypt_pbkdf` 的轮数、
输出的字节序、密钥与 IV 的派生次序，随便哪一处理解偏了，自加密自解密都照样通过，
而拿到别人的钥就静默失败。这类错不会报错，只会在特定输入上给出错误结果。

所以标准答案由 OpenSSH 出：`.pub` 里那段 blob 是它写的，
用例断言「我们解出来的公钥与它逐字节相同」。

## 怎么重新生成

```bash
cd tests/VelaShell.Ssh.Tests/Keys/Fixtures
rm -f ed25519-* rsa-* ecdsa-*
PASS='correct horse battery staple'
gen() { local name=$1 type=$2 cipher=$3; shift 3
        ssh-keygen -q -t "$type" -f "./$name" -N "$PASS" \
                   -C "velashell-ssh test key ($name)" -Z "$cipher" "$@" </dev/null; }

gen ed25519-aes256ctr   ed25519 aes256-ctr
gen ed25519-aes256cbc   ed25519 aes256-cbc
gen ed25519-aes128ctr   ed25519 aes128-ctr
gen ed25519-aes256gcm   ed25519 aes256-gcm@openssh.com
gen ed25519-chachapoly  ed25519 chacha20-poly1305@openssh.com
gen ed25519-rounds64    ed25519 aes256-ctr -a 64
gen rsa-aes256ctr       rsa     aes256-ctr -b 2048
gen ecdsa-aes256ctr     ecdsa   aes256-ctr -b 256

ssh-keygen -q -t ed25519 -f ./ed25519-plain -N "" \
           -C "velashell-ssh test key (plain)" </dev/null
```

### 传统加密 PEM（`Proc-Type: 4,ENCRYPTED`）

OpenSSH 7.8 之前加口令时默认写这种格式（口令只经一次 MD5 派生、常配 3DES）。本库**不读**这种过时格式，
这几份样本用来验证它会被清楚地拒绝、并给出转换办法（`ssh-keygen -p`），而不是被误报成「口令不对」。
`legacy-rsa-aes128` 是 `ssh-keygen -m PEM` 写的（现实里最常见的那种）；3DES 与 EC 的两份由 `openssl` 写。

```bash
PASS='correct horse battery staple'
ssh-keygen -q -t rsa -b 2048 -m PEM -N "$PASS" \
           -C "velashell-ssh test key (legacy-rsa-aes128)" -f ./legacy-rsa-aes128 </dev/null
openssl genrsa 2048 | openssl rsa -des3 -traditional -passout "pass:$PASS" -out legacy-rsa-des3
openssl ecparam -name prime256v1 -genkey -noout | openssl ec -aes256 -passout "pass:$PASS" -out legacy-ecdsa-aes256
```

### 主机证书（`hostcert-*`）

`HostCertificateTests` 用的主机证书，全部由 `ssh-keygen -s` 签发，私钥不加密。
验签范围、字段边界这类理解偏差，只有对着别人签的证书才看得出来 —— 自己签自己验，两边会一起错。

| 文件 | 用途 |
| --- | --- |
| `hostcert-ca` / `hostcert-ca-rsa` / `hostcert-other-ca` | 签发用的 CA（ed25519 / RSA 3072 / 一个不被信任的 ed25519） |
| `hostcert-key`、`hostcert-ecdsa`、`hostcert-rsa`、`hostcert-rsa1024` | 主机密钥（ed25519 / P-256 / RSA 2048 / RSA 1024，最后一把用来验 RSA 长度下限） |
| `hostcert-key-cert.pub` | 合格：主体 `server.example,10.0.0.1`，永久有效 |
| `hostcert-window-cert.pub` | 有效期 2026-01-01 到 2027-01-01（用例传入固定时刻，不会随日期过期） |
| `hostcert-farfuture-cert.pub` | 有效期从 2^48 秒到 2^62 秒（都在 9999 年以后，超出 `DateTimeOffset` 的范围） |
| `hostcert-noprincipals-cert.pub` / `hostcert-usertype-cert.pub` | 没列主体 / 用户证书 |
| `hostcert-sha1-cert.pub` / `hostcert-rsasha512-cert.pub` | RSA CA 用 `ssh-rsa`（SHA-1）/ `rsa-sha2-512` 签 |
| `hostcert-othersigned-cert.pub` | 别的 CA 签的 |

```bash
k() { ssh-keygen -q -t "$1" -f "./$2" -N "" -C "velashell-ssh test key ($2)" "${@:3}" </dev/null; }
k ed25519 hostcert-ca;  k rsa hostcert-ca-rsa -b 3072;  k ed25519 hostcert-other-ca
k ed25519 hostcert-key; k ecdsa hostcert-ecdsa -b 256
k rsa hostcert-rsa -b 2048; k rsa hostcert-rsa1024 -b 1024

# 同一把主机钥签多张证书：ssh-keygen 把证书写到「<输入名>-cert.pub」，所以先复制成不同的名字，签完删掉副本。
for n in window farfuture noprincipals usertype sha1 rsasha512 othersigned; do cp hostcert-key.pub hostcert-$n.pub; done
s() { ssh-keygen -q "$@" </dev/null; }
s -s hostcert-ca -h -I host-valid -n server.example,10.0.0.1 hostcert-key.pub
s -s hostcert-ca -h -I host-window -n server.example -V 20260101:20270101 hostcert-window.pub
s -s hostcert-ca -h -I host-farfuture -n server.example -V 0x1000000000000:0x4000000000000000 hostcert-farfuture.pub
s -s hostcert-ca -h -I host-noprincipals hostcert-noprincipals.pub
s -s hostcert-ca -I host-usertype -n server.example hostcert-usertype.pub
s -s hostcert-ca-rsa -t ssh-rsa -h -I host-sha1 -n server.example hostcert-sha1.pub
s -s hostcert-ca-rsa -t rsa-sha2-512 -h -I host-rsasha512 -n server.example hostcert-rsasha512.pub
s -s hostcert-other-ca -h -I host-othersigned -n server.example hostcert-othersigned.pub
s -s hostcert-ca -h -I host-ecdsa -n server.example hostcert-ecdsa.pub
s -s hostcert-ca -h -I host-rsa -n server.example hostcert-rsa.pub
s -s hostcert-ca -h -I host-rsa1024 -n server.example hostcert-rsa1024.pub
for n in window farfuture noprincipals usertype sha1 rsasha512 othersigned; do rm hostcert-$n.pub; done
```

重新生成之后，`HostCertificateTests` 里那条指纹断言（`ssh-keygen -lf hostcert-key-cert.pub` 的输出）要跟着改。

## 这些钥是公开的

它们只存在于本仓库的用例里，从未用于任何真实主机，口令也写在上面。
**不要把它们当成"泄漏的密钥"处理** —— 也不要拿它们去连任何东西。

### PuTTY 的 `.ppk`（`putty-ed25519-*`）

`PuttyKeyTests` 用的 Ed25519 `.ppk` 由**真的 `puttygen`（PuTTY 0.83）**生成，`.pub` 是 `ssh-keygen -y` 从 `puttygen` 导出的
OpenSSH 私钥里取的 —— 标准答案来自外部，不是我们自己拼的 `.ppk`。`-hi` / `-lo` 是私钥字段首字节 ≥ / < 0x80 的各一把
（这一点正是曾经读错的地方：PuTTY 把它写成定长 32 字节，不是 mpint）；`-enc` 是同一把 `-hi` 加上口令另存，口令同上。
没装 PuTTY 时在 Debian 上不用 root 也能拿到：`apt-get download putty-tools && dpkg -x putty-tools_*.deb root`。

```bash
PG=root/usr/bin/puttygen
PASS='correct horse battery staple'
: > empty; printf '%s' "$PASS" > pass
first() { awk '/^Private-Lines:/{n=$2;next} n>0{print;n--}' "$1" | tr -d '\r\n' | base64 -d | od -An -tu1 -j4 -N1 | tr -d ' '; }
pick() {   # 版本 hi|lo 名字：一直生成，直到首字节符合要求
  while :; do
    $PG -t ed25519 -C "velashell-ssh test key ($3)" --new-passphrase empty --ppk-param version=$1 -o cand.ppk
    b=$(first cand.ppk)
    { [ "$2" = hi ] && [ "$b" -ge 128 ]; } || { [ "$2" = lo ] && [ "$b" -lt 128 ]; } && break
  done
  mv cand.ppk "$3.ppk"
}
for v in 2 3; do
  pick $v hi putty-ed25519-v$v-hi
  pick $v lo putty-ed25519-v$v-lo
  $PG putty-ed25519-v$v-hi.ppk --old-passphrase empty --new-passphrase pass --ppk-param version=$v \
      -C "velashell-ssh test key (putty-ed25519-v$v-hi-enc)" -o putty-ed25519-v$v-hi-enc.ppk
done
for n in putty-ed25519-v2-hi putty-ed25519-v2-lo putty-ed25519-v3-hi putty-ed25519-v3-lo; do
  $PG $n.ppk --old-passphrase empty -O private-openssh-new -o $n.openssh && chmod 600 $n.openssh
  ssh-keygen -y -f $n.openssh > $n.pub && rm $n.openssh
done
cp putty-ed25519-v2-hi.pub putty-ed25519-v2-hi-enc.pub
cp putty-ed25519-v3-hi.pub putty-ed25519-v3-hi-enc.pub
```

### PKCS#8（`pkcs8-*`）

`EncryptedPkcs8KeyTests` 用的 PKCS#8 由**真的 `openssl`（3.5）**生成，默认的 PBES2（PBKDF2-HMAC-SHA256 + AES-256-CBC），口令同上。
`-rsa-enc` / `-ecdsa-enc` 的 `.pub` 是 `ssh-keygen -y` 给的；DSA、brainpool 两份是「口令对、钥不受支持」的样本，没有 `.pub`。
两份 Ed25519 的 `.pub` 由 WSL 里的 `ssh-keygen -y`（OpenSSH 10.0p2，链接 OpenSSL）给出 —— Windows 自带的那个读 PKCS#8 里的 Ed25519 报 `invalid format`。

`pkcs8-ed25519-rfc8410*` 是 **RFC 8410 原文里的例子**（§10.3 与附录 A），逐字抄下、补上 PEM 头尾：

| 文件 | 出处 | 内容 |
| --- | --- | --- |
| `pkcs8-ed25519-rfc8410` | §10.3 第一个 | v1，只有私钥 |
| `pkcs8-ed25519-rfc8410-v2` | §10.3 第二个 | v2，带一个属性 `[0]` 与公钥 `[1]` |
| `pkcs8-ed25519-rfc8410-ber` | 附录 A | 同一把钥的 BER 不定长编码（RFC 5958 要求接受 BER） |
| `pkcs8-ed25519-rfc8410-badpub1` / `-badpub2` | 附录 A 末尾 | 「错误的钥」：公钥少了首 / 尾一个字节 |

前三份是同一把钥，`.pub`（`pkcs8-ed25519-rfc8410.pub`）也是 WSL 的 `ssh-keygen -y` 给的，三份它都读得出、给的是同一行。
两份错例它**照样读得出**，交出来的是文件里写的那个（错的）公钥 —— 本库按与 `openssh-key-v1`、`.ppk` 同一口径报 `KeyFormatInvalid`。

```bash
PASS='correct horse battery staple'
openssl genpkey -algorithm ed25519 -aes-256-cbc -pass "pass:$PASS" -out pkcs8-ed25519-enc
openssl genpkey -algorithm ed25519 -out pkcs8-ed25519
openssl genpkey -genparam -algorithm DSA -pkeyopt dsa_paramgen_bits:2048 -out dsaparam
openssl genpkey -paramfile dsaparam -aes-256-cbc -pass "pass:$PASS" -out pkcs8-dsa-enc && rm dsaparam
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:brainpoolP256r1 -aes-256-cbc -pass "pass:$PASS" -out pkcs8-brainpool-enc
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -aes-256-cbc -pass "pass:$PASS" -out pkcs8-rsa-enc
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-384 -aes-256-cbc -pass "pass:$PASS" -out pkcs8-ecdsa-enc
for n in pkcs8-rsa-enc pkcs8-ecdsa-enc; do chmod 600 $n; ssh-keygen -y -P "$PASS" -f $n > $n.pub; done
# 在 WSL 里（OpenSSH 10.0p2）：
ssh-keygen -y -f pkcs8-ed25519 > pkcs8-ed25519.pub
ssh-keygen -y -P "$PASS" -f pkcs8-ed25519-enc > pkcs8-ed25519-enc.pub
ssh-keygen -y -f pkcs8-ed25519-rfc8410 > pkcs8-ed25519-rfc8410.pub
```