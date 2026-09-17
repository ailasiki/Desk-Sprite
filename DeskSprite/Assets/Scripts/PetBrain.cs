using UnityEngine;

/// <summary>
/// 状态机：把鼠标信号变成"该播哪个动作"。
///
/// <code>
///   Idle    ──鼠标压到她身上──→  Hover
///   Idle    ──间隔到了──────→  Ambient    ← 从待机池随机挑一个（§5.7）
///   Ambient ──播完一个周期──→  Idle
///   Ambient ──鼠标压到她────→  Hover      ← 互动随时打断
///   Hover   ──移开──────────→  Idle
///   Hover   ──按下──────────→  Pressed      ← "还不知道是点还是拖"
///   Pressed ──移动 ≥ 4px────→  Drag
///   Pressed ──按住 ≥ 300ms──→  Drag         （"拎住了"）
///   Pressed ──松开──────────→  Click
///   Drag    ──松开──────────→  Hover / Idle  ← **再也不回 Click**
///   Click   ──播完──────────→  Hover / Idle
/// </code>
///
/// 为什么要有 Pressed 这个中间态？
///   如果按下就直接进 Drag，那么一次**普通点击**的路径会是 hover→drag→click ——
///   她会闪一下拖拽姿势。Pressed 期间**不改动画**，就没有这一闪。
///
/// 为什么 Drag 松开后不能回 Click？
///   否则"按住不动再松开"会变成 Pressed→Drag→Click，
///   又会出现"按住不动松手，结果跳去点击"的怪现象。
///   **点击只可能从 Pressed 里产生。**
///
/// 待机池（Ambient）的模型见《皮肤契约设计.md》§5.7：
///   **`idle` 是节点，池动作是挂在它上面的一次性分支。**
///   所以 Ambient **只能从 Idle 进、只能回 Idle** —— 池动作之间永远不会直接相连，
///   中间一定夹着至少一个完整的 `idle` 周期。
/// </summary>
public class PetBrain : MonoBehaviour
{
    public enum State { Idle, Hover, Pressed, Drag, Click, Ambient }

    /// <summary>判定"开始拖"的位移阈值（像素，曼哈顿距离）。</summary>
    const int DragThresholdPx = 4;

    /// <summary>
    /// 按住多久没动也算"拎住了"（秒）。
    ///
    /// 代价：**按得慢的点击会被当成拎住，于是不触发点击反应。**
    /// 典型点击只有 50~150ms，300ms 留了余量。觉得不对就调大，或者把这条例判据去掉。
    /// </summary>
    const float HoldToDragSeconds = 0.3f;

    public State Current { get; private set; } = State.Idle;

    /// <summary>给面板看的短诊断（英文小写，屏幕上只用 ASCII）。</summary>
    public string Diagnostics { get; private set; } = "(init)";

    PetSkin _skin;
    PetAnimator _anim;

    bool _downLast;
    int _pressX, _pressY;           // 按下那一刻的鼠标屏幕坐标
    int _grabOffsetX, _grabOffsetY; // 抓取点相对于窗口客户区左上角的偏移
    bool _movedEnough;
    float _pressTimer;
    float _clickTimer;

    // ── hover 的输入迟滞（为什么要它，见 Update 里 Hover 分支的注释）──

    /// <summary>死区：鼠标移动超过这么多**屏幕像素**，才算"用户动了鼠标"。</summary>
    const float HoverFreezePixels = 3f;

    /// <summary>进入 hover 那一刻的鼠标屏幕坐标 —— 迟滞的锚点。</summary>
    Vector2 _hoverAnchor;

    /// <summary>
    /// 第二道确认：越过死区之后，还要**持续**这么多秒，才真的算"走了"。
    ///
    /// 为什么光有距离不够：手抖能让光标在死区边界来回跳，跳出去就退出、
    /// 跳回来又进入 —— 变成以抖动频率闪（触摸板尤其容易）。
    /// 加了时间之后，抖动期间总有一帧落回"没动"，计时被清零，永远累积不到阈值。
    /// </summary>
    const float HoverExitConfirmSeconds = 0.1f;

    /// <summary>"鼠标越过死区、而且不在她身上"已经持续了多久。</summary>
    float _hoverOffTimer;

    // ── 待机池（§5.7）──
    bool _idleReady;                // idle 播完第一个完整周期了吗（计时从那之后才开始）
    float _idleTimer;               // 从"idle 播完第一个周期"起累积的**等待**秒数
    int _idleCycleSeen;             // 已经"结算"过的 idle 循环次数
    float _ambientTimer;            // 池动作已经播了多久（超时兜底用）
    PetAction _ambientAction;       // 正在播的池动作（可能为 null）
    PetAction _lastAmbient;         // 上一次播的池动作 —— 用来避免连着播同一个

    void Awake()
    {
        _anim = GetComponent<PetAnimator>();
    }

    public void Init(PetSkin skin)
    {
        _skin = skin;
        // 走 Enter 而不是直接赋值：待机计时的两个游标、以及那行日志，都在 Enter 里初始化。
        // 直接写 Current = Idle 会漏掉它们（现在恰好靠默认值 0 也能跑，但那是巧合）。
        Enter(State.Idle);
    }

    void Update()
    {
        if (_skin == null || _anim == null) return;

        bool onPet = PetHitTest.CursorOnPet;
        bool down = PetHitTest.LeftButtonDown;

        // 电平 → 边沿（和全局热键同一个道理：IsKeyDown 问的是"现在按着吗"）
        bool pressed = down && !_downLast;
        bool released = !down && _downLast;
        _downLast = down;

        switch (Current)
        {
            case State.Idle:
                if (onPet) Enter(State.Hover);
                else TickIdlePool();
                break;

            case State.Ambient:
                _ambientTimer += Time.deltaTime;
                // 互动随时打断：她正在"睡觉"，你鼠标一过来她就醒
                if (onPet) Enter(State.Hover);
                // 播完一个完整周期就回 idle。
                // ⚠️ 超时是**必须的兜底**，理由和 ClickTimeout 一模一样：
                //    如果皮肤给的动作有问题（比如 0 帧、或者哪天再有别的边界），
                //    Finished 可能永远不变成 true，而她就会**永久卡在这个姿势上**。
                else if (_anim.Finished || _ambientTimer >= AmbientTimeout()) Enter(State.Idle);
                break;

            case State.Hover:
                if (pressed) BeginPress();
                else
                {
                    // ── 退出 hover 用「与」逻辑：鼠标动过 **且** 现在不在她身上 ──
                    //
                    // 为什么不能只看 onPet：命中用的是**正在播的那一帧**的像素，
                    // 而 hover 这个动作本身就会改变轮廓（抬手、换姿势……）。
                    // 于是"鼠标没动、只是她在做表情"也会被判成"已经离开了" ——
                    // 状态一退一进，表现就是 30Hz 无限闪。
                    //（顺带：点击穿透也看 CursorOnPet，所以那块地方的点击会时灵时不灵。）
                    //
                    // 所以：只要鼠标没动（还在死区内），无论动画把她画到哪，
                    // 都算"鼠标还在她身上"；动过之后才恢复正常判定。
                    // 这是**输入迟滞**（去抖）—— 做在**鼠标位置**这一侧，
                    // 命中判定的语义一个字没改（"只有她画到的像素吃鼠标"仍然成立）。
                    // 两道门：先看**距离**（动没动），再看**时间**（是不是真的走了）。
                    // 只用距离的话，手抖能让光标在死区边界来回跳 ——
                    // 跳出去就退、跳回来又进，变成以抖动频率闪。
                    bool moved = Vector2.Distance(PetHitTest.CursorScreenPos, _hoverAnchor)
                               > HoverFreezePixels;
                    if (!moved)
                    {
                        _hoverOffTimer = 0f;       // 没动 → 冻结，并把"要走"的计时清零
                        break;
                    }

                    if (onPet)
                    {
                        _hoverAnchor = PetHitTest.CursorScreenPos;   // 动了但还在她身上 → 锚点跟上
                        _hoverOffTimer = 0f;
                    }
                    else
                    {
                        // 动了、也确实不在她身上 —— 但还要再等一小会儿才认。
                        // 手抖会在下一帧把计时清零，所以它永远累积不到阈值。
                        _hoverOffTimer += Time.deltaTime;
                        if (_hoverOffTimer >= HoverExitConfirmSeconds) Enter(State.Idle);
                    }
                }
                break;

            case State.Pressed:
                _pressTimer += Time.deltaTime;
                if (TickMoved()) BeginActualDrag();               // 动了 → 拖
                else if (_pressTimer >= HoldToDragSeconds) BeginActualDrag();  // 拎久了 → 也算拖
                else if (released) Enter(State.Click);            // 之前松开 → 点
                break;

            case State.Drag:
                TickDrag();
                if (released) Enter(onPet ? State.Hover : State.Idle);   // **不回 Click**
                break;

            case State.Click:
                _clickTimer += Time.deltaTime;
                if (_anim.Finished || _clickTimer >= ClickTimeout())
                {
                    Enter(onPet ? State.Hover : State.Idle);
                }
                break;
        }
    }

    /// <summary>
    /// 被点一下 —— 顺手刷新余额。
    ///
    /// 【为什么由这里发起】因为这是**用户唯一的刷新入口**（按钮和定时轮询都去掉了）：
    ///   · 每一次请求都是用户**主动**要的 —— 这是这个功能的 API 使用策略
    ///   · `Click` 这个状态**只在"按下又松开、而且没拖动"时**产生（见类注释），
    ///     所以拖她和按住都不会误触发
    ///
    /// ⚠️ 用户关掉了余额就**什么都不做** —— 不是"查了不显示"，是**根本不查**。
    ///    这条由 DeepSeekBalance 那边保证（Disabled 状态下 Update/RefreshNow 都直接 return，
    ///    连 Key 都不读），这里只是不去多此一举地叫它。
    /// </summary>
    void OnClickedForBalance()
    {
        if (PetConfig.HideBalance) return;
        DeepSeekBalance.RefreshNow();
    }

    // ------------------------------------------------------------------
    // 待机池（《皮肤契约设计.md》§5.7）
    // ------------------------------------------------------------------

    /// <summary>
    /// 在 Idle 里数时间，到点了就从池子里随机挑一个动作来播一次。
    ///
    /// 【计时从哪里开始】—— **从 idle 播完第一个完整周期那一刻开始**，不是从进入 idle 开始。
    /// 也就是说：**"间隔"是两次动作之间的停顿，不含 idle 本身的时长。**
    ///
    /// <code>
    ///   idle 播一圈 → 【等 interval 秒】→ 待机动作 → idle 播一圈 → 【等 interval 秒】→ …
    /// </code>
    ///
    /// 为什么这样定：
    ///   ① 语义干净 —— "间隔 5 秒"就是"两次动作之间空 5 秒"，
    ///      而不是"每 N 秒动一次"（后者会把 idle 的时长偷偷算进去，
    ///      而 idle 的时长是**素材**决定的，一改帧数行为就变了）。
    ///   ② 它和"idle 是节点"是同一件事的两面：间隔 = 0 时就是
    ///      `idle 播一圈 → 待机 → idle 播一圈 → 待机`，idle **永远不被跳过**。
    ///      如果按字面把起点定在"**待机动作**的末尾帧"，间隔 0 时两个待机动作
    ///      就会紧挨着，idle 被跳过 —— 和节点规则冲突。
    ///
    /// 【"一个完整周期"是数出来的】依据是 <see cref="PetAnimator.LoopCount"/>，
    /// 不是拿秒数算的。所以帧率怎么变、素材有几个帧，这条都成立。
    ///
    /// 【两条规则都不可选】任何互动都会 `Enter(Idle)`，那里会把这两个游标重置 ——
    /// 所以"她隔多久动一下"是从**最后一次理她**算起，而不是从开机算起。
    /// </summary>
    void TickIdlePool()
    {
        if (_skin == null || _skin.IdlePool.Count == 0) return;

        if (!_idleReady)
        {
            // 等 idle 播完一个完整周期。这一帧只负责"把计时起点挪到这里"。
            if (_anim.LoopCount == _idleCycleSeen) return;
            _idleReady = true;
            _idleCycleSeen = _anim.LoopCount;
            _idleTimer = 0f;
            return;
        }

        _idleTimer += Time.deltaTime;

        // 0 = 不等待，所以用 >= 而不是 >
        if (_idleTimer < PetConfig.IdleSwitchSeconds) return;

        EnterAmbient();
    }

    /// <summary>从池子里随机挑一个（**不连着挑同一个**）并播一次。</summary>
    void EnterAmbient()
    {
        int n = _skin.IdlePool.Count;
        if (n == 0) return;

        int pick;
        if (n == 1)
        {
            pick = 0;                     // 池里只有一个：没得选，"不重复"这条自动失效
        }
        else
        {
            // ⚠️ 池里只有 2 个动作时，均匀随机有 **50%** 概率连续播同一个 ——
            //    看起来就是"卡住了/功能没生效"。所以排除掉上一次那个。
            //    做法不是"重摇直到不同"（那在 n=2 时平均要摇 2 次，而且是隐式的），
            //    而是在 n-1 个候选里等概率挑一个、再跳过上一次 —— 一次就够，且均匀。
            int last = IndexOfLast();
            pick = Random.Range(0, n - 1);
            if (last >= 0 && pick >= last) pick++;   // 把 [0, n-1) 映射到"除 last 以外"的 n-1 个
        }

        PetAction a = _skin.IdlePool[pick];
        _lastAmbient = a;
        _ambientAction = a;
        _ambientTimer = 0f;

        // ⚠️ 这里**不直接调 _anim.PlayOnce**，而是走 Enter -> PlayFor。
        //    "某状态该播哪个动作"只在 PlayFor 一个地方决定 ——
        //    绕过它就会漏掉 Current 的赋值（状态进不去 Ambient），
        //    而且以后加东西时会出现两个地方各播各的。
        //
        // 日志里带上**实际等待了多少秒**和**池动作有多长**：
        // "间隔设了 N 秒到底有没有生效"如果只能靠眼睛掐秒表，那它就是一个没法查的问题。
        // 有了这两个数，日志自己就能回答。（PetAnimator 的帧数/帧率决定后者。）
        Debug.Log(string.Format(
            "[DeskSprite] 待机池 -> {0}（池 {1} 个，挑中第 {2} 个；设定间隔 {3}s，实际等了 {4:F2}s；"
            + "这个动作 {5} 帧 @{6}fps ≈ {7:F2}s）",
            a.Name, n, pick, PetConfig.IdleSwitchSeconds, _idleTimer,
            a.FrameCount, a.Fps, a.FrameCount / Mathf.Max(0.01f, a.Fps)));
        Enter(State.Ambient);
    }

    int IndexOfLast()
    {
        if (_lastAmbient == null) return -1;
        for (int i = 0; i < _skin.IdlePool.Count; i++)
        {
            if (ReferenceEquals(_skin.IdlePool[i], _lastAmbient)) return i;
        }
        return -1;
    }

    /// <summary>
    /// 池动作最长播多久 —— **纯粹是兜底**，正常情况下靠 <see cref="PetAnimator.Finished"/>。
    ///
    /// 和 <see cref="ClickTimeout"/> 是同一个陷阱的第二次出现：
    /// 只要"动作播完了"这件事有可能永远不成立，状态机就必须有自己走出来的能力，
    /// 否则她卡住之后就**再也不理你了**，而且看起来像"程序死了"。
    /// </summary>
    float AmbientTimeout()
    {
        PetAction a = _ambientAction;
        if (a == null || a.FrameCount == 0) return 1f;
        return 0.5f + 1.5f * a.FrameCount / Mathf.Max(1f, a.Fps);
    }

    void Enter(State s)
    {
        Current = s;
        _clickTimer = 0f;
        PlayFor(s);

        // 进入 idle = 新一轮计时的开始。三样都要在这里重置：
        //   · _idleReady = false —— **先等 idle 播完一个完整周期**，计时从那之后才开始
        //   · _idleTimer 归零 —— 任何互动之后都重新数间隔
        //   · _idleCycleSeen 记下**当前**的循环计数 —— "下一个周期边界"才是候选起点。
        //     必须放在 PlayFor **之后**：Play 对"同一个动作"会直接 return
        //    （比如皮肤没有 hover 时，hover -> idle 其实没换动作），那时 LoopCount 不会被清零。
        if (s == State.Idle)
        {
            _idleReady = false;
            _idleTimer = 0f;
            _idleCycleSeen = _anim.LoopCount;
        }

        // 进 hover 时记下鼠标位置 —— 之后判断"用户离开了没有"以它为准，
        // 而不是以"这一帧她有没有画到那个像素"为准（理由见 Hover 分支）。
        // **每次重新进入都要重记**：这样"动过之后退出、又立刻进来"能自动回到冻结态。
        if (s == State.Hover) _hoverAnchor = PetHitTest.CursorScreenPos;

        // 被点一下 -> 顺手刷新余额。放在 Enter 里而不是 Update 的 Click 分支里：
        // Enter 只会在**进入**这个状态时跑一次，而 Update 每帧都跑。
        if (s == State.Click) OnClickedForBalance();

        Diagnostics = s.ToString().ToLowerInvariant();

        // 记一次状态切换。
        // 为什么不靠眼睛看面板？因为快速点击时 pressed 只存在约 100ms（3 帧），
        // 面板上根本来不及看清。日志能给出**精确的序列**，例如
        //     brain -> hover / brain -> pressed / brain -> click / brain -> hover
        // —— 有没有中间夹一个 drag，一眼就能看出来。
        Debug.Log("[DeskSprite] brain -> " + Diagnostics);
    }

    /// <summary>
    /// 播放某个状态对应的动作。
    ///
    /// 注意用的是 <see cref="PetSkin.GetAction"/>，它带**兜底链**（没有 hover 就回 idle）。
    /// 而 <see cref="PetAnimator.Play"/> 对**同一个动作不会重播** —— 所以：
    ///
    ///     正在播 idle → 进入 Hover → 兜底回 idle → Play(idle) → 同一对象，直接 return
    ///     → **动作不变，但状态已经是 hover**
    ///
    /// 这正是我们要的"没有 hover 动作就保持原样，但逻辑上算 hover"。
    /// </summary>
    void PlayFor(State s)
    {
        switch (s)
        {
            case State.Idle:  _anim.Play(_skin.GetAction("idle")); break;
            case State.Hover: _anim.Play(_skin.GetAction("hover")); break;
            case State.Drag:  _anim.Play(_skin.GetAction("drag")); break;
            case State.Click: _anim.Play(_skin.GetAction("click"), true); break;

            case State.Pressed:
                // 按下但还不知道是点还是拖 —— **先不要改动作**，否则一次普通点击会闪一下拖拽姿势。
                // 皮肤如果真的有 press 动作，那就用它。
                if (_skin.HasAction("press")) _anim.Play(_skin.GetAction("press"), true);
                break;

            case State.Ambient:
                // 池动作是"挂在 idle 上的一次性分支"（§5.7）：
                // 用 PlayOnce，**不管作者把它声明成循环还是不是** ——
                // 播完一个周期就回 idle。想让她睡久一点就多画几帧。
                if (_ambientAction != null) _anim.PlayOnce(_ambientAction);
                break;
        }
    }

    /// <summary>记录按下：抓住她身上的哪一点，以及记住"还没确认是拖"。</summary>
    void BeginPress()
    {
        Win32.POINT p;
        int ox, oy;
        if (!Win32.GetCursorPos(out p) || !DeskWindow.GetClientOrigin(out ox, out oy))
        {
            Enter(State.Hover);
            return;
        }

        _pressX = p.x;
        _pressY = p.y;
        _grabOffsetX = p.x - ox;
        _grabOffsetY = p.y - oy;
        _movedEnough = false;
        _pressTimer = 0f;

        Enter(State.Pressed);
    }

    /// <summary>这一帧光标相对按下点移动够了吗？</summary>
    bool TickMoved()
    {
        if (_movedEnough) return true;

        Win32.POINT p;
        if (!Win32.GetCursorPos(out p)) return false;

        int moved = Mathf.Abs(p.x - _pressX) + Mathf.Abs(p.y - _pressY);
        if (moved >= DragThresholdPx) _movedEnough = true;
        return _movedEnough;
    }

    /// <summary>
    /// 确认是拖，正式开始跟着光标走。
    ///
    /// ⚠️ 这里**重新记录抓取偏移**。因为从按下到确认是拖，光标可能已经移了阈值那么多像素；
    ///    不重记的话，她会在开始拖的瞬间"跳"那几像素。
    /// </summary>
    void BeginActualDrag()
    {
        Win32.POINT p;
        int ox, oy;
        if (Win32.GetCursorPos(out p) && DeskWindow.GetClientOrigin(out ox, out oy))
        {
            _grabOffsetX = p.x - ox;
            _grabOffsetY = p.y - oy;
        }
        Enter(State.Drag);
    }

    void TickDrag()
    {
        Win32.POINT p;
        if (!Win32.GetCursorPos(out p)) return;

        // ⚠️ 拖动期间**不要**看 PetHitTest.CursorOnPet ——
        //    她的动画在换帧，抓取点那个像素可能突然变透明，
        //    依赖命中结果的话，拖到一半状态就会自己掉出去。
        //    规则：**一旦抓住，就抓到松开为止。**
        //
        // 抓到哪一点，那一点就一直在光标底下 —— 所以是"跟着手走"，
        // 不会跳，也不会因为她被拖到光标正中央而显得别扭。
        DeskWindow.MoveTo(p.x - _grabOffsetX, p.y - _grabOffsetY);
    }

    /// <summary>
    /// Click 状态最长待多久。
    ///
    /// 为什么需要它：如果皮肤**没有** click 动作，GetAction 会兜底回 idle；
    /// 而 idle 是**循环**的 —— PetAnimator.Finished 永远是 false，
    /// 状态机就会**永久卡死在 Click 里**（点一次之后她再也不理你了）。
    /// 有了超时，兜底情况也能自己走出来。
    ///
    /// 这个 bug 特别阴：它只在"皮肤缺动作"时出现 ——
    /// 而我们的占位皮肤**有** click，所以开发期永远碰不到，直到用户装了个简版皮肤。
    /// </summary>
    float ClickTimeout()
    {
        PetAction a = _skin.GetAction("click");
        if (a == null || a.FrameCount == 0) return 0.2f;
        return 1.5f * a.FrameCount / Mathf.Max(1f, a.Fps);
    }
}
