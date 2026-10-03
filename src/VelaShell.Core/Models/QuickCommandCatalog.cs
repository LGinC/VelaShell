using VelaShell.Core.Resources;

namespace VelaShell.Core.Models;

/// <summary>quick_commands/commands 的 v2 聚合文档。</summary>
public sealed class QuickCommandData
{
    /// <summary>当前支持的快捷命令文档版本。</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>文档结构版本。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>默认、内置和用户分组。</summary>
    public List<QuickCommandGroup> Groups { get; set; } = [];

    /// <summary>
    /// 自定义快捷命令列表。与内置命令同 <see cref="QuickCommand.Id" /> 的一条是用户改过的内置命令,
    /// 显示时顶替目录里的那一条(见 <see cref="QuickCommandCatalog.VisibleBuiltIns" />)。
    /// </summary>
    public List<QuickCommand> Commands { get; set; } = [];

    /// <summary>
    /// 用户删掉的内置命令。内置命令由目录提供、不落盘,删除只能记成「不再显示哪几条」;
    /// 「恢复内置命令」即清空它。
    /// </summary>
    /// <remarks>
    /// 不认识的标识(更新的版本里才有的内置命令)原样留着:旧版本把它剔掉再同步回去,
    /// 新版本那边删掉的命令就又冒出来了。
    /// </remarks>
    public List<Guid> HiddenBuiltInIds { get; set; } = [];
}

/// <summary>
/// 内置快捷命令目录:设置 → 快捷命令 的预置项,同时作为命令补全建议
/// (plan.md #16)的数据源之一。自定义命令另存于 quick_commands 集合。
/// 描述文案本地化;命令文本本身(shell 命令)不翻译。首次访问时按当前语言取值
/// (启动流程先应用语言设置再构建 UI,顺序有保证)。
/// </summary>
/// <remarks>
/// 用户对内置命令的改动不改目录,而是叠一层覆盖(#555):删掉的记进
/// <see cref="QuickCommandData.HiddenBuiltInIds" />,改过的存成同标识的自定义命令。
/// 没动过的内置命令因此仍跟着界面语言切换描述,新版本新增的内置命令也会自动出现。
/// </remarks>
public static class QuickCommandCatalog
{
    /// <summary>内置快捷命令列表(描述按当前语言本地化)。</summary>
    public static IReadOnlyList<QuickCommand> BuiltIns { get; } =
    [
        BuiltIn("netstat -tlnp", "Network", "netstat -tlnp", "QuickCmd_ShowListeningPorts", 0),
        BuiltIn("systemctl status", "System", "systemctl status", "QuickCmd_SystemdStatus", 0),
        BuiltIn("journalctl -f", "System", "journalctl -f", "QuickCmd_FollowJournal", 1),
        BuiltIn(
            "Enabled Services",
            "System",
            "sudo systemctl list-unit-files --type service | grep enabled",
            "QuickCmd_EnabledServices",
            2
        ),
        BuiltIn(
            "Linux Kernel Packages",
            "System",
            "dpkg --list | grep linux-image",
            "QuickCmd_KernelPackages",
            3
        ),
        BuiltIn("docker ps", "Docker", "sudo docker ps -a", "QuickCmd_DockerPs", 0),
        BuiltIn("docker stats", "Docker", "sudo docker stats", "QuickCmd_DockerStats", 1),
        BuiltIn(
            "docker system prune",
            "Docker",
            "sudo docker system prune -f",
            "QuickCmd_DockerPrune",
            2
        ),
        BuiltIn("ss listening ports", "Network", "ss -tlnp", "QuickCmd_SsListeningPorts", 1),
        BuiltIn("ip addresses", "Network", "ip -brief addr", "QuickCmd_IpAddresses", 2),
        BuiltIn("Public IP", "Network", "curl -s ifconfig.me && echo", "QuickCmd_PublicIp", 3),
        BuiltIn("Connection summary", "Network", "ss -s", "QuickCmd_ConnectionSummary", 4),
        BuiltIn(
            "Failed services",
            "System",
            "systemctl list-units --failed",
            "QuickCmd_FailedServices",
            4
        ),
        BuiltIn("OS release", "System", "cat /etc/os-release", "QuickCmd_OsRelease", 5),
        BuiltIn("Kernel info", "System", "uname -a", "QuickCmd_KernelInfo", 6),
        BuiltIn("Recent logins", "System", "last -n 20", "QuickCmd_RecentLogins", 7),
        BuiltIn("Uptime & load", "Monitor", "uptime", "QuickCmd_Uptime", 0),
        BuiltIn("Memory usage", "Monitor", "free -h", "QuickCmd_MemoryUsage", 1),
        BuiltIn(
            "Top CPU processes",
            "Monitor",
            "ps aux --sort=-%cpu | head -15",
            "QuickCmd_TopCpuProcesses",
            2
        ),
        BuiltIn(
            "Top memory processes",
            "Monitor",
            "ps aux --sort=-%mem | head -15",
            "QuickCmd_TopMemProcesses",
            3
        ),
        BuiltIn("vmstat", "Monitor", "vmstat 1 5", "QuickCmd_VmStat", 4),
        BuiltIn("Disk usage", "Files", "df -h", "QuickCmd_DiskUsage", 0),
        BuiltIn(
            "Largest directories",
            "Files",
            "du -xh --max-depth=1 . 2>/dev/null | sort -hr | head -15",
            "QuickCmd_LargestDirs",
            1
        ),
        BuiltIn(
            "Large files (>100MB)",
            "Files",
            "find . -xdev -type f -size +100M -exec ls -lh {} + 2>/dev/null | head -15",
            "QuickCmd_LargeFiles",
            2
        ),
        BuiltIn(
            "Recently modified",
            "Files",
            "find . -type f -mmin -60 -not -path '*/.*' 2>/dev/null | head -20",
            "QuickCmd_RecentlyModified",
            3
        ),
        BuiltIn("docker compose up", "Docker", "docker compose up -d", "QuickCmd_ComposeUp", 3),
        BuiltIn(
            "docker compose logs",
            "Docker",
            "docker compose logs -f --tail=100",
            "QuickCmd_ComposeLogs",
            4
        ),
        BuiltIn("docker disk usage", "Docker", "docker system df", "QuickCmd_DockerDf", 5),
    ];

    private static readonly HashSet<Guid> BuiltInIds = [.. BuiltIns.Select(command => command.Id)];

    /// <summary>该标识是否属于内置目录(自定义命令用了这个标识,即是改过的内置命令)。</summary>
    public static bool IsBuiltInId(Guid id) => BuiltInIds.Contains(id);

    /// <summary>
    /// 叠上用户的覆盖层之后仍应显示的内置命令:删掉的、改过的(有同标识的自定义命令顶替)都不在其中。
    /// </summary>
    public static IEnumerable<QuickCommand> VisibleBuiltIns(QuickCommandData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var hidden = (data.HiddenBuiltInIds ?? []).ToHashSet();
        var overridden = (data.Commands ?? []).Select(command => command.Id).ToHashSet();
        return BuiltIns.Where(command => !hidden.Contains(command.Id) && !overridden.Contains(command.Id));
    }

    private static QuickCommand BuiltIn(
        string name,
        string group,
        string commandText,
        string descriptionKey,
        int sortOrder
    ) =>
        new()
        {
            Id = QuickCommandGroupCatalog.IdForName($"builtin-command:{name}"),
            GroupId = QuickCommandGroupCatalog.IdForName(group),
            Name = name,
            CommandText = commandText,
            Description = Strings.Get(descriptionKey),
            SortOrder = sortOrder,
            IsBuiltIn = true,
        };
}
