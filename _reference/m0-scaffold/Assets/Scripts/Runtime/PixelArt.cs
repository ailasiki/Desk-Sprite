using System.Collections.Generic;
using UnityEngine;

namespace DeskSprite
{
    /// <summary>
    /// 像素画工具：把"字符网格 + 调色板"变成 Texture2D。
    ///
    /// 为什么不用 PNG？因为 PNG 进 Unity 要配一堆导入设置（Pixels Per Unit、Filter Mode、
    /// Sprite Mode、压缩格式……），而且每次改图都要点回编辑器。用代码生成的话：
    ///   · 改一个字符就是改一个像素，diff 看得见，能进 git
    ///   · 不用管任何 .meta 文件
    ///   · 想要 PNG 了，随时用编辑器菜单导出来（以后加）
    /// 代价是画不了渐变和大图 —— 但像素画本来就不需要。
    /// </summary>
    public static class PixelArt
    {
        /// <summary>
        /// 从字符网格生成贴图。
        /// rows[0] 是**最上面**那一行（符合人肉画画的直觉），内部会翻过来，
        /// 因为 Unity 的纹理坐标 y 轴朝上。
        /// </summary>
        public static Texture2D FromGrid(string[] rows, Dictionary<char, Color32> palette, Color32 transparent)
        {
            int h = rows.Length;
            int w = 0;
            for (int i = 0; i < h; i++) if (rows[i].Length > w) w = rows[i].Length;

            var tex = NewTexture(w, h);
            var px = new Color32[w * h];

            for (int y = 0; y < h; y++)
            {
                string row = rows[h - 1 - y]; // 翻 y 轴
                for (int x = 0; x < w; x++)
                {
                    char c = x < row.Length ? row[x] : '.';
                    Color32 col;
                    if (c != '.' && palette.TryGetValue(c, out col))
                    {
                        // 调色板里有这个字符，用它
                    }
                    else
                    {
                        col = transparent; // '.' 或者调色板里没定义的字符 → 透明
                    }
                    px[y * w + x] = col;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>新建一张"像素画专用"贴图：点采样、不重复、不压缩、不生成 mipmap。</summary>
        public static Texture2D NewTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,        // 放大时保留硬边，不糊
                wrapMode = TextureWrapMode.Clamp,     // 边缘不重复
                anisoLevel = 0,
            };
            return tex;
        }

        /// <summary>把贴图包成 Sprite。PPU 就是"多少像素算一个世界单位"。</summary>
        public static Sprite ToSprite(Texture2D tex, float pixelsPerUnit, Vector2 pivot)
        {
            // 必须显式指定 FullRect：默认的 Tight 网格会按 alpha 裁边，
            // 对我们这种"整张图都是内容"的像素画没必要，还会让 pivot 变得难以预测。
            return Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                pivot,
                pixelsPerUnit,
                0,
                SpriteMeshType.FullRect);
        }

        public static Sprite ToSprite(Texture2D tex, float pixelsPerUnit)
        {
            return ToSprite(tex, pixelsPerUnit, new Vector2(0.5f, 0.5f));
        }

        /// <summary>棋盘格贴图，用来在 Editor 里假装"透明区域"（就像 Photoshop 那样）。</summary>
        public static Texture2D Checkerboard(int w, int h, int cell, Color32 a, Color32 b)
        {
            var tex = NewTexture(w, h);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool even = ((x / cell) + (y / cell)) % 2 == 0;
                    px[y * w + x] = even ? a : b;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>
        /// M0 的"体检表"。四个象限各测一件事，一眼就能看出渲染管线有没有毛病：
        ///   左上 纯色      → 颜色有没有被改（色彩空间、压缩）
        ///   右上 1 像素棋盘 → 是不是点采样（糊了就是 FilterMode 不对 / 被拉伸了）
        ///   左下 渐变      → 有没有色带（banding）
        ///   右下 硬边镂空  → alpha 是不是真的 0（逐像素透明能不能用）
        /// </summary>
        public static Texture2D DiagnosticCard(int size)
        {
            var tex = NewTexture(size, size);
            var px = new Color32[size * size];
            int half = size / 2;
            var border = new Color32(255, 255, 255, 255);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Color32 c;

                    if (x < half && y < half)
                    {
                        c = new Color32(0, 200, 255, 255);                       // 左下：纯青
                    }
                    else if (x >= half && y < half)
                    {
                        c = ((x + y) % 2 == 0)
                            ? new Color32(255, 255, 255, 255)                     // 右下：1 像素棋盘
                            : new Color32(20, 20, 20, 255);
                    }
                    else if (x < half)
                    {
                        byte t = (byte)(255 * y / (size - 1));                    // 左上：纵向渐变
                        c = new Color32(60, t, (byte)(255 - t), 255);
                    }
                    else
                    {
                        // 右上：中间挖一个 alpha=0 的洞，留一圈不透明的框
                        int dx = x - half - half / 2;
                        int dy = y - half - half / 2;
                        int r = half / 3;
                        c = (dx * dx + dy * dy) <= r * r
                            ? new Color32(255, 210, 60, 0)                        // 透明洞
                            : new Color32(255, 210, 60, 255);
                    }

                    // 整张卡描一圈白边，方便看贴图的实际边界
                    if (x == 0 || y == 0 || x == size - 1 || y == size - 1) c = border;
                    px[y * size + x] = c;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }
    }
}
