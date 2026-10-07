// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 第 4 节「Errors」:Alloc「The server failed to allocate the requested resource」
//   (任何请求都可以回它)
//   架构:velashell-docs/zh/xserver/design/architecture.md §5(线程模型:一条请求从头到尾持着像素锁)

namespace VelaShell.XServer.Protocol;

/// <summary>
/// 一项工作(一条请求、一次宿主注入)能花的工作量。执行线程执行一项工作期间一直持着像素锁,宿主的 UI 线程读像素时等这把锁 ——
/// 一条请求跑几分钟,整个宿主就冻几分钟。代价与「请求字节数 × 绘图面积」不成比例的路径(大坐标范围、显示列表、重叠的行……)
/// 在热循环里按处理的像素 / 行 / 顶点扣预算,扣光就抛 <see cref="XWorkBudgetExhausted" />,由分派层回 Alloc。
/// </summary>
/// <remarks>
/// 预算挂在线程上:一项工作从头到尾在同一个线程上同步执行(执行循环在两项之间才 await)。
/// 不在任何工作项里的调用(直接驱动光栅化的单元测试)不计数。
/// </remarks>
internal static class WorkBudget
{
    /// <summary>默认每项工作的预算:约 2.7 亿个单位(一个单位约等于处理一个像素),大约是 4K 屏整屏填满 32 遍。</summary>
    public const long DefaultUnits = 1L << 28;

    [ThreadStatic]
    private static long _remaining;

    [ThreadStatic]
    private static bool _active;

    /// <summary>开始一项工作:预算重置为 <paramref name="units" />。</summary>
    public static void Begin(long units)
    {
        _remaining = units;
        _active = true;
    }

    /// <summary>这项工作结束。</summary>
    public static void End() => _active = false;

    /// <summary>花掉 <paramref name="units" /> 个单位;花光时抛 <see cref="XWorkBudgetExhausted" />。</summary>
    public static void Charge(long units)
    {
        if (!_active)
        {
            return;
        }
        _remaining -= units;
        if (_remaining < 0)
        {
            _active = false;   // 只抛一次:抛出之后的清理代码不必再计数
            throw new XWorkBudgetExhausted();
        }
    }
}

/// <summary>一项工作花光了预算:请求回 Alloc(GLX 的渲染命令记 OUT_OF_MEMORY)。</summary>
internal sealed class XWorkBudgetExhausted() : XProtocolError(XErrorCode.Alloc);
