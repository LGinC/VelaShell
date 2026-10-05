// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §3.2

using VelaShell.Ssh.Session;

namespace VelaShell.Ssh.Tests.Session;

[TestClass]
[TestCategory("Session")]
public sealed class SendBudgetTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 额度空出来时从队头一个一个放行，放到额度又用完为止 —— 后面的接着睡，不惊群；放行的那一刻就记上账。
    /// 曾经每刷出一轮就叫醒全部等待者，它们一起看额度、一起通过，积压一下子冲过上限好几个报文。
    /// </summary>
    [TestMethod]
    public async Task 额度空出来时按排队顺序一个一个放行()
    {
        SendBudget budget = new(100);
        await budget.ReserveAsync(100, CancellationToken.None);

        ValueTask first = budget.ReserveAsync(10, CancellationToken.None);
        ValueTask second = budget.ReserveAsync(10, CancellationToken.None);
        ValueTask third = budget.ReserveAsync(10, CancellationToken.None);
        Assert.AreEqual(3, budget.Queued);

        budget.Refund(5);   // 95：只够放行一个
        Assert.AreEqual(2, budget.Queued, "额度只够一个，却放行了不止一个");
        Assert.AreEqual(105, budget.Pending, "放行的那一刻就该替它记上账");
        await first.AsTask().WaitAsync(Patience);
        Assert.IsFalse(second.IsCompleted);

        budget.Refund(20);   // 85：放行第二个到 95，还没满，接着放行第三个到 105
        Assert.AreEqual(0, budget.Queued);
        Assert.AreEqual(105, budget.Pending);
        await second.AsTask().WaitAsync(Patience);
        await third.AsTask().WaitAsync(Patience);
    }

    /// <summary>有人排着队时，额度刚空出来的那一刻新来的也排到后面（放行在还额度时当场做完，轮不到它插队）。</summary>
    [TestMethod]
    public async Task 新来的排在已经排着的后面()
    {
        SendBudget budget = new(100);
        await budget.ReserveAsync(100, CancellationToken.None);
        ValueTask early = budget.ReserveAsync(60, CancellationToken.None);

        budget.Refund(10);   // 90：放行先到的，记到 150
        await early.AsTask().WaitAsync(Patience);

        ValueTask late = budget.ReserveAsync(1, CancellationToken.None);
        Assert.AreEqual(1, budget.Queued, "额度已经被先到的用掉，后来的要排队");
        budget.Refund(60);
        await late.AsTask().WaitAsync(Patience);
    }

    /// <summary>不等额度的（应答、窗口回补、保活探测）超过上限也照记，不等。</summary>
    [TestMethod]
    public void 直接记账的不等额度()
    {
        SendBudget budget = new(100);
        budget.Charge(150);
        budget.Charge(10);
        Assert.AreEqual(160, budget.Pending);
    }

    /// <summary>排队期间取消：抛取消、不记账；轮到它时被跳过，不挡住后面的人。</summary>
    [TestMethod]
    public async Task 取消的等待者不记账也不挡路()
    {
        SendBudget budget = new(100);
        await budget.ReserveAsync(100, CancellationToken.None);

        using CancellationTokenSource cancel = new();
        ValueTask cancelled = budget.ReserveAsync(10, cancel.Token);
        ValueTask behind = budget.ReserveAsync(20, CancellationToken.None);

        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled);

        budget.Refund(100);
        await behind.AsTask().WaitAsync(Patience);
        Assert.AreEqual(20, budget.Pending, "取消了的那一位不该记账");
        Assert.AreEqual(0, budget.Queued);
    }

    /// <summary>收尾：排着队的拿到收尾的原因，之后再来的也是；账清零。</summary>
    [TestMethod]
    public async Task 收尾时排队的与后来的都拿到原因()
    {
        SendBudget budget = new(100);
        await budget.ReserveAsync(100, CancellationToken.None);
        ValueTask waiting = budget.ReserveAsync(10, CancellationToken.None);

        InvalidOperationException reason = new("连接收尾了");
        budget.Close(reason);

        Assert.AreSame(reason, await Assert.ThrowsAsync<InvalidOperationException>(async () => await waiting));
        Assert.AreSame(reason, await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await budget.ReserveAsync(1, CancellationToken.None)));
        Assert.AreEqual(0, budget.Pending);
    }

    /// <summary>
    /// 很多发送方同时排队、同时还额度：没有人被漏掉一直等着。
    /// 「看额度」与「进队」之间还回来的额度最容易漏掉那一位（见 ReserveAsync 里的顺序说明）。
    /// </summary>
    [TestMethod]
    public async Task 并发排队与还额度不漏醒任何人()
    {
        SendBudget budget = new(1000);

        Task[] senders =
        [
            .. Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < 2000; i++)
                {
                    await budget.ReserveAsync(300, CancellationToken.None);
                    await Task.Yield();
                    budget.Refund(300);
                }
            })),
        ];

        await Task.WhenAll(senders).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(0, budget.Pending);
        Assert.AreEqual(0, budget.Queued);
    }
}
