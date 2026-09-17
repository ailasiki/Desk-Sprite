using System;

/// <summary>
/// skin.json 的"数据形状"。
///
/// 用 Unity 内置的 JsonUtility 解析，所以有两个必须遵守的约定
/// （见《皮肤契约设计.md》§5.5）：
///
///   · **不支持字典** → actions 是数组，动作名写在 name 里
///   · **区分不了"没写"和"写了 0/false"** → 所以：
///       - 数值字段用 0 表示"没写"（JsonUtility 缺失的数值就是 0）
///       - 用 noLoop 而不是 loop，这样"没写 = false = 循环"语义正好对上
///
/// 这层只负责"把 json 变成字段"，不理解任何业务规则。
/// 所有默认值和校验都在 SkinLoader 里。
/// </summary>
[Serializable]
public class SkinData
{
    public int formatVersion;

    public string name;
    public string author;
    public string description;

    // ── 几何 ──
    public int[] frameSize;        // null 或长度不足 2 = 没写
    public float[] pivot;          // null 或长度不足 2 = 没写

    // ── 缩放（二选一）──
    public float scale;            // 0 = 没写
    public float displayHeight;    // 0 = 没写

    // ── 动作参数 ──
    public int defaultFps;         // 0 = 没写（用 8）
    public string[] fallback;      // null = 没写
    public SkinActionData[] actions;

    // ── 命中 ──
    public string hitTest;         // null/"" = 没写（用 perPixel）

    // ── 渲染 ──
    /// <summary>
    /// 贴图滤波。null/"" = 没写 → **默认 bilinear**（非像素画）。
    /// 像素画请显式写 "point"，否则放大时会糊。
    /// </summary>
    public string filter;

    // ── 余额文字（可选）──
    public int[] textArea;         // null = 没写 = 不显示余额
}

/// <summary>skin.json 里 actions 数组的一项。动作名由**文件夹名**决定，这里只是参数。</summary>
[Serializable]
public class SkinActionData
{
    public string name;
    public int fps;        // 0 = 没写
    public bool noLoop;    // 没写 = false = 循环
}
