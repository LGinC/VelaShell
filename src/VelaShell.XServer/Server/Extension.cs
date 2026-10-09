// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

/// <summary>一个扩展:名字、分到的主操作码与事件 / 错误编号、请求处理,以及清理状态的钩子。</summary>
internal sealed class Extension(string name, byte majorOpcode, Action<XClient, XRequestReader> handle)
{
    public string Name { get; } = name;

    public byte MajorOpcode { get; } = majorOpcode;

    /// <summary>这个扩展的第一个事件码;没有自己的事件时为 0。</summary>
    public byte FirstEvent { get; init; }

    /// <summary>占用几个事件码(从 <see cref="FirstEvent" /> 起)。</summary>
    public int EventCount { get; init; }

    /// <summary>这个扩展的第一个错误码;没有自己的错误时为 0。</summary>
    public byte FirstError { get; init; }

    /// <summary>占用几个错误码(从 <see cref="FirstError" /> 起)。</summary>
    public int ErrorCount { get; init; }

    /// <summary>只对部分客户端可见(比如 MIT-SHM 只给同一台机器上的);null = 对谁都可见。</summary>
    public Func<XClient, bool>? VisibleTo { get; init; }

    /// <summary>
    /// 客户端的连接断开了:清它在这个扩展里与连接有关的状态 —— 事件选择、计时器、等着的请求。每个客户端调一次。
    /// 它的资源这时<b>不一定</b>已经释放:以 RetainPermanent / RetainTemporary 断开的,资源还留在资源表里,
    /// 挂在资源上的状态归 <see cref="ClientResourcesDestroyed" /> 清。
    /// </summary>
    public Action<XClient>? ClientClosed { get; init; }

    /// <summary>
    /// 客户端的资源刚从资源表里销毁(Destroy 模式断开时,或者以 Retain 模式留下的资源被 KillClient 销毁时):
    /// 清这个扩展里挂在那些资源上的状态。每个客户端调一次。原先只有 <see cref="ClientClosed" />:Retain 模式断开时它就清掉了
    /// 还留在资源表里的资源(DAMAGE 从此不再累积,SYNC 的报警器不再触发),KillClient 时又调一次。
    /// </summary>
    public Action<XClient>? ClientResourcesDestroyed { get; init; }

    /// <summary>窗口销毁时清这个扩展里与它有关的状态。</summary>
    public Action<XWindow>? WindowDestroyed { get; init; }

    /// <summary>像素图的 ID 释放了(FreePixmap、客户端断开):清这个扩展里挂在它上面的状态。</summary>
    public Action<XPixmap>? PixmapFreed { get; init; }

    /// <summary>任何一个资源离开了资源表(释放请求、客户端断开):释放这个扩展挂在它上面、记在账上的东西(比如 GLX 上下文的 GL 对象)。</summary>
    public Action<XResource>? ResourceFreed { get; init; }

    public bool IsVisibleTo(XClient client) => VisibleTo?.Invoke(client) ?? true;

    public void Handle(XClient client, XRequestReader request) => handle(client, request);
}
