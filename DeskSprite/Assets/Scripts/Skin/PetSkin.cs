using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 一个动作：一段帧序列 + 播放参数。
///
/// 注意"动作"和"状态"是**两个概念**（《皮肤契约设计.md》§5.2）：
/// 状态机里的 idle 是一个**状态**，它将来可能对应多个动作变体（idle_a / idle_b …）。
/// 所以引擎这一层只认识"动作名"，状态到动作的映射由状态机决定。
/// </summary>
public class PetAction
{
    public string Name;
    public Sprite[] Frames;
    public float Fps;
    public bool Loop;

    /// <summary>帧尺寸。和 PetSkin 上的那份冗余，是为了让 PetAction 能自己判断命中。</summary>
    public int FrameWidth;
    public int FrameHeight;

    /// <summary>
    /// 每一帧的 **alpha 通道**（长度 = 宽 × 高，一个像素一个字节）。
    ///
    /// 为什么要存它？因为"按像素命中测试"每帧都要查"鼠标底下那个像素透不透明"。
    /// 存下来就是一次数组查表；不存的话每帧都要 GetPixels32（要分配、还慢）。
    /// 内存代价：64×64 = 4KB/帧，80 帧也才 320KB。
    /// </summary>
    public byte[][] Alphas;

    public int FrameCount
    {
        get { return Frames == null ? 0 : Frames.Length; }
    }

    /// <summary>
    /// 第 <paramref name="frameIndex"/> 帧的某个像素算不算"命中"。
    ///
    /// padding 是**膨胀**：padding=1 表示"周围一圈也算命中"。
    /// 为什么需要它？因为如果没有膨胀，鼠标正好停在边缘像素上时，
    /// 命中状态会随着鼠标抖动反复开合（穿透开关跟着闪）。
    /// 加一圈安全边距，用户完全察觉不到，但状态就稳了。
    /// </summary>
    /// <summary>
    /// alpha 到多少才算"她画到了这里"。
    ///
    /// ⚠️ 这里**故意不是 `> 0`**。原来写的是"只要不是全透明就算命中"，
    /// 那对像素画没问题（边缘非 0 即 255），但对**外部素材**是个坑：
    /// 非像素画（尤其是 AI 生成的图）常常带一圈**很淡的投影/光晕** ——
    /// 那些像素 alpha 只有几个值，肉眼看不见，却会让**一大片看不见的区域也吃鼠标**，
    /// 桌面图标在那片区域点不到，而用户完全看不出为什么。
    ///
    /// 取 64（约 25%）：真的抗锯齿边缘（一般 ≥ 50%）照样算命中，
    /// 而淡到看不见的投影被排除。对像素画完全无影响（它的 alpha 只有 0 和 255）。
    /// </summary>
    public const byte HitAlpha = 64;

    public bool IsOpaque(int frameIndex, int x, int y, int padding)
    {
        if (Alphas == null || frameIndex < 0 || frameIndex >= Alphas.Length) return false;

        byte[] mask = Alphas[frameIndex];
        if (mask == null) return false;

        for (int dy = -padding; dy <= padding; dy++)
        {
            int yy = y + dy;
            if (yy < 0 || yy >= FrameHeight) continue;

            for (int dx = -padding; dx <= padding; dx++)
            {
                int xx = x + dx;
                if (xx < 0 || xx >= FrameWidth) continue;

                if (mask[yy * FrameWidth + xx] >= HitAlpha) return true;
            }
        }
        return false;
    }
}

/// <summary>
/// 加载完成的一套皮肤。
///
/// ⚠️ 引擎只认识这个类，**不认识"刻晴"**。
/// 它里面没有任何"角色"的概念，只有：帧、锚点、缩放、动作名。
/// 这就是《皮肤契约设计.md》§1.4 说的"引擎不知道她在哪，所以不可能搞错她在哪"。
/// </summary>
public class PetSkin
{
    public string Name = "(无名)";
    public string SourceDir;

    /// <summary>
    /// 皮肤**目录名**。**这才是皮肤的身份**，用它给用户数据文件命名。
    ///
    /// ⚠️ 不要用 <see cref="Name"/>：那是**显示名**，来自 `skin.json` 的 name 字段，
    ///    作者可以随便改、两套皮肤也可能重名。而目录名在同一台机器上是唯一的。
    ///    用显示名的后果很隐蔽：用户"拷一份皮肤、改个文件夹名"之后，
    ///    两套皮肤会**共用同一个 skin.user.json** —— 调了 A 的牌子位置，B 也跟着变。
    /// </summary>
    public string FolderName
    {
        get
        {
            if (string.IsNullOrEmpty(SourceDir)) return "";
            return System.IO.Path.GetFileName(SourceDir);
        }
    }

    public int FrameWidth;
    public int FrameHeight;

    /// <summary>归一化锚点。默认**底边中点** (0.5, 0) —— 见《皮肤契约设计.md》§2。</summary>
    public Vector2 Pivot = new Vector2(0.5f, 0f);

    /// <summary>最终缩放倍率 Z：1 个贴图像素 = Z 个屏幕像素。</summary>
    public float Scale = 1f;

    /// <summary>
    /// **皮肤声明的**缩放（不含用户缩放）。
    ///
    /// 为什么要和 <see cref="Scale"/> 分开存：用户随时可能改"她多大"，
    /// 那时要从这个基准值重算，而不是在已经乘过一次的结果上再乘一次
    ///（那样每调一次都会累积漂移）。
    /// </summary>
    public float AuthorScale = 1f;

    /// <summary>
    /// 余额数字放在帧里的哪里：`[中心 x, 底边 y, 宽, 高]`。
    ///
    /// ⚠️ **语义变过一次，两处都要知道**：
    ///   · `x` 现在是数字的**水平中心**（原来是文字区矩形的左边缘）
    ///   · `y` 仍然是**底边**（0 = 帧的底边）—— 和 <see cref="Pivot"/> 的
    ///     `(0.5, 0)` 保持同一套方向，两个字段才不会搞混
    ///   · `宽/高` **已经不用了**：文字块的大小现在由**内容**决定
    ///    （数字位数 × 字号倍率），这样调大字号永远不会被裁掉。
    ///     留着是为了兼容老皮肤 —— 读的时候用 `x + 宽/2` 反推中心，
    ///     老皮肤显示的位置和以前**完全一样**。
    ///
    /// **默认是 `[0, 0, 0, 0]`（帧左下角）** —— 皮肤不声明也有，
    /// 因为"要不要显示余额"已经由用户的 `hideBalance` 决定了，
    /// 不该再由"皮肤写没写这个字段"来决定。
    /// </summary>
    public int[] TextArea;

    /// <summary>
    /// 贴图滤波方式。**默认 bilinear**（非像素画优先，见 `SkinLoader.ResolveFilter`）。
    /// 这里只留一份给日志/诊断用 —— 真正设上去的是每个 `Texture2D.filterMode`。
    /// </summary>
    public FilterMode Filter = FilterMode.Bilinear;

    /// <summary>
    /// 这套皮肤有没有"余额数字"这个部件。
    ///
    /// 加载器现在**一定会**填 <see cref="TextArea"/>（不声明就是原点），
    /// 所以这个属性实际上恒为 true —— 保留它是为了让调用点读起来仍然是
    /// "有没有这个东西"而不是"数组是不是 null"，也为了以后万一再需要区分。
    /// </summary>
    public bool HasTextArea
    {
        get { return TextArea != null && TextArea.Length >= 4; }
    }

    /// <summary>
    /// 窗口尺寸 = 帧尺寸 × Z（《皮肤契约设计.md》§1.3）。
    /// 帧在窗口里的位置：**贴底边、水平居中** —— 所以窗口的底边就是她的落脚线。
    /// </summary>
    public int WindowWidth
    {
        get { return Mathf.RoundToInt(FrameWidth * Scale); }
    }

    public int WindowHeight
    {
        get { return Mathf.RoundToInt(FrameHeight * Scale); }
    }

    /// <summary>动作表。键是**小写**的动作名。</summary>
    public readonly Dictionary<string, PetAction> Actions = new Dictionary<string, PetAction>();

    /// <summary>一定有值：加载时保证 idle 存在，否则算加载失败。</summary>
    public PetAction Idle;

    /// <summary>
    /// **待机池**（《皮肤契约设计.md》§5.7）：动作名以 <c>idle_</c> 开头的那些。
    ///
    /// 模型：<c>idle</c> 是**默认状态，一直循环播放**；池里的动作是挂在它上面的
    /// **一次性分支** —— 随机挑一个、播完一个完整周期、回到 <c>idle</c>。
    /// 所以池子为空是完全正常的（那就是"只有 idle"，也就是"关掉了随机待机"）。
    ///
    /// **顺序是稳定的**（加载时按名字排过序）。这一条是刻意的：池子建立在
    /// Dictionary 的遍历顺序上时，"随机挑一个"出了问题就没法复现。
    /// </summary>
    public readonly List<PetAction> IdlePool = new List<PetAction>();
    /// <summary>
    /// 按名字取动作，带兜底链（《皮肤契约设计.md》§5.2）：
    ///     具体动作 → idle
    /// 只有 idle 是必须存在的，其它动作缺失都能兜住。
    /// </summary>
    public PetAction GetAction(string name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            PetAction a;
            if (Actions.TryGetValue(name.ToLowerInvariant(), out a)) return a;
        }
        return Idle;
    }

    public bool HasAction(string name)
    {
        return !string.IsNullOrEmpty(name) && Actions.ContainsKey(name.ToLowerInvariant());
    }

    /// <summary>
    /// 这套皮肤**拥有的**贴图清单。
    ///
    /// 为什么要单独记一份，而不是"从 Sprite 反推"：加载是**分步**的 ——
    /// 贴图先读进来，Sprite 后来才建。中途任何一步失败（某张 PNG 坏了、
    /// 帧尺寸不齐、没有 idle），那些已经读进来的贴图**还没有 Sprite 挂着它们**。
    /// 有了这份清单，那种"半套"也能被完整放掉。
    /// </summary>
    readonly List<Texture2D> _owned = new List<Texture2D>();

    /// <summary>加载器每读进来一张贴图就登记一次（见 <see cref="_owned"/>）。</summary>
    public void Track(Texture2D tex)
    {
        if (tex != null) _owned.Add(tex);
    }

    /// <summary>
    /// 释放这套皮肤占用的**图形资源**：每一帧的 Sprite，以及它背后的 Texture2D。
    ///
    /// 为什么必须有人显式做：这些贴图是加载器用 `new Texture2D(...)` 造出来的
    /// **独立资源**，不属于任何一个 GameObject —— 所以销毁形象那个 GameObject
    /// **带不走**它们（Unity 只回收挂在 GameObject 上的东西）。
    ///
    /// 不释放的代价是实测出来的：一套 2048×2048 × 7 帧的皮肤，进内存是
    /// 16 MB/帧 × 7 = **112 MB**，而**每换一次皮肤就多一份、旧的永不回收** ——
    /// 换十几次就是 GB 级（进程占 1.8 GB ≈ 一共加载了 16 次）。
    ///
    /// 调完**这套皮肤就不要再用了**（它的 Sprite 已经被销毁）。
    /// 重复调用是安全的：Unity 里销毁过的对象和 null 比较为真，循环会跳过它们。
    /// </summary>
    public void Release()
    {
        // 按 InstanceID 去重。现在的加载器是"一个 PNG 一个 Texture2D"，不会有共享，
        // 但万一以后支持了"多个动作引用同一帧"，这里就不会把同一个资源销毁两次。
        var seen = new HashSet<int>();

        // 先放 Sprite，再放贴图 —— 反过来会留下"指向已销毁贴图"的精灵。
        foreach (PetAction a in Actions.Values)
        {
            if (a == null || a.Frames == null) continue;

            foreach (Sprite s in a.Frames)
            {
                if (s == null) continue;                        // 空帧 / 已经销毁过
                if (seen.Add(s.GetInstanceID())) UnityEngine.Object.Destroy(s);
            }
        }

        foreach (Texture2D t in _owned)
        {
            if (t == null) continue;                            // 已经销毁过
            if (seen.Add(t.GetInstanceID())) UnityEngine.Object.Destroy(t);
        }
        _owned.Clear();
    }
}
