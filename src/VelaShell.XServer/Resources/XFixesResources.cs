// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Fixes Extension, Version 5.0 —— §8「Region Objects」、§12「Pointer Barriers」

using VelaShell.XServer.Drawing;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>XFIXES 的区域对象。</summary>
internal sealed class XRegionResource(uint id, XClient? owner, Region region) : XResource(id, owner)
{
    public Region Region { get; set; } = region;
}

/// <summary>
/// XFIXES 的指针屏障。只登记、不生效(宿主的系统指针不归我们限制);单独一种资源类型,
/// 删除时才能只认屏障 —— 否则拿任意 ID 调 DestroyPointerBarrier 就能删掉根窗口、默认颜色表或别人的资源。
/// </summary>
internal sealed class XPointerBarrier(uint id, XClient? owner) : XResource(id, owner);
