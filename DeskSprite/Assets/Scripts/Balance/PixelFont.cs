using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 5×7 位图字体。只做余额牌子上要用的那些字形：数字、`.`、`-`、`¥`、`$`、空格，外加一个 `?`。
///
/// 为什么不用 TextMeshPro？
///   ① **色键透明模式下，抗锯齿文字会带紫边** —— 文字边缘是半透明像素，
///      和品红背景混在一起洗不干净（《皮肤契约设计.md》§4 那条）。
///   ② 位图字体是**硬边**的，和像素画风格天然一致。
///   ③ 中文字形要引系统字体、要处理授权 —— 而牌子上只有数字。
///      （等设置 UI 要做中文界面时，再引 TextMeshPro，那是另一条路。）
///
/// 字形写作"一行一个 glyph"，用 `#` 表示点亮的像素。这样它既是数据、又能直接看：
/// <code>
///     "8:.###./#...#/#...#/.###./#...#/#...#/.###."
/// </code>
/// </summary>
public static class PixelFont
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;
    /// <summary>每个字形占的横向宽度（含 1 像素间距）。</summary>
    public const int Advance = GlyphWidth + 1;

    static readonly string[] Raw =
    {
        "0:.###./#...#/#..##/#.#.#/##..#/#...#/.###.",
        "1:..#../.##../..#../..#../..#../..#../.###.",
        "2:.###./#...#/....#/...#./..#../.#.../#####",
        "3:#####/...#./..#../...#./....#/#...#/.###.",
        "4:...#./..##./.#.#./#..#./#####/...#./...#.",
        "5:#####/#..../####./....#/....#/#...#/.###.",
        "6:..##./.#.../#..../####./#...#/#...#/.###.",
        "7:#####/....#/...#./..#../.#.../.#.../.#...",
        "8:.###./#...#/#...#/.###./#...#/#...#/.###.",
        "9:.###./#...#/#...#/.####/....#/...#./.##..",
        ".:...../...../...../...../...../.##../.##..",
        "-:...../...../...../.####/...../...../.....",
        "¥:#...#/.#.#./..#../#####/..#../#####/..#..",
        "$:..#../.####/#.#../.###./..#.#/####./..#..",
        " :...../...../...../...../...../...../.....",
        // 不认识的字符就用它 —— 让"缺字形"这件事在屏幕上是**看得见**的
        "?:.###./#...#/....#/..##./..#../...../..#..",
    };

    static readonly Dictionary<char, string[]> Glyphs = Build();
    static bool _validated;

    static Dictionary<char, string[]> Build()
    {
        var d = new Dictionary<char, string[]>();
        foreach (string entry in Raw)
        {
            int colon = entry.IndexOf(':');
            if (colon != 1) continue;                 // 我们的键都是单字符
            d[entry[0]] = entry.Substring(colon + 1).Split('/');
        }
        return d;
    }

    /// <summary>
    /// 一段文字需要多宽（像素）。
    /// <paramref name="scale"/> 是整数倍放大（默认 1 = 原大小）。
    /// </summary>
    public static int MeasureWidth(string text, int scale = 1)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (scale < 1) scale = 1;
        return (text.Length * Advance - 1) * scale;    // 末尾不留间距
    }

    /// <summary>一段文字需要多高（像素）。</summary>
    public static int MeasureHeight(int scale = 1)
    {
        return GlyphHeight * (scale < 1 ? 1 : scale);
    }

    /// <summary>
    /// 把文字画进一个像素数组。**坐标以左下角为原点**（和 Unity 纹理一致）。
    /// <paramref name="x0"/><paramref name="y0"/> 是文字块左下角。
    /// 越界的部分直接跳过 —— 所以字太长会被裁掉，而不是崩。
    ///
    /// <paramref name="scale"/> 是**整数倍**放大：每个亮点画成 scale×scale 的方块。
    /// 为什么只允许整数倍 —— 位图字体非整数倍会出现"有的笔画两像素宽、有的三像素"，
    /// 看起来就是糊了/坏了。要大就 ×2 ×3 ×4，始终是硬边。
    /// </summary>
    public static void Draw(Color32[] px, int texW, int texH,
                            string text, int x0, int y0, Color32 color, int scale = 1)
    {
        if (px == null || string.IsNullOrEmpty(text)) return;
        ValidateOnce();

        if (scale < 1) scale = 1;

        for (int i = 0; i < text.Length; i++)
        {
            string[] g;
            if (!Glyphs.TryGetValue(text[i], out g)) g = Glyphs['?'];

            int gx = x0 + i * Advance * scale;

            for (int row = 0; row < g.Length && row < GlyphHeight; row++)
            {
                // 字形是"从上往下"写的，而纹理坐标 y 朝上 —— 这里翻一次
                int py = y0 + (GlyphHeight - 1 - row) * scale;
                if (py + scale <= 0 || py >= texH) continue;

                string bits = g[row];
                for (int cx = 0; cx < bits.Length && cx < GlyphWidth; cx++)
                {
                    if (bits[cx] != '#') continue;
                    int pxx = gx + cx * scale;
                    if (pxx + scale <= 0 || pxx >= texW) continue;

                    // 一个亮点 -> scale×scale 的实心方块
                    for (int dy = 0; dy < scale; dy++)
                    {
                        int yy = py + dy;
                        if (yy < 0 || yy >= texH) continue;

                        for (int dx = 0; dx < scale; dx++)
                        {
                            int xx = pxx + dx;
                            if (xx < 0 || xx >= texW) continue;
                            px[yy * texW + xx] = color;
                        }
                    }
                }
            }
        }
    }

    /// <summary>字形尺寸写错了就报一次 —— 免得"某个数字看起来怪怪的"要查半天。</summary>
    static void ValidateOnce()
    {
        if (_validated) return;
        _validated = true;

        foreach (var kv in Glyphs)
        {
            string[] g = kv.Value;
            if (g.Length != GlyphHeight)
            {
                Debug.LogWarning(string.Format(
                    "[DeskSprite] PixelFont 字形 '{0}' 有 {1} 行，应该是 {2} 行", kv.Key, g.Length, GlyphHeight));
                continue;
            }
            for (int r = 0; r < g.Length; r++)
            {
                if (g[r].Length != GlyphWidth)
                {
                    Debug.LogWarning(string.Format(
                        "[DeskSprite] PixelFont 字形 '{0}' 第 {1} 行有 {2} 个字符，应该是 {3} 个",
                        kv.Key, r, g[r].Length, GlyphWidth));
                }
            }
        }
    }
}
