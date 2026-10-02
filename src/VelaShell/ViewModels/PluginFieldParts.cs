using System.Collections.ObjectModel;
using System.Globalization;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.ViewModels;

/// <summary>
/// 分段按钮 / 小标签里的一个选项(<see cref="ProtocolSettingPresentation.Segmented" /> /
/// <see cref="ProtocolSettingPresentation.Chips" /> 画法)。点它就把字段值设成它的 <see cref="Value" />。
/// </summary>
public sealed class PluginOptionViewModel : ReactiveObject
{
    /// <summary>从 SDK 的候选项建。</summary>
    /// <param name="choice">候选项。</param>
    /// <param name="owner">所属字段。</param>
    public PluginOptionViewModel(ProtocolSettingChoice choice, PluginProtocolFieldViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(owner);
        Value = choice.Value;
        Label = choice.Label;
        Tone = choice.Tone;
        SelectCommand = ReactiveCommand.Create(() => { owner.Text = Value; });
    }

    /// <summary>落盘值。</summary>
    public string Value { get; }

    /// <summary>展示文案。</summary>
    public string Label { get; }

    /// <summary>语气色(小标签选中时用)。</summary>
    public ProtocolTone Tone { get; }

    /// <summary>选中。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>危险语气(生产环境)。</summary>
    public bool IsDanger => Tone == ProtocolTone.Danger;

    /// <summary>警告语气。</summary>
    public bool IsWarning => Tone == ProtocolTone.Warning;

    /// <summary>成功语气。</summary>
    public bool IsSuccess => Tone == ProtocolTone.Success;

    /// <summary>信息语气。</summary>
    public bool IsInfo => Tone == ProtocolTone.Info;

    /// <summary>强调语气。</summary>
    public bool IsAccent => Tone == ProtocolTone.Accent;

    /// <summary>选中它。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SelectCommand { get; }
}

/// <summary>
/// 主机列表(<see cref="ProtocolSettingKind.HostList" />):一行一台。字段值是英文逗号分隔的
/// <c>host:port</c>,两边双向同步 —— 行变了就写回字段值,字段值被外部改了(读入既有配置)就重建行。
/// </summary>
public sealed class PluginHostListViewModel : ReactiveObject
{
    private readonly PluginProtocolFieldViewModel _owner;
    private bool _committing;

    /// <summary>建一个与字段值同步的主机列表。</summary>
    /// <param name="owner">所属字段。</param>
    public PluginHostListViewModel(PluginProtocolFieldViewModel owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Parse(owner.Text);
        AddCommand = ReactiveCommand.Create(Add);
    }

    /// <summary>各行。</summary>
    public ObservableCollection<PluginHostRowViewModel> Rows { get; } = [];

    /// <summary>「添加主机」。</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddCommand { get; }

    /// <summary>加一行(端口取当前形态的默认端口)。</summary>
    public void Add()
    {
        Rows.Add(new PluginHostRowViewModel(this, string.Empty, _owner.DefaultPortProvider?.Invoke() ?? 0));
        Commit();
    }

    /// <summary>删一行。</summary>
    internal void Remove(PluginHostRowViewModel row)
    {
        Rows.Remove(row);
        Commit();
    }

    /// <summary>行变了:写回字段值(主机还空着的行不写 —— 半填的行不该变成一个 <c>:27017</c>)。</summary>
    internal void Commit()
    {
        _committing = true;
        try
        {
            _owner.Text = Serialize();
        }
        finally
        {
            _committing = false;
        }
    }

    /// <summary>字段值被外部改了:与当前各行对不上时重建。</summary>
    internal void OnTextChanged()
    {
        if (_committing || string.Equals(Serialize(), _owner.Text, StringComparison.Ordinal))
        {
            return;
        }
        Parse(_owner.Text);
    }

    /// <summary>
    /// 按连接检查报回的端点给各行标上角色(PRIMARY / SECONDARY);对不上的行清掉旧徽章 ——
    /// 上一次测出来的角色,在改过地址之后就不再可信。
    /// </summary>
    /// <param name="endpoints">发现的端点。</param>
    public void ApplyBadges(IReadOnlyList<WorkspaceProbeEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (PluginHostRowViewModel row in Rows)
        {
            WorkspaceProbeEndpoint? match = endpoints.FirstOrDefault(endpoint => SameAddress(endpoint.Address, row.Address));
            row.Badge = match?.Role;
            row.BadgeTone = match?.Tone ?? ProtocolTone.Neutral;
        }
    }

    /// <summary>两个 <c>host:port</c> 是否同一台(主机不分大小写)。</summary>
    internal static bool SameAddress(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private string Serialize() =>
        string.Join(',', Rows.Where(static row => row.Host.Trim().Length > 0).Select(static row => row.Address));

    private void Parse(string text)
    {
        Rows.Clear();
        foreach (string entry in text.Split([',', ';', ' ', '\n', '\r', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (string host, int port) = Split(entry);
            Rows.Add(new PluginHostRowViewModel(this, host, port));
        }
    }

    /// <summary>
    /// 拆 <c>host:port</c>:<c>[::1]:27017</c> 认方括号;只有一个冒号时最后一段是端口;
    /// 没有冒号(或裸 IPv6)就没有端口(记 0,写回时也不带端口)。
    /// </summary>
    internal static (string Host, int Port) Split(string entry)
    {
        if (entry.StartsWith('[') && entry.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
        {
            string host = entry[1..close];
            return entry.Length > close + 2 && entry[close + 1] == ':'
                   && int.TryParse(entry[(close + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out int v6Port)
                ? (host, v6Port)
                : (host, 0);
        }
        int colon = entry.LastIndexOf(':');
        if (colon > 0 && entry.IndexOf(':') == colon
            && int.TryParse(entry[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            return (entry[..colon], port);
        }
        return (entry, 0);
    }
}

/// <summary>主机列表的一行。</summary>
public sealed class PluginHostRowViewModel : ReactiveObject
{
    private readonly PluginHostListViewModel _owner;
    private string _host;
    private int _port;

    /// <summary>建一行。</summary>
    /// <param name="owner">所属列表。</param>
    /// <param name="host">主机。</param>
    /// <param name="port">端口;0 表示没写端口。</param>
    public PluginHostRowViewModel(PluginHostListViewModel owner, string host, int port)
    {
        _owner = owner;
        _host = host;
        _port = port;
        RemoveCommand = ReactiveCommand.Create(() => _owner.Remove(this));
    }

    /// <summary>主机。</summary>
    public string Host
    {
        get => _host;
        set
        {
            if (string.Equals(_host, value, StringComparison.Ordinal))
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _host, value);
            _owner.Commit();
        }
    }

    /// <summary>端口(1–65535;0 = 不写)。</summary>
    public int Port
    {
        get => _port;
        set
        {
            if (_port == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _port, value);
            _owner.Commit();
        }
    }

    /// <summary>落盘的 <c>host:port</c>(IPv6 加方括号)。</summary>
    public string Address
    {
        get
        {
            string host = _host.Trim();
            if (host.Contains(':', StringComparison.Ordinal))
            {
                host = $"[{host}]";
            }
            return _port > 0 ? string.Create(CultureInfo.InvariantCulture, $"{host}:{_port}") : host;
        }
    }

    /// <summary>测试连接后发现的角色(PRIMARY / SECONDARY);没有为 <see langword="null" />。</summary>
    public string? Badge
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasBadge));
        }
    }

    /// <summary>角色徽章的语气色。</summary>
    public ProtocolTone BadgeTone
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsBadgeSuccess));
            this.RaisePropertyChanged(nameof(IsBadgeInfo));
            this.RaisePropertyChanged(nameof(IsBadgeWarning));
            this.RaisePropertyChanged(nameof(IsBadgeDanger));
        }
    }

    /// <summary>有角色徽章。</summary>
    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>徽章成功色(主节点)。</summary>
    public bool IsBadgeSuccess => BadgeTone == ProtocolTone.Success;

    /// <summary>徽章信息色(从节点)。</summary>
    public bool IsBadgeInfo => BadgeTone == ProtocolTone.Info;

    /// <summary>徽章警告色。</summary>
    public bool IsBadgeWarning => BadgeTone == ProtocolTone.Warning;

    /// <summary>徽章危险色。</summary>
    public bool IsBadgeDanger => BadgeTone == ProtocolTone.Danger;

    /// <summary>删除这一行。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RemoveCommand { get; }
}
