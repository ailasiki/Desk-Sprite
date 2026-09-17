using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 常驻的"司机"：每帧跑，负责那些"要盯着变化"和"要挑时机做"的事。
///
/// 为什么需要它？因为 [RuntimeInitializeOnLoadMethod] 是**一次性**的，跑完就结束了；
/// 而"窗口大小变了要重算相机"是**每帧**都要检查的事。
/// 这就是 PetBootstrap（一次性）和 PetRunner（常驻）分工的原因。
/// </summary>
public class PetRunner : MonoBehaviour
{
    // ── 窗口尺寸 ──
    // 三层"设置"都没用，最后必须自己动手。这里记一下整个踩坑过程：
    //   ① Player Settings 里写 480×320  → 只对"第一次运行"生效
    //   ② 因为 Unity 的播放器会把窗口尺寸**存进注册表**，下次启动用它覆盖 ①
    //      （实测：设置里是 480×320，实际窗口是 588×602）
    //   ③ 那就用 Screen.SetResolution() 在代码里设？
    //      → **它会顺手把 Unity 自己的窗口装饰套回来**：标题栏、边框、关闭按钮全回来了，
    //        我们设的 WS_POPUP 被覆盖，透明也跟着坏掉（实测：窗口变白、长出叉叉）
    // 最终答案：**用 Win32 的 SetWindowPos 直接设像素尺寸**，完全不经过 Unity。
    //
    // ── 现在它由皮肤算出来（M1）──
    //     窗口 = 帧尺寸 × 缩放倍率              （《皮肤契约设计.md》§1.3）
    // 长度没变，只是从"写死的两个常量"变成了"算出来的两个数"。
    // 加载失败时退化成一个安全的小尺寸，保证窗口仍然可见、仍然能关掉。
    int WindowWidth
    {
        get { return SkinLoader.Current != null ? SkinLoader.Current.WindowWidth : 128; }
    }

    int WindowHeight
    {
        get { return SkinLoader.Current != null ? SkinLoader.Current.WindowHeight : 128; }
    }

    /// <summary>最终缩放倍率 Z：1 个贴图像素 = Z 个屏幕像素。</summary>
    float Z
    {
        get { return SkinLoader.Current != null ? SkinLoader.Current.Scale : 1f; }
    }

    // ── 设置模式（S1：先把"窗口临时放大 + 她正确重贴底边"这条做通）──
    // 一个设置面板装不进 192×264 的窗口（《皮肤契约设计.md》§9.2），
    // 所以打开设置时必须把窗口临时放大、关掉再缩回去。
    //
    // 高度 400 -> 480 -> 540 -> 600 -> 640，改了四次，原因值得记一下：
    //   400 -> 480：她占底部 264，400 时面板只剩 136，一个标题加几行字就满了。
    //   480 -> 540：等真正的控件清单具体下来（标题/余额/消息/Key/开关/微调/按钮 = 7 行），
    //               240 才放得下，480 只给面板留了 200。
    //   540 -> 600：加"随机待机间隔"一行。
    //   600 -> 640：加"她多大"一行。
    // 每次都是"按当时的控件数估的"，所以每次都估小了 ——
    // **布局尺寸应该在控件清单确定之后再定**。
    //
    // ⚠️ **下次加"皮肤选择"之前先改成两列**：单列再长下去这个窗口就要占满屏幕了
    //    （640 在 1080 屏上已经是 59%）。
    //
    // ⚠️ 已知局限：窗口是**从上往下**长大的（SetWindowPos 只改 size，左上角不动）。
    //    所以改尺寸时**锚定的是她的底边中点**（见 ToggleSettings）——
    //    她在屏幕上不挪窝，面板往她头顶长。位置持久化还没做（见 README 待办）。
    const int SettingsWindowWidth = 480;
    const int SettingsWindowHeight = 640;

    public static bool SettingsOpen { get; private set; }
    bool _settingsKeyHeld;
    float _panelRefreshTimer;

    // ── 面板 → 司机的单向请求 ──
    // 面板上的"关闭设置"按钮不能自己动窗口（改窗口是 PetRunner 的活，它手上才有
    // _anim / _sizeFixAttempts 这些东西）。所以它只**举旗**，下一帧由 PetRunner 统一处理。
    //
    // 为什么不用"给面板一个 PetRunner 的单例引用"？那会让依赖变成双向的，
    // 而这个组件本来就不需要认识面板。举旗是单向的：面板知道 PetRunner 存在，
    // PetRunner 不需要知道面板做了什么。
    static bool _toggleRequested;
    public static void RequestSettingsToggle() { _toggleRequested = true; }

    static bool ConsumeToggleRequest()
    {
        bool r = _toggleRequested;
        _toggleRequested = false;
        return r;
    }

    // ── 面板 → 司机的换皮肤请求 ──
    // 和上面同一个道理：面板不该自己去销毁重建形象（那要动 _anim / _brain / 相机 / 窗口），
    // 它只把"已经验证过能加载的那套皮肤"交给司机，由司机在下一帧统一换。
    //
    // ⚠️ 传的是**已经 Load 好的 PetSkin**，不是路径 ——
    //    面板为了让用户"当场知道行不行"已经加载并验证过一遍了，
    //    这里再按路径读一次就等于把贴图读两遍。
    static PetSkin _pendingSkin;
    static string _pendingSkinPath;

    public static void RequestSkinReload(PetSkin skin, string path)
    {
        _pendingSkin = skin;
        _pendingSkinPath = path;
    }

    // ── 设置模式期间"她的屏幕位置"要记账 ──
    // _pre*    = 打开设置**之前**她在哪
    // _placed* = 打开设置后我们**实际**把她放到了哪（可能因为屏幕边缘放不下而被夹走）
    // 关闭时：如果她没被用户拖过（当前位置 == _placed*），就还回 _pre*；
    //         被拖过就以用户拖的为准，不能硬把她拽回去。
    int _preAnchorX, _preAnchorY;
    int _placedAnchorX, _placedAnchorY;
    bool _havePreAnchor;

    /// <summary>窗口现在**应该**多大。设置模式下和平时不一样 —— 那个每帧的不变式要按它来。</summary>
    int TargetWindowWidth { get { return SettingsOpen ? SettingsWindowWidth : WindowWidth; } }
    int TargetWindowHeight { get { return SettingsOpen ? SettingsWindowHeight : WindowHeight; } }

    // 缓存 Camera.main —— 它内部是"按标签查找对象"，不是免费操作。
    Camera _cam;
    PetAnimator _anim;
    PetBrain _brain;
    int _screenHeight;
    int _frame;

    // ── 全局热键的"边沿检测"状态 ──
    // Win32.IsKeyDown 问的是"现在按着吗"（电平），不是"刚刚按下吗"（边沿）。
    // 直接用它会变成"按住不放就每帧触发一次"。所以我们要自己记住上一帧的状态，
    // 只在"松开 → 按下"那一瞬间动作。
    bool _modeKeyHeld;
    bool _quitKeyHeld;
    bool _cfgKeyHeld;
    bool _nudgeKeyHeld;
    bool _rmbHeld;

    // 尺寸纠正的次数。为什么要"重试"？见 Update 里那段注释。
    int _sizeFixAttempts;
    const int MaxSizeFixAttempts = 15;

    /// <summary>屏幕上那块诊断面板。M0/M1 测试期用它，以后正式版会关掉。</summary>
    /// <summary>
    /// 屏幕上那个诊断面板（`brain` / `hit` / `thru` 几行）要不要画。
    ///
    /// **默认关**：它是开发工具，正常用户看不懂、也不该看到。
    /// 开关在**设置面板的左上角**，不是热键 —— 热键对不知情的用户是"碰运气"，
    /// 而面板上的勾选框是一个**看得见的选择**（这条是用户定的，比我原来想的对）。
    /// </summary>
    public static bool ShowPanel = false;

    /// <summary>面板度量日志只打一次，不要每帧刷屏。</summary>
    bool _panelLogged;

    void Awake()
    {
        _cam = Camera.main;
        _anim = Object.FindObjectOfType<PetAnimator>();
        _brain = Object.FindObjectOfType<PetBrain>();
        _screenHeight = Screen.height;
    }

    void Update()
    {
        _frame++;

        // ── 第 2 帧：先把窗口尺寸设成我们要的 ──
        // 此时窗口还带着标题栏和边框，所以这里设的是**外框**尺寸 —— 客户区会小一圈。
        // 没关系：第 12 帧换成 WS_POPUP 之后，客户区就等于外框了。
        if (_frame == 2)
        {
            DeskWindow.ResizeTo(TargetWindowWidth, TargetWindowHeight);
        }

        // ── 第 12 帧：改窗口样式，然后再设一次尺寸 ──
        // 为什么不在 Awake 里做？那时 Unity 可能还没把窗口完全显示出来。
        // 抢在它前面改样式，容易被它之后的重建覆盖掉。
        //
        // 顺序有讲究：WS_POPUP 会让客户区等于整个外框，
        // 所以必须在**样式改完之后**再设一次尺寸，那才是最终尺寸。
        if (_frame == 12)
        {
            DeskWindow.Apply();
            DeskWindow.ResizeTo(TargetWindowWidth, TargetWindowHeight);
            Debug.Log("[DeskSprite] " + DeskWindow.Diagnostics);

            // 启动时把"她多大"用一遍（含"不超过工作区"这条结构上限）。
            // 放在这里而不是更早：此刻窗口句柄一定有效，工作区才问得到。
            ApplyZoomIfChanged(true);
        }

        // ── "她多大"：用户改了缩放就跟着改窗口 ──
        // 每帧比一下整数，几乎白送；比订阅/事件简单，也不会漏。
        ApplyZoomIfChanged(false);

        // ── 尺寸是一个"不变式"，不是"设一次就完的事" ──
        // 为什么？因为窗口尺寸有好几个"争夺者"：
        //   · Unity 会从注册表恢复上一次的尺寸 —— 而且可能发生在我们设完之后
        //   · WS_POPUP 会改变客户区
        //   · DPI 缩放
        // 在某个固定时机"设一次"，永远会输给"之后才发生的那个"。
        //
        // 所以：每帧检查、不对就纠正。但**有次数上限** —— 免得和谁打起来无限循环。
        // 而且每次纠正都打日志：这样我们能看到它到底纠正了几次、每次看到什么尺寸。
        // （如果打满 15 次还是不对，就说明 Screen.width/height 不是客户区，
        //   或者有别的东西在持续改它 —— 那日志本身就是答案。）
        if (_frame > 12 && _sizeFixAttempts < MaxSizeFixAttempts
            && (Screen.width != TargetWindowWidth || Screen.height != TargetWindowHeight))
        {
            _sizeFixAttempts++;
            Debug.Log(string.Format(
                "[DeskSprite] 尺寸不对：现在 {0}x{1}，要求 {2}x{3} —— 第 {4} 次纠正",
                Screen.width, Screen.height, TargetWindowWidth, TargetWindowHeight, _sizeFixAttempts));

            // 这里**故意**按是否在设置模式分两种行为，理由值得写下来：
            //
            //   设置模式开着 → 以**她当前的位置**为锚改尺寸。
            //     这时她在屏幕上的位置是**有意义的**（用户正看着面板），
            //     不能因为一次尺寸纠正就把面板顶出屏幕、或者让她漂走。
            //
            //   平时（启动阶段的尺寸纠正） → ResizeTo（只改尺寸，左上角不动）。
            //     启动时"她在屏幕哪里"还没有含义 —— 那是 Unity 和注册表留下的。
            //     这时保持左上角不动才是**确定**的；要是用锚点方案，
            //     "她最终出现在屏幕哪个位置"就取决于"注册表恢复有没有撞上来"，
            //     那是不确定行为 —— 比固定在中间或固定在底部都糟。
            if (SettingsOpen)
            {
                int ax, ay;
                if (DeskWindow.GetClientBottomCenter(out ax, out ay))
                    DeskWindow.ResizeAroundBottomCenter(ax, ay, TargetWindowWidth, TargetWindowHeight);
                else
                    DeskWindow.ResizeTo(TargetWindowWidth, TargetWindowHeight);   // 拿不到锚点就退回兜底
            }
            else
            {
                DeskWindow.ResizeTo(TargetWindowWidth, TargetWindowHeight);
            }
        }

        // ── 窗口高度变了就重算相机，并把她重新贴到底边 ──
        // 让"1 贴图像素 = 1 屏幕像素"在任何窗口尺寸下都成立。
        if (Screen.height != _screenHeight)
        {
            _screenHeight = Screen.height;
            if (_cam != null) _cam.orthographicSize = Screen.height / (2f * Z);
            RepositionPet();
        }

        // ── 命中测试（按像素）──
        // 每帧问一次"鼠标现在压在她**画到的像素**上吗"。
        // 开销很小：两次 Win32 调用 + 一次数组查表（膨胀一圈 = 9 次比较）。
        //
        // 第四个参数是**她当前的世界坐标**。必须是问出来的，不能算 —— 见 PetHitTest 的类注释：
        // 以前这里靠"她的原点在屏幕竖直中心"推算，只在窗口高 == 帧高×Z 时成立，
        // 设置模式把窗口放大之后她的下半截就整个失效了。
        PetHitTest.Update(SkinLoader.Current,
                          _anim != null ? _anim.CurrentAction : null,
                          _anim != null ? _anim.CurrentFrame : 0,
                          _anim != null ? _anim.transform.position : Vector3.zero);

        // ── 余额查询 ──
        // 它自己管"到点了才发请求"，这里只是给它一个每帧的心跳。
        DeepSeekBalance.Update();

        // ── 设置面板刷新 ──
        // 打开着的时候，界面上要能自己变（比如"正在查询…" -> "¥110.00"）。
        // 但**不要每帧刷**：Refresh 会拼字符串，30 帧/秒白造垃圾。
        // 4 次/秒够了 —— 人眼看不出差别，而状态变化都不会短于一帧。
        if (SettingsOpen && SettingsPanel.Instance != null)
        {
            _panelRefreshTimer += Time.deltaTime;
            if (_panelRefreshTimer >= 0.25f)
            {
                _panelRefreshTimer = 0f;
                SettingsPanel.Instance.Refresh();
            }
        }
        else
        {
            _panelRefreshTimer = 0f;
        }

        // ── 切换透明方案：全局，不需要焦点 ──
        // C = change。之前用 F1 失败就是因为焦点；而 F 键在笔记本上还会被 Fn 层吃掉。
        // 字母键没有 Fn 层这一说，所以用 Ctrl+Shift+字母。
        bool modeCombo = Win32.IsKeyDown(Win32.VK_CONTROL)
                      && Win32.IsKeyDown(Win32.VK_SHIFT)
                      && Win32.IsKeyDown(Win32.VK_C);
        if (modeCombo && !_modeKeyHeld) SwitchTransparencyMode();   // 只在"刚按下"那一帧
        _modeKeyHeld = modeCombo;

        // ── 打开配置（Ctrl+Shift+O）──
        // 这是**过渡手段**：等设置 UI 做出来之后，用户就在界面里改了，不需要碰文件。
        // 现在还没有 UI，所以留一个能直达文件的入口。
        //
        // （原来还有一个 Ctrl+Shift+L 打开日志，已删除：
        //   你要看日志用命令更方便，而且我这边也能直接读。
        //   把"给用户用的入口"和"给我们自己用的工具"分开，界面才不会被调试功能撑满。）
        bool cfgCombo = Win32.IsKeyDown(Win32.VK_CONTROL)
                     && Win32.IsKeyDown(Win32.VK_SHIFT)
                     && Win32.IsKeyDown(Win32.VK_O);
        if (cfgCombo && !_cfgKeyHeld) PetConfig.RevealConfig();
        _cfgKeyHeld = cfgCombo;

        // ── 微调牌子上文字的位置（Ctrl+Shift+方向键）──
        // textArea 是每个皮肤自己声明的坐标，而"这套坐标对不对"只能看着调。
        // 有了实时微调，方向键一按文字就挪一格；调好后把日志里那行 textArea 填回 skin.json。
        bool nudgeMod = Win32.IsKeyDown(Win32.VK_CONTROL) && Win32.IsKeyDown(Win32.VK_SHIFT);
        int ndx = 0, ndy = 0;
        if (nudgeMod)
        {
            if (Win32.IsKeyDown(Win32.VK_LEFT)) ndx = -1;
            else if (Win32.IsKeyDown(Win32.VK_RIGHT)) ndx = 1;
            else if (Win32.IsKeyDown(Win32.VK_UP)) ndy = 1;
            else if (Win32.IsKeyDown(Win32.VK_DOWN)) ndy = -1;
        }
        bool nudging = (ndx != 0 || ndy != 0);
        if (nudging && !_nudgeKeyHeld)
        {
            PetSignText.Nudge(ndx, ndy);
            // 日志要显式调：位置计算里**故意不打**（设置面板边打字边生效，
            // 每敲一个字符打一行会把日志刷没）。热键是"按一次 = 一次编辑"，所以这里打一行。
            PetSignText.LogArea();
        }
        _nudgeKeyHeld = nudging;

        // ── 按像素点击穿透 ──
        // 鼠标在她**画到的像素**上 → 窗口吃掉点击；在她的**透明区域** → 让点击穿到桌面。
        // 不做的话，她周围那一整块透明矩形都在吃鼠标，桌面图标就点不到了。
        //
        // ⚠️ 三条例外，缺一条就会出问题：
        //   ① **设置模式**下完全不做自动穿透 —— 面板需要接收鼠标。
        //      不然鼠标一移到面板上（那不是"她的像素"），窗口就变穿透，面板点不动。
        //      代价是那 480×400 会暂时挡住桌面 —— 但那正是任何模态窗口的行为。
        //   ② **拖动**（或刚按下、还没确认是点还是拖）期间保持不吃穿透 ——
        //      否则鼠标一移出她的像素，窗口立刻变穿透，拖拽会断在半路。
        bool grabbing = PetHitTest.LeftButtonDown && _brain != null
                     && (_brain.Current == PetBrain.State.Pressed
                      || _brain.Current == PetBrain.State.Drag);

        if (SettingsOpen) DeskWindow.SetClickThrough(false);
        else DeskWindow.SetClickThrough(!PetHitTest.CursorOnPet && !grabbing);

        // ── 右键点她 = 打开设置 ──
        // 为什么用右键而不是热键？桌面上可交互的东西，右键是约定俗成的"我要对它做点什么"，
        // 用户不需要被教；而且它**不依赖"窗口有没有焦点"** ——
        // 我们的窗口没有标题栏，用户根本看不出它有没有焦点。
        bool rmb = PetHitTest.RightButtonDown;
        if (rmb && !_rmbHeld && PetHitTest.CursorOnPet) ToggleSettings();
        _rmbHeld = rmb;

        // ── 开/关设置模式：热键是**开发者备用**入口，主入口是上面的右键 ──
        // ── 面板上的"关闭设置"按钮举的旗 ──
        // 放在热键前面：不管谁先按的，一帧内只切一次。
        if (ConsumeToggleRequest()) ToggleSettings();

        // ── 换皮肤 ──
        // 位置说明（免得下一个人找错地方）：这一段在 Update 的**中后段**，
        // 排在命中测试和尺寸不变式**之后**。所以换皮肤那一帧里，上面那些还是按旧皮肤跑的 ——
        // 没关系：`ReloadSkinNow` 自己会把窗口重算一遍，而命中测试下一帧就是新的了。
        if (_pendingSkin != null) ReloadSkinNow();

        bool setCombo = Win32.IsKeyDown(Win32.VK_CONTROL)
                     && Win32.IsKeyDown(Win32.VK_SHIFT)
                     && Win32.IsKeyDown(Win32.VK_S);
        if (setCombo && !_settingsKeyHeld) ToggleSettings();
        _settingsKeyHeld = setCombo;

        // ── 逃生舱 2：全局，不需要焦点 ──
        bool quitCombo = Win32.IsKeyDown(Win32.VK_CONTROL)
                      && Win32.IsKeyDown(Win32.VK_SHIFT)
                      && Win32.IsKeyDown(Win32.VK_Q);
        if (quitCombo && !_quitKeyHeld) Quit();
        _quitKeyHeld = quitCombo;

        // ── 需要焦点的顺手入口（Input.GetKeyDown 本身就带边沿，不用我们自己判）──
        if (Input.GetKeyDown(KeyCode.Escape)) Quit();
        if (Input.GetKeyDown(KeyCode.C)) SwitchTransparencyMode();
    }

    /// <summary>
    /// 切换透明方案。两个入口（全局热键 / F1），一个出口 —— 和 Quit() 同一个习惯。
    ///
    /// 切完必须同步相机的背景色，因为**相机背景就是"透明色"**：
    ///   DWM 方案 → alpha=0 的黑；色键方案 → 实心品红。两者必须配套，忘了同步就会出怪现象。
    /// </summary>
    void SwitchTransparencyMode()
    {
        DeskWindow.ToggleMode();
        if (_cam != null) _cam.backgroundColor = DeskWindow.CameraBackground;
        Debug.Log("[DeskSprite] 透明方案 → " + DeskWindow.Mode + " | " + DeskWindow.LastDetail);
    }

    /// <summary>
    /// 开/关设置模式：把窗口在"她的尺寸"和"面板的尺寸"之间来回切。
    ///
    /// ⚠️ **她不会被隐藏。** 两个原因：
    ///   ① 这样才看得出"窗口放大后她还贴着底边"（藏起来就没法验了）
    ///   ② 以后微调文字位置时，用户需要**看着她**调 —— 这正是"可视化操作"的意义
    /// 所以：她留在窗口**底部**，设置面板摆在上面。
    ///
    /// 三件事必须一起做，顺序也有讲究：
    ///   ① 改目标尺寸（那个每帧的不变式按它来）
    ///   ② 立刻改窗口尺寸（不等不变式，用户按了就该马上有反应）
    ///   ③ 重新算她的位置 —— **并且直接用目标尺寸算**，不要等 Screen 更新：
    ///      窗口尺寸是异步生效的，等 Screen 变会有一帧她站在错误的地方（会看到闪一下）
    ///
    /// ⚠️ 第 ② 步改尺寸时，锚点是"**她在屏幕上的那一点**"= 窗口底边中点，
    ///    不是窗口的某条边。理由：
    ///      · 她水平居中在窗口里 → 固定**左边缘**会让窗口中心右移，
    ///        也就是**她右移**（192 宽变 480 宽时右移 144 像素，肉眼很明显）
    ///      · 她贴在窗口底边 → 固定**下边缘**才对，固定上边缘会把她推下去
    ///    所以"固定边"是错的思路，**固定她那一点**才是对的。
    ///
    /// ⚠️ 另外要**记两笔账**：打开前她在哪（_pre）、打开后实际把她放到了哪（_placed）。
    ///    屏幕边缘放不下 480×540，打开时她会被夹走一点；关闭时窗口小了、原位置放得下，
    ///    所以要**还回 _pre**。但如果这期间用户自己把她拖走了，那就该以用户拖的为准。
    /// </summary>
    void ToggleSettings()
    {
        SettingsOpen = !SettingsOpen;

        // 重置尺寸纠正次数：那个不变式有 15 次上限，
        // 不重置的话"用完之后再改尺寸"就没人纠正了。
        _sizeFixAttempts = 0;

        int ax, ay;
        bool haveAnchor = DeskWindow.GetClientBottomCenter(out ax, out ay);

        if (SettingsOpen)
        {
            // 打开：先记住她原来的位置
            _preAnchorX = ax;
            _preAnchorY = ay;
            _havePreAnchor = haveAnchor;
        }
        else if (_havePreAnchor && haveAnchor
                 && ax == _placedAnchorX && ay == _placedAnchorY)
        {
            // 关闭，而且她在这期间**没被拖动过** → 还回打开之前的位置。
            ax = _preAnchorX;
            ay = _preAnchorY;
        }
        // 关闭但她被拖过 → 用她现在的位置（用户的操作优先，不硬拽回去）

        DeskWindow.ResizeAroundBottomCenter(ax, ay, TargetWindowWidth, TargetWindowHeight);

        // 记下"实际把她放到了哪" —— 关闭时靠它判断用户有没有再拖过。
        // SetWindowPos 是同步生效的，所以这里读到的就是真值。
        if (SettingsOpen)
        {
            int px, py;
            if (DeskWindow.GetClientBottomCenter(out px, out py))
            {
                _placedAnchorX = px;
                _placedAnchorY = py;
            }
        }

        // 真正的面板。注意是 SetVisible（只切 Canvas.enabled），不是重建 ——
        // 面板里的文本内容会保留，下次打开不用重新填。
        if (SettingsPanel.Instance != null) SettingsPanel.Instance.SetVisible(SettingsOpen);

        if (_anim != null)
        {
            // 用**目标**尺寸算位置，不等 Screen 更新 —— 否则会闪一下
            float halfView = TargetWindowHeight / (2f * Z);
            _anim.transform.localPosition = new Vector3(0f, -halfView, 0f);
        }

        // 把窗口实际被摆到哪儿也打出来：万一锚点或夹取算错了，这行就是现场证据。
        Debug.Log(string.Format("[DeskSprite] 设置模式 -> {0}  窗口目标 {1}x{2}  锚({3},{4})  {5}",
                                SettingsOpen ? "开" : "关",
                                TargetWindowWidth, TargetWindowHeight,
                                ax, ay, DeskWindow.LastDetail));
    }

    // ── "她多大"（用户缩放）──

    /// <summary>已经应用过的缩放（%）。初值是一个不可能等于正常值的数，所以第一帧一定会算一次。</summary>
    int _appliedZoom = int.MinValue;

    /// <summary>
    /// 把用户缩放应用到皮肤上，并跟着改窗口大小。
    ///
    /// 【为什么把结果**写回 `skin.Scale`**，而不是另记一个"有效缩放"】
    /// 读它的有三个地方：相机（决定 1 贴图像素占几个屏幕像素）、
    /// 窗口尺寸（`帧 × 缩放`）、命中测试（屏幕坐标 → 贴图坐标）。
    /// 写回一处赋值，三处自动一致 —— 多一个概念就多一个会忘记同步的地方。
    ///
    /// 【为什么要从 `AuthorScale` 重算，而不是在当前值上乘】
    /// 否则用户每调一次都会在上一次的结果上再乘一遍，几次之后就漂到天边去了。
    ///
    /// 【结构上限：窗口不能超过显示器工作区】
    /// 这不是"太大不好看"，是**有一部分会永远在屏幕外** —— 用户看不到她，
    /// 那个设置也就失去了意义。所以到顶就夹住并说明。
    /// 注意这条和"设置面板能不能打开"无关：设置模式的窗口是**固定 480×600** 的，
    /// 所以她再大，右键点她照样能打开面板、把缩放调回来 —— **这个设定是可恢复的**。
    /// </summary>
    void ApplyZoomIfChanged(bool force)
    {
        PetSkin skin = SkinLoader.Current;
        if (skin == null) return;

        // 第 12 帧之前不动窗口：那时样式还是带标题栏的，改尺寸会被之后的
        // WS_POPUP 再改一遍（而且注册表恢复也可能插一脚）。启动那次由
        // `force = true` 在第 12 帧统一做。
        if (!force && _frame <= 12) return;

        int zoom = PetConfig.ZoomPercent;
        if (!force && zoom == _appliedZoom) return;
        _appliedZoom = zoom;

        float s = skin.AuthorScale * (zoom / 100f);

        // 像素画吸附到整数倍：1.37× 会让硬边有的占 1 个屏幕像素、有的占 2 个 —— 看起来就是坏了
        if (skin.Filter == FilterMode.Point) s = Mathf.Max(1f, Mathf.Round(s));
        if (s <= 0.01f) s = 0.01f;

        // 夹进工作区
        int w = Mathf.RoundToInt(skin.FrameWidth * s);
        int h = Mathf.RoundToInt(skin.FrameHeight * s);

        int wl, wt, wr, wb;
        bool clamped = false;
        if (DeskWindow.GetWorkArea(out wl, out wt, out wr, out wb))
        {
            int maxW = Mathf.Max(1, wr - wl);
            int maxH = Mathf.Max(1, wb - wt);
            float fit = Mathf.Min(1f, Mathf.Min(maxW / (float)w, maxH / (float)h));
            if (fit < 1f)
            {
                s *= fit;
                // 像素画夹完还要落回整数，否则前面的吸附白做了
                if (skin.Filter == FilterMode.Point) s = Mathf.Max(1f, Mathf.Floor(s));
                clamped = true;
            }
        }

        skin.Scale = s;
        w = skin.WindowWidth;
        h = skin.WindowHeight;

        Debug.Log(string.Format(
            "[DeskSprite] 缩放 {0}% → 倍率 {1:F2}，窗口 {2}×{3}{4}（像素画会吸附到整数倍）",
            zoom, s, w, h, clamped ? "  [已夹到工作区内]" : ""));

        // 相机**无论什么模式都要跟上** —— 它决定"1 贴图像素占几个屏幕像素"。
        // 设置模式下面板固定 480×600，但她的倍率变了，相机不跟着算她就会画错大小。
        if (_cam != null)
        {
            _cam.orthographicSize = Screen.height / (2f * Mathf.Max(0.01f, s));
            RepositionPet();
        }

        // 设置模式有自己的固定窗口尺寸，别去动它。
        // （关掉面板时 ToggleSettings 会用新的 TargetWindowWidth/Height 缩回去。）
        if (SettingsOpen) return;

        int ax, ay;
        if (!DeskWindow.GetClientBottomCenter(out ax, out ay)) return;

        // 锚定**她那一点**（窗口底边中点）—— 缩放时她不会在屏幕上乱跳
        DeskWindow.ResizeAroundBottomCenter(ax, ay, w, h);
        _screenHeight = h;
    }

    // ── 换皮肤（现场生效，不用重开程序）──

    /// <summary>
    /// 把形象换成面板刚验证过的那套皮肤。
    ///
    /// 一次换皮肤要动四样东西，缺一样都会留下"看起来没换干净"的症状：
    ///   ① 形象本身      → 销毁重建（帧尺寸、动作表、贴图、牌子全是按皮肤建的）
    ///   ② 司机的引用    → `_anim` / `_brain` 指向的都是旧对象，必须重新拿
    ///   ③ 牌子的用户调整 → 它是**按皮肤**存的，换皮肤时必须清掉旧的，
    ///                      否则上一套的数字位置/字号会套到新皮肤上（而且看起来像 bug）
    ///   ④ 窗口尺寸      → 新皮肤的帧尺寸可能不同，`ApplyZoomIfChanged(true)` 会重算
    ///                      倍率、夹进工作区、并锚定她的底边中点改窗口
    /// </summary>
    void ReloadSkinNow()
    {
        PetSkin skin = _pendingSkin;
        string path = _pendingSkinPath;
        _pendingSkin = null;
        _pendingSkinPath = null;
        if (skin == null) return;

        // ③ 先清掉上一套皮肤的用户调整 —— 必须在 BuildPet 之前，
        //    因为 BuildPet 里的 BuildSignText 会读**新皮肤自己**的覆盖。
        PetSignText.ResetOffset();
        PetSignText.SetFontScale(1);

        // ② 旧引用先置空，免得这一帧里有人用到已销毁的对象
        _anim = null;
        _brain = null;

        SkinLoader.Adopt(skin);

        // ① 重建形象。面板和司机是**兄弟节点**，不受影响 —— 面板会一直开着。
        GameObject vis = PetBootstrap.RebuildPet(skin);
        if (vis == null)
        {
            Debug.LogWarning("[DeskSprite] 换皮肤失败：形象没建出来");
            return;
        }

        _anim = vis.GetComponent<PetAnimator>();
        _brain = vis.GetComponent<PetBrain>();

        // ④ 重算窗口。force = true 让它无论如何都跑一遍。
        _appliedZoom = int.MinValue;
        ApplyZoomIfChanged(true);

        Debug.Log("[DeskSprite] 换皮肤 -> " + skin.Name + "（" + path + "）\n  "
                + SkinLoader.Diagnostics);
    }

    /// <summary>
    /// 让她的**底边始终贴着窗口底边**。    ///
    /// 用实际可见的半个世界高度（= 相机的 orthographicSize），而不是"帧高 / 2"。
    /// 两者只在"窗口高 = 帧高 × Z"时才相等 —— 设置面板会把窗口放大，
    /// 那时按帧高算她就会浮到窗口中间去。
    /// </summary>
    void RepositionPet()
    {
        if (_anim == null || _cam == null) return;

        Transform t = _anim.transform;
        t.localPosition = new Vector3(0f, -_cam.orthographicSize, t.localPosition.z);
    }

    /// <summary>
    /// 屏幕上的诊断面板。
    ///
    /// ⚠️ 两条硬规则：
    ///   ① **只用 ASCII。** 打包后 Unity 内置的 GUI 字体不一定有中文字形，
    ///      中文会显示成一串方块。（皮肤名可能是中文，所以这里**不显示名字**，只显示数字 ——
    ///      名字在 Player.log / Console 里。）
    ///   ② **必须够窄。** 窗口尺寸由皮肤决定，现在是 192×192，不是当初的 480×320。
    ///      所以这里字号 10、四行、每行都短。
    ///
    /// 另外：色键模式下这段文字会带紫边 —— 文字边缘是抗锯齿的半透明像素，
    /// 和品红背景混在一起洗不干净。这正好是"色键和抗锯齿文字不兼容"的现场演示。
    /// </summary>
    void OnGUI()
    {
        if (!ShowPanel) return;

        // 设置模式里**不画**这个诊断面板。
        //
        // 原因很实际：面板占了窗口顶部 260 高的一条，而这个诊断面板在左上角，
        // 两个会叠在一起；她想挪到下面又放不下 —— 她占着底部中间 x 144..336，
        // 左右两边各只剩 144，而这行字要 ~184 宽。
        //
        // （窗口高 540、她占底部 264，所以正好是 260 这个数：276 可用里留 16 给面板下沿。）
        //
        // 所以设置模式下看设置面板本身（那里有皮肤和余额的中文状态），
        // 要看 brain / hit / thru 这些细节就去读 Player.log。
        if (SettingsOpen) return;

        var label = new GUIStyle(GUI.skin.label);
        label.fontSize = 10;
        label.wordWrap = false;

        PetSkin s = SkinLoader.Current;

        string where = Application.isEditor ? "EDIT" : "PLAY";
        string mode = DeskWindow.Mode == TransparencyMode.DwmAlpha ? "DWM" : "KEY";

        // ── 先把要显示的**行**准备好，再据此算面板高度 ──
        // 为什么要这样？因为我们刚被坑过：原来高度是硬编码的 58，
        // 行数一多，最后一行就被 GUI 裁掉了 —— 而且没有任何报错，就是"看不见"。
        // 用 CalcSize 量出一行真正需要的高度，字号和行数怎么变都不会截断。
        var lines = new List<string>();
        lines.Add("M1 " + where + " " + mode + " " + Screen.width + "x" + Screen.height + " Z" + Z
                + (SettingsOpen ? " [SET]" : ""));
        if (s == null)
        {
            lines.Add("skin LOAD FAILED");
            lines.Add("(reason in Player.log)");
        }
        else
        {
            // 三样东西分三行：
            //   状态机现在在哪个状态 —— **验证状态机的主要手段**
            //   命中 / 左键        —— 鼠标信号通不通
            //   余额               —— B1 的成果
            lines.Add("brain " + (_brain != null ? _brain.Diagnostics : "none")
                    + "  hit " + (PetHitTest.CursorOnPet ? "HIT" : "miss")
                    + "  LMB " + (PetHitTest.LeftButtonDown ? "1" : "0"));
            // thru = 鼠标穿透开着吗（1 = 点穿到桌面）。
            // 和她并排显示，方便一眼看出"穿透有没有跟着鼠标正确开合"。
            lines.Add("thru " + (DeskWindow.ClickThrough ? "1" : "0")
                    + "  " + DeepSeekBalance.PanelLine());

            // 正在微调文字位置时，把"调好之后的 textArea"显示出来 —— 直接抄这一行回 skin.json
            if ((PetSignText.OffsetX != 0 || PetSignText.OffsetY != 0) && s.HasTextArea)
            {
                lines.Add(string.Format("textArea [{0},{1},{2},{3}]",
                                        s.TextArea[0] + PetSignText.OffsetX,
                                        s.TextArea[1] + PetSignText.OffsetY,
                                        s.TextArea[2],
                                        s.TextArea[3]));
            }
        }

        // ── 一行需要多高？**量出来**，不要拿 lineHeight 硬算 ──
        // 实测数据（日志里那行 panel: 打出来的）：
        //     fontSize=10  lineHeight=11.0  calcSize=17.0  padding=3.0/3.0
        // 说明：
        //     lineHeight=11 是"行距"，calcSize.y=17 才是"一行真正需要的高"，
        //     **而 calcSize 里已经含了 padding**。
        // 我上一版又加了一次 padding（17+6+2=25），等于算了两遍 —— 每行多 6 像素，三行多 18。
        // 现在有数据了，就用数据：calcSize.y + 2。
        var probe = label.CalcSize(new GUIContent("Ag"));
        float lineH = Mathf.Ceil(probe.y) + 2f;

        const float pad = 5f;
        float h = lines.Count * lineH + pad * 2f;

        // 画一次日志：把面板的真实度量记下来。
        // 为什么必须有它？因为"被截断"这件事我前两次都是靠猜的。记一次数据，就不用猜了。
        if (!_panelLogged)
        {
            _panelLogged = true;
            Debug.Log(string.Format(
                "[DeskSprite] panel: lines={0} fontSize={1} lineHeight={2:F1} calcSize={3:F1} "
                + "fixedH={4:F1} padding={5:F1}/{6:F1} margin={7:F1}/{8:F1} -> lineH={9:F1} boxH={10:F1} screen={11}x{12}",
                lines.Count, label.fontSize, label.lineHeight, probe.y,
                label.fixedHeight, label.padding.top, label.padding.bottom,
                label.margin.top, label.margin.bottom, lineH, h, Screen.width, Screen.height));
        }

        var area = new Rect(2f, 2f, Screen.width - 4f, h);
        GUI.Box(area, GUIContent.none);

        // ⚠️ 这里**故意用 GUI.Label + 精确矩形**，而不是 GUILayout.Label。
        //   原因：GUILayout 会按 style 的 margin 自动加元素间距，手算高度时很容易漏掉它 ——
        //   漏了就表现为"最后几行被静默裁掉"。给自己的每一行算好 y 坐标，就没有这个变量了。
        for (int i = 0; i < lines.Count; i++)
        {
            var r = new Rect(area.x + 4f, area.y + pad + i * lineH,
                             area.width - 8f, lineH);
            GUI.Label(r, lines[i], label);
        }
    }

    /// <summary>两个入口，一个出口。以后再加第三条逃生路（比如托盘菜单），只改这里。</summary>
    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;   // 编辑器里 Application.Quit() 不生效
#else
        Application.Quit();
#endif
    }
}
