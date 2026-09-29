namespace VelaShell.Core.Data;

/// <summary>
/// 审计日志(<c>audit_log</c>)与连接历史(<c>conn_history</c>)的保留策略。
/// </summary>
/// <remarks>
/// 两张时序表每连一次各写一条,原先只增不减。保留天数在 设置 → 安全审计,启动时删掉更早的记录
/// (与会话日志、录制的过期清理同一时机)。两张表共用一个天数:连接历史就是「最近连接」的底账,
/// 比审计留得更久没有意义,留得更短又会让审计里的会话在侧栏里对不上。
/// </remarks>
public static class AuditRetention
{
    /// <summary>删掉超过 <paramref name="retentionDays" /> 天的审计日志与连接历史;哪一边没有就跳过哪一边。</summary>
    /// <param name="auditLog">审计日志。</param>
    /// <param name="history">连接历史。</param>
    /// <param name="retentionDays">保留天数,小于 1 按 1 算(不存在「一天都不留」)。</param>
    /// <param name="timeProvider">时钟;测试用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task PruneAsync(IAuditLogService? auditLog,
        IRecentConnectionService? history,
        int retentionDays,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset cutoff = (timeProvider ?? TimeProvider.System).GetUtcNow().AddDays(-Math.Max(1, retentionDays));
        if (auditLog is not null)
        {
            await auditLog.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }
        if (history is not null)
        {
            await history.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }
    }
}
