// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 行为规格: velashell-docs/zh/ssh/spec/03-key-exchange.md §5.4(指纹图的画法)

using System.Text;

namespace VelaShell.Ssh.HostKeys;

/// <summary>OpenSSH 风格的指纹图：在 17×9 的画布上按摘要的比特随机游走，走过的次数画成字符。</summary>
internal static class FingerprintArt
{
    private const int Width = 17;
    private const int Height = 9;

    /// <summary>计数 0、1、2… 画成的字符；更多的也画最后一个。</summary>
    private const string Symbols = " .o+=*BOX@%&#/^";

    /// <summary>画一张指纹图。</summary>
    /// <param name="digest">指纹的摘要（SHA-256 的 32 字节）。</param>
    /// <param name="title">上框里的标题，如 <c>ED25519 256</c>。</param>
    /// <param name="hashName">下框里的摘要名，如 <c>SHA256</c>。</param>
    public static string Render(ReadOnlySpan<byte> digest, string title, string hashName)
    {
        int[,] visits = new int[Width, Height];
        int x = Width / 2;
        int y = Height / 2;
        (int startX, int startY) = (x, y);

        foreach (byte value in digest)
        {
            int bits = value;
            for (int step = 0; step < 4; step++)
            {
                // 第 0 位：右 / 左；第 1 位：下 / 上 —— 每一步都是斜着走，撞墙的那个方向不动。
                x = Math.Clamp(x + ((bits & 1) != 0 ? 1 : -1), 0, Width - 1);
                y = Math.Clamp(y + ((bits & 2) != 0 ? 1 : -1), 0, Height - 1);
                visits[x, y]++;
                bits >>= 2;
            }
        }

        StringBuilder art = new((Width + 3) * (Height + 2));
        art.Append(Border(title)).Append('\n');
        for (int row = 0; row < Height; row++)
        {
            art.Append('|');
            for (int column = 0; column < Width; column++)
            {
                art.Append(
                    (column, row) == (x, y) ? 'E'
                    : (column, row) == (startX, startY) ? 'S'
                    : Symbols[Math.Min(visits[column, row], Symbols.Length - 1)]);
            }
            art.Append("|\n");
        }
        art.Append(Border(hashName));
        return art.ToString();
    }

    /// <summary><c>+</c>、居中的 <c>[文字]</c> 用 <c>-</c> 补到 17 个字符、<c>+</c>。</summary>
    private static string Border(string text)
    {
        string label = $"[{text}]";
        if (label.Length > Width)
        {
            label = label[..Width];
        }
        int left = (Width - label.Length) / 2;
        return "+" + new string('-', left) + label + new string('-', Width - label.Length - left) + "+";
    }
}
