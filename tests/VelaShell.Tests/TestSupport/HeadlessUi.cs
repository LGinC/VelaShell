using Avalonia.Headless;

namespace VelaShell.Tests.TestSupport;

/// <summary>
/// 异步用例(<c>async Task</c> 测试方法)往 headless UI 线程上派发用例体的唯一入口。
/// </summary>
/// <remarks>
/// 直接 <c>await session.Dispatch(…)</c> 有两个坑,这里一处堵住:
/// <list type="number">
/// <item>会话没有 <c>Func&lt;Task&gt;</c> 重载:无返回值的 async lambda 绑到 <c>Dispatch&lt;Task&gt;</c>,
/// 等到的只是外层 —— 用例体跑到第一个 await 就算「通过」,之后的断言全部丢失(AGENTS.md)。</item>
/// <item>会话在 UI 线程上完成返回的任务,<c>await</c> 它的续体就地跑在 UI 线程上:MSTest 于是在 UI 线程上
/// 接着跑下一条用例,下一条若是 <c>Dispatch(…).GetAwaiter().GetResult()</c> 的写法,就是在 UI 线程上
/// 等 UI 线程 —— 整套卡死,而且只在「await 写法的用例排在阻塞写法的用例前面」时才出现。
/// <c>ConfigureAwait(ConfigureAwaitOptions.ForceYielding)</c> 治不了它:那只管「任务已经完成」的情形。</item>
/// </list>
/// 同步的测试方法照旧用 <c>Dispatch(…).GetAwaiter().GetResult()</c>(在测试线程上等,不受第二条影响)。
/// </remarks>
internal static class HeadlessUi
{
    /// <summary>在 UI 线程上跑异步用例体,等它整个跑完。</summary>
    /// <param name="session">程序集共用的 headless 会话。</param>
    /// <param name="body">用例体。</param>
    /// <returns>用例体跑完(含其中的断言)才完成、并且在线程池上完成的任务。</returns>
    public static Task RunOnUiAsync(this HeadlessUnitTestSession session, Func<Task> body) =>
        OffUiThread(session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None));

    /// <summary>在 UI 线程上跑同步用例体。</summary>
    /// <param name="session">程序集共用的 headless 会话。</param>
    /// <param name="body">用例体。</param>
    /// <returns>用例体跑完才完成、并且在线程池上完成的任务。</returns>
    public static Task RunOnUiAsync(this HeadlessUnitTestSession session, Action body) =>
        OffUiThread(session.Dispatch(body, CancellationToken.None));

    /// <summary>在线程池上转一手,<c>await</c> 它的续体就不会就地跑在 UI 线程上;异常原样透出。</summary>
    private static Task OffUiThread(Task onUi) =>
        onUi.ContinueWith(static t => t.GetAwaiter().GetResult(), CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
}
