using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Data;

/// <summary>
/// 审计日志与连接历史的保留:同一个截止时刻、天数不小于 1、哪边缺了就跳过哪边。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public class AuditRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task BothTables_ArePrunedAtTheSameCutoff()
    {
        IAuditLogService audit = Substitute.For<IAuditLogService>();
        IRecentConnectionService history = Substitute.For<IRecentConnectionService>();

        await AuditRetention.PruneAsync(audit, history, 180, new FixedClock(Now), TestContext.CancellationToken);

        DateTimeOffset cutoff = Now.AddDays(-180);
        await audit.Received(1).DeleteOlderThanAsync(cutoff, Arg.Any<CancellationToken>());
        await history.Received(1).DeleteOlderThanAsync(cutoff, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-5)]
    public async Task ARetentionBelowOneDay_KeepsOneDay(int days)
    {
        // 「一天都不留」不存在:那等于每次启动都把审计清空
        IAuditLogService audit = Substitute.For<IAuditLogService>();

        await AuditRetention.PruneAsync(audit, null, days, new FixedClock(Now), TestContext.CancellationToken);

        await audit.Received(1).DeleteOlderThanAsync(Now.AddDays(-1), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task AMissingService_IsSkipped()
    {
        IRecentConnectionService history = Substitute.For<IRecentConnectionService>();

        await AuditRetention.PruneAsync(null, history, 30, new FixedClock(Now), TestContext.CancellationToken);

        await history.Received(1).DeleteOlderThanAsync(Now.AddDays(-30), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void TheDefaultIsHalfAYear_AndTheSettingIsClamped()
    {
        Assert.AreEqual(180, new AppSettings().Security.AuditLogRetentionDays);

        var tooLong = new AppSettings();
        tooLong.Security.AuditLogRetentionDays = 100_000;
        tooLong.Normalize();
        Assert.AreEqual(SecurityOptions.MaxAuditLogRetentionDays, tooLong.Security.AuditLogRetentionDays);

        var zero = new AppSettings();
        zero.Security.AuditLogRetentionDays = 0;
        zero.Normalize();
        Assert.AreEqual(1, zero.Security.AuditLogRetentionDays);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
