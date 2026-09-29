# tests/fixtures/ —— 插件运行时的测试夹具

宿主的插件运行时测试(发现、装载、惰性激活、隔离进程、装卸载、开发挂载、终端协议
适配)需要**一个真实的插件程序集**才能端到端跑通 —— 手写一个假的 `IVelaPlugin` 类
放在测试程序集里不行,那样走不到"从磁盘上的目录发现 `plugin.json` → 在可收集 ALC
里装载一个独立 dll → 跨进程激活"这条真实路径。

拆库之前这个角色由 `plugins/VelaShell.Plugin.HelloWorld` 和
`plugins/VelaShell.Plugin.Telnet` 兼任。插件已于 2026-08-21 搬到独立的插件仓库
<https://github.com/VelaShellLabs/velashell-plugins>,本仓库拿不到它们了,
于是把"当夹具"这件事**显式化**成这两个工程:

| 工程 | id | 用途 |
| --- | --- | --- |
| [VelaShell.TestPlugin](VelaShell.TestPlugin/) | `velashell.test-fixture` | 通用夹具:激活写存储、注册若干命令。驱动发现/装载/激活/停用/启停/装卸载/惰性激活/隔离进程的全部用例 |
| [VelaShell.TestPlugin.Terminal](VelaShell.TestPlugin.Terminal/) | `velashell.test-terminal` | 终端协议夹具:清单里声明 `contributes.protocols` + `onProtocol` 惰性激活,激活后注册成终端协议。驱动"清单发现 → 惰性激活 → 终端协议 → 宿主适配 → 真实套接字"整条链 |

两个夹具都**刻意保持零第三方依赖**(只引 `VelaShell.PluginSdk`):用例大量使用
"只把入口 dll 复制到临时插件根"的铺法,多一个依赖就要多复制一个文件,
而那不是这些用例要验的东西。

它们不是示例代码 —— 想看插件怎么写,读第一方插件仓库的
[`plugins/`](https://github.com/VelaShellLabs/velashell-plugins/tree/main/plugins)
与 [开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)。

## ssh-shells —— 多 shell SSH 靶子(不是插件夹具)

[ssh-shells](ssh-shells/Dockerfile) 是另一类夹具:一台 sshd 容器,七个账号,登录 shell
分别是 bash / zsh / fish / dash / ash,外加两个「用户已经动过手脚」的 bash 账号
(`vela-pyenv` 的 `PROMPT_COMMAND` 结尾带分号、`vela-starship` 每次提示符重写 `PS1`)。

它服务的是 [VelaShell.ShellIntegration.Tests](../VelaShell.ShellIntegration.Tests/README.md) ——
「文件浏览器跟随终端目录」那条链路只有在真 shell、真 PTY 上才暴露得出问题
(回显折行重绘、fish 的解析期报错、`;;` 语法错误)。

为什么不复用 `docker-compose.test.yml` 里那台 `openssh-server`:它的 sshd 以**普通用户**
身份运行(rootless),没法 setuid,因此一台只认一个用户 —— 而这里要验的恰恰是
「同一份代码面对不同登录 shell 各自怎么表现」。

```bash
docker compose -f docker-compose.test.yml up -d ssh-shells   # 端口 2223,口令一律 velapass
```

## ssh-2fa —— 两步验证 SSH 靶子(不是插件夹具)

[ssh-2fa](ssh-2fa/Dockerfile) 是一台只开 keyboard-interactive 的 sshd,经 PAM 先问口令(`pam_unix`)、
再问 TOTP 动态码(`pam_google_authenticator`,种子写死,测试按 RFC 6238 现算)。三个账号:
`vela-otp`(口令 + 动态码)、`vela-strict`(同上,但 `MaxAuthTries 1`,多浪费一次尝试就被断开)、
`vela-keyotp`(`AuthenticationMethods publickey,keyboard-interactive`,钥是同目录的 `id_ed25519`,**仅供测试**)。

它服务的是 `VelaShell.Core.Tests` 的 `KeyboardInteractiveIntegrationTests` —— 「口令自动代答、只有动态码才问人」
要在真实 PAM 的提问顺序下才验得出来:PAM 每轮只问一条、两轮都不回显,恰恰是会让密码被填进验证码那一轮的形状。

```bash
docker compose -f docker-compose.test.yml up -d --build ssh-2fa   # 端口 2224,口令一律 velapass
```
