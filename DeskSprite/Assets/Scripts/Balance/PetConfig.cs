using System;
using System.IO;
using UnityEngine;

/// <summary>config.json 的数据形状。字段名必须和 json 的键完全一致（JsonUtility 的规矩）。</summary>
[Serializable]
public class PetConfigData
{
    /// <summary>给用户看的说明。不是给程序读的 —— json 不支持注释，就用一个字段代替。</summary>
    public string _help;

    public string apiKey;

    /// <summary>
    /// 多久查一次余额（秒）。
    ///
    /// ⚠️ **已经不用了**（只为了让老配置文件不报错、且保存时不丢字段而留着）。
    /// 现在的 API 使用策略是"**只在用户点她的时候查**"，没有定时轮询 ——
    /// 见 `DeepSeekBalance.RefreshNow` 的注释。
    /// </summary>
    public int pollSeconds;
    /// <summary>
    /// 关掉余额显示。**注意是反着写的** —— 因为 JsonUtility 区分不了
    /// "字段没写"和"写了 false"：缺省就是 false，所以用 hide 而不是 show，
    /// "没写 = 不隐藏 = 显示"，语义正好。（和 loop/noLoop 同一个套路。）
    /// </summary>
    public bool hideBalance;

    /// <summary>
    /// 随机待机的切换间隔（秒）。**0 = 不等待**（池动作一个接一个）。
    ///
    /// ⚠️ 这里 0 **不表示"没写"**，和 pollSeconds 不一样 ——
    /// 因为"0 = 不等待"本身是一个有意义的取值（《皮肤契约设计.md》§5.7），
    /// 所以缺省必须由**另一个哨兵值**表示。用 -1 表示"没写"，读出来时替换成默认值。
    /// </summary>
    public int idleSwitchSeconds;

    /// <summary>
    /// 用户缩放（百分比）。100 = 素材按自己的大小显示。
    /// **0 = 没写** → 用 100（这里 0 不是合法值，所以不用额外的哨兵）。
    /// </summary>
    public int zoomPercent;

    /// <summary>
    /// 用户**挑中的那套皮肤的完整路径**。空 = 没挑过，按搜索顺序找第一套能用的。
    ///
    /// 为什么记**路径**而不是名字：皮肤可以在磁盘上的任何地方
    ///（用户自己的图库、下载目录、U 盘…），不再要求放进我们的目录。
    /// 路径失效（被移走/改名/删掉）就退回默认扫描，并且**说清原因**。
    /// </summary>
    public string skinPath;
}

/// <summary>
/// 用户配置：API Key、轮询间隔之类。
///
/// 为什么放在 `%APPDATA%\DeskSprite\` 而不是程序目录？
///   · **不会被 git 记录** —— 密钥永远不该进仓库
///   · **不会被打进 exe** —— 打包时它根本不在工程里
///   · **换程序版本不会丢**，也不需要管理员权限
///
/// Key 的查找顺序（《皮肤契约设计.md》那条安全约定的实现）：
///   ① 环境变量 `DEEPSEEK_API_KEY`   ← 便于临时测试，也不用把密钥写进磁盘
///   ② `%APPDATA%\DeskSprite\config.json` 里的 apiKey
/// </summary>
public static class PetConfig
{
    public const int DefaultPollSeconds = 300;

    static string _resolvedPath;

    /// <summary>
    /// 配置和用户数据的**唯一**位置：`%APPDATA%\DeskSprite\`。
    ///
    /// 【为什么只剩这一个】这里原来还有"exe 所在目录"这一档（便携模式），去掉了：
    ///
    ///   · **"已存在的优先"这条规则本身就是个坑。** 两个候选位置意味着
    ///     "配置到底在哪"要靠猜 —— 用户改了 A 那份、程序读的是 B 那份，
    ///     而症状是"我改了怎么没反应"，极难自查。只剩一个位置，
    ///     这个问题就不存在了：**要看路径就去看日志**。
    ///   · **exe 目录可能是只读的**（装在 `Program Files` 下、或者只读介质），
    ///     所以"便携模式"本来就不是一个可靠的承诺 —— 一半情况下它会静默地
    ///     退到 `%APPDATA%`，那就又变回"两个位置"了。
    ///   · 代价是**便携性没了**（拷走程序不带走设置）。桌宠不是那种软件，
    ///     这个代价可以接受。
    ///
    /// 需要"看得见、好找"的问题，用**界面上的按钮**解决（打开配置/皮肤文件夹），
    /// 而不是靠"把文件放在显眼的地方" —— 后者必然会带来第二个位置。
    /// </summary>
    public static string[] CandidateFolders()
    {
        var list = new System.Collections.Generic.List<string>();

        try
        {
            list.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskSprite"));
        }
        catch { }

        return list.ToArray();
    }

    /// <summary>
    /// 配置文件在哪。现在只有一个候选位置，所以就是它。
    /// （"挑第一个写得进去的"那套逻辑留着 —— 它处理"目录建不出来"的情况。）
    /// </summary>
    public static string ConfigPath
    {
        get
        {
            if (!string.IsNullOrEmpty(_resolvedPath)) return _resolvedPath;

            string[] folders = CandidateFolders();

            foreach (string f in folders)
            {
                string p = Path.Combine(f, "config.json");
                if (File.Exists(p)) { _resolvedPath = p; return p; }
            }

            foreach (string f in folders)
            {
                if (CanWriteTo(f)) { _resolvedPath = Path.Combine(f, "config.json"); return _resolvedPath; }
            }

            // 都写不进去：返回最后一个（标准位置），后面写的时候会报失败原因
            _resolvedPath = Path.Combine(folders[folders.Length - 1], "config.json");
            return _resolvedPath;
        }
    }

    /// <summary>这个目录写得进去吗？真去写一个临时文件试一下 —— 别猜。</summary>
    static bool CanWriteTo(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".write_probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 用户的**数据目录** —— config.json 所在的那个文件夹，别的用户文件也跟着它走。
    ///
    /// 暴露出来是为了 <see cref="SkinUserOverride"/>：皮肤的用户覆盖也要存在同一个地方，
    /// 而不是存在皮肤目录里。
    ///
    /// ⚠️ **为什么不能存在皮肤目录里**：打包出来的皮肤在
    /// <c>DeskSprite_Data/StreamingAssets/skins/…</c> 下面，而那整个 <c>_Data</c> 文件夹
    /// **每次重新打包都会被重建** —— 用户的设置会被打包冲掉。
    /// 症状特别有欺骗性：你测"保存 → 重新打包 → 还在不在"，会看到"没了"，
    /// 然后以为保存功能坏了，而去查一个完全无关的地方。
    /// （顺带：装在 Program Files 时那里也是只读的。）
    /// </summary>
    public static string UserDataFolder
    {
        get { return Path.GetDirectoryName(ConfigPath); }
    }

    public static PetConfigData Data { get; private set; }

    /// <summary>密钥是从哪来的（**只记来源，不记密钥本身**，这行会被打进日志）。</summary>
    public static string Diagnostics { get; private set; } = "(还没读)";

    public static string ApiKey { get; private set; } = "";

    public static int PollSeconds
    {
        get
        {
            if (Data != null && Data.pollSeconds > 0) return Data.pollSeconds;
            return DefaultPollSeconds;
        }
    }

    /// <summary>随机待机的默认间隔（秒）。用户/作者都没写时用它。</summary>
    public const int DefaultIdleSwitchSeconds = 90;

    /// <summary>
    /// 素材没声明 scale / displayHeight 时，把她装进多大的屏幕高度（物理像素）。
    ///
    /// 这一条是给**非像素素材**兜底的：AI 生成的立绘动辄 1024×1536，
    /// 按"窗口 = 图片"会得到一个盖住屏幕的窗口。装进这个高度之后，
    /// 用户再用"她多大"的缩放去调。
    ///
    /// 300 的来历：1080 屏上占 28%，属于"比较小的桌宠但脸看得清"；
    /// 而且自带占位皮肤按 3 倍算出来是 264 高，放在 300 以内**不需要被缩**。
    /// </summary>
    public const int DefaultPetHeight = 300;

    /// <summary>用户缩放的默认值、以及允许范围（百分比）。</summary>
    public const int DefaultZoomPercent = 100;
    public const int MinZoomPercent = 25;
    public const int MaxZoomPercent = 400;

    /// <summary>
    /// 用户缩放（百分比）。100 = 素材按自己的大小显示。
    ///
    /// ⚠️ 和 <see cref="PollSeconds"/> 的读法**不一样**：这里 `0` **不是**合法值
    ///（0% 等于看不见），所以 `0` 可以直接当"没写"用 —— 不用像
    /// `idleSwitchSeconds` 那样去翻原始文本。
    /// </summary>
    public static int ZoomPercent
    {
        get
        {
            int v = (Data != null && Data.zoomPercent > 0) ? Data.zoomPercent : DefaultZoomPercent;
            if (v < MinZoomPercent) v = MinZoomPercent;
            if (v > MaxZoomPercent) v = MaxZoomPercent;
            return v;
        }
    }

    /// <summary>
    /// 用户挑中的皮肤路径（空 = 没挑过）。见 <see cref="PetConfigData.skinPath"/>。
    /// </summary>
    public static string SkinPath
    {
        get { return (Data != null && Data.skinPath != null) ? Data.skinPath : ""; }
    }

    /// <summary>只改**内存里**的皮肤路径，不碰文件。</summary>
    public static void SetSkinPathInMemory(string path)
    {
        if (Data == null)
        {
            Data = new PetConfigData();
            Data._help = "（配置文件读不出来，这是内存里临时重建的一份）";
            Data.apiKey = ApiKey;
        }
        Data.skinPath = path ?? "";
    }

    /// <summary>只改**内存里**的缩放，不碰文件。</summary>
    public static void SetZoomPercentInMemory(int percent)
    {
        if (Data == null)
        {
            Data = new PetConfigData();
            Data._help = "（配置文件读不出来，这是内存里临时重建的一份）";
            Data.apiKey = ApiKey;
        }
        if (percent < MinZoomPercent) percent = MinZoomPercent;
        if (percent > MaxZoomPercent) percent = MaxZoomPercent;
        Data.zoomPercent = percent;
    }

    /// <summary>
    /// 随机待机的切换间隔（秒）。**0 = 不等待**（池动作一个接一个）。
    ///
    /// ⚠️ 和 <see cref="PollSeconds"/> 的读法**不一样**，别照抄：
    /// 那边 0 表示"没写"，所以 `> 0` 才算有效；
    /// 这边 0 是"不等待"这个**有意义**的取值，所以"没写"由
    /// <see cref="TryReadFile"/> 在**读文件时**用原始文本判定并补成默认值 ——
    /// 到了这里，`Data.idleSwitchSeconds` 已经是一个确定的、可以直接用的数。
    /// </summary>
    public static int IdleSwitchSeconds
    {
        get
        {
            if (Data != null && Data.idleSwitchSeconds >= 0) return Data.idleSwitchSeconds;
            return DefaultIdleSwitchSeconds;
        }
    }

    /// <summary>
    /// 只改**内存里**的开关间隔，**不碰文件**（保存由设置面板的"保存"负责）。
    /// 负数按 0 处理 —— 间隔不可能是负的，而 0 是合法值（"不等待"）。
    /// </summary>
    public static void SetIdleSwitchSecondsInMemory(int seconds)
    {
        if (Data == null)
        {
            Data = new PetConfigData();
            Data._help = "（配置文件读不出来，这是内存里临时重建的一份）";
            Data.apiKey = ApiKey;
        }
        Data.idleSwitchSeconds = seconds < 0 ? 0 : seconds;
    }

    /// <summary>
    /// 用户是否关掉了余额显示。
    ///
    /// 关掉之后：**不读 Key、不发请求、不联网、不写余额日志** —— 就是一只能互动的桌宠。
    /// 这比"发请求但不显示"干净得多：不需要 Key，也不浪费网络。
    /// </summary>
    public static bool HideBalance
    {
        get { return Data != null && Data.hideBalance; }
    }

    /// <summary>
    /// 只改**内存里**的 Key，**不碰文件**。
    ///
    /// 这是 S3b 和 S3c 的分界线：S3b 让设置在**本次运行**里当场生效（不用重启），
    /// S3c 才把它写进 config.json（重开还在）。
    /// 刻意先做内存版，是因为"生效"和"持久化"是两个可以分开验证的问题 ——
    /// 合在一起做，出问题时分不清是"没生效"还是"没存下来"。
    ///
    /// ⚠️ 它会**盖过环境变量**：用户刚在面板里打完字，当然以他打的为准。
    ///    但这件事必须看得见，所以 Diagnostics 会改写成"来自设置面板"。
    /// </summary>
    public static void SetApiKeyInMemory(string key)
    {
        key = (key ?? "").Trim();

        // Data 为 null 只可能发生在"config.json 坏了/读不出来"的情况。
        // 这里补一个干净的对象，免得后面写文件时对着 null 崩掉。
        if (Data == null)
        {
            Data = new PetConfigData();
            Data._help = "把 apiKey 填成你自己的 DeepSeek API Key（sk- 开头）。";
        }

        Data.apiKey = key;
        ApiKey = key;

        bool envExists = false;
        try { envExists = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")); }
        catch { /* 读不到就当没有 */ }

        Diagnostics = "key 来自设置面板（本次运行）"
                    + (envExists ? "；已盖过环境变量 DEEPSEEK_API_KEY" : "");
    }

    /// <summary>
    /// 只改**生效的** Key，**不碰 Data**（也就是不碰"将来会被写进文件的那一份"）。
    ///
    /// 设置面板"放弃改动、退回原样"时必须用它，不能用 SetApiKeyInMemory：
    /// 要退回的那个 Key 可能来自**环境变量**，而 SetApiKeyInMemory 会顺手把它写进 Data ——
    /// 于是用户下一次点保存，环境变量里的 Key 就落进文件了。
    /// 用户把 Key 放环境变量，往往正是为了不让它落在文件里，所以我们不能替他决定这件事。
    /// </summary>
    public static void RestoreEffectiveApiKey(string key)
    {
        ApiKey = (key ?? "").Trim();
    }

    /// <summary>只改**内存里**的"是否显示余额"，**不碰文件**（同 SetApiKeyInMemory）。</summary>
    public static void SetHideBalanceInMemory(bool hide)
    {
        if (Data == null)
        {
            Data = new PetConfigData();
            Data._help = "hideBalance = true 时完全不查余额、不联网，只留一只可互动的桌宠。";
        }

        Data.hideBalance = hide;
    }

    public static void Load()
    {
        ApiKey = "";
        Data = null;
        Diagnostics = "";
        _readError = null;

        // ── ① 环境变量优先 ──
        // 注意：日志里**只写来源**，绝不写密钥内容。
        string env = null;
        try { env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"); }
        catch { /* 读环境变量失败就当没有 */ }

        TryReadFile();   // 顺便把 pollSeconds 读出来（有没有 key 都要读）

        if (!string.IsNullOrEmpty(env))
        {
            ApiKey = env.Trim();
            Diagnostics = "key 来自环境变量 DEEPSEEK_API_KEY";
            return;
        }

        if (Data != null && !string.IsNullOrEmpty(Data.apiKey))
        {
            ApiKey = Data.apiKey.Trim();
            Diagnostics = "key 来自 config.json";
            return;
        }

        Diagnostics = (Data == null)
            ? (_readError != null ? _readError : "没有 config.json，也没有环境变量")
            : "config.json 里 apiKey 是空的，也没有环境变量";
    }

    /// <summary>
    /// 读文件失败的原因。**必须单独存一份**，因为它会被下面那行 Diagnostics 覆盖掉。
    ///
    /// ⚠️ 为什么要专门说这件事：这个项目一直在讲"失败要看得见"，而这里恰好违反过 ——
    ///    `TryReadFile` 把"config.json 读失败：xxx"写进 Diagnostics，
    ///    紧接着 `Load` 末尾又用"没有 config.json，也没有环境变量"把它**盖掉了**。
    ///    结果是：用户手改 config.json 时漏了一个逗号 -> 整个文件解析失败 ->
    ///    所有设置退回默认值、API Key 看起来"丢了" ->
    ///    而日志里只说"没有 config.json"，把人往**完全错误的方向**引。
    ///    一个逗号的问题，查了半小时。
    /// </summary>
    static string _readError;

    static void TryReadFile()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            string json = File.ReadAllText(ConfigPath);
            Data = JsonUtility.FromJson<PetConfigData>(json);

            // ⚠️ 老配置文件里**没有** idleSwitchSeconds 这个字段，而 JsonUtility 会把
            //    缺失的 int 填成 **0** —— 偏偏 0 在这套语义里是**合法值**（"不等待"）。
            //    于是"没写"和"写了 0"分不开，老用户一升级就会看到
            //    "待机动作一个接一个地播"。所以这里显式补上默认值。
            //
            //    这是本项目第一次遇到"0 是合法值"的情况 —— pollSeconds 那边 0 只表示
            //    "没写"，所以 `> 0` 就够了；这里不行。
            if (Data != null && !RawHasField(json, "idleSwitchSeconds"))
            {
                Data.idleSwitchSeconds = DefaultIdleSwitchSeconds;
                Debug.Log("[DeskSprite] config.json 里没有 idleSwitchSeconds，"
                        + "按默认 " + DefaultIdleSwitchSeconds + " 秒处理（保存时会写进文件）");
            }
        }
        catch (Exception e)
        {
            Data = null;

            // ⚠️ 存进 _readError，**不是** Diagnostics —— 后者会被 Load 末尾覆盖掉。
            //    JSON 对格式很苛刻（一个逗号就能让整个文件读不出来），
            //    而手工编辑 json 最容易犯的正是这种错，所以这条信息必须活到最后。
            _readError = "config.json 读失败（" + e.GetType().Name + "）：" + e.Message
                       + "\n  文件还在，只是格式不对 —— 常见原因是少写/多写了一个逗号。"
                       + "\n  路径：" + ConfigPath;
        }
    }

    /// <summary>
    /// 原始 json 文本里**有没有这个键**。
    ///
    /// 为什么要看原始文本：JsonUtility 区分不了"字段没写"和"写了 0"（见上面那段）。
    /// 那为什么不直接搜键名？因为模板的 `_help` 里也提到了这些字段名（那是给人看的说明），
    /// 光搜名字会把说明文字误判成"写过了"。所以要求后面跟着一个**冒号**（JSON 的键值分隔符）。
    /// </summary>
    static bool RawHasField(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return false;

        string token = "\"" + key + "\"";
        int i = json.IndexOf(token, StringComparison.Ordinal);
        while (i >= 0)
        {
            int j = i + token.Length;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j < json.Length && json[j] == ':') return true;

            i = json.IndexOf(token, i + 1, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>
    /// 没有配置文件就生成一个模板 —— 这样用户至少知道该往哪儿填、填什么。
    /// 不生成的话，用户只会看到"没有 API Key"，然后不知道去哪配。
    /// </summary>
    public static void EnsureTemplate()
    {
        string path = ConfigPath;
        if (File.Exists(path)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var t = new PetConfigData();
            t._help = "把 apiKey 填成你自己的 DeepSeek API Key（sk- 开头），保存后重启本程序即可。"
                    + " pollSeconds 已经不用了（余额只在用户点她的时候查，没有定时轮询），留着只是为了老配置不报错。"
                    + " idleSwitchSeconds 是随机待机的切换间隔（秒）："
                    + "0 = 不等待（待机动作一个接一个），不写默认 " + DefaultIdleSwitchSeconds + "。"
                    + " zoomPercent 是她多大（百分比，100 = 素材按自己的大小显示），"
                    + "范围 " + MinZoomPercent + "~" + MaxZoomPercent + "，不写默认 " + DefaultZoomPercent + "。"
                    + " 本文件不会进 git，也不会被打进 exe。";
            t.apiKey = "";
            t.pollSeconds = DefaultPollSeconds;
            t.idleSwitchSeconds = DefaultIdleSwitchSeconds;
            t.zoomPercent = DefaultZoomPercent;

            // 写 UTF-8 **带 BOM**：这个文件是给用户手改的，
            // 带 BOM 才能让编辑器正确识别编码（不带的话中文说明可能被存成 GBK）。
            File.WriteAllText(path, JsonUtility.ToJson(t, true),
                              new System.Text.UTF8Encoding(true));

            Debug.Log("[DeskSprite] 已生成配置模板，请填入 apiKey：" + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DeskSprite] 写配置模板失败：" + e.Message + "  路径：" + path);
        }
    }

    /// <summary>
    /// 在资源管理器里打开配置文件（热键 Ctrl+Shift+O 用）。
    /// 文件还不存在就先建模板 —— 用户按热键，应该看到"一个可以填的文件"，而不是空文件夹。
    /// </summary>
    public static void RevealConfig()
    {
        string path = ConfigPath;
        if (!File.Exists(path)) EnsureTemplate();
        ShellOpen.Reveal(path);
    }

    /// <summary>
    /// 把内存里的配置写回 config.json（设置面板的"保存"用）。
    ///
    /// ⚠️ **Data 为 null 时拒绝保存。** 这不是防御性编程，是一个真实的毁数据路径：
    ///    JsonUtility 是"整个对象覆盖写"，写出去的就是 Data 的全部内容。
    ///    而 Data 为 null 只可能发生在"配置文件坏了/读不出来"的情况 ——
    ///    这时候如果照写，就会把用户原来那个（可能有他自己的 API Key 的）文件**冲成一个空壳**。
    ///    宁可保存失败并说明原因，也不能替用户删掉他的 Key。
    ///
    /// ⚠️ 另一个已知局限：JsonUtility 认不出的字段会**在保存时丢掉**。
    ///    所以我们只写自己的 schema；用户不要往 config.json 里加自定义字段。
    ///    （这也是"用户的东西放 skin.user.json、作者的东西放 skin.json"这条分工的延伸。）
    /// </summary>
    public static bool Save(out string error, out string pathUsed)
    {
        error = null;
        pathUsed = ConfigPath;

        if (Data == null)
        {
            error = "配置没读出来（可能是 config.json 坏了），为避免把原文件冲掉，这次不保存。"
                  + "路径：" + pathUsed;
            return false;
        }

        // ⚠️ 这里**故意不写** Data.apiKey = ApiKey。
        //    ApiKey 可能来自**环境变量**，把它抄进文件等于："用户特意用环境变量让 Key 不落在文件里，
        //    结果被我们写进去了。" —— 这是替他做一个他没同意的安全决定。
        //    而"用户在设置面板里填的 Key"那条路，SetApiKeyInMemory 已经同时更新了
        //    ApiKey 和 Data.apiKey，不需要这里再补一次。
        //    （所以 Data 里的 apiKey 只在"来自 config.json"或"用户在面板里填过"时才非空。）
        return JsonStore.Save(pathUsed, Data, out error);
    }
}
