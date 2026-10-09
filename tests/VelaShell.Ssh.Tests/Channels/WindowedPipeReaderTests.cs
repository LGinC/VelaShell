// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/05-connection.md §3.2、§5.2

using System.Buffers;
using System.IO.Pipelines;
using VelaShell.Ssh.Channels;

namespace VelaShell.Ssh.Tests.Channels;

[TestClass]
[TestCategory("Channels")]
public sealed class WindowedPipeReaderTests
{
    /// <summary>
    /// 读的一方收尾时先叫停写入方、再清点管道里剩下的：清点之后才到的数据不进管道，由写入方丢弃并回补。
    /// 原先直接清点，清点完到完成内层读端之间接收循环照样往里写 —— 那几包随读端一起丢掉、没人回补，
    /// 丢够半个窗口对端就停在零窗口上（CI 上 <c>StopStandardOutputAsync</c> 之后偶发 30 秒超时）。
    /// 清点的回调正好跑在那段空隙里，这里在回调里模拟接收循环送到一包。
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "Complete")]
    [DataRow(true, DisplayName = "CompleteAsync")]
    public async Task 收尾时先叫停写入方再清点_之后到的数据由写入方回补(bool async)
    {
        Lock gate = new();
        Pipe pipe = new();
        long credited = 0;
        long refused = 0;
        bool injected = false;
        WindowedPipeReader? reader = null;
        reader = new WindowedPipeReader(pipe.Reader, consumed =>
        {
            credited += consumed;
            if (!injected)
            {
                injected = true;
                Deliver(100);
            }
        }, gate);

        Deliver(50);
        if (async)
        {
            await reader.CompleteAsync();
        }
        else
        {
            reader.Complete();
        }

        Assert.IsTrue(injected, "清点到了管道里原有的数据");
        Assert.AreEqual(50, credited, "清点到的是管道里原有的那些");
        Assert.AreEqual(100, refused, "清点之后到的那包被写入方拒收、由它回补，没有落进马上要完成的管道里");

        // 照 SshChannel.TryDeliver 的规矩：在同一把锁里看读的一方还要不要，不要了就丢弃（调用方替它回补）。
        void Deliver(int bytes)
        {
            lock (gate)
            {
                if (reader!.IsAbandoned)
                {
                    refused += bytes;
                    return;
                }
                pipe.Writer.Write(new byte[bytes]);
                Assert.IsTrue(pipe.Writer.FlushAsync().AsTask().IsCompletedSuccessfully, "离暂停水位还远，同步写完");
            }
        }
    }
}
