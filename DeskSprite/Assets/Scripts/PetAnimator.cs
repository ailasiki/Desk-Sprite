using UnityEngine;

/// <summary>
/// 帧动画播放器 —— 就是"按时间换 sprite"这一件事。
///
/// 为什么不用 Unity 的 Animator？（《皮肤契约设计.md》§5.1）
///   Animator 需要 **AnimatorController 资产** 和 **AnimationClip 资产**，
///   而这些资产必须在编辑器里存在。我们的图片是**运行时从文件夹读的**，
///   工程里没有任何对应的编辑器资产。
///
/// 所以自己写。全部逻辑就是下面 Update 里那几行。
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class PetAnimator : MonoBehaviour
{
    SpriteRenderer _sr;

    PetAction _action;
    float _timer;
    int _frame;

    /// <summary>这一次播放是否**强制只播一个周期**（<see cref="PlayOnce"/> 设的）。</summary>
    bool _forceNoLoop;

    /// <summary>非循环动作播完了吗？（状态机靠它决定"该回 idle 了"）</summary>
    public bool Finished { get; private set; }

    /// <summary>
    /// **循环动作绕回第 0 帧**的次数（每次绕回 +1）。切动作时归零。
    ///
    /// 为什么需要它：待机池的规则是"最早也要等 idle 播完**一个完整周期**才能切走"
    /// （《皮肤契约设计.md》§5.7）。如果只按"过了多少秒"来判断，就会在一个周期的
    /// **中途**切走 —— 那它就不是"节点"了，而且间隔设成 0 时行为和预期差一截。
    /// 有了这个计数，"一个完整周期"就是**数出来的**，不是**算出来的**。
    /// </summary>
    public int LoopCount { get; private set; }

    public PetAction CurrentAction
    {
        get { return _action; }
    }

    public int CurrentFrame
    {
        get { return _frame; }
    }

    void Awake()
    {
        _sr = GetComponent<SpriteRenderer>();
    }

    /// <summary>切到一个动作。同一个动作再调用不会重头播（除非 forceRestart）。</summary>
    public void Play(PetAction action, bool forceRestart = false)
    {
        if (action == null || action.FrameCount == 0) return;
        if (!forceRestart && ReferenceEquals(action, _action)) return;
        StartAction(action, false);
    }

    /// <summary>
    /// 播**一个周期就停**，不管动作自己声明的 Loop 是什么。**每次都重头播。**
    ///
    /// 待机池要用它：池动作是"挂在 idle 上的一次性分支"（§5.7），播完必须回来。
    /// 而作者完全可能把一个池动作声明成循环的（比如"睡觉"本身想一直呼吸）——
    /// 那也该只播一个周期就回 idle。想睡久一点就**多画几帧**：
    /// **时长属于素材，不属于引擎。**
    /// </summary>
    public void PlayOnce(PetAction action)
    {
        if (action == null || action.FrameCount == 0) return;
        StartAction(action, true);
    }

    void StartAction(PetAction action, bool forceNoLoop)
    {
        _action = action;
        _forceNoLoop = forceNoLoop;
        _frame = 0;
        _timer = 0f;
        Finished = false;
        LoopCount = 0;
        if (_sr != null) _sr.sprite = _action.Frames[0];
    }

    void Update()
    {
        if (_action == null) return;
        if (Finished) return;

        // ⚠️ 这里**故意不**对单帧动作提前 return。
        //    原来写的是 `if (_action.FrameCount <= 1) return;` —— 看起来是省一点无谓的工作，
        //    但那会让**单帧动作的 Finished 永远是 false**：待机池里放一张静态姿势
        //   （单帧是**完全合理**的作者选择）就会让她**永久卡在那个姿势上**。
        //    现在单帧动作也走下面的计时逻辑：一帧的时长就是 1/fps，到点后
        //    循环的绕回、一次性的结束 —— 都没有特例。
        float step = 1f / Mathf.Max(0.01f, _action.Fps);
        _timer += Time.deltaTime;

        // 用 while 而不是 if：某一帧卡了（比如 0.05 秒）不会让动画越播越慢。
        // 而 _timer -= step 把余数留下来，所以播放在任何帧率下都是准的。
        //
        // guard 是为了防止"长时间暂停后恢复"时一次性补几百帧。
        int guard = 0;
        while (_timer >= step && guard < 8)
        {
            _timer -= step;
            guard++;
            _frame++;

            if (_frame >= _action.FrameCount)
            {
                if (_action.Loop && !_forceNoLoop)
                {
                    _frame = 0;
                    LoopCount++;
                }
                else
                {
                    _frame = _action.FrameCount - 1;   // 停在最后一帧
                    Finished = true;
                    break;
                }
            }
        }

        // 实在追不上（比如刚从系统暂停恢复）就放弃追赶，别让 _timer 无限膨胀
        if (_timer > step * 8f) _timer = 0f;

        _sr.sprite = _action.Frames[_frame];
    }
}
