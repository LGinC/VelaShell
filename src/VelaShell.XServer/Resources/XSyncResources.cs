// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Synchronization Extension Protocol, Version 3.1 —— §3「Types」(COUNTER、TRIGGER 的 value-type / test-type、ALARM 的状态、FENCE)

using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>SYNC 计数器。系统计数器(SERVERTIME / IDLETIME)的值由服务端按需算出。</summary>
internal sealed class XSyncCounter(uint id, XClient? owner, long value, string? systemName = null) : XResource(id, owner)
{
    public long Value { get; set; } = value;

    /// <summary>系统计数器的名字;普通计数器为 null。</summary>
    public string? SystemName { get; } = systemName;
}

/// <summary>SYNC 的触发器:计数器与等待值的比较。</summary>
internal sealed class XSyncTrigger
{
    public const uint PositiveTransition = 0, NegativeTransition = 1, PositiveComparison = 2, NegativeComparison = 3;

    public const uint Absolute = 0, Relative = 1;

    public XSyncCounter? Counter { get; set; }

    /// <summary>value-type:0 Absolute,1 Relative —— 客户端给的,照原样留着(QueryAlarm 报它,重新初始化时按它算)。</summary>
    public uint ValueType { get; set; }

    /// <summary>wait-value:客户端给的值;报警器触发后按 delta 推进时一同加上。</summary>
    public long WaitValue { get; set; }

    /// <summary>测试值:初始化时按 value-type 算出(Relative 时是当时的计数器值 + wait-value),比较、事件里都用它。</summary>
    public long TestValue { get; set; }

    public uint TestType { get; set; }

    /// <summary>上一次求值时计数器的值(Transition 要看「从哪一侧跨过来」)。</summary>
    public long LastValue { get; set; }

    public bool Satisfied(long value) => TestType switch
    {
        PositiveComparison => value >= TestValue,
        NegativeComparison => value <= TestValue,
        PositiveTransition => LastValue < TestValue && value >= TestValue,
        _ => LastValue > TestValue && value <= TestValue,
    };
}

/// <summary>SYNC 报警器。</summary>
internal sealed class XSyncAlarm(uint id, XClient owner) : XResource(id, owner)
{
    public const byte Active = 0, Inactive = 1, Destroyed = 2;

    /// <summary>触发器;没给的属性取 CreateAlarm 的默认值:counter None、value-type Absolute、value 0、test-type PositiveComparison。</summary>
    public XSyncTrigger Trigger { get; } = new() { TestType = XSyncTrigger.PositiveComparison };

    public long Delta { get; set; } = 1;

    public byte State { get; set; } = Active;

    /// <summary>要收 AlarmNotify 的客户端(创建者默认要;别的客户端经 ChangeAlarm 的 events 选)。</summary>
    public HashSet<XClient> Listeners { get; } = [];
}

/// <summary>SYNC 栅栏。</summary>
internal sealed class XSyncFence(uint id, XClient owner, bool triggered) : XResource(id, owner)
{
    public bool Triggered { get; set; } = triggered;
}
