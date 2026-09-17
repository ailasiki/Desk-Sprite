using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 皮肤加载器：把 `StreamingAssets/skins/&lt;名字&gt;/` 变成一个 <see cref="PetSkin"/>。
///
/// 目录约定（《皮肤契约设计.md》§5.1）：
/// <code>
/// keqing/
/// ├── skin.json          ← 可以完全不写
/// ├── idle/  00.png ...  ← **文件夹名 = 动作名**，文件名排序 = 帧顺序
/// └── walk/  00.png ...
/// </code>
///
/// 设计原则（沿用 M0）：
///   · **失败要留痕，不要静默。** 所有问题都写进 <see cref="Diagnostics"/>，屏幕上能看到。
///   · **默认皮肤走和用户皮肤完全相同的路径**（dogfooding）——
///     所以这个加载器从第一天就在接受真实测试。
/// </summary>
public static class SkinLoader
{
    /// <summary>加载好的皮肤。null = 加载失败。</summary>
    /// <summary>当前皮肤。</summary>
    public static PetSkin Current { get; private set; }

    /// <summary>加载结果或失败原因。屏幕上那块诊断面板会显示它。</summary>
    public static string Diagnostics { get; private set; } = "(还没加载)";

    /// <summary>打包**自带**的皮肤根目录（跟着 exe 走，可能是只读的）。</summary>
    public static string BundledSkinsRoot
    {
        get { return Path.Combine(Application.streamingAssetsPath, "skins"); }
    }

    /// <summary>
    /// 皮肤的**搜索根目录**，按优先级从高到低：用户的在前，打包自带的在后。
    ///
    /// 【为什么用户的优先】这样"用户放一套皮肤进去"就能生效，
    /// 完全不用碰安装目录里的任何东西（那里可能只读）。
    /// 而打包自带的那份永远在，所以最坏情况也总有东西可显示。
    ///
    /// 【为什么用户皮肤放在"用户数据目录"下面】和 `skin.user.json` 在同一个地方
    /// （见 §10）：
    ///   · **不会被打包重建冲掉** —— 打包产物里的 `_Data` 每次重新构建都会被重建，
    ///     放那里等于"保存了，下次打包就没了"
    ///   · 装在 `Program Files` 时那里是只读的，放不了用户的东西
    ///   · 一个用户的所有东西集中在一处，删掉 = 恢复出厂
    /// </summary>
    public static string[] SkinRoots()
    {
        return new string[]
        {
            Path.Combine(PetConfig.UserDataFolder, "skins"),   // 用户皮肤（优先）
            BundledSkinsRoot,                                  // 打包自带（兜底）
        };
    }

    /// <summary>
    /// 按优先级扫所有搜索根目录，加载**第一个**能用的皮肤。
    ///
    /// "能用"的定义：目录里有 `idle/`，且里面至少有一张能读的 PNG。
    /// 排序是为了保证结果稳定 —— 目录枚举顺序在不同机器上不一定一样。
    ///
    /// ⚠️ **坏皮肤会被跳过，不会让整体失败。** 用户手放的皮肤出问题是常事
    /// （少个目录、PNG 坏了），这时应该继续往下找，而不是让她整个人消失。
    /// 跳过时把原因记进问题清单 —— 用户能看见"我放的那套为什么没生效"。
    /// </summary>
    public static PetSkin LoadDefaultSkin()
    {
        Current = null;

        string[] roots = SkinRoots();

        // 用户皮肤目录不存在就**建出来**。理由和"生成配置模板"是同一条：
        // 用户需要一个**看得见的地方**放东西。让他自己去猜
        // "%APPDATA%\DeskSprite\skins" 这种路径，等于把这个功能藏起来。
        // （打包自带那个根目录**不建** —— 它不存在就是安装有问题，该报出来。）
        try { Directory.CreateDirectory(roots[0]); }
        catch (Exception e)
        {
            Debug.LogWarning("[DeskSprite] 建不出用户皮肤目录：" + roots[0] + " —— " + e.Message);
        }

        // 把搜索顺序打出来。这一行是"我把皮肤放哪才生效"这个问题的**答案**，
        // 而它必须在**成功和失败两种情况下都出现** —— 否则出问题时反而看不到该看的东西。
        Debug.Log("[DeskSprite] 皮肤搜索根目录（按优先级）：\n  " + string.Join("\n  ", roots));

        var problems = new List<string>();
        int candidateCount = 0;

        // ── ① 用户**明确挑过**一套皮肤 → 就用它 ──
        //
        // 优先于所有搜索顺序：用户点过"选择文件夹"，那就是他的决定，不该被别的皮肤盖过。
        // 但路径可能**失效**（被移走、改名、删掉），那种情况**退回默认扫描**并说清原因 ——
        // 不能因为一个失效的路径就让她整个人消失。
        string picked = PetConfig.SkinPath;
        if (!string.IsNullOrEmpty(picked))
        {
            if (Directory.Exists(Path.Combine(picked, "idle")))
            {
                PetSkin pickedSkin = Load(picked);
                if (pickedSkin != null)
                {
                    Current = pickedSkin;
                    Diagnostics += "\n  来源：（你在设置里选的）" + picked;
                    return pickedSkin;
                }
                problems.Add("你选的那套皮肤加载失败 —— " + Diagnostics
                           + "\n  （下面按默认顺序继续找）");
            }
            else
            {
                problems.Add("你选的那套皮肤已经无效了（里面找不到 idle/ 目录）：" + picked
                           + "\n  它可能被移走、改名或者删掉了。（下面按默认顺序继续找）");

                // 最常见的原因：**选错了层级**。GitHub 上下的压缩包解开常常是
                // `皮肤名-main/皮肤名/`，用户一眼看到的是外面那层。
                // 与其让他去猜，不如直接指出"里面那个看起来对"。
                try
                {
                    var looksLikeSkin = new List<string>();
                    foreach (string sub in Directory.GetDirectories(picked))
                    {
                        if (Directory.Exists(Path.Combine(sub, "idle")))
                            looksLikeSkin.Add(Path.GetFileName(sub));
                    }
                    if (looksLikeSkin.Count > 0)
                    {
                        problems.Add("  ⚠️ 不过它**里面**有看起来像皮肤的文件夹："
                                   + string.Join("、", looksLikeSkin.ToArray())
                                   + " —— 你是不是想选那一个？");
                    }
                }
                catch { /* 连子目录都读不了就算了，上面的信息已经够用 */ }
            }
        }

        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                problems.Add("（跳过）这个目录不存在：" + root);
                continue;
            }

            string[] dirs = Directory.GetDirectories(root);
            // 排序：目录枚举顺序在不同机器上不一定一样，而不确定的顺序没法复现
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            candidateCount += dirs.Length;

            foreach (string dir in dirs)
            {
                string folder = Path.GetFileName(dir);

                if (!Directory.Exists(Path.Combine(dir, "idle")))
                {
                    problems.Add(folder + "：没有 idle/ 目录（idle 是兜底链的地基，必须有）");
                    continue;
                }

                PetSkin skin = Load(dir);
                if (skin != null)
                {
                    Current = skin;

                    // ⚠️ **把"从哪个路径加载的"明说出来。**
                    //    "外部导入素材能不能用"这件事全靠这一行 ——
                    //    两套皮肤长得一模一样时，光看画面分不出加载的是哪一份；
                    //    而这类问题（"我改了素材怎么没反应"）十有八九是
                    //    "改的不是程序实际读的那一份"。
                    Diagnostics += "\n  来源：" + dir;
                    return skin;
                }

                problems.Add(folder + "：" + Diagnostics);
            }
        }

        if (candidateCount == 0)
        {
            Diagnostics = "一个皮肤目录都没找到。找过这些地方：\n  "
                        + string.Join("\n  ", roots);
        }
        else
        {
            Diagnostics = "没有可用的皮肤：\n  " + string.Join("\n  ", problems.ToArray());
        }
        return null;
    }

    /// <summary>
    /// 把"当前皮肤"换成**已经加载好的**这一套。
    ///
    /// 用于"用户当场换了皮肤"这条路：面板已经用 <see cref="Load"/> 验证过一遍
    /// （不通过就当场报错、不会走到这里），所以这里直接采用那一份，
    /// **不再重复读一遍贴图**。
    /// </summary>
    public static void Adopt(PetSkin skin)
    {
        if (skin == null) return;
        Current = skin;
        Diagnostics = "已换上皮肤「" + skin.Name + "」："
                    + skin.Actions.Count + " 个动作，窗口 "
                    + skin.WindowWidth + "×" + skin.WindowHeight
                    + "\n  来源：" + skin.SourceDir;
    }

    /// <summary>加载一个皮肤目录。失败返回 null，原因写进 <see cref="Diagnostics"/>。</summary>
    public static PetSkin Load(string dir)
    {
        var skin = new PetSkin();
        skin.SourceDir = dir;
        skin.Name = Path.GetFileName(dir);

        // ── 1) skin.json（可以完全不存在 —— 见 §5.2）──
        SkinData data = null;
        string jsonPath = Path.Combine(dir, "skin.json");
        if (File.Exists(jsonPath))
        {
            string json;
            try
            {
                json = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
            }
            catch (Exception e)
            {
                Diagnostics = "skin.json 读失败：" + e.Message;
                return null;
            }

            try
            {
                data = JsonUtility.FromJson<SkinData>(json);
            }
            catch (Exception e)
            {
                Diagnostics = "skin.json 不是合法 JSON：" + e.Message;
                return null;
            }

            if (data == null)
            {
                Diagnostics = "skin.json 解析结果为空";
                return null;
            }
        }

        // ── 2) 扫动作目录：子目录名 = 动作名，里面的 *.png 排序 = 帧序列 ──
        //
        // 滤波要在**读图之前**定下来（读的时候就要设上去）。
        // 默认 bilinear —— 见 ResolveFilter 里的理由。
        var ignored = new List<string>();
        FilterMode filter = ResolveFilter(data != null ? data.filter : null, ignored);
        skin.Filter = filter;

        string[] actionDirs = Directory.GetDirectories(dir);
        Array.Sort(actionDirs, StringComparer.OrdinalIgnoreCase);
        if (actionDirs.Length == 0)
        {
            Diagnostics = "皮肤目录里没有任何动作子目录";
            return null;
        }

        var loaded = new List<KeyValuePair<string, Texture2D[]>>();
        foreach (string ad in actionDirs)
        {
            string actionName = Path.GetFileName(ad);
            string[] pngs = Directory.GetFiles(ad, "*.png");
            Array.Sort(pngs, StringComparer.OrdinalIgnoreCase);
            if (pngs.Length == 0) continue;   // 空目录直接忽略

            var texs = new Texture2D[pngs.Length];
            for (int i = 0; i < pngs.Length; i++)
            {
                Texture2D t = LoadPng(pngs[i], filter);
                if (t == null)
                {
                    Diagnostics = actionName + "/" + Path.GetFileName(pngs[i])
                                + "：读不出，或者不是合法的 PNG";
                    // 收尾：这次已经读进来的贴图必须放掉。
                    // 它们不在任何 GameObject 上，无人引用**也不会**被自动回收 ——
                    // 而"选到一个坏文件夹"正是皮肤作者试错时最常做的事，
                    // 每失败一次漏一整套帧是不能接受的（见 PetSkin.Release）。
                    skin.Release();
                    return null;
                }
                texs[i] = t;
                skin.Track(t);      // 登记所有权：中途失败时靠这份清单全放掉
            }
            loaded.Add(new KeyValuePair<string, Texture2D[]>(actionName, texs));
        }

        if (loaded.Count == 0)
        {
            Diagnostics = "所有动作目录都是空的（一张 PNG 都没有）";
            return null;
        }

        // ── 3) 帧尺寸：json 里写了就用它，否则取第一张图 ──
        int wantW, wantH;
        if (data != null && data.frameSize != null && data.frameSize.Length >= 2
            && data.frameSize[0] > 0 && data.frameSize[1] > 0)
        {
            wantW = data.frameSize[0];
            wantH = data.frameSize[1];
        }
        else
        {
            wantW = loaded[0].Value[0].width;
            wantH = loaded[0].Value[0].height;
        }
        skin.FrameWidth = wantW;
        skin.FrameHeight = wantH;

        // 校验：同一皮肤所有帧必须一样大（《皮肤契约设计.md》§5.4 硬性要求）
        foreach (var kv in loaded)
        {
            foreach (Texture2D t in kv.Value)
            {
                if (t.width != wantW || t.height != wantH)
                {
                    Diagnostics = string.Format(
                        "{0}/ 里有 {1}×{2} 的帧，但这一套的帧尺寸是 {3}×{4}\n"
                        + "同一动作内所有帧必须一样大 —— 不一样的话动画会抖",
                        kv.Key, t.width, t.height, wantW, wantH);

                    // 到这一步已经读了整套贴图，但 Sprite 还没建 —— 全靠 _owned
                    // 那份登记才能把它们放干净。**这条最容易踩**：AI 生成的图
                    // 尺寸常常差几个像素，一试就是一次失败。
                    skin.Release();
                    return null;
                }
            }
        }

        // ── 4) 锚点（默认底边中点）──
        if (data != null && data.pivot != null && data.pivot.Length >= 2)
        {
            skin.Pivot = new Vector2(data.pivot[0], data.pivot[1]);
        }

        // ── 5) 缩放倍率 Z ──
        // 三个来源，按优先级：皮肤声明的 scale > 皮肤声明的 displayHeight > **自动**
        // 最后再乘上用户的缩放（设置面板里的"她多大"）。
        skin.AuthorScale = ResolveAuthorScale(data, wantH);
        skin.Scale = ResolveFinalScale(skin.AuthorScale, filter);

        // ── 5.5) 余额数字的位置（**永远有**）──
        //
        // 语义变过一次，这里说明白：
        //   · 原来是"皮肤写了 textArea 才显示余额，不写就是纯角色桌宠"
        //   · 现在**永远有一个位置**（皮肤不声明就是原点 [0,0]），
        //     而"要不要显示"交给**用户的 `hideBalance`** 决定
        //
        // 为什么改：素材全部来自外部之后，"皮肤作者愿不愿意画一块牌子"变成了
        // 一个用户管不着的问题 —— 用户放一张自己的图进来，却因为作者没写
        // textArea 而永远看不到余额，那是说不过去的。
        // 现在的分工是：**位置由用户调，显不显示也由用户定。**
        //
        // x 是"数字的水平中心"、y 是"底边"，老皮肤的 [x,y,w,h] 用 x+w/2 反推中心 ——
        // 见 PetSkin.TextArea 和 PetSignText.Init 的注释。
        if (data != null && data.textArea != null && data.textArea.Length >= 4)
        {
            skin.TextArea = data.textArea;
        }
        else
        {
            skin.TextArea = new int[] { 0, 0, 0, 0 };   // 原点（帧左下角）
        }

        // ── 6) 建 Sprite + 动作参数 ──
        // （ignored 这个清单在第 2 步前面就建好了 —— 那里的滤波解析也可能往里加东西，
        //   两处共用一份，免得出现同名变量或者漏报。）

        foreach (var kv in loaded)
        {
            string actionName = kv.Key;
            Texture2D[] texs = kv.Value;

            int fps = (data != null && data.defaultFps > 0) ? data.defaultFps : 8;
            bool loop = true;

            if (data != null && data.actions != null)
            {
                foreach (SkinActionData a in data.actions)
                {
                    if (a == null || string.IsNullOrEmpty(a.name)) continue;
                    if (!string.Equals(a.name, actionName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (a.fps > 0) fps = a.fps;
                    loop = !a.noLoop;
                    break;
                }
            }

            // ⚠️ `idle` 声明成 noLoop 是个**很难自查**的错误，两个后果：
            //    ① 她会**停在最后一帧**不动（不再呼吸）
            //    ② **随机待机池永远不会触发** —— 引擎是靠"idle 绕回第一帧"来数一个完整周期的
            //       （PetAnimator.LoopCount），不循环就永远数不到第一圈
            //    所以这里直接报出来，并且**兜住**：宁可忽略这条声明，也不让她真的卡住。
            if (actionName.Equals("idle", StringComparison.OrdinalIgnoreCase) && !loop)
            {
                ignored.Add("idle 被声明成 noLoop —— 她会停在最后一帧，"
                          + "而且随机待机池永远不会触发（idle 必须循环）。已按循环处理");
                loop = true;
            }

            var frames = new Sprite[texs.Length];
            var alphas = new byte[texs.Length][];
            for (int i = 0; i < texs.Length; i++)
            {
                // PPU = 1：1 个世界单位 = 1 个贴图像素。
                // 屏幕上的实际大小由 Scale（以及相机的 orthographicSize）决定。
                // FullRect：不让 Unity 按 alpha 自动裁边，否则 pivot 会变得难以预测。
                frames[i] = Sprite.Create(texs[i], new Rect(0, 0, wantW, wantH),
                                          skin.Pivot, 1f, 0, SpriteMeshType.FullRect);

                // 顺手把 alpha 通道抄下来 —— 命中测试要用（见 PetAction.Alphas）
                alphas[i] = BuildAlphaMask(texs[i]);
            }

            var action = new PetAction();
            action.Name = actionName;
            action.Frames = frames;
            action.Fps = fps;
            action.Loop = loop;
            action.FrameWidth = wantW;
            action.FrameHeight = wantH;
            action.Alphas = alphas;
            skin.Actions[actionName.ToLowerInvariant()] = action;
        }

        // ── 7) idle 是硬性要求 ──
        PetAction idle;
        if (!skin.Actions.TryGetValue("idle", out idle))
        {
            Diagnostics = "缺少 idle 动作 —— 它是兜底链的地基，必须有（《皮肤契约设计.md》§5.4）";

            // 这一步 Sprite 已经建好了，所以 Release 会连精灵带贴图一起放掉。
            // 形态最常见于"动作目录叫 stand / walk，忘了必须有 idle"。
            skin.Release();
            return null;
        }
        skin.Idle = idle;

        // ── 7.5) 待机池：动作名以 idle_ 开头的那些（《皮肤契约设计.md》§5.7）──
        //
        // 为什么用"前缀约定"而不是在 skin.json 里列一个数组：
        //   创作者建一个叫 idle_sleep/ 的目录就完事了，**不用改 json**；
        //   而且目录和配置不会不一致 —— 列表式的话，"列表里写了但目录不存在"
        //   是个默不作声的 bug（反过来"目录存在但列表里忘了写"也一样）。
        //
        // ⚠️ 必须**排序**再放进池子：skin.Actions 是 Dictionary，遍历顺序没有保证。
        //    而"随机挑一个"如果建立在一个不确定的顺序上，出了问题就没法复现。
        var poolNames = new List<string>();
        foreach (var kv in skin.Actions)
        {
            // 注意 `idle` 本身不算池成员（它正好是那个"节点"），所以要求有下划线
            if (kv.Key.StartsWith("idle_", StringComparison.OrdinalIgnoreCase))
                poolNames.Add(kv.Key);
        }
        poolNames.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string n in poolNames) skin.IdlePool.Add(skin.Actions[n]);

        if (data != null && !string.IsNullOrEmpty(data.name)) skin.Name = data.name;

        // ── 8) 配置里有、但 v1 还没实现的字段：**说出来，别静默忽略** ──
        if (data != null)
        {
            // hitTest 目前只有 perPixel 一种实现
            if (!string.IsNullOrEmpty(data.hitTest)
                && !data.hitTest.Equals("perPixel", StringComparison.OrdinalIgnoreCase))
            {
                ignored.Add("hitTest=" + data.hitTest + "（v1 只实现了 perPixel）");
            }
            if (data.textArea != null && data.textArea.Length >= 4)
            {
                // 已经实现了（见上面读 textArea 的地方），不再算"忽略"
            }
            if (data.fallback != null && data.fallback.Length > 0)
            {
                ignored.Add("fallback（自定义兜底链，v1 固定为 idle）");
            }
        }

        Diagnostics = string.Format(
            "已加载皮肤「{0}」：{1} 个动作，帧 {2}×{3}，锚点 {4}，缩放 {5}×，滤波 {6}，窗口 {7}×{8}；{9}",
            skin.Name, skin.Actions.Count, wantW, wantH, skin.Pivot, skin.Scale,
            filter, skin.WindowWidth, skin.WindowHeight, PoolText(skin));

        if (ignored.Count > 0)
        {
            Diagnostics += "\n[注意] skin.json 里这些字段 v1 还没实现，已忽略："
                         + string.Join("、", ignored.ToArray());
        }

        return skin;
    }

    /// <summary>
    /// 待机池的一句话描述（进日志，也进屏幕诊断面板）。
    ///
    /// **池子为空时要明说**，不能只打个"0" —— 因为"池子空"和"随机待机坏了"
    /// 在界面上长得一模一样（都是只有 idle 在循环）。把原因写出来，
    /// 才不会让人去查一个不存在的 bug。
    /// </summary>
    static string PoolText(PetSkin skin)
    {
        if (skin.IdlePool.Count == 0)
            return "待机池为空（没有 idle_ 开头的动作，只有 idle 一直循环）";

        var names = new List<string>();
        foreach (PetAction a in skin.IdlePool) names.Add(a.Name);
        return "待机池 " + skin.IdlePool.Count + " 个（" + string.Join("、", names.ToArray()) + "）";
    }

    /// <summary>
    /// 皮肤声明的缩放（**不含用户缩放**）。
    ///
    /// <code>
    ///   scale > 0          → 用它（作者的控制权最大，像素画基本都走这条）
    ///   displayHeight > 0  → displayHeight / 帧高
    ///   都没写              → 自动：把帧装进"默认大小"里，超大素材不再撑出巨窗
    /// </code>
    ///
    /// 最后那条是给**非像素素材**准备的：AI 生成的立绘动辄 1024×1536，
    /// 按"窗口 = 图片"会得到一个盖住屏幕的窗口。默认把它们装进
    /// <see cref="PetConfig.DefaultPetHeight"/> 里，用户再用缩放去调大小。
    ///
    /// ⚠️ 自动那一档**只缩不放**（`Mathf.Min(1f, …)`）：小图本来就该小，
    /// 放大是作者该用 `scale` 明说的事（像素画尤其如此）。
    /// </summary>
    static float ResolveAuthorScale(SkinData data, int frameH)
    {
        if (data != null && data.scale > 0f) return data.scale;
        if (data != null && data.displayHeight > 0f) return data.displayHeight / frameH;
        if (frameH <= 0) return 1f;
        return Mathf.Min(1f, PetConfig.DefaultPetHeight / (float)frameH);
    }

    /// <summary>
    /// 作者缩放 × 用户缩放 → 最终缩放。
    ///
    /// ⚠️ **像素画（point）必须吸附到整数倍**：1.37× 会让它的硬边变成
    /// 有的像素占 1 个屏幕像素、有的占 2 个 —— 看起来就是"坏了"。
    /// 所以这一档按四舍五入取整，且**下限是 1**（0 倍等于看不见）。
    /// 非像素画（bilinear）不受限制，任意倍率都行。
    /// </summary>
    static float ResolveFinalScale(float authorScale, FilterMode filter)
    {
        float s = authorScale * (PetConfig.ZoomPercent / 100f);

        if (filter == FilterMode.Point) s = Mathf.Max(1f, Mathf.Round(s));

        // 兜底：任何情况下都不该是 0 或负数（那等于她不存在）
        if (s <= 0.01f) s = 0.01f;
        return s;
    }

    /// <summary>把一张贴图的 alpha 通道抄成一维数组（命中测试用）。</summary>
    static byte[] BuildAlphaMask(Texture2D tex)
    {
        Color32[] px = tex.GetPixels32();
        var a = new byte[px.Length];
        for (int i = 0; i < px.Length; i++) a[i] = px[i].a;
        return a;
    }

    /// <summary>读一个 PNG 变成 Texture2D。失败返回 null。</summary>
    static Texture2D LoadPng(string path, FilterMode filter)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }

        // 先用 2×2 占位建贴图，LoadImage 会按 PNG 自己的尺寸重建它。
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);

        // 第二个参数 markNonReadable = false：
        // **保持可读**。以后做"按像素命中测试"要读 alpha 通道。
        if (!tex.LoadImage(bytes, false))
        {
            UnityEngine.Object.Destroy(tex);
            return null;
        }

        // 滤波由皮肤声明（见 SkinLoader.ResolveFilter）。
        // **默认是 bilinear**：这套设计主要面向外部素材，而那多半是非像素画。
        // 像素画必须显式写 "point"，否则放大时会糊成一团。
        tex.filterMode = filter;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.anisoLevel = 0;
        return tex;
    }

    /// <summary>
    /// 皮肤声明的滤波方式 → Unity 的 <see cref="FilterMode"/>。
    ///
    /// **默认 bilinear，不是 point。** 这条是刻意的：
    /// 本项目现在的主要素材来源是用户在外部准备的（常常是 AI 生成的非像素画），
    /// 对那种图 point 采样会产生严重的锯齿和闪烁。
    /// 代价是**像素画必须显式声明** `"filter": "point"` ——
    /// 我们自带的占位皮肤就声明了（见 `_tools/make_skin.py`）。
    ///
    /// 写错的值不静默吞掉：报出来，然后按默认走。
    /// </summary>
    static FilterMode ResolveFilter(string value, List<string> ignored)
    {
        if (string.IsNullOrEmpty(value)) return FilterMode.Bilinear;

        if (value.Equals("point", StringComparison.OrdinalIgnoreCase)) return FilterMode.Point;
        if (value.Equals("bilinear", StringComparison.OrdinalIgnoreCase)) return FilterMode.Bilinear;

        ignored.Add("filter=\"" + value + "\"（只认 point / bilinear，按默认 bilinear 处理）");
        return FilterMode.Bilinear;
    }
}
