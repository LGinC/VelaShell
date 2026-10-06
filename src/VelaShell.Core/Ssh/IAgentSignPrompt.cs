namespace VelaShell.Core.Ssh;

/// <summary>远端经 agent 转发请求签名时,交给用户裁决的那一次请求。</summary>
/// <param name="Target">哪条会话在要:<c>用户@主机:端口</c>。</param>
/// <param name="KeyType">密钥类型,如 <c>ssh-ed25519</c>。</param>
/// <param name="Fingerprint">密钥指纹(<c>SHA256:…</c>)。</param>
/// <param name="Comment">这把钥在 agent 里的注释,通常是私钥文件路径;agent 没给时为空串。</param>
/// <param name="Timeout">多久没人应答就按拒绝处理;弹窗据此告诉用户还剩多久。</param>
/// <remarks>
/// 只有钥和注释的话,用户分不出这是自己刚在远端敲的 <c>git pull</c>,还是那台机器上有人在拿这把钥登录别处。
/// 所以被签的数据认得出来时,下面几项说明「签来做什么」;它们都由签名本身绑死,远端编不出别的值,
/// 文本已经清掉了控制字符。
/// </remarks>
public sealed record AgentSignRequest(string Target, string KeyType, string Fingerprint, string Comment, TimeSpan Timeout)
{
    /// <summary>被签的是一次 SSH 登录时,要以哪个用户登录;不是登录时为 <see langword="null" />。</summary>
    public string? LoginUser { get; init; }

    /// <summary>这次登录的目的主机的主机密钥指纹(<c>SHA256:…</c>);核实不了时为 <see langword="null" />。</summary>
    public string? DestinationFingerprint { get; init; }

    /// <summary>
    /// 已知主机里与 <see cref="DestinationFingerprint" /> 对得上的条目(<c>主机</c>,非 22 端口为 <c>主机:端口</c>);
    /// 对不上或核实不了时为空。
    /// </summary>
    public IReadOnlyList<string> DestinationHosts { get; init; } = [];

    /// <summary>
    /// 被签的是 SSHSIG 签名(<c>ssh-keygen -Y sign</c>、git 的 SSH 提交签名)时的命名空间,如 <c>git</c>;否则为 <see langword="null" />。
    /// </summary>
    public string? SignatureNamespace { get; init; }
}

/// <summary>用户对一次 agent 签名请求的裁决。</summary>
public enum AgentSignDecision
{
    /// <summary>拒签。关窗、超时、出错一律落在这里。</summary>
    Deny,

    /// <summary>只允许这一次。</summary>
    AllowOnce,

    /// <summary>这条会话里这把钥之后的签名都不再问。</summary>
    AllowForSession,
}

/// <summary>
/// agent 转发的「逐次确认」:远端每次要用本机 agent 签名时问用户一次。
/// </summary>
/// <remarks>
/// 与 <see cref="IHostKeyPrompt" /> 同一个模式:基础设施层在后台线程上等,界面层负责弹窗。
/// 实现方必须 <b>fail-closed</b> —— 拿不到主窗口、弹窗出错、
/// 取消令牌触发(通道关了或超时)一律返回 <see cref="AgentSignDecision.Deny" />,并收起还开着的窗口。
/// </remarks>
public interface IAgentSignPrompt
{
    /// <summary>问用户是否允许这次签名。</summary>
    /// <param name="request">这次请求。</param>
    /// <param name="cancellationToken">取消时收起窗口并按拒绝处理。</param>
    Task<AgentSignDecision> ConfirmAsync(AgentSignRequest request, CancellationToken cancellationToken);
}
