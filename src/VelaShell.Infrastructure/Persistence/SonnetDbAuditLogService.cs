using System.Diagnostics;
using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 时序 measurement <c>audit_log</c> 的审计日志
/// (tags: category/action/profile_id;fields: detail)。
/// </summary>
public sealed class SonnetDbAuditLogService(SonnetDbEngine engine) : IAuditLogService
{
    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>将一条审计记录写入 SonnetDB 时序 measurement。</summary>
    /// <param name="entry">待写入的审计记录。</param>
    /// <param name="cancellationToken">用于取消写入操作的令牌。</param>
    /// <returns>表示异步写入操作的任务。</returns>
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // SonnetDB 不允许空 tag 值:缺省字段不写入。
        var tags = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(entry.Category))
        {
            tags["category"] = entry.Category;
        }
        if (!string.IsNullOrEmpty(entry.Action))
        {
            tags["action"] = entry.Action;
        }
        if (entry.ProfileId is { } profileId)
        {
            tags["profile_id"] = profileId.ToString("D");
        }
        var fields = new Dictionary<string, FieldValue>
        {
            ["detail"] = FieldValue.FromString(entry.Detail)
        };
        return _engine.WritePointAsync(SonnetDbEngine.AuditLogMeasurement, entry.Timestamp, tags, fields, cancellationToken);
    }

    /// <summary>删掉早于 <paramref name="cutoff" /> 的审计记录。</summary>
    /// <remarks>
    /// 按时间条件的 DELETE:只写墓碑,段文件在合并时才真正变小 —— 审计是逐条的短文本,
    /// 不值得像录制那样「暂存 → 重建 → 回灌」(那一套在进程死在中途时会丢掉存活数据)。
    /// 方言不支持时记一笔、原样保留,绝不因为清理失败而丢掉还在保留期内的记录。
    /// </remarks>
    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        int affected = await DeleteOlderThanAsync(_engine, SonnetDbEngine.AuditLogMeasurement, cutoff, cancellationToken)
            .ConfigureAwait(false);
        if (affected < 0)
        {
            Trace.WriteLine($"[VelaShell] audit_log retention: DELETE by time is not supported, nothing was pruned (cutoff {cutoff:O}).");
        }
    }

    /// <summary>按时间删掉某个 measurement 里早于 <paramref name="cutoff" /> 的点;返回受影响的序列数,不支持时 -1。</summary>
    internal static Task<int> DeleteOlderThanAsync(SonnetDbEngine engine, string measurement, DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var parameters = new SqlParameters();
        parameters.AddNamed("cutoff", cutoff.ToUnixTimeMilliseconds());
        return engine.TryDeleteAsync($"DELETE FROM {measurement} WHERE time < @cutoff", parameters, cancellationToken);
    }

    /// <summary>按时间倒序查询审计记录,可按分类过滤。</summary>
    /// <param name="limit">返回的最大记录条数;小于等于 0 时返回空列表。</param>
    /// <param name="category">可选的分类过滤条件,为空时不过滤。</param>
    /// <param name="cancellationToken">用于取消查询操作的令牌。</param>
    /// <returns>按时间倒序排列的审计记录列表。</returns>
    public async Task<List<AuditEntry>> QueryAsync(int limit, string? category = null, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }
        string where = string.IsNullOrEmpty(category) ? string.Empty : $" WHERE category = '{category.Replace("'", "''")}'";
        SelectExecutionResult result = await _engine.QueryAsync($"SELECT time, category, action, profile_id, detail FROM {SonnetDbEngine.AuditLogMeasurement}{where} ORDER BY time DESC LIMIT {limit}",
                                           cancellationToken).ConfigureAwait(false);
        var entries = new List<AuditEntry>(result.Rows.Count);
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Columns.Count && i < row.Count; i++)
            {
                values[result.Columns[i]] = row[i];
            }
            if (values.GetValueOrDefault("time") is not { } time)
            {
                continue;
            }
            entries.Add(new()
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(time)),
                Category = values.GetValueOrDefault("category")?.ToString() ?? string.Empty,
                Action = values.GetValueOrDefault("action")?.ToString() ?? string.Empty,
                ProfileId = Guid.TryParse(values.GetValueOrDefault("profile_id")?.ToString(), out Guid id) ? id : null,
                Detail = values.GetValueOrDefault("detail")?.ToString() ?? string.Empty
            });
        }
        return entries;
    }
}
