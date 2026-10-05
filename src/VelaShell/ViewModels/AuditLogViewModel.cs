using System.Collections.ObjectModel;
using System.Globalization;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>审计日志窗口里的一行。</summary>
/// <param name="Timestamp">事件时间。</param>
/// <param name="Category">类别原值(<c>connection</c> / <c>security</c> …)。</param>
/// <param name="Action">动作原值(<c>connect</c> / <c>hostkey-rejected</c> …)。</param>
/// <param name="CategoryLabel">类别的界面文字。</param>
/// <param name="ActionLabel">动作的界面文字;认不出的动作原样显示。</param>
/// <param name="SessionName">会话名;记录里没有配置 Id、或配置已删时为 —。</param>
/// <param name="Detail">详情(<c>用户@主机:端口</c>、指纹裁决的说明…)。</param>
/// <param name="IsProblem">是否值得留意:连接失败、拒绝或接受了变更的主机指纹。</param>
public sealed record AuditLogRow(
    DateTimeOffset Timestamp,
    string Category,
    string Action,
    string CategoryLabel,
    string ActionLabel,
    string SessionName,
    string Detail,
    bool IsProblem)
{
    /// <summary>本地时间,精确到秒。</summary>
    public string TimeText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// 审计日志窗口(设置 → 安全审计 → 查看审计日志):按时间倒序列出 <c>audit_log</c>,可按类别、只看异常与关键字筛。
/// </summary>
/// <remarks>
/// <para>
/// 一次取最近 <see cref="MaxRows" /> 条,筛选在本地做 —— 审计是逐条的短文本,几千条的筛选不值得来回查库,
/// 而类别、异常、关键字三种条件组合起来,查库的 SQL 反而要拼出一堆分支。
/// </para>
/// <para>
/// 记录里只有配置 Id;会话名在载入时从会话库对一次。配置删掉之后那一行显示 —,详情里的
/// <c>用户@主机:端口</c> 照样看得出是哪台。
/// </para>
/// </remarks>
public sealed class AuditLogViewModel : ReactiveObject
{
    /// <summary>一次最多载入的条数。</summary>
    public const int MaxRows = 2000;

    private readonly IAuditLogService _auditLog;
    private readonly ISessionRepository? _sessions;
    private List<AuditLogRow> _all = [];
    private bool _truncated;

    /// <summary>创建审计日志视图模型。</summary>
    /// <param name="auditLog">审计日志。</param>
    /// <param name="sessions">会话库,用来把配置 Id 对成会话名;没有时会话一栏显示 —。</param>
    public AuditLogViewModel(IAuditLogService auditLog, ISessionRepository? sessions = null)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _sessions = sessions;
        CategoryOptions =
        [
            Strings.Get("AuditLog_CategoryAll"),
            CategoryLabel("connection"),
            CategoryLabel("security"),
        ];
        RefreshCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
        this.WhenAnyValue(x => x.CategoryIndex, x => x.OnlyProblems, x => x.SearchText)
            .Skip(1)
            .Subscribe(_ => ApplyFilter());
    }

    /// <summary>筛选后的行。</summary>
    public ObservableCollection<AuditLogRow> Rows { get; } = [];

    /// <summary>类别下拉:全部 / 连接 / 安全。</summary>
    public IReadOnlyList<string> CategoryOptions { get; }

    /// <summary>类别下拉的选中项:0 全部、1 连接、2 安全。</summary>
    public int CategoryIndex
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>只看异常:连接失败、拒绝或接受了变更的主机指纹。</summary>
    public bool OnlyProblems
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>关键字:在事件、类别、会话名与详情里找(不区分大小写)。</summary>
    public string SearchText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>一行摘要:共几条、筛出几条、是否只载入了最近的一部分;载入失败时是失败原因。</summary>
    public string Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>筛选之后一条都没有。</summary>
    public bool IsEmpty
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>重新读一遍审计日志。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>读最近 <see cref="MaxRows" /> 条,对上会话名,再按当前条件筛一遍。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        List<AuditEntry> entries;
        try
        {
            entries = await _auditLog.QueryAsync(MaxRows, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _all = [];
            _truncated = false;
            Rows.Clear();
            IsEmpty = true;
            Status = Strings.Format("AuditLog_LoadFailed", ex.Message);
            return;
        }

        Dictionary<Guid, string> names = await SessionNamesAsync(entries);
        _all = [.. entries.Select(e => ToRow(e, names))];
        _truncated = entries.Count >= MaxRows;
        ApplyFilter();
    }

    /// <summary>把一条记录翻成界面上的一行。</summary>
    internal static AuditLogRow ToRow(AuditEntry entry, IReadOnlyDictionary<Guid, string> sessionNames) =>
        new(entry.Timestamp,
            entry.Category,
            entry.Action,
            CategoryLabel(entry.Category),
            ActionLabel(entry.Action),
            entry.ProfileId is { } id && sessionNames.TryGetValue(id, out string? name) ? name : "—",
            entry.Detail,
            IsProblem(entry.Action));

    /// <summary>类别的界面文字;认不出的原样返回。</summary>
    internal static string CategoryLabel(string category) => category switch
    {
        "connection" => Strings.Get("AuditLog_CategoryConnection"),
        "security" => Strings.Get("AuditLog_CategorySecurity"),
        _ => category,
    };

    /// <summary>动作的界面文字;认不出的原样返回(以后加的新动作至少看得见原值)。</summary>
    internal static string ActionLabel(string action) => action switch
    {
        "connect" => Strings.Get("AuditLog_ActionConnect"),
        "connect-failed" => Strings.Get("AuditLog_ActionConnectFailed"),
        "hostkey-rejected" => Strings.Get("AuditLog_ActionHostKeyRejected"),
        "hostkey-trusted-once" => Strings.Get("AuditLog_ActionHostKeyTrustedOnce"),
        "hostkey-changed-accepted" => Strings.Get("AuditLog_ActionHostKeyChangedAccepted"),
        "hostkey-persist-failed" => Strings.Get("AuditLog_ActionHostKeyPersistFailed"),
        "external-launch" => Strings.Get("AuditLog_ActionExternalLaunch"),
        _ => action,
    };

    /// <summary>值得留意的动作:没连上、拒掉了指纹、接受了一把变过的指纹、信任了却没能保存。</summary>
    internal static bool IsProblem(string action) =>
        action is "connect-failed" or "hostkey-rejected" or "hostkey-changed-accepted" or "hostkey-persist-failed";

    private async Task<Dictionary<Guid, string>> SessionNamesAsync(List<AuditEntry> entries)
    {
        if (_sessions is null || !entries.Any(e => e.ProfileId is not null))
        {
            return [];
        }
        try
        {
            List<SessionProfile> profiles = await _sessions.GetAllSessionsAsync();
            return profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 对不上会话名只是少一栏信息,记录本身照常显示。
            return [];
        }
    }

    private void ApplyFilter()
    {
        string? category = CategoryIndex switch
        {
            1 => "connection",
            2 => "security",
            _ => null,
        };
        string keyword = SearchText.Trim();
        AuditLogRow[] matches =
        [
            .. _all.Where(row =>
                (category is null || row.Category == category)
                && (!OnlyProblems || row.IsProblem)
                && (keyword.Length == 0
                    || row.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || row.SessionName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || row.ActionLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || row.CategoryLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        ];
        Rows.Clear();
        foreach (AuditLogRow row in matches)
        {
            Rows.Add(row);
        }
        IsEmpty = matches.Length == 0;
        string count = matches.Length == _all.Count
            ? Strings.Format("AuditLog_Count", _all.Count)
            : Strings.Format("AuditLog_CountFiltered", matches.Length, _all.Count);
        Status = _truncated ? $"{count} · {Strings.Format("AuditLog_Truncated", MaxRows)}" : count;
    }
}
