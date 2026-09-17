using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;   // EventSystem / StandaloneInputModule：uGUI 收鼠标事件要有它
using UnityEngine.UI;

/// <summary>
/// 设置面板。
///
/// 【分阶段】
///   S2  面板骨架 + 中文显示           —— 已通
///   S3a 一个按钮，验证"点击能被收到"   —— 已通
///   S3b **全部控件 + 当场生效（不落盘）** —— 本文件现在这里
///   S3c 写回文件 + 重开还在
///
/// S3b 的目标是一句话：**每个控件当场都有效果，但关掉程序就忘。**
/// "生效"和"记得"是两个可以分开验证的问题，所以刻意分开做 ——
/// 合在一起，出问题时分不清是"没生效"还是"没存下来"。
///
/// 【为什么用 uGUI + 系统字体，而不是 TextMeshPro】
/// TMP 要显示中文，得先用 Font Asset Creator 生成中文图集 —— 那是个多分钟的 GUI 流程，
/// 生成出来的资源又大又要进 git（而且字体授权也得想一想）。
/// 系统动态字体（Font.CreateDynamicFontFromOSFont）是**零编辑器步骤**的：
/// 不导入任何资源、不加任何 package、不点任何菜单。代价是字形由系统光栅化，
/// 不走我们的像素风 —— 但设置面板是普通 UI，不是像素画，这个代价可以接受。
///
/// 【为什么 CanvasScaler 用 ConstantPixelSize 且 scaleFactor = 1】
/// 这样 1 个 UI 单位 = 1 个屏幕像素。Unity 是 DPI 感知的，Screen.width 就是物理像素，
/// 所以面板上写 14 就是真的 14 物理像素，不受系统缩放（这台机器是 125%）影响，字不会糊。
///
/// ⚠️ 但也正因为是**物理**像素，字号要按"逻辑字号 × 缩放比例"来给：
/// 常见的界面正文是 12~14 逻辑像素，125% 下就等于 15~17.5 物理像素。
/// 如果我按习惯写 fontSize = 12，实际看起来只有 9.6 逻辑像素 —— 会明显偏小。
/// 这就是这里给 17/14 而不是 15/12 的原因。（本机 DPI 缩放 = 125%。）
/// </summary>
public class SettingsPanel : MonoBehaviour
{
    /// <summary>
    /// 面板条高度。
    /// 窗口 540 高，她占底部 264，所以从上往有 276 可用；留 16 空隙就是 260。
    /// 这个数必须和 PetRunner.SettingsWindowHeight 一起看 —— 改一个就要看另一个。
    /// 下面各行的 TOP 常量加起来不能超过它。
    /// </summary>
    public const float PanelHeight = 360f;

    const float PadX = 14f;        // 左右内边距
    const int TitleFontSize = 17;
    const int BodyFontSize = 14;
    const int SmallFontSize = 13;

    /// <summary>
    /// 设置模式的窗口宽度。**必须和 PetRunner.SettingsWindowWidth 一致。**
    ///
    /// 为什么这里要再写一遍：下面有些控件的宽度是按"内容区有多宽"算出来的
    /// （比如输入框 278 = 452 − 74 − 90 − 10）。与其把 480 抄进一堆算式里
    /// （抄错一次就是十几像素的错位），不如只写一遍，然后**在运行时量一次**
    /// 实际宽度对不对（见 Refresh 里的 self check）。
    /// </summary>
    const float ExpectWindowWidth = 480f;

    /// <summary>内容区宽度 = 窗口宽 − 左右内边距。左右两端的控件都用锚点定位，不靠这个数。</summary>
    const float ContentW = ExpectWindowWidth - PadX * 2f;   // 452

    // ── 每一行在面板里的位置（从面板**顶边**往下量，y 越大越靠下）──
    // 集中放在这里，是因为它们必须互相对齐：改一个数字要能一眼看到和邻居的关系。
    // 校验：最后一行底边 = 292 + 32 = 324 ≤ 360 ✓（余下 36 像素给后面的皮肤选择）
    //
    // ⚠️ 这个面板已经长高四次了（260 → 320 → 360）。**下次加控件之前先想清楚**：
    //    单列布局再长下去就要占满屏幕了（640 高的窗口在 1080 屏上已经是 59%）。
    //    "皮肤选择"那一行加上来时，应该改成**两列**，而不是继续往下长。
    const float TitleTop = 12f, TitleH = 28f;
    const float StatusTop = 46f, StatusH = 20f;
    const float MessageTop = 68f, MessageH = 20f;
    const float KeyTop = 96f, KeyH = 32f;
    const float ToggleTop = 134f;   // 这一行没有整行容器：标签/开关/状态字各按自己的高度摆
    const float CoordTop = 168f, CoordH = 32f;
    const float IdleTop = 208f, IdleH = 32f;
    const float ZoomTop = 248f, ZoomH = 32f;

    // ── 底部一排：立刻刷新余额 / ... / 保存 / 关闭设置 ──
    // 两个右边的按钮用**右锚点**（见 PlaceRight），所以宽度是从右边往左算的，
    // 谁在谁左边一目了然，改宽度也不会挤到对面。
    const float BottomButtonGap = 8f;
    const float CloseButtonW = 130f;   // 要放得下"关闭并丢弃"
    const float SaveButtonW = 130f;
    const float SaveRight = PadX + CloseButtonW + BottomButtonGap;   // 保存按钮距右边的距离
    const float BottomTop = 292f, BottomH = 32f;

    // ── API Key 行 ──
    // 左边标签、中间输入框、右边"应用"按钮。按钮用**右锚点**，
    // 所以输入框的宽度必须为它预留出位置 —— 下面这个算式就是那个"预留"，
    // 而不是拍一个 278 出来（哪天窗口变宽，输入框会自己跟着变宽，按钮不动）。
    const float KeyLabelW = 70f;
    const float KeyRowGap = 10f;
    const float KeyFieldX = PadX + KeyLabelW + 4f;
    const float KeyButtonW = 90f;
    const float KeyFieldW = ContentW - KeyLabelW - 4f - KeyButtonW - KeyRowGap;

    // ── 牌子文字坐标行 ──
    // 三项（X / Y / 字号）并成一行：它们本来就是同一件事 —— 那串数字长什么样。
    // 标签缩到 70（"余额坐标"）+ 两个 60 宽的数字框 + "字号" + 一个 50 宽的框 + 还原，
    // 一路排下来 430 ≤ 452 ✓
    const float CoordLabelW = 70f;
    const float CoordTagW = 18f;
    const float CoordFieldW = 60f;
    const float CoordGap = 10f;
    const float CoordTagGap = 4f;
    const float CoordResetW = 60f;
    const float CoordFontLabelW = 28f;     // "字号"
    const float CoordFontFieldW = 50f;

    const float CoordX = PadX + CoordLabelW + CoordGap;
    const float CoordFieldX1 = CoordX + CoordTagW + CoordTagGap;
    const float CoordTagX2 = CoordFieldX1 + CoordFieldW + CoordGap;
    const float CoordFieldX2 = CoordTagX2 + CoordTagW + CoordTagGap;
    const float CoordFontLabelX = CoordFieldX2 + CoordFieldW + CoordGap;
    const float CoordFontFieldX = CoordFontLabelX + CoordFontLabelW + CoordTagGap;
    const float CoordResetX = CoordFontFieldX + CoordFontFieldW + CoordGap;

    // ── 随机待机间隔行 ──
    const float IdleFieldX = PadX + 120f + CoordGap;   // 标签要给"待机动作切换间隔"留够
    const float IdleFieldW = 70f;

    /// <summary>当前实例。PetRunner 靠它开关面板，不用自己去翻层级。</summary>
    public static SettingsPanel Instance;

    Canvas _canvas;
    RectTransform _strip;        // 顶部 260 高的面板条，面板里的东西全部相对它布局
    Text _title;
    Text _statusBalance;
    Text _statusOffset;
    Text _message;               // "刚才发生了什么" —— 每次操作都改它
    InputField _keyField;
    InputField _xField;          // 牌子文字的绝对坐标 x
    InputField _yField;          // 牌子文字的绝对坐标 y（从帧**底边**量）
    InputField _fontField;       // 余额数字的字号倍率（整数倍）
    Button _coordReset;
    InputField _idleField;       // 随机待机间隔（秒）
    Text _idleHint;
    InputField _zoomField;       // 她多大（%）
    Text _zoomHint;
    Button _saveButton;
    Text _saveLabel;
    Text _closeLabel;
    Text _skinName;              // 底部那排显示当前皮肤的名字
    Toggle _hideToggle;
    Toggle _debugToggle;         // 左上角的"调试界面"开关（默认关）
    Text _hideState;             // "开 / 关" —— 光看小方块不够确定，再加一行字
    Font _font;

    // 上一行操作的结果。Refresh() 只读它，不重置 —— 否则每 0.25 秒被刷掉一次。
    string _messageText = "还没操作过";

    // Toggle.isOn 是**程序化赋值也会触发 onValueChanged** 的。
    // 不挡的话：Refresh() 里赋值 → 回调里改配置 → 再 Refresh() → 无限回环（每帧都在转）。
    bool _suppressToggleCallback;

    // 同理：我们给坐标框写 .text 也会触发它的 onValueChanged。见 SyncCoordFields。
    bool _syncingFields;

    // ── "有没有未保存的改动" ──
    // 我们记的是**上次保存时的值**，用"当前值 vs 它"来算脏 —— 而不是用一个 bool 标记。
    // 为什么？bool 标记会把"改成 20 又改回 8"也算脏，用户就会看到一个永远说"有改动"
    // 的面板点了保存还在说"有改动"，然后他就不信这个提示了。
    //
    // ⚠️ _savedKey 里存着 API Key 本身（内存本来就有），但它**绝不进日志、绝不上界面** ——
    //    只用来做相等比较。
    string _savedKey = "";
    bool _savedHide;
    int _savedCoordX, _savedCoordY;
    int _savedFontScale = 1;
    string _savedSkinPath = "";
    int _savedIdleSeconds;
    int _savedZoomPercent;

    /// <summary>面板是不是正显示着。</summary>
    public bool IsVisible { get { return _canvas != null && _canvas.enabled; } }

    // ------------------------------------------------------------------
    // 创建
    // ------------------------------------------------------------------

    /// <summary>建出面板（默认不显示），挂在 <paramref name="parent"/> 下面。</summary>
    public static SettingsPanel Create(Transform parent)
    {
        // ⚠️ 这里必须用 new GameObject(name, typeof(RectTransform))。
        //    不能先 new GameObject() 再 AddComponent<RectTransform>() ——
        //    GameObject 一建出来就带了个普通 Transform，而 Transform 是**没法替换**成
        //    RectTransform 的，AddComponent 会直接失败（或者被 Unity 拒掉）。
        //    UI 节点必须在创建的那一刻就带上 RectTransform。
        var go = new GameObject("SettingsPanel", typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var panel = go.AddComponent<SettingsPanel>();
        panel.Build();
        panel.SetVisible(false);

        Instance = panel;
        return panel;
    }

    void Build()
    {
        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;   // 跟着窗口走，跟摄像机无关
        _canvas.sortingOrder = 100;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        // 没有 GraphicRaycaster，鼠标事件根本不会进 uGUI
        gameObject.AddComponent<GraphicRaycaster>();

        _font = LoadCjkFont();

        // ---- 面板条：顶部 260 高的一条，它自己就是面板内容的坐标系 ----
        //
        // ⚠️ 为什么要多这一层容器？
        //    Canvas 的矩形是**整个窗口**（480×540）。如果直接把按钮锚到"左下角"，
        //    它会跑到窗口底部去 —— 也就是**她站的地方**。
        //    面板实际只占顶部 260 高，所以必须有个"只有 260 高"的父节点，
        //    里面的锚点才是相对面板算的。
        //    这层同时兼任背景板（Image）。
        _strip = NewUiNode("Strip", transform);
        var img = _strip.gameObject.AddComponent<Image>();
        img.color = new Color(0.10f, 0.11f, 0.14f, 0.94f);
        img.raycastTarget = true;      // 挡住鼠标：面板上操作时不该误触到桌宠
        TopStrip(_strip, PanelHeight, 0f, 0f);

        // ---- 标题 + 调试开关 ----
        // 调试开关在**右上角**（用户定的），标题在左。
        // 为什么不做成热键：热键对不知情的用户是"碰运气"，而面板上的勾选框是一个
        // **看得见的选择**，而且**默认关闭**（`PetRunner.ShowPanel = false`）。
        // 符合这个项目一直在用的那条：面向用户的入口和开发者的工具分开。
        _title = MakeText("Title", _strip, TitleFontSize, new Color(0.96f, 0.87f, 0.55f));
        _title.fontStyle = FontStyle.Bold;
        _title.alignment = TextAnchor.MiddleLeft;
        Place((RectTransform)_title.transform, PadX, TitleTop, 340f, TitleH);

        // 右端：勾选框贴右边缘（14..466 里的最右 20 像素），标签在它左边 ——
        // 这样"钩子"就在最右上角。标签用**右锚点**，所以它的右边缘固定在勾选框左边 8 像素。
        _debugToggle = MakeToggleRight("DebugToggle", PadX, TitleTop + 4f, 20f, 20f);
        _debugToggle.onValueChanged.AddListener(OnDebugToggled);

        var dbgLabel = MakeText("DebugLabel", _strip, BodyFontSize, new Color(0.78f, 0.81f, 0.86f));
        dbgLabel.text = "调试界面";
        dbgLabel.alignment = TextAnchor.MiddleRight;
        PlaceRight((RectTransform)dbgLabel.transform, PadX + 28f, TitleTop + 6f, 78f, 16f);

        // ---- 余额 + 文字偏移 ----
        // 左边显示余额（它是"实时"的，会自己变），右边显示牌子上文字的当前偏移。
        _statusBalance = MakeText("StatusBalance", _strip, BodyFontSize, new Color(0.88f, 0.90f, 0.94f));
        _statusBalance.alignment = TextAnchor.MiddleLeft;
        Place((RectTransform)_statusBalance.transform, PadX, StatusTop, 320f, StatusH);

        _statusOffset = MakeText("StatusOffset", _strip, SmallFontSize, new Color(0.66f, 0.70f, 0.78f));
        _statusOffset.alignment = TextAnchor.MiddleRight;
        PlaceRight((RectTransform)_statusOffset.transform, PadX, StatusTop, 132f, StatusH);

        // ---- 操作结果消息 ----
        _message = MakeText("Message", _strip, BodyFontSize, new Color(0.62f, 0.86f, 0.66f));
        _message.alignment = TextAnchor.MiddleLeft;
        TopStrip((RectTransform)_message.transform, MessageH, PadX, MessageTop);

        // ---- API Key 行 ----
        // 注意 x 是**相对面板左边缘**（不是相对内容区），所以这里要自己加 PadX。
        MakeLabel("KeyLabel", "API Key", PadX, KeyTop + 8f, KeyLabelW, 16f);

        _keyField = MakeInputField("KeyField", KeyFieldX, KeyTop, KeyFieldW, KeyH,
                                   "粘贴你的 DeepSeek Key（sk- 开头）",
                                   InputField.ContentType.Password);

        var applyBtn = MakeButtonRight("ApplyKeyButton", "应用", PadX, KeyTop, KeyButtonW, KeyH);
        applyBtn.onClick.AddListener(OnApplyKeyClicked);

        // 回车 / 点到别处也算"填完了"。因为它是幂等的（空输入直接忽略），
        // 所以"点应用"和"点到别处"重复触发不会出问题。
        _keyField.onEndEdit.AddListener(OnKeyFieldEndEdit);

        // ---- 显示余额开关 ----
        MakeLabel("HideLabel", "显示DeepSeek余额", PadX, ToggleTop + 6f, 152f, 16f);
        _hideToggle = MakeToggle("HideToggle", PadX + 158f, ToggleTop + 3f, 22f, 22f);
        _hideToggle.onValueChanged.AddListener(OnHideToggled);

        _hideState = MakeLabel("HideState", "开", PadX + 188f, ToggleTop + 6f, 240f, 16f);
        _hideState.color = new Color(0.88f, 0.90f, 0.94f);

        // ---- 余额数字：位置 + 字号 ----
        // 三项并成一行（它们本来就是同一件事：那串数字长什么样）。
        //
        // 用**绝对坐标**输入，不用方向键一格一格挪：
        //   ① 日志打的就是绝对值，用户要直接抄进皮肤文件
        //   ② "偏移量"是我们为了不改皮肤文件引入的中间量，属于实现细节
        // 而**字号是单独的**（不跟着素材走）—— 用户按"看着顺眼"调就行，
        // 不用去想"这张图的牌子有多大"。面板是边打字边变的，所以能直接看到效果。
        MakeLabel("CoordLabel", "余额坐标", PadX, CoordTop + 8f, CoordLabelW, 16f);

        MakeLabel("CoordTagX", "X", CoordX, CoordTop + 7f, CoordTagW, 16f);
        _xField = MakeInputField("CoordXField", CoordFieldX1, CoordTop, CoordFieldW, CoordH,
                                 "0", InputField.ContentType.IntegerNumber);
        _xField.characterLimit = 6;      // 坐标不会有更长的；不限长的话 IntegerNumber 允许一直打下去
        _xField.onValueChanged.AddListener(OnCoordinateChanged);
        _xField.onEndEdit.AddListener(OnCoordinateFieldEndEdit);

        MakeLabel("CoordTagY", "Y", CoordTagX2, CoordTop + 7f, CoordTagW, 16f);
        _yField = MakeInputField("CoordYField", CoordFieldX2, CoordTop, CoordFieldW, CoordH,
                                 "0", InputField.ContentType.IntegerNumber);
        _yField.characterLimit = 6;
        _yField.onValueChanged.AddListener(OnCoordinateChanged);
        _yField.onEndEdit.AddListener(OnCoordinateFieldEndEdit);

        MakeLabel("CoordFontLabel", "字号", CoordFontLabelX, CoordTop + 7f, CoordFontLabelW, 16f);
        _fontField = MakeInputField("CoordFontField", CoordFontFieldX, CoordTop, CoordFontFieldW, CoordH,
                                    "1", InputField.ContentType.IntegerNumber);
        _fontField.characterLimit = 2;   // 字号是整数倍，两位足够（×99 已经很大了）
        _fontField.onValueChanged.AddListener(OnCoordinateChanged);
        _fontField.onEndEdit.AddListener(OnCoordinateFieldEndEdit);

        var resetBtn = MakeButton("CoordReset", "还原", CoordResetX, CoordTop, CoordResetW, CoordH);
        resetBtn.onClick.AddListener(OnResetOffsetClicked);
        _coordReset = resetBtn;

        // ---- 随机待机间隔 ----
        // 一个数字控制"她多久换一次姿势"。0 = 不等待（动作一个接一个）。
        //
        // ⚠️ 面板上专门写一句"0 = 不等待"，不是装饰：这个值**手改 json 很容易踩** ——
        //    我们真的见过一次（填成 0 之后她根本停不下来，还以为是功能坏了）。
        //    含义写在界面上的成本是一行字，写不在就得靠用户去读文档。
        MakeLabel("IdleLabel", "待机动作切换间隔", PadX, IdleTop + 8f, 120f, 16f);

        _idleField = MakeInputField("IdleField", IdleFieldX, IdleTop, IdleFieldW, IdleH,
                                    "90", InputField.ContentType.IntegerNumber);
        _idleField.characterLimit = 6;
        _idleField.onValueChanged.AddListener(OnIdleFieldChanged);
        _idleField.onEndEdit.AddListener(OnIdleFieldEndEdit);

        _idleHint = MakeLabel("IdleHint", "秒（0 = 不等待）", IdleFieldX + IdleFieldW + 12f,
                              IdleTop + 8f, 200f, 16f);
        _idleHint.color = new Color(0.66f, 0.70f, 0.78f);

        // ---- 她多大（缩放）----
        // 这一行存在的理由很直接：**用户不该为了改一个数字去手改 JSON**。
        // 我们刚吃过一次亏 —— config.json 少一个逗号，整个文件读不出来，
        // 所有设置退回默认值、Key 看起来"丢了"，而日志当时还说错了原因。
        // 面板能改的东西越多，用户碰那个文件的理由就越少。
        MakeLabel("ZoomLabel", "角色大小", PadX, ZoomTop + 8f, 120f, 16f);

        _zoomField = MakeInputField("ZoomField", IdleFieldX, ZoomTop, IdleFieldW, ZoomH,
                                    "100", InputField.ContentType.IntegerNumber);
        _zoomField.characterLimit = 4;      // 100~400 最多三位，留一位余量
        _zoomField.onValueChanged.AddListener(OnZoomFieldChanged);
        _zoomField.onEndEdit.AddListener(OnZoomFieldEndEdit);

        _zoomHint = MakeLabel("ZoomHint", "%（100 = 素材原本大小）", IdleFieldX + IdleFieldW + 12f,
                              ZoomTop + 8f, 240f, 16f);
        _zoomHint.color = new Color(0.66f, 0.70f, 0.78f);

        // ---- 底部一排：皮肤 / 保存 / 关闭 ----
        // 这里原来有一个"立刻刷新余额"按钮，**去掉了**：刷新的入口现在只有一个 ——
        // **点她**（见 DeepSeekBalance.RefreshNow）。
        // 空出来的位置正好放"换皮肤"：它和保存/关闭同属"总体操作"，放一排读起来也顺。

        MakeLabel("SkinLabel", "皮肤", PadX, BottomTop + 8f, 28f, 16f);

        _skinName = MakeText("SkinName", _strip, BodyFontSize, new Color(0.88f, 0.90f, 0.94f));
        _skinName.alignment = TextAnchor.MiddleLeft;
        Place((RectTransform)_skinName.transform, PadX + 34f, BottomTop, 76f, BottomH);

        var pickBtn = MakeButton("PickSkinButton", "选择…", PadX + 116f, BottomTop, 62f, BottomH);
        pickBtn.onClick.AddListener(OnPickSkinClicked);

        _saveButton = MakeButtonRight("SaveButton", "保存", SaveRight, BottomTop, SaveButtonW, BottomH);
        _saveButton.onClick.AddListener(OnSaveClicked);
        _saveLabel = _saveButton.GetComponentInChildren<Text>();

        var closeBtn = MakeButtonRight("CloseButton", "关闭设置", PadX, BottomTop, CloseButtonW, BottomH);
        closeBtn.onClick.AddListener(OnCloseClicked);
        _closeLabel = closeBtn.GetComponentInChildren<Text>();

        // uGUI 收鼠标事件要有 EventSystem。场景里本来没有（我们的场景是空的），所以自己建一个。
        EnsureEventSystem();

        // 记账起点：此刻内存里的值就是"磁盘上那份"（启动时刚读进来的、外加 skin.user.json 的覆盖），
        // 所以现在算"没有未保存的改动"。
        SnapshotSaved();

        Debug.Log("[DeskSprite] SettingsPanel 建好了：字体=" + (_font != null ? _font.name : "(null)")
                + " 面板高=" + PanelHeight + " 窗口=" + Screen.width + "x" + Screen.height);
    }

    /// <summary>
    /// 保证场景里有一个 EventSystem。
    ///
    /// 为什么"保证"而不是"直接建"：EventSystem 是**全局唯一**的，
    /// 场景里出现第二个 Unity 会警告并且行为不确定（"Multiple EventSystems in scene"）。
    /// 现在场景是空的，但我不想给以后埋一个"改场景就坏"的雷。
    ///
    /// 另外 StandaloneInputModule 用的是**旧版** Input Manager 的
    /// Horizontal/Vertical/Submit/Cancel 轴 —— 这些是 Unity 默认就有的，
    /// 所以不需要额外配置（工程里 activeInputHandler = 0，就是旧版）。
    /// </summary>
    static void EnsureEventSystem()
    {
        if (Object.FindObjectOfType<EventSystem>() != null) return;

        var go = new GameObject("EventSystem");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
        Debug.Log("[DeskSprite] 建了 EventSystem（uGUI 点击要靠它）");
    }

    // ------------------------------------------------------------------
    // 开关 / 刷新
    // ------------------------------------------------------------------

    /// <summary>
    /// 显示或隐藏。
    ///
    /// 隐藏时如果还有未保存的改动，就**退回上次保存的样子** —— 见 <see cref="RevertToSaved"/>。
    /// 所以"关闭"在这套设计里就是"取消"，不需要额外的关闭拦截。
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_canvas == null) return;

        if (visible)
        {
            Refresh();
        }
        else
        {
            // 关闭时把上一次的提示清掉 —— 下次打开不该还挂着"这套皮肤用不了"之类
            // 早就过期的消息（用户会以为又发生了一次）。
            //
            // ⚠️ 清在**前面**：下面 RevertToSaved 如果跑了，它会写入
            //    "上次退出时没有保存，已退回改动前的样子" —— 那条是**特意留给下次打开看的**，
            //    不能被这一句清掉。顺序在这里是有意义的。
            _messageText = "";

            if (IsDirty()) RevertToSaved();
        }

        _canvas.enabled = visible;
    }

    /// <summary>
    /// 把内存里的设置**退回上次保存的样子**（关闭面板且没按保存时调）。
    ///
    /// 【为什么必须这么做，而不是"留着不存"】
    /// 面板里所有改动都是**当场生效**的（那是为了让你边调边看效果）。
    /// 如果关掉面板就只是"不写文件"，那就留下一个悬在中间的状态：
    /// **这次运行里它是新的，重开又是旧的** —— 用户没法预测自己看到的是哪一份。
    /// 要么存下来（点保存），要么退回去（关面板），不留中间态。
    ///
    /// 【"改动前的样子"= 上次保存的状态，不是"打开面板前的状态"】
    /// 如果你这次打开面板先保存了一次、又接着调了几下、然后关掉，
    /// 那么退回的是**那次保存的**样子 —— 因为那才是"最后确定的"。
    /// </summary>
    void RevertToSaved()
    {
        // 先判断"要不要惊动余额那边"：只有 Key 或开关真的变了才需要。
        // 不然光把牌子文字挪回去也会顺带打断一次正在进行的余额请求 —— 白费一次网络往返，
        // 而且会让"正在查询…"莫名其妙地重来。
        bool balanceChanged = (PetConfig.ApiKey ?? "") != _savedKey
                           || PetConfig.HideBalance != _savedHide;

        // Key 要用"只改生效值、不碰 Data"的那个版本：见 RestoreEffectiveApiKey 的注释
        // （要退回的 Key 可能来自环境变量，不能顺手写进将来要落盘的那份数据里）。
        PetConfig.RestoreEffectiveApiKey(_savedKey);
        PetConfig.SetHideBalanceInMemory(_savedHide);
        PetConfig.SetIdleSwitchSecondsInMemory(_savedIdleSeconds);
        PetConfig.SetZoomPercentInMemory(_savedZoomPercent);
        PetConfig.SetSkinPathInMemory(_savedSkinPath);
        PetSignText.SetCurrent(_savedCoordX, _savedCoordY);
        PetSignText.SetFontScale(_savedFontScale);

        // 光改数据不够：Key 和开关的**效果**挂在这些状态上（比如余额要不要查、牌子要不要空），
        // 得让余额那边重新评估一次，否则界面上会留着旧效果。
        if (balanceChanged) DeepSeekBalance.Reevaluate("退出设置（放弃未保存的改动）");

        // 面板马上就藏起来了，这条消息是留给**下次打开**看的 ——
        // 不然用户下次打开只会发现"位置怎么回去了"，而不知道为什么。
        _messageText = "上次退出时没有保存，已退回改动前的样子";

        Debug.Log(string.Format(
            "[DeskSprite] 退出设置：未保存的改动已退回（牌子文字 ({0}, {1})；key 长度 {2}；hideBalance={3}）",
            _savedCoordX, _savedCoordY, (_savedKey ?? "").Length, _savedHide));
    }

    /// <summary>把当前真实数据填进面板。数据源变了就调它。</summary>
    public void Refresh()
    {
        if (_title == null || _statusBalance == null) return;

        RunLayoutSelfCheckOnce();

        SetTextChecked(_title, "灵伴 · 设置");
        SetTextChecked(_statusBalance, "余额：" + BalanceLineZh());

        // 显示的是**绝对坐标**（= 日志里 textArea 的前两个数），不是偏移量。
        // 面板编辑的、日志打的、skin.json 里写的，三处是同一个数 —— 用户不用心算。
        //
        // ⚠️ 皮肤没声明 textArea 时，PetSignText.Init 根本不会跑，
        //    CurrentX/CurrentY 就一直是 0 —— 那是**假数据**，会让人以为"能调但调了没反应"。
        //    所以这种情况直接说"没有文字区"并把控件禁掉。
        bool hasTextArea = SkinLoader.Current != null && SkinLoader.Current.HasTextArea;

        SetTextChecked(_statusOffset, hasTextArea
            ? "文字坐标 (" + PetSignText.CurrentX + ", " + PetSignText.CurrentY + ")"
            : "（无文字区）");
        SetTextChecked(_message, _messageText);

        if (_xField != null)
        {
            _xField.interactable = hasTextArea;
            _yField.interactable = hasTextArea;
            _coordReset.interactable = hasTextArea;
        }

        // 坐标/间隔的输入框跟着真实值走（正在打字时不覆盖，见各自的 Sync 方法）
        SyncCoordFields(false);
        SyncIdleField(false);
        SyncZoomField(false);

        // ── 开关：把配置的状态**画**出来 ──
        // 必须挡一下 onValueChanged：程序化赋值也会触发它，
        // 而回调里又会改配置 + 再 Refresh() —— 不挡就是无限回环。
        if (_hideToggle != null)
        {
            _suppressToggleCallback = true;
            _hideToggle.isOn = PetConfig.HideBalance;
            _debugToggle.isOn = PetRunner.ShowPanel;
            _suppressToggleCallback = false;
        }
        if (_hideState != null)
        {
            bool hide = PetConfig.HideBalance;
            SetTextChecked(_hideState, hide ? "关（不查余额）" : "开");
            _hideState.color = hide
                ? new Color(0.72f, 0.62f, 0.62f)
                : new Color(0.62f, 0.86f, 0.66f);
        }

        // ── 保存 / 关闭两个按钮：把"有没有未保存的改动"**画**出来 ──
        // 这一条不是装饰。用户选了"显式保存"，那"忘了按保存"就是这一类设计**必然**的失败模式；
        // 而它的后果是"调了半天、关掉、白调，而且不知道为什么"。
        // 所以状态必须一直挂在脸上 —— 而且**关闭按钮也要跟着改字**：
        // 既然关面板 = 丢弃改动，那这个后果必须写在按钮上，而不是让用户去猜。
        if (_saveButton != null && _saveLabel != null)
        {
            bool dirty = IsDirty();
            SetTextChecked(_saveLabel, dirty ? "保存（有改动）" : "保存");
            SetTextChecked(_closeLabel, dirty ? "关闭并丢弃" : "关闭设置");
            SetTextChecked(_skinName, CurrentSkinDisplay());

            var img = _saveButton.image;
            if (img != null)
            {
                img.color = dirty
                    ? new Color(0.42f, 0.32f, 0.14f, 1f)    // 暖色：在喊"来按我"
                    : new Color(0.19f, 0.20f, 0.24f, 1f);   // 暗色：没什么可做的
            }
        }

        if (_keyField != null)
        {
            // 占位提示要反映"现在到底有没有 Key" —— 这是用户最需要知道的一件事，
            // 而密码框里是看不出有没有值的（两者的显示都是空的）。
            _keyField.placeholder.GetComponent<Text>().text =
                string.IsNullOrEmpty(PetConfig.ApiKey)
                    ? "粘贴你的 DeepSeek Key（sk- 开头）"
                    : "已设置（重新输入可覆盖）";
        }
    }

    /// <summary>
    /// 只在内容真的变了才赋值，顺手量一下有没有超出框宽。
    ///
    /// 为什么要"变了才赋"：赋值会触发 uGUI 重排版，而 Refresh 每秒跑 4 次。
    /// 顺手量宽度是因为 Overflow 模式下"字比框宽"**不报错**，只画到框外面 ——
    /// 看起来像布局串了，其实是文案太长（这个坑我们在余额那行踩过一次）。
    /// </summary>
    static void SetTextChecked(Text t, string s)
    {
        if (t == null || t.text == s) return;
        t.text = s;
        WarnIfTextTooWide(t, s);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    bool _layoutChecked;
    int _layoutStreak;

    /// <summary>
    /// 布局自检：量一次面板实际宽度对不对，再算一遍有没有控件互相压住。
    ///
    /// 为什么要机械化：这个面板是**手写坐标**摆出来的（15 个控件），而手写坐标最典型的
    /// 两种错就是"右边少算了内边距"和"两个控件坐标写重了" —— 两种都**看起来像没对齐**，
    /// 不像算错，所以靠眼睛扫数字很难查。写这个文件的时候我就真的把一处 x 写成了 0
    /// （应是内容区左边 14），是自己回头读才发现的。让程序算一遍比人眼可靠。
    ///
    /// ⚠️ 时机：窗口放大是**异步**的。SetVisible(true) 时 Screen.width 可能还是旧的 192，
    ///    那一刻量出来的宽度当然不对 —— 但这**不是**布局错了。
    ///    所以要求"连续两次看到窗口已经是设置模式该有的宽度"才量。
    ///    宁可晚一点、稳一点，也不要误报：**误报会让人不再相信日志，那比不报更糟。**
    /// </summary>
    void RunLayoutSelfCheckOnce()
    {
        if (_layoutChecked || _strip == null) return;

        if (Screen.width != (int)ExpectWindowWidth)
        {
            _layoutStreak = 0;               // 窗口还没到位，重新数
            return;
        }

        if (++_layoutStreak < 2) return;
        if (_strip.rect.width <= 0f) return; // 布局还没跑过

        _layoutChecked = true;
        CheckPanelWidth();
        CheckNoOverlap();
        CheckTextFits();
    }

    /// <summary>
    /// 每个 Text 的实际需要宽度不能超过它自己的框。
    ///
    /// 为什么这个也要查：面板里的 Text 都用 <c>Overflow</c>（因为换行是我们自己排的），
    /// 而 Overflow 遇到"字比框宽"**不会报错**，直接画到框外面去 ——
    /// 症状是"这行字盖住了旁边那个控件"，看起来像布局串了，其实是文案太长。
    /// 这个坑我在写 <see cref="BalanceLineZh"/> 时就踩到过一次（NoKey 那条 30 个字）。
    ///
    /// ⚠️ 这里要把**按钮上的文字**也算进去（用 GetComponentsInChildren 递归），
    ///    不能只看直接子节点 —— 按钮上的字是最容易改的（"保存" → "保存（有改动）"），
    ///    也正是最容易悄悄超出按钮宽度的地方。
    ///    **但输入框里的文字要跳过**：那是 InputField 自己在管的，
    ///    内容可以比框长（它会滚动/裁剪），拿它来报"超宽"就是假警报。
    /// </summary>
    void CheckTextFits()
    {
        Text[] all = GetComponentsInChildren<Text>(true);
        int over = 0;
        int checkedCount = 0;

        for (int i = 0; i < all.Length; i++)
        {
            Text t = all[i];

            // 输入框（含它的占位文字）不归我们管宽度
            if (t.GetComponentInParent<InputField>() != null) continue;

            checkedCount++;
            if (WarnIfTextTooWide(t, t.text)) over++;
        }

        if (over == 0) Debug.Log("[DeskSprite] 文字宽度 self check 通过（量了 " + checkedCount + " 处）");
        else Debug.LogWarning("[DeskSprite] 有 " + over + " 处文字超出框宽（见上面的 warning）");
    }

    /// <summary>文字比框宽就报出来。返回是否超了。</summary>
    static bool WarnIfTextTooWide(Text t, string content)
    {
        if (t == null || string.IsNullOrEmpty(content)) return false;

        float need = t.preferredWidth;
        float have = t.rectTransform.rect.width;
        if (need <= have + 0.5f) return false;

        Debug.LogWarning(string.Format(
            "[DeskSprite] 面板文字超出框宽：\"{0}\" 需要 {1:F0}px，而框只有 {2:F0}px",
            content, need, have));
        return true;
    }

    void CheckPanelWidth()
    {
        float w = _strip.rect.width;
        if (Mathf.Abs(w - ExpectWindowWidth) > 0.5f)
        {
            Debug.LogWarning(string.Format(
                "[DeskSprite] 面板实际宽度 {0}，但布局常量按 {1} 写的 —— "
                + "输入框/按钮的宽度可能要跟着改（SettingsPanel.ExpectWindowWidth）",
                w, ExpectWindowWidth));
        }
        else
        {
            Debug.Log("[DeskSprite] 面板宽度 self check 通过：" + w);
        }
    }

    /// <summary>
    /// 面板里**同一层**的控件不该互相压住。
    ///
    /// 只比 _strip 的直接子节点：按钮上的字、开关里的对勾、输入框里的文字都是**故意**
    /// 和父节点重叠的，把它们算进来就会满屏假警报。
    /// </summary>
    void CheckNoOverlap()
    {
        var items = new List<RectTransform>();
        for (int i = 0; i < _strip.childCount; i++)
        {
            var rt = _strip.GetChild(i) as RectTransform;
            if (rt != null) items.Add(rt);
        }

        int bad = 0;
        for (int i = 0; i < items.Count; i++)
        {
            for (int j = i + 1; j < items.Count; j++)
            {
                if (!Overlaps(items[i], items[j])) continue;
                bad++;
                Debug.LogWarning("[DeskSprite] 布局重叠：" + items[i].name + " 与 " + items[j].name);
            }
        }

        if (bad == 0)
            Debug.Log("[DeskSprite] 布局 self check 通过：" + items.Count + " 个控件互不重叠");
        else
            Debug.LogWarning("[DeskSprite] 布局 self check 发现 " + bad + " 处重叠（见上面的 warning）");
    }

    /// <summary>两个控件在屏幕上的实际矩形是否真的相交。留 0.5 像素容差 —— "紧贴"不算重叠。</summary>
    static bool Overlaps(RectTransform a, RectTransform b)
    {
        Rect ra = WorldRect(a);
        Rect rb = WorldRect(b);
        return ra.xMin < rb.xMax - 0.5f && rb.xMin < ra.xMax - 0.5f
            && ra.yMin < rb.yMax - 0.5f && rb.yMin < ra.yMax - 0.5f;
    }

    static Rect WorldRect(RectTransform rt)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        return new Rect(c[0].x, c[0].y, c[2].x - c[0].x, c[2].y - c[0].y);
    }

    // ------------------------------------------------------------------
    // 控件的事件
    // ------------------------------------------------------------------

    /// <summary>
    /// "选择…"：弹出系统的文件夹选择框，让用户挑一套皮肤。
    ///
    /// **当场验证、当场生效**（用户定的）：
    ///   · 先**试加载一遍** —— 不合格就当场把"哪里不对"说在面板上，
    ///     而不是等到重启之后她不见了，用户还不知道为什么
    ///   · 合格就把它交给司机，下一帧就换成新样子（**不用重开程序**）
    ///
    /// 注意选的是**一套皮肤**的目录（直接装着 `skin.json` 和 `idle/` 的那个），
    /// 不是它的上一层 —— 选错了日志里会提示"里面那个看起来对"。
    /// 而且**换了文件夹不等于换了规矩**：里面的结构和命名仍然必须守约定。
    /// </summary>
    void OnPickSkinClicked()
    {
        string err;
        string path = ShellOpen.PickFolder("选择一套皮肤（里面直接有 skin.json 和 idle/ 的那个文件夹）", out err);

        if (!string.IsNullOrEmpty(err))
        {
            SetMessage("打不开选择框：" + Head(err, 24));
            Refresh();
            return;
        }
        if (string.IsNullOrEmpty(path))
        {
            SetMessage("没有选择（已取消）");
            Refresh();
            return;
        }

        // 试加载。这一段**只为了验证** —— 通过的话，这份 skin 直接交给司机用，
        // 不会再按路径读第二遍（见 PetRunner.RequestSkinReload 的注释）。
        PetSkin skin = SkinLoader.Load(path);
        if (skin == null)
        {
            // 不合格：面板上给一句话（第一行 + 截断），完整原因进日志。
            // Diagnostics 可能有好几行，面板一行放不下 —— 但**必须给出可读的原因**，
            // 否则用户只会看到"点了没反应"。
            string why = FirstLine(SkinLoader.Diagnostics);
            Debug.LogWarning("[DeskSprite] 选中的文件夹不是一套可用的皮肤：\n"
                           + path + "\n" + SkinLoader.Diagnostics);
            SetMessage("这套皮肤用不了：" + Head(why, 22) + "（详见日志）");
            Refresh();
            return;
        }

        PetConfig.SetSkinPathInMemory(path);
        PetRunner.RequestSkinReload(skin, path);
        SetMessage("已换皮肤：" + System.IO.Path.GetFileName(path));
        Refresh();
    }

    /// <summary>多行文本的第一行（诊断信息常常是多行的，而面板只有一行）。</summary>
    static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(没给原因)";
        int i = s.IndexOf('\n');
        return i < 0 ? s : s.Substring(0, i);
    }

    /// <summary>
    /// 底部那排显示的皮肤名 —— **显示真正加载的那一套**，不是你最后一次选的那一套。
    ///
    /// ⚠️ 这里踩过一次：原来显示的是 `PetConfig.SkinPath`（你选的路径）。
    ///    于是选了一个**用不了**的文件夹之后，面板明明说"这套皮肤用不了"，
    ///    旁边却还挂着那个文件夹的名字 —— 看起来像"其实换上了"，
    ///    用户会以为是自己看错了。**显示的必须是真相，不是意图。**
    /// </summary>
    static string CurrentSkinDisplay()
    {
        string name = (SkinLoader.Current != null) ? SkinLoader.Current.FolderName : "";
        if (string.IsNullOrEmpty(name)) name = "(无)";
        return Head(name, 6);      // 太长会压到旁边的按钮，截断
    }

    /// <summary>"应用"按钮：把输入框里的 Key 用起来（**只在本次运行**）。</summary>
    void OnApplyKeyClicked()
    {
        ApplyKeyFromField("按钮");
    }

    /// <summary>回车 / 点到别处：也算填完了。</summary>
    void OnKeyFieldEndEdit(string _)
    {
        ApplyKeyFromField("回车/失焦");
    }

    void ApplyKeyFromField(string how)
    {
        if (_keyField == null) return;

        // ⚠️ 密码模式下 InputField.text 返回的是**真实的 Key**，不是界面上那些圆点。
        //    （圆点只是 textComponent 上显示的东西，m_Text 里存的还是原文。）
        //    写反了的话会拿一串 "*" 去当密钥，然后得到一个莫名其妙的 401。
        string typed = _keyField.text ?? "";
        if (typed.Trim().Length == 0)
        {
            // 空输入**什么都不做** —— 包括不清掉已有的 Key。
            // "清空输入框"不该等于"删掉密钥"，那是两件事。
            return;
        }

        PetConfig.SetApiKeyInMemory(typed);
        DeepSeekBalance.Reevaluate("设置面板/" + how);

        // ⚠️ 日志里只记**长度**，绝不记内容。密钥进了日志就等于泄露了。
        SetMessage("已应用新 Key（" + typed.Trim().Length + " 字符，本次运行有效）");
        Refresh();
    }

    void OnHideToggled(bool on)
    {
        if (_suppressToggleCallback) return;   // 这是 Refresh() 画出来的，不是用户点的

        PetConfig.SetHideBalanceInMemory(on);
        DeepSeekBalance.Reevaluate("设置面板/开关");
        SetMessage(on ? "已隐藏余额（本次运行）" : "已显示余额（本次运行）");
        Refresh();
    }

    /// <summary>
    /// 左上角的"调试界面"开关。
    ///
    /// **不进配置文件** —— 它是开发工具，重启就该回到默认关闭 ✓
    ///（"默认关闭"如果还要记进文件，那就不是默认了）。
    /// </summary>
    void OnDebugToggled(bool on)
    {
        if (_suppressToggleCallback) return;

        PetRunner.ShowPanel = on;
        Debug.Log("[DeskSprite] 调试界面 -> " + (on ? "开" : "关"));
        Refresh();
    }

    /// <summary>
    /// 坐标框里的内容**每变一次**就调（敲一个字符就一次）—— 让用户边打边看文字挪。
    ///
    /// 三条刻意的规则：
    ///   · **空**、或者只有个负号 "-" → 什么都不做。打字中途必然经过这些状态，
    ///     那既不是"想把坐标设成 0"，也不是"打错了"。
    ///   · 解析不出来 → 也什么都不做，而且**不报错**。
    ///     真正的报错留给 onEndEdit：那时候才说明"他确实是这么填的"。
    ///     中途一直弹"必须是整数"会变成噪声，用户就学会无视提示了。
    ///   · 我们自己写 .text 时不要响应（_syncingFields），否则"写值 → 回调 → 再写值"会绕起来。
    /// </summary>
    void OnCoordinateChanged(string _)
    {
        if (_syncingFields || _xField == null || _yField == null) return;

        int x, y;
        if (!TryParseBoth(out x, out y)) return;    // 打字中途的残缺状态，静默忽略

        PetSignText.SetCurrent(x, y);

        // 字号单独解析：它可以和坐标不同步（用户可能只改字号）。
        // 解析不了就保持现状，不报错 —— 理由和坐标一样（打字中途的残缺）。
        int scale;
        if (TryParseFont(out scale)) PetSignText.SetFontScale(scale);

        // 消息**直接写字段、不走 SetMessage** —— SetMessage 会打日志，
        // 而这里每敲一个字符就一次，日志会被刷得没法看。
        // 一次编辑真正结束（onEndEdit）时才用 SetMessage 打一行。
        _messageText = "余额数字 -> 中心 " + x + "，底边 " + y
                     + "，字号 ×" + PetSignText.FontScale;
        Refresh();
    }

    /// <summary>
    /// 坐标输入框填完了（回车 / 点到别处）。
    ///
    /// 实时那条路（onValueChanged）已经把值应用过了，这里只负责**收尾**：
    /// 把显示规范化（去掉多余空格、把 "007" 变成 "7"）、打一行日志、并在
    /// 内容非法时给出唯一一次提示。
    ///
    /// 两条刻意的设计：
    ///   · **空输入 = 不表态**：把显示拉回真实值，什么都不改。
    ///     用户清空一个框子，多半是想重打，而不是"把坐标设成 0" ——
    ///     悄悄改成 0 的话文字会突然跳到帧左下角，还很难理解为什么。
    ///   · 解析失败就说清楚。IntegerNumber 只是**输入过滤**，粘贴能绕过一部分校验，
    ///     所以不能假设进来的一定是整数。
    /// </summary>
    void OnCoordinateFieldEndEdit(string _)
    {
        if (_xField == null || _yField == null) return;

        int x, y;
        bool coordOk = TryParseBoth(out x, out y);

        int scale;
        bool fontOk = TryParseFont(out scale);

        if (!coordOk && !fontOk)
        {
            // 两个都没解析出来：非空才报错（空 = 不表态）
            if (NonEmpty(_xField) || NonEmpty(_yField) || NonEmpty(_fontField))
                SetMessage("只能填整数，已还原");

            SyncCoordFields(true);
            Refresh();
            return;
        }

        if (coordOk) PetSignText.SetCurrent(x, y);
        if (fontOk) PetSignText.SetFontScale(scale);

        SetMessage("余额数字 -> 中心 " + PetSignText.CurrentX + "，底边 " + PetSignText.CurrentY
                 + "，字号 ×" + PetSignText.FontScale);
        PetSignText.LogArea();      // 就是用户可以抄进皮肤文件的那一行
        SyncCoordFields(true);
        Refresh();
    }

    static bool NonEmpty(InputField f)
    {
        return f != null && (f.text ?? "").Trim().Length > 0;
    }

    /// <summary>两个坐标框都解析成整数才算成功；任一为空或残缺就返回 false。</summary>
    bool TryParseBoth(out int x, out int y)
    {
        x = 0;
        y = 0;
        string sx = (_xField.text ?? "").Trim();
        string sy = (_yField.text ?? "").Trim();
        if (sx.Length == 0 || sy.Length == 0) return false;
        return int.TryParse(sx, out x) && int.TryParse(sy, out y);
    }

    /// <summary>字号框：空或解析不出来都返回 false（意思是"不改变现状"）。</summary>
    bool TryParseFont(out int scale)
    {
        scale = 0;
        if (_fontField == null) return false;
        string s = (_fontField.text ?? "").Trim();
        if (s.Length == 0) return false;
        if (!int.TryParse(s, out scale)) return false;
        if (scale < 1) scale = 1;
        return true;
    }

    void OnResetOffsetClicked()
    {
        PetSignText.ResetOffset();
        PetSignText.SetFontScale(1);
        SetMessage("余额数字已还原成皮肤声明的值");
        SyncCoordFields(true);
        Refresh();
    }

    /// <summary>
    /// 把两个坐标框的内容刷成当前真实值。
    ///
    /// ⚠️ <paramref name="force"/> = false 时，**用户正在里面打字就不覆盖**。
    ///    因为 Refresh 每秒跑 4 次，不挡的话你打一半就被冲掉了 ——
    ///    而且现象是"输入框自己会变"，很难联想到是刷新干的。
    /// </summary>
    void SyncCoordFields(bool force)
    {
        if (_xField == null || _yField == null) return;
        if (!force && (_xField.isFocused || _yField.isFocused || _fontField.isFocused)) return;

        // 我们自己写 .text 会触发 onValueChanged，得挡一下，
        // 不然"写值 → 回调 → 又写值"会绕起来（和 Toggle 那个坑是同一类）。
        _syncingFields = true;
        _xField.text = PetSignText.CurrentX.ToString();
        _yField.text = PetSignText.CurrentY.ToString();
        _fontField.text = PetSignText.FontScale.ToString();
        _syncingFields = false;
    }

    // ------------------------------------------------------------------
    // 随机待机间隔
    // ------------------------------------------------------------------

    /// <summary>边打字边生效（和坐标框同一套做法，见 OnCoordinateChanged 的三条规则）。</summary>
    void OnIdleFieldChanged(string _)
    {
        if (_syncingFields || _idleField == null) return;

        int v;
        if (!int.TryParse((_idleField.text ?? "").Trim(), out v)) return;   // 打字中途的残缺，静默
        if (v < 0) v = 0;

        PetConfig.SetIdleSwitchSecondsInMemory(v);
        _messageText = "待机动作切换间隔 -> " + v + " 秒" + (v == 0 ? "（不等待）" : "");
        Refresh();
    }

    void OnIdleFieldEndEdit(string _)
    {
        if (_idleField == null) return;

        string s = (_idleField.text ?? "").Trim();
        int v;
        if (s.Length == 0 || !int.TryParse(s, out v))
        {
            // 空 = 不表态（用户多半是想重打），非空但解析不了才说一句
            if (s.Length > 0) SetMessage("间隔必须是整数，已还原");
            SyncIdleField(true);
            Refresh();
            return;
        }

        if (v < 0) v = 0;
        PetConfig.SetIdleSwitchSecondsInMemory(v);
        SetMessage(v == 0
            ? "待机动作切换间隔 -> 0 秒（不等待，待机动作一个接一个）"
            : "待机动作切换间隔 -> " + v + " 秒");
        SyncIdleField(true);
        Refresh();
    }

    /// <summary>把间隔框刷成当前真实值。和 SyncCoordFields 一样：正在打字就不覆盖。</summary>
    void SyncIdleField(bool force)
    {
        if (_idleField == null) return;
        if (!force && _idleField.isFocused) return;

        _syncingFields = true;
        _idleField.text = PetConfig.IdleSwitchSeconds.ToString();
        _syncingFields = false;
    }

    // ------------------------------------------------------------------
    // 她多大（缩放）
    // ------------------------------------------------------------------

    /// <summary>
    /// 边打字边生效 —— 你能**直接看到她变大变小** ✓。
    ///
    /// 真正改窗口的是 `PetRunner.ApplyZoomIfChanged`：它每帧比一下这个值，
    /// 变了就把 `skin.Scale` 重算、窗口跟着改（而且锚定她的底边中点，她不会乱跳）。
    /// 面板只负责"把用户的意图写进配置"——**谁的地盘谁管事**。
    /// </summary>
    void OnZoomFieldChanged(string _)
    {
        if (_syncingFields || _zoomField == null) return;

        int v;
        if (!int.TryParse((_zoomField.text ?? "").Trim(), out v)) return;   // 打字中途的残缺，静默
        ApplyZoomInput(v, false);
    }

    void OnZoomFieldEndEdit(string _)
    {
        if (_zoomField == null) return;

        string s = (_zoomField.text ?? "").Trim();
        int v;
        if (s.Length == 0 || !int.TryParse(s, out v))
        {
            if (s.Length > 0) SetMessage("缩放必须是整数，已还原");
            SyncZoomField(true);
            Refresh();
            return;
        }

        ApplyZoomInput(v, true);
        SyncZoomField(true);
        Refresh();
    }

    /// <summary>
    /// 把输入框的数字落到配置上。超范围时**夹住并说明** ——
    /// 不静默改数（用户会发现"我明明打的是 900 怎么变成 400 了"），也不接受（会算出一个荒唐的窗口）。
    /// </summary>
    void ApplyZoomInput(int v, bool log)
    {
        int clamped = v;
        if (clamped < PetConfig.MinZoomPercent) clamped = PetConfig.MinZoomPercent;
        if (clamped > PetConfig.MaxZoomPercent) clamped = PetConfig.MaxZoomPercent;

        PetConfig.SetZoomPercentInMemory(clamped);

        string msg = "角色大小 -> " + clamped + "%";
        if (clamped != v) msg += "（超出 " + PetConfig.MinZoomPercent + "~"
                               + PetConfig.MaxZoomPercent + "%，已夹住）";

        _messageText = msg;
        if (log) Debug.Log("[DeskSprite] 面板：" + msg);
        Refresh();
    }

    /// <summary>把缩放框刷成当前真实值。正在打字就不覆盖。</summary>
    void SyncZoomField(bool force)
    {
        if (_zoomField == null) return;
        if (!force && _zoomField.isFocused) return;

        _syncingFields = true;
        _zoomField.text = PetConfig.ZoomPercent.ToString();
        _syncingFields = false;
    }

    void OnCloseClicked()
    {
        // 只**举旗**，真正关面板由 PetRunner 下一帧做（见 PetRunner.RequestSettingsToggle 的注释）
        PetRunner.RequestSettingsToggle();
    }

    // ------------------------------------------------------------------
    // 保存
    // ------------------------------------------------------------------

    /// <summary>把"当前值"记成"磁盘上的值"。启动时调一次，每次保存成功后按项调。</summary>
    void SnapshotSaved()
    {
        _savedKey = PetConfig.ApiKey ?? "";
        _savedHide = PetConfig.HideBalance;
        _savedCoordX = PetSignText.CurrentX;
        _savedCoordY = PetSignText.CurrentY;
        _savedFontScale = PetSignText.FontScale;
        _savedIdleSeconds = PetConfig.IdleSwitchSeconds;
        _savedZoomPercent = PetConfig.ZoomPercent;
        _savedSkinPath = PetConfig.SkinPath ?? "";
    }

    /// <summary>内存里的值有没有哪一项和磁盘上的不一样。</summary>
    bool IsDirty()
    {
        return (PetConfig.ApiKey ?? "") != _savedKey
            || PetConfig.HideBalance != _savedHide
            || PetConfig.IdleSwitchSeconds != _savedIdleSeconds
            || PetConfig.ZoomPercent != _savedZoomPercent
            || (PetConfig.SkinPath ?? "") != _savedSkinPath
            || PetSignText.CurrentX != _savedCoordX
            || PetSignText.CurrentY != _savedCoordY
            || PetSignText.FontScale != _savedFontScale;
    }

    /// <summary>
    /// "保存"：把内存里的设置**写进文件**，好让重开程序还在。
    ///
    /// 落点分两处，按"这东西是谁的"分：
    ///   · 牌子文字坐标 → `skin.user.json`（和皮肤放一起）—— 那是**用户对这套皮肤**的调整
    ///   · API Key / 是否显示余额 → `config.json` —— 那是**程序级**的配置，和皮肤无关
    ///
    /// 两项**分开处理、分开记账**：一项成功一项失败时，成功的才算"已保存"，
    /// 这样那个"有改动"的提示仍然是诚实的（会说还有东西没存下来）。
    /// 如果失败也一律标成已保存，用户就会以为存好了 —— 那是最糟的结果。
    /// </summary>
    void OnSaveClicked()
    {
        if (!IsDirty())
        {
            SetMessage("没有改动可保存");
            Refresh();
            return;
        }

        var problems = new List<string>();

        // ① 皮肤相关：余额数字的位置和字号
        bool coordDirty = PetSignText.CurrentX != _savedCoordX
                       || PetSignText.CurrentY != _savedCoordY
                       || PetSignText.FontScale != _savedFontScale;
        if (coordDirty)
        {
            string err, pathUsed;
            if (SkinUserOverride.Save(SkinLoader.Current, PetSignText.CurrentX, PetSignText.CurrentY,
                                      PetSignText.FontScale, out err, out pathUsed))
            {
                Debug.Log("[DeskSprite] 已保存余额数字的位置/字号到 " + pathUsed);
                _savedCoordX = PetSignText.CurrentX;
                _savedCoordY = PetSignText.CurrentY;
                _savedFontScale = PetSignText.FontScale;
            }
            else
            {
                Debug.LogWarning("[DeskSprite] 保存余额数字的位置/字号失败：" + err);
                problems.Add(err);
            }
        }

        // ② 程序配置：Key / 是否显示余额 / 待机动作切换间隔 / 角色大小 / 皮肤路径
        bool configDirty = (PetConfig.ApiKey ?? "") != _savedKey
                        || PetConfig.HideBalance != _savedHide
                        || PetConfig.IdleSwitchSeconds != _savedIdleSeconds
                        || PetConfig.ZoomPercent != _savedZoomPercent
                        || (PetConfig.SkinPath ?? "") != _savedSkinPath;
        if (configDirty)
        {
            string err, pathUsed;
            if (PetConfig.Save(out err, out pathUsed))
            {
                // ⚠️ 日志里只说"存了"，**绝不打 Key 本身**。要说区别就只说长度。
                Debug.Log("[DeskSprite] 已保存配置到 " + pathUsed
                        + "（key 长度 " + (PetConfig.ApiKey ?? "").Length + "，内容不记录）");
                _savedKey = PetConfig.ApiKey ?? "";
                _savedHide = PetConfig.HideBalance;
                _savedIdleSeconds = PetConfig.IdleSwitchSeconds;
                _savedZoomPercent = PetConfig.ZoomPercent;
                _savedSkinPath = PetConfig.SkinPath ?? "";
            }
            else
            {
                Debug.LogWarning("[DeskSprite] 保存配置失败：" + err);
                problems.Add(err);
            }
        }

        if (problems.Count == 0)
        {
            SetMessage("已保存");
        }
        else
        {
            // 界面上只放**一行能放下**的摘要，完整原因进日志 ——
            // 失败要看得见，但也不是把一整段 Windows 错误码糊在面板上。
            SetMessage("保存失败：" + Head(problems[0], 20) + "（详见日志）");
        }

        Refresh();
    }

    /// <summary>把一句话截到 n 个字符，后面加省略号。界面空间有限时用。</summary>
    static string Head(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "(没给原因)";
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    void SetMessage(string s)
    {
        // 只记下来 + 打日志。真正的赋值和"有没有超出框宽"的检查在 Refresh -> SetTextChecked 里 ——
        // 那里是唯一给 _message.text 赋值的地方，校验只做一次、不会漏。
        _messageText = s;
        Debug.Log("[DeskSprite] 面板：" + s);
    }

    // ------------------------------------------------------------------
    // 中文文案
    // ------------------------------------------------------------------

    /// <summary>
    /// 余额的中文一行。
    ///
    /// 刻意**没有**复用 DeepSeekBalance.PanelLine() —— 那一行是给屏幕诊断面板用的，
    /// 必须是纯 ASCII（打包后引擎内置字体没有中文字形，会变方块）。
    /// 这里是真实 UI，有系统字体，所以该说人话。
    /// </summary>
    static string BalanceLineZh()
    {
        switch (DeepSeekBalance.State)
        {
            // ⚠️ 这一行左边只有 320px 宽（右边要留给"文字偏移"），
            //    14 号字一个汉字约 14px → **最多 22 个汉字**。
            //    所以这些句子都刻意写短了；要加长请同时改 _statusBalance 的宽度。
            case DeepSeekBalance.Status.NoKey:        return "还没有 Key（在下面输入）";
            case DeepSeekBalance.Status.Disabled:     return "已关闭（见下方开关）";
            case DeepSeekBalance.Status.Fetching:     return "正在查询…";
            case DeepSeekBalance.Status.Ok:           return "¥" + DeepSeekBalance.Amount + " " + DeepSeekBalance.Currency + "（" + AgeZh() + "）";
            case DeepSeekBalance.Status.AuthError:    return "Key 不对或没权限（HTTP 401/403）";
            case DeepSeekBalance.Status.NetworkError: return "连不上服务器（网络问题，不是 Key 的问题）";
            case DeepSeekBalance.Status.ParseError:   return "服务器返回的内容看不懂";
            default:                                  return "还没查询";
        }
    }

    /// <summary>距离上次成功拿到余额多久了。</summary>
    static string AgeZh()
    {
        if (DeepSeekBalance.LastSuccessUtc == System.DateTime.MinValue) return "还没有数据";
        System.TimeSpan age = System.DateTime.UtcNow - DeepSeekBalance.LastSuccessUtc;
        if (age.TotalSeconds < 90) return "刚刚";
        if (age.TotalMinutes < 90) return (int)age.TotalMinutes + " 分钟前";
        return (int)age.TotalHours + " 小时前";
    }

    // ------------------------------------------------------------------
    // 字体
    // ------------------------------------------------------------------

    /// <summary>
    /// 从系统里挑一个装了中文的字体。
    ///
    /// ⚠️ 关键坑：<c>Font.CreateDynamicFontFromOSFont("不存在的名字", size)</c>
    /// **不会返回 null**，它会悄悄回退到某个默认字体 —— 也就是说，如果只写
    /// <c>Font.CreateDynamicFontFromOSFont("微软雅黑", 32)</c>（用中文名），
    /// 你拿到的是一个能用的 Font 对象，然后中文全变方块，而且没有任何报错。
    ///
    /// 所以这里先查 <c>Font.GetOSInstalledFontNames()</c> 确认真装了这个字体，再创建。
    /// 一个都没找到就明说 —— "失败要看得见"。
    /// </summary>
    static Font LoadCjkFont()
    {
        // 顺序有讲究：雅黑 UI 是 Win10/11 的界面字体，字形最全也最好看；
        // 后面几个是各版本 Windows 都可能有的兜底。
        string[] candidates =
        {
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "SimHei",
            "SimSun",
            "Noto Sans CJK SC",
            "Source Han Sans SC",
        };

        var installed = new HashSet<string>(Font.GetOSInstalledFontNames());
        Debug.Log("[DeskSprite] 系统装了 " + installed.Count + " 个字体");

        foreach (string name in candidates)
        {
            if (!installed.Contains(name)) continue;

            Font f = Font.CreateDynamicFontFromOSFont(name, 32);
            if (f != null) return f;

            Debug.LogWarning("[DeskSprite] 系统说装了 '" + name + "'，但创建字体失败，试下一个");
        }

        Debug.LogError("[DeskSprite] 找不到任何中文字体，面板上的中文会变成方块。试过："
                     + string.Join("、", candidates));
        return Font.CreateDynamicFontFromOSFont("Arial", 32);
    }

    // ------------------------------------------------------------------
    // 布局小工具
    // ------------------------------------------------------------------

    /// <summary>建一个带 RectTransform 的 UI 节点。</summary>
    static RectTransform NewUiNode(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>
    /// 把节点拉成"宽度撑满父节点、贴着顶边的横条"。
    /// 面板里的每一**行**都用它 —— 行会随窗口变宽，行里的控件则用 <see cref="Place"/> 定位。
    ///
    /// 用的是**拉伸锚点**（anchorMin.x=0, anchorMax.x=1）+ sizeDelta.x = -2*padX，
    /// 意思是"比父节点窄 2*padX，且居中"。这样窗口一改尺寸，行自动跟着变宽，
    /// 不需要我们在 PetRunner 里手算 —— 少一处会忘记同步的地方。
    /// </summary>
    static void TopStrip(RectTransform rt, float height, float padX, float padTop)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -padTop);
        rt.sizeDelta = new Vector2(-padX * 2f, height);
    }

    /// <summary>
    /// 在**行内**放一个固定尺寸的控件：<paramref name="x"/> 是距行左边，
    /// <paramref name="y"/> 是距行**顶边**（往下为正 —— 和上面那些 TOP 常量同一套方向）。
    ///
    /// 行用拉伸锚点、行内用左上角锚点，是刻意分开的两套：
    /// 行要跟着窗口变宽，控件不该跟着变（按钮被拉宽很难看）。
    /// </summary>
    static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    /// <summary>
    /// 和 <see cref="Place"/> 一样，但从**右边**量：<paramref name="fromRight"/> 是距行右边缘。
    ///
    /// 为什么要单独有这么一个：面板右边缘的坐标是"窗口宽 − 内边距"，
    /// 而这个数一旦被抄进算式（比如写成 `452-110`），左边一改它就跟不上，
    /// 而且错的是**十几像素的错位**，看起来像"没对齐"而不是"算错了"，很难查。
    /// 用右锚点就永远不会错。
    /// </summary>
    static RectTransform PlaceRight(RectTransform rt, float fromRight, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-fromRight, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    /// <summary>建一个 Text，字体/溢出策略统一在这里定，省得每处各写一遍。</summary>
    Text MakeText(string name, Transform parent, int fontSize, Color color)
    {
        var rt = NewUiNode(name, parent);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = _font;
        t.fontSize = fontSize;
        t.color = color;
        t.alignment = TextAnchor.UpperLeft;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;   // 换行是我们自己排的，别让它自作主张
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;                              // 文字不吃鼠标，事件留给它下面的控件
        t.supportRichText = false;                            // 不解析富文本：内容里有 <> 也不会出怪事
        return t;
    }

    /// <summary>建一个放在行里的静态标签。</summary>
    Text MakeLabel(string name, string text, float x, float y, float w, float h)
    {
        Text t = MakeText(name, _strip, BodyFontSize, new Color(0.78f, 0.81f, 0.86f));
        t.alignment = TextAnchor.MiddleLeft;
        t.text = text;
        Place((RectTransform)t.transform, x, y, w, h);
        return t;
    }

    /// <summary>
    /// 建一个按钮。
    ///
    /// 两个 uGUI 的常识，写下来免得以后踩：
    ///   · <c>Button</c> 自己**不画任何东西**，它只是个"可点击的壳"，
    ///     必须有 Image（或别的 Graphic）才有视觉，也才有可点击区域。
    ///     忘了加就会得到一个"看得见文字但点不动"的按钮。
    ///   · 按下/悬停的变色由 Button 的 ColorBlock 负责，它染的是
    ///     CanvasRenderer 上的颜色，和 Image.color 是**相乘**关系（不是覆盖），
    ///     所以这里的底色可以照常给。
    /// </summary>
    Button MakeButton(string name, string label, float x, float y, float w, float h)
    {
        Button b = MakeButtonCore(name, label, w, h);
        Place((RectTransform)b.transform, x, y, w, h);
        return b;
    }

    /// <summary>贴右边放的按钮（用右锚点，见 <see cref="PlaceRight"/> 的注释）。</summary>
    Button MakeButtonRight(string name, string label, float fromRight, float y, float w, float h)
    {
        Button b = MakeButtonCore(name, label, w, h);
        PlaceRight((RectTransform)b.transform, fromRight, y, w, h);
        return b;
    }

    /// <summary>按钮的"本体"（画图 + 加组件），**不含定位** —— 定位交给 Place / PlaceRight。</summary>
    Button MakeButtonCore(string name, string label, float w, float h)
    {
        var rt = NewUiNode(name, _strip);
        rt.sizeDelta = new Vector2(w, h);

        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(0.22f, 0.25f, 0.33f, 1f);

        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;

        Text t = MakeText("Label", rt, BodyFontSize, Color.white);
        t.text = label;
        t.alignment = TextAnchor.MiddleCenter;
        var trt = (RectTransform)t.transform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.pivot = new Vector2(0.5f, 0.5f);
        trt.anchoredPosition = Vector2.zero;
        trt.sizeDelta = Vector2.zero;      // 撑满父节点：四边锚点 + sizeDelta = 0
        return btn;
    }

    /// <summary>
    /// 建一个复选框（Toggle）。
    ///
    /// 和 Button 一样，Toggle 也不画东西：要给它两块 Image ——
    /// 外框（targetGraphic）和对勾（graphic）。
    /// 我们没有任何 sprite，所以两块都是纯色块：外框深色、对勾亮绿色。
    /// Toggle 靠改对勾的 **alpha** 来显示开/关（不是 SetActive），所以对勾节点一直都在。
    ///
    /// 光靠一个色块表示开/关不够确定（尤其是深色背景下），所以面板上还配了一行
    /// "开 / 关（不查余额）"的字 —— 状态要能被读出来，不能靠猜。
    /// </summary>
    Toggle MakeToggle(string name, float x, float y, float w, float h)
    {
        Toggle t = MakeToggleCore(name, w, h);
        Place((RectTransform)t.transform, x, y, w, h);
        return t;
    }

    /// <summary>贴右边放的勾选框（用右锚点，见 <see cref="PlaceRight"/> 的注释）。</summary>
    Toggle MakeToggleRight(string name, float fromRight, float y, float w, float h)
    {
        Toggle t = MakeToggleCore(name, w, h);
        PlaceRight((RectTransform)t.transform, fromRight, y, w, h);
        return t;
    }

    /// <summary>勾选框的"本体"（画图 + 加组件），**不含定位**。</summary>
    Toggle MakeToggleCore(string name, float w, float h)
    {
        var rt = NewUiNode(name, _strip);
        rt.sizeDelta = new Vector2(w, h);

        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(0.05f, 0.06f, 0.08f, 1f);

        var toggle = rt.gameObject.AddComponent<Toggle>();
        toggle.targetGraphic = bg;

        var checkRt = NewUiNode("Checkmark", rt);
        Place(checkRt, 4f, 4f, w - 8f, h - 8f);
        var check = checkRt.gameObject.AddComponent<Image>();
        check.color = new Color(0.45f, 0.85f, 0.55f, 1f);

        toggle.graphic = check;
        return toggle;
    }

    /// <summary>
    /// 建一个输入框。
    ///
    /// InputField 的规矩比 Button/Toggle 多：
    ///   · 必须告诉它**文字画在哪个 Text 上**（textComponent），否则它不知道往哪写
    ///   · 还要一个 placeholder（占位文字），不然空的时候是个纯黑框，用户不知道能点
    ///   · <paramref name="contentType"/> 决定两件事：怎么过滤输入，以及**怎么显示**。
    ///     Password → 显示成圆点（API Key 不该明晃晃挂在屏幕上：这台桌宠是置顶窗口，
    ///     随时可能被截图或录屏）；IntegerNumber → 只让打数字和一个负号（坐标用）。
    ///
    /// ⚠️ 这里先把节点 SetActive(false)，组件全部配好再 SetActive(true)。
    ///    因为 AddComponent 会立刻跑 Awake/OnEnable，而那时 textComponent 还没赋值 ——
    ///    虽然 uGUI 内部有 null 保护，但"先配好再启用"是运行时搭 UI 的稳妥习惯，
    ///    免得以后某个版本开始报 "InputField: no text component assigned"。
    /// </summary>
    InputField MakeInputField(string name, float x, float y, float w, float h,
                              string placeholderText, InputField.ContentType contentType)
    {
        var rt = NewUiNode(name, _strip);
        Place(rt, x, y, w, h);
        rt.gameObject.SetActive(false);      // ← 关键：先关掉，见上面的注释

        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(0.05f, 0.06f, 0.08f, 1f);
        bg.raycastTarget = true;             // 点框框就能开始输入

        // 真正的文字（InputField 往这个 Text 里写）
        Text text = MakeText("Text", rt, BodyFontSize, Color.white);
        text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;
        var trt = (RectTransform)text.transform;
        Place(trt, 8f, 0f, w - 16f, h);      // 左右各留 8px 内边距，不然字贴着框很难看

        // 占位提示（内容为空时显示）
        Text placeholder = MakeText("Placeholder", rt, BodyFontSize, new Color(0.45f, 0.48f, 0.54f));
        placeholder.alignment = TextAnchor.MiddleLeft;
        placeholder.text = placeholderText;
        Place((RectTransform)placeholder.transform, 8f, 0f, w - 16f, h);

        var field = rt.gameObject.AddComponent<InputField>();
        field.textComponent = text;
        field.placeholder = placeholder;
        field.contentType = contentType;
        field.characterLimit = 128;
        field.lineType = InputField.LineType.SingleLine;

        rt.gameObject.SetActive(true);
        return field;
    }
}
