using System;
using UnityEngine;

/// <summary>
/// 牌子上的动态数字。
///
/// 做法：**整块文字区就一张贴图**，余额一变就重画一次。
///   为什么不是"每个数字一个精灵"？因为那样要管理位置、间距、换位数……
///   而"重画一张小贴图"只有两个动作：清空、写像素。而且余额 5 分钟才变一次，
///   重画的成本可以忽略。
///
/// ⚠️ 文字的位置**固定**来自皮肤的 textArea。所以牌子**不能在帧之间移动** ——
///    牌子一挪，数字就跟不上它（《皮肤契约设计.md》§6.2）。
///    占位皮肤的生成器里，牌子被故意排除在"呼吸"范围之外，就是为了这条。
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class PetSignText : MonoBehaviour
{
    /// <summary>写在木板上的墨色（深棕，比纯黑温和）。</summary>
    static readonly Color32 Ink = new Color32(0x2E, 0x1C, 0x0C, 255);

    /// <summary>
    /// 调试用的偏移（`Ctrl+Shift+方向键` 调）。
    ///
    /// 为什么要它：`textArea` 是每个皮肤自己声明的坐标，而"这套坐标对不对"只能**看着调** ——
    /// 手改 json、重启、再看，一轮就是几十秒。有了实时微调，方向键一按就能看到文字挪一格。
    ///
    /// 调好之后把日志里那行 `textArea = [x, y, w, h]` 填回 `skin.json` 就行。
    /// （**故意不自动写回文件**：那要用 JsonUtility 重写整个 json，
    ///   会把用户手写的排版全冲掉。等设置 UI 做出来再正经做"调完保存"。）
    /// </summary>
    public static int OffsetX, OffsetY;

    public static void Nudge(int dx, int dy)
    {
        OffsetX += dx;
        OffsetY += dy;
    }

    public static void ResetOffset()
    {
        OffsetX = 0;
        OffsetY = 0;
    }

    // ── "绝对坐标"这一层 ──
    //
    // 面板上要编辑的是**绝对值**（帧坐标里的 x、y），不是偏移量。两个理由：
    //   ① 日志里打出来的就是绝对值，用户要直接抄进皮肤文件。
    //      显示偏移量的话，他还得自己加上皮肤声明的值 —— 多一步心算，多一个出错机会。
    //   ② 偏移量是**实现细节**：它是"为了不改皮肤文件而引入的中间量"。
    //      用户想的是"数字在帧里的哪个位置"，不是"比原作者写的位置偏了多少"。
    //
    // 所以对外只暴露 CurrentX/Y（= 皮肤声明 + 偏移），偏移量留在内部。
    //
    // ⚠️ **x 是"数字的水平中心"，y 是"数字的底边"**（帧坐标，y 从帧底边算）。
    //    用中心而不是左边缘：文字块的宽度会随金额位数变化，
    //    左边缘对齐的话数字会左右漂，中心对齐才是"它在原地往两边长"。

    /// <summary>
    /// 字号倍率：5×7 位图字放大几倍。**只允许整数**（非整数倍会让笔画宽度不齐）。
    /// 由用户的 `skin.user.json` 决定，默认 1。
    /// </summary>
    public static int FontScale = 1;

    public static void SetFontScale(int scale)
    {
        FontScale = scale < 1 ? 1 : scale;
    }

    /// <summary>皮肤声明的数字**中心**（帧坐标）。Init 时写入。</summary>
    static int _baseX, _baseY;

    /// <summary>皮肤声明的文字区尺寸。只为日志/诊断用。</summary>
    static int _baseW, _baseH;

    /// <summary>余额数字现在的位置（帧坐标：x = 水平中心、y = 底边）。</summary>
    public static int CurrentX { get { return _baseX + OffsetX; } }
    public static int CurrentY { get { return _baseY + OffsetY; } }

    /// <summary>直接设**绝对坐标**，内部换算成偏移。</summary>
    public static void SetCurrent(int x, int y)
    {
        OffsetX = x - _baseX;
        OffsetY = y - _baseY;
    }

    /// <summary>
    /// 把当前的位置和字号打一行日志 —— 用户直接抄这一行回去填皮肤文件。
    ///
    /// 为什么和 <see cref="ApplyPosition"/> **分开**：
    /// 设置面板是**边打字边生效**的，每敲一个字符位置都会变。
    /// 要是 ApplyPosition 里顺手打日志，输入 "105" 就会刷出三行，日志立刻没法看。
    /// 所以"摆位置"和"报告结果"分开：位置每次都摆，日志只在**一次编辑真正结束**时打一行。
    /// </summary>
    public static void LogArea()
    {
        Debug.Log(string.Format("[DeskSprite] 余额数字：中心 x={0}  底边 y={1}  字号 ×{2}",
                                CurrentX, CurrentY, FontScale));
    }

    SpriteRenderer _sr;
    PetSkin _skin;

    Texture2D _tex;
    Color32[] _px;
    int _w, _h;

    string _shown;
    int _appliedOffX = int.MinValue;
    int _appliedOffY = int.MinValue;
    int _appliedScale = int.MinValue;

    void Awake()
    {
        _sr = GetComponent<SpriteRenderer>();
    }

    public void Init(PetSkin skin)
    {
        _skin = skin;

        // ⚠️ **x 现在是"数字的水平中心"，不是"文字区左边缘"。**
        //
        // 为什么改这个含义：文字区的大小现在由**内容**决定（见 Redraw），
        // 所以"左边缘"会随着余额数字的位数变化而漂移 —— 金额从 ¥9.00 变成 ¥110.00，
        // 整串数字就会往右挪。而"中心"是稳的：位数变了，数字在原地往两边长。
        //
        // 兼容老皮肤：它们声明的是 [x, y, w, h]（x 是左边缘），
        // 所以这里取**那个矩形的中心** = x + w/2 —— 显示位置和以前**完全一样**。
        _baseX = skin.TextArea[0] + skin.TextArea[2] / 2;
        _baseY = skin.TextArea[1];
        _baseW = skin.TextArea[2];
        _baseH = skin.TextArea[3];

        EnsureTexture(1, 1);      // 真正的尺寸由 Redraw 按内容重算
        _sr.sortingOrder = 1;     // 画在她上面

        ApplyPosition();
    }

    /// <summary>
    /// 保证贴图是 w×h 的（不够或不等就重建）。
    ///
    /// 为什么需要"按内容重建"：字号可以调（1×、2×、4×…），数字的位数也会变
    ///（`--` / `¥9.00` / `¥1234.56`），所以文字块的大小不是固定的。
    /// 贴图很小（最多几十×几十像素），重建几乎不花时间；
    /// 而**裁剪**才是大问题 —— 字号调大之后被旧尺寸裁掉一截，看起来就是"坏了"。
    /// </summary>
    void EnsureTexture(int w, int h)
    {
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (_tex != null && _w == w && _h == h) return;

        // ⚠️ 旧的**贴图和精灵都要销毁**，不能只销毁贴图。
        //
        // 只销毁 _tex 的话，`Sprite.Create` 造出来的那个精灵对象会留下 ——
        // 它同样不在任何 GameObject 上（运行时资源），没人回收。
        // 而这条路径**每次改字号都会走一遍**：拖一次"字号"滑块 = 重建几十次
        // = 漏掉几十个精灵 + 贴图。
        if (_sr != null && _sr.sprite != null) Destroy(_sr.sprite);
        if (_tex != null) Destroy(_tex);
        _tex = null;

        _w = w;
        _h = h;

        _tex = new Texture2D(_w, _h, TextureFormat.RGBA32, false);
        _tex.filterMode = FilterMode.Point;      // 像素画：点采样，放大不糊
        _tex.wrapMode = TextureWrapMode.Clamp;
        _tex.anisoLevel = 0;

        _px = new Color32[_w * _h];
        Clear();

        // pivot 用 (0,0) = **左下角**。
        // 这样文字块的左下角就正好落在 transform 的位置上 ——
        // 换算坐标时不用再想"半个宽度"这种事，少一个出错的地方。
        _sr.sprite = Sprite.Create(_tex, new Rect(0, 0, _w, _h), Vector2.zero, 1f,
                                   0, SpriteMeshType.FullRect);

        _shown = null;           // 尺寸变了，之前画的内容作废，逼 Redraw 重画
    }

    /// <summary>
    /// 这个组件挂在她（形象）的子节点上 —— 换皮肤时整个形象被销毁，组件跟着没了，
    /// 但**它自己 new 出来的贴图和精灵不会**。所以这里手动带走：
    /// 不加这一段，每换一次皮肤就漏一块数字贴图（小，但同样是"永不回收"）。
    /// </summary>
    void OnDestroy()
    {
        if (_sr != null && _sr.sprite != null) Destroy(_sr.sprite);
        if (_tex != null) Destroy(_tex);
        _tex = null;
    }

    /// <summary>
    /// 把文字块摆到"帧坐标 → 世界坐标"该在的地方。
    ///
    /// 换算（这是最容易想错的一步）：
    ///   · 坐标用**帧坐标**：x 从左边缘、y 从**下边**（0 = 帧底边）
    ///   · 本节点的父对象（PetVisual）已经位于"帧的**底边中点**"
    ///   · 所以相对它的偏移是：
    ///         x = 帧内 x − 帧宽/2     （把"帧左边缘"换算成"相对帧中心"）
    ///         y = 帧内 y              （帧底边就是父对象的位置，不用再减）
    ///   · 文字精灵的 pivot 用 (0,0) = 左下角，所以这个偏移是它左下角的位置 ——
    ///     而 CurrentX 是**中心**，所以要再减半个贴图宽，数字才会以它为中心。
    /// </summary>
    void ApplyPosition()
    {
        // 用 CurrentX / CurrentY（绝对坐标）算，而不是在这里再写一次"声明值 + 偏移"的加法 ——
        // 面板编辑的是绝对值、日志打的是绝对值，所以这里也只认绝对值：
        // 一处定义，处处一致，少一个"两处加法哪天不一致"的机会。
        float baseX = CurrentX - _w * 0.5f - _skin.FrameWidth * 0.5f;
        float baseY = CurrentY;

        transform.localPosition = new Vector3(baseX, baseY, 0f);

        // ⚠️ 这里**只记位置**，不记字号。
        //    字号那个"我处理过了"的标记必须由**重画**那一支写 ——
        //    否则同时改位置和字号时，这一支会把字号改动悄悄吃掉（踩过一次，见 Update 的注释）。
        _appliedOffX = OffsetX;
        _appliedOffY = OffsetY;

        // 这里**不打日志** —— 位置可能每敲一个字符就变一次。
        // 要打日志请调 LogArea()（在一次编辑真正结束时调一次）。见 LogArea 的注释。
    }

    void Update()
    {
        if (_skin == null || _tex == null) return;

        // 位置变了 → 只挪位置（贴图内容不用重画）
        if (OffsetX != _appliedOffX || OffsetY != _appliedOffY) ApplyPosition();

        // 字号变了 → **只把内容标记为作废**，真正的重画交给下面那一段。
        //
        // ⚠️ 这里踩过一次：原来 `ApplyPosition()` 里顺手写了 `_appliedScale = FontScale`，
        //    于是"同时改位置和字号"时，位置那一支先跑、把 `_appliedScale` 提前更新了，
        //    字号这一支再看就"没变化" —— **永远不重画，贴图还是旧尺寸**。
        //    症状特别像 UI bug（输入框显示 1、字却没变回去），其实是**一个记账字段被两处写**。
        //    教训：这种"我处理过了"的标记，只能由**真正做那件事**的地方写。
        if (FontScale != _appliedScale)
        {
            _appliedScale = FontScale;
            _shown = null;                 // 尺寸和内容都作废
        }

        string s = CurrentText();
        if (s == _shown) return;                 // 只有变了才重画
        _shown = s;
        Redraw(s);                         // 内部按当前字号重算贴图尺寸
        ApplyPosition();                   // 宽度可能变了 -> 中心对齐要重算
    }

    /// <summary>
    /// 牌子上写什么。
    ///
    /// 拿不到余额时写 `--` 而不是留空 —— **留空和"余额为零"看起来一样**，
    /// 而 `--` 明确表示"还不知道"。这是"失败要看得见"的一个小应用。
    /// </summary>
    string CurrentText()
    {
        // 用户主动关掉了 → 牌子上**什么都不写**。
        // 注意这里和下面的 "--" 不一样：写 "--" 会让人以为"出错了"，
        // 而这次是用户自己的选择，空白才是对的。
        if (DeepSeekBalance.State == DeepSeekBalance.Status.Disabled) return "";

        // 正在查、而且已经有上一次的数据 → **继续显示旧值**。
        // 为什么不显示 "--"：现在"点她"就会刷新，如果每点一下牌子都清空一瞬间，
        // 看起来像"点一下就出错"。旧值 + 一秒后更新，才是更好的反馈。
        // （这和"失败不清空上一次的好数据"是同一条原则。）
        if (DeepSeekBalance.State == DeepSeekBalance.Status.Fetching
            && DeepSeekBalance.LastSuccessUtc != System.DateTime.MinValue)
        {
            return CurrencySymbol(DeepSeekBalance.Currency) + DeepSeekBalance.Amount;
        }

        if (DeepSeekBalance.State == DeepSeekBalance.Status.Ok)
        {
            return CurrencySymbol(DeepSeekBalance.Currency) + DeepSeekBalance.Amount;
        }
        return "--";
    }

    static string CurrencySymbol(string currency)
    {
        if (string.IsNullOrEmpty(currency)) return "";
        if (currency.Equals("CNY", StringComparison.OrdinalIgnoreCase)) return "¥";
        if (currency.Equals("USD", StringComparison.OrdinalIgnoreCase)) return "$";
        return "";
    }

    void Clear()
    {
        var none = new Color32(0, 0, 0, 0);
        for (int i = 0; i < _px.Length; i++) _px[i] = none;
    }

    /// <summary>
    /// 重画。**贴图尺寸由内容决定** —— 不再依赖皮肤声明的 w/h。
    ///
    /// 为什么不再用固定尺寸的框：字号是可调的（×1、×2、×4…），
    /// 框一旦写死，调大字号就会被裁掉 ✗ —— 而"截断"这种失败看起来像坏了，
    /// 不像"设置项没生效"。让贴图刚好等于文字大小，就永远不会裁。
    ///
    /// 代价是贴图会随内容重建（位数变化、字号变化时）—— 但它只有几十×几十像素，
    /// 重建几乎不花时间。而"永不裁剪"换来的确定性值这个价。
    /// </summary>
    void Redraw(string s)
    {
        int scale = FontScale < 1 ? 1 : FontScale;
        int tw = PixelFont.MeasureWidth(s, scale);
        int th = PixelFont.MeasureHeight(scale);

        EnsureTexture(Mathf.Max(1, tw), Mathf.Max(1, th));
        Clear();

        // 贴图刚好等于文字，所以不用居中 —— 左下角就是 (0,0)。
        // 数字的**位置**由 ApplyPosition 按"中心"对齐去摆。
        PixelFont.Draw(_px, _w, _h, s, 0, 0, Ink, scale);

        _tex.SetPixels32(_px);
        _tex.Apply(false, false);
    }
}
