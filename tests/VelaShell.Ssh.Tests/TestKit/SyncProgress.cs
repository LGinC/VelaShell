// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>同步回调的 <see cref="IProgress{T}"/>：<see cref="Progress{T}"/> 经同步上下文异步回调，断言时可能还没到。</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
