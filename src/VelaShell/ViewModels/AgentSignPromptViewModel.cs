using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>
/// agent 转发「逐次确认」弹窗的视图模型:远端要用本机 agent 里的某把钥签名时,
/// 摆出是哪条会话、哪把钥、签来做什么(以谁登录哪台主机),给「拒绝 / 允许一次 / 本次会话内允许」三种处置。
/// </summary>
/// <remarks>
/// 「签来做什么」才是用户判断得了的东西:自己刚在远端敲了 <c>git pull</c>,目的主机就该是 github;
/// 目的主机是一台没见过的机器、或者核实不了,就是该拒的时候。所以后两种用警告色摆出来。
/// </remarks>
public class AgentSignPromptViewModel : ReactiveObject
{
    /// <summary>用一次签名请求构造。</summary>
    /// <param name="request">远端的这次签名请求。</param>
    public AgentSignPromptViewModel(AgentSignRequest request)
    {
        Target = request.Target;
        KeyType = request.KeyType;
        Fingerprint = request.Fingerprint;
        Comment = request.Comment;
        Purpose = request switch
        {
            { LoginUser: { } user } => Strings.Format("AgentSign_PurposeLogin", user),
            { SignatureNamespace: { } ns } => Strings.Format("AgentSign_PurposeSshSig", ns),
            _ => Strings.Get("AgentSign_PurposeUnknown"),
        };
        IsLogin = request.LoginUser is not null;
        Destination = request switch
        {
            { DestinationHosts.Count: > 0 } => string.Join(", ", request.DestinationHosts),
            { DestinationFingerprint: { } fingerprint } => Strings.Format("AgentSign_DestinationNotKnown", fingerprint),
            _ => Strings.Get("AgentSign_DestinationUnverified"),
        };
        IsDestinationUnfamiliar = request.DestinationHosts.Count == 0;
        TimeoutText = Strings.Format("AgentSign_TimeoutHint", (int)Math.Ceiling(request.Timeout.TotalSeconds));

        DenyCommand = ReactiveCommand.Create(() => { Result = AgentSignDecision.Deny; });
        AllowOnceCommand = ReactiveCommand.Create(() => { Result = AgentSignDecision.AllowOnce; });
        AllowForSessionCommand = ReactiveCommand.Create(() => { Result = AgentSignDecision.AllowForSession; });
    }

    /// <summary>哪条会话在要:<c>用户@主机:端口</c>。</summary>
    public string Target { get; }

    /// <summary>密钥类型。</summary>
    public string KeyType { get; }

    /// <summary>密钥指纹。</summary>
    public string Fingerprint { get; }

    /// <summary>agent 里的注释(通常是私钥文件路径);可能为空。</summary>
    public string Comment { get; }

    /// <summary>有没有注释可显示。</summary>
    public bool HasComment => Comment.Length > 0;

    /// <summary>签来做什么:以谁登录 SSH 服务器、哪个命名空间的数据签名,或认不出来。</summary>
    public string Purpose { get; }

    /// <summary>被签的是不是一次 SSH 登录;是的话才有「目的主机」一行。</summary>
    public bool IsLogin { get; }

    /// <summary>登录的目的主机:已知主机里的名字,或「不在已知主机里」「无法核实」。</summary>
    public string Destination { get; }

    /// <summary>目的主机不在已知主机里或核实不了:这一行用警告色。</summary>
    public bool IsDestinationUnfamiliar { get; }

    /// <summary>「N 秒内不作答将自动拒绝」。</summary>
    public string TimeoutText { get; }

    /// <summary>用户的裁决;未作答时为 null。</summary>
    public AgentSignDecision? Result
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>拒签。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DenyCommand { get; }

    /// <summary>只允许这一次。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AllowOnceCommand { get; }

    /// <summary>这条会话里这把钥之后都不再问。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AllowForSessionCommand { get; }
}
