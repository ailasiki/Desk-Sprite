using UnityEngine;

/// <summary>
/// 启动引导：把"从无到有"这件事一次做完。
///
/// 为什么不用场景文件？空场景按 Play，这个方法在场景加载完后自己跑，
/// 把相机、精灵、控制器全部凭空造出来。这样"整个游戏"就是几个 .cs 文件，
/// 可以 diff、可以 review、可以随时重建。
/// </summary>
public static class PetBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        // 桌宠必须失焦也继续跑。为什么写在代码里而不是改 Player Settings？
        // 因为这样换台机器、换个工程设置，它还是对的。
        Application.runInBackground = true;

        // 桌宠不需要 60 帧。像素动画一秒 8~12 帧，30 帧足够，省一半的电。
        Application.targetFrameRate = 30;

        // 关掉 MSAA（多重采样抗锯齿）。
        // 原因：多重采样时 alpha 会被 resolve 掉，DWM 那套逐像素透明会直接失效。
        QualitySettings.antiAliasing = 0;

        // ── 配置：**必须最先读** ──
        //
        // ⚠️ 这里踩过一次顺序坑，值得写清楚：
        //    `config.json` 是在 DeepSeekBalance.Start() 里读的，而它原来排在
        //    "加载皮肤"**之后**。于是皮肤加载器去读 `PetConfig.ZoomPercent`
        //    的时候配置还是空的 —— 只能拿到默认值，用户设的"她多大"完全不生效，
        //    而且**看起来一切正常**（没有任何报错）。
        //
        //    为什么 `idleSwitchSeconds` 就没这个问题：它是 `PetBrain` 在**每帧运行时**
        //    读的（那时配置早读好了）；而缩放是**加载皮肤时**就要用的。
        //
        //    **规则：任何"加载期就要用"的配置，都必须排在读配置之后。**
        //    所以配置放在最前面，而不是"哪儿顺手放哪儿"。
        DeepSeekBalance.Start();

        // ── 加载皮肤 ──
        // 默认皮肤走**和用户皮肤完全相同**的运行时加载路径（dogfooding）——
        // 所以这个加载器从第一天起就在接受真实测试。
        PetSkin skin = SkinLoader.LoadDefaultSkin();
        float z = (skin != null) ? skin.Scale : 1f;
        Debug.Log("[DeskSprite] " + SkinLoader.Diagnostics);

        // 把**读到的配置值**报一遍（**绝不含 Key 内容**，只说长度）。
        // 用途很具体："我改了配置怎么没生效" —— 一眼就能看出是没读到、
        // 还是读到了但没生效，而不用去猜。
        Debug.Log(string.Format(
            "[DeskSprite] 配置：{0}  zoomPercent={1} idleSwitchSeconds={2} hideBalance={3} key长度={4}",
            PetConfig.ConfigPath, PetConfig.ZoomPercent, PetConfig.IdleSwitchSeconds,
            PetConfig.HideBalance, (PetConfig.ApiKey ?? "").Length));

        // 配置读不出来时必须**大声说**：所有设置都会退回默认值，API Key 会看起来"丢了"，
        // 而症状（"我的设置怎么全没了"）离原因（少了一个逗号）非常远。
        // 这一条用 LogError，让它没法被忽略。
        if (PetConfig.Data == null)
        {
            Debug.LogError("[DeskSprite] 配置没读出来，所有设置退回默认值。原因：\n"
                         + PetConfig.Diagnostics);
        }

        // ── 相机 ──
        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.orthographic = true;

            // 目标：**1 个贴图像素 = Z 个屏幕像素**
            //   屏幕像素 / 世界单位 = Screen.height / (2 × orthographicSize)
            //   而 PPU = 1（1 个世界单位 = 1 个贴图像素），所以这个比值就是"每个贴图像素占几个屏幕像素"
            cam.orthographicSize = Screen.height / (2f * z);

            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = DeskWindow.CameraBackground;   // 由当前透明方案决定
            cam.allowHDR = false;   // HDR 会改变 backbuffer 格式，alpha 通道可能保不住
            cam.allowMSAA = false;

            Debug.Log(string.Format("[DeskSprite] 相机 orthographicSize={0} Z={1}",
                                    cam.orthographicSize, z));
        }
        else
        {
            Debug.LogError("[DeskSprite] 场景里没有 Main Camera");
        }

        // ── 根节点 ──
        var root = new GameObject("DeskSprite");
        Object.DontDestroyOnLoad(root);
        _root = root.transform;
        _cam = cam;

        // ── 形象 ──
        if (skin != null) BuildPet(root.transform, skin, cam);
        else BuildLoadFailureMarker(root.transform);

        // ── 设置面板 ──
        // 也放在**皮肤加载失败也要建**的这一侧：皮肤坏了的时候，用户更需要看到面板，
        // 而不是盯着一个红色失败标记猜发生了什么。
        // **注意它和"形象"是兄弟节点** —— 换皮肤时只销毁形象，面板不受影响（会一直开着）。
        SettingsPanel.Create(root.transform);

        // ── 司机：逃生舱、窗口尺寸、热键 ──
        // **不管皮肤加载成功没有，它都必须存在** —— 否则程序可能关不掉。
        var runner = new GameObject("PetRunner");
        runner.transform.SetParent(root.transform, false);
        runner.AddComponent<PetRunner>();
    }

    /// <summary>根节点（换皮肤时往它下面重建形象）。</summary>
    static Transform _root;

    /// <summary>主相机。BuildPet 要用它算她的位置，所以存一份。</summary>
    static Camera _cam;

    /// <summary>当前的形象节点。换皮肤时会被销毁重建，所以**不要长期持有它**。</summary>
    public static GameObject PetVisual { get; private set; }

    /// <summary>
    /// 当前形象用的那套皮肤 —— 记着它，是为了在换皮肤时**释放它占的贴图**。
    ///
    /// 为什么需要这个字段：贴图和精灵是加载器 `new` 出来的独立资源，不挂在任何
    /// GameObject 上，所以 `Destroy(PetVisual)` **带不走**它们。必须"谁建的谁负责销毁"
    /// —— 见 <see cref="PetSkin.Release"/>。不记的话，一套 2048² × 7 帧的皮肤
    /// 每换一次就漏 112 MB（实测：进程 1.8 GB ≈ 加载了 16 次）。
    /// </summary>
    static PetSkin _builtSkin;

    /// <summary>
    /// 把"当前形象占用的图形资源"放掉。
    ///
    /// <paramref name="keep"/> 是**马上要用的**那套皮肤：如果它和当前这套是同一个
    /// 对象，就不能放 —— 那些贴图正在被使用，销毁了屏幕上的她会瞬间变空。
    /// （正常换皮肤时加载器给的是一个全新对象，所以这个判断通常都会放行。）
    /// </summary>
    static void ReleaseVisualResources(PetSkin keep)
    {
        if (_builtSkin != null && !ReferenceEquals(_builtSkin, keep))
        {
            _builtSkin.Release();
        }
        _builtSkin = null;
    }

    /// <summary>
    /// 换皮肤：**销毁当前形象、用新皮肤重建一个**。
    ///
    /// 为什么是"重建"而不是"改参数"：帧尺寸、动作表、贴图、锚点、牌子……
    /// 全都是按皮肤建出来的，换一套等于换了一个人。干脆销毁重来，
    /// 就不会有"忘了更新某一处"的残留 —— 那种残留最难查。
    ///
    /// 面板和司机**不在形象下面**（是兄弟节点），所以它们不受影响：
    /// 用户点完"选择…"，面板还开着，她已经在旁边变成新样子了。
    /// </summary>
    public static GameObject RebuildPet(PetSkin skin)
    {
        if (_root == null) return null;

        if (PetVisual != null) Object.Destroy(PetVisual);
        PetVisual = null;

        // 旧形象占的贴图不在 GameObject 上，得手动放（见 ReleaseVisualResources）。
        // 顺序无所谓：`Destroy` 是**这一帧结束时**才真的执行，不会出现
        //"贴图已经没了、形象还在画"的中间状态，也不会漏掉这一帧的绘制。
        ReleaseVisualResources(skin);

        if (skin != null) return BuildPet(_root, skin, _cam);

        BuildLoadFailureMarker(_root);
        return null;
    }

    static GameObject BuildPet(Transform parent, PetSkin skin, Camera cam)
    {
        // 记下"这套皮肤正在被使用"——换皮肤时靠它释放贴图（见 _builtSkin）。
        // 放在这里而不是调用点：启动和换皮肤这两条路都会经过这个方法。
        _builtSkin = skin;

        var go = new GameObject("PetVisual");
        go.transform.SetParent(parent, false);
        PetVisual = go;
        var sr = go.AddComponent<SpriteRenderer>();

        // ── 把帧"贴窗口底边、水平居中"（《皮肤契约设计.md》§1.3）──
        // ⚠️ 用的是**相机实际能看到的半个世界高度**（= orthographicSize），
        //    而不是"帧高 / 2"。
        //    两者只在"窗口高 = 帧高 × Z"时才相等 —— 而设置面板会把窗口放大，
        //    那时按帧高算，她就会浮到窗口中间去。
        float halfView = (cam != null)
            ? cam.orthographicSize
            : skin.FrameHeight * 0.5f;      // 没有相机时的兜底
        go.transform.localPosition = new Vector3(0f, -halfView, 0f);

        // 帧动画播放器：先播 idle。
        var anim = go.AddComponent<PetAnimator>();
        anim.Play(skin.Idle, true);

        // 状态机：把鼠标信号（悬停 / 按下 / 拖动）变成"该播哪个动作"。
        // 它会接管播放 —— 上面那句只是保证在它 Init 之前也有东西可显示。
        var brain = go.AddComponent<PetBrain>();
        brain.Init(skin);

        // ── 牌子上的数字 ──
        // 现在**永远有**（皮肤不声明就是帧左下角），"显不显示"由用户的 hideBalance 决定。
        BuildSignText(go.transform, skin);

        return go;
    }

    /// <summary>
    /// 在牌子上的指定位置放一块"动态文字"。
    /// 坐标换算（帧坐标 → 世界坐标）由 <see cref="PetSignText.ApplyPosition"/> 负责 ——
    /// 位置只由那一个地方决定，这里只负责"把它造出来"。
    ///
    /// 顺序有讲究：**先 Init（它会记下皮肤声明的坐标），再套用户覆盖**。
    /// 因为用户的覆盖在内部表示成"相对作者声明值的偏移" ——
    /// 这样面板上的"还原"才能真的回到**作者声明的值**，
    /// 而不是回到"上次保存的用户值"（那是另一件事，用户会分不清）。
    /// </summary>
    static void BuildSignText(Transform petVisual, PetSkin skin)
    {
        var go = new GameObject("SignText");
        go.transform.SetParent(petVisual, false);

        var st = go.AddComponent<PetSignText>();
        st.Init(skin);

        SkinUserOverride.Override ov;
        string note;
        if (SkinUserOverride.TryLoad(skin, out ov, out note))
        {
            PetSignText.SetCurrent(ov.X, ov.Y);
            PetSignText.SetFontScale(ov.FontScale);
            Debug.Log("[DeskSprite] " + note);
        }
        else if (note != null)
        {
            // 读不出来 / 没有有效覆盖：都不是错误，但要说一句，免得以后以为是"保存没生效"
            Debug.Log("[DeskSprite] " + note);
        }
    }

    /// <summary>
    /// 皮肤加载失败时，画一个**看得见的**标记。
    ///
    /// 为什么必须有它？因为我们的窗口是全透明的 —— 什么都不画的话，
    /// 程序看起来就像"根本没启动"，而真正的原因（比如某个 PNG 坏了）
    /// 只躺在 Player.log 里没人看。
    ///
    /// 沿用 M0 那条原则：**失败要看得见。**
    /// </summary>
    static void BuildLoadFailureMarker(Transform parent)
    {
        const int n = 16;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                px[y * n + x] = ((x + y) % 2 == 0)
                    ? new Color32(230, 60, 60, 255)     // 红
                    : new Color32(255, 220, 60, 255);   // 黄
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        tex.filterMode = FilterMode.Point;

        var go = new GameObject("SkinLoadFailed");
        go.transform.SetParent(parent, false);
        go.transform.localScale = Vector3.one * 4f;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 1f,
                                  0, SpriteMeshType.FullRect);
    }
}
