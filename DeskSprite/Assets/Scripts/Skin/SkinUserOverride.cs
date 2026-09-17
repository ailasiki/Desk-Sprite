using System;
using System.IO;
using UnityEngine;

/// <summary>
/// 用户对某套皮肤的本地覆盖：<c>&lt;用户数据目录&gt;/skins/&lt;皮肤名&gt;.user.json</c>。
///
/// 【为什么不直接改 skin.json】这是这个模块存在的全部理由：
///   ① <c>skin.json</c> 是**皮肤作者**的文件。用户在设置面板里调出来的位置不是作者的意图；
///      写进去的话，作者下次更新皮肤就和用户的改动冲突了 —— 而且冲突是**静默**的：
///      作者的更新会被用户的旧值盖掉，或者反过来。
///   ② 安装目录可能是只读的（装在 Program Files 之类）。用户文件的写入失败是**可以降级**的
///      （回到作者声明的位置），但要是把作者文件写坏，那是真的坏了。
///   ③ 用户想"恢复原样"时，只要**删掉这个文件**就行 —— 不用去动作者的文件，也不用记原值是多少。
///
/// 【为什么**不**放在皮肤目录里】
/// 我一开始就是那么写的（"覆盖跟着皮肤走"听起来很美），但它有个致命问题：
/// 打包出来的皮肤位于 <c>DeskSprite_Data/StreamingAssets/skins/…</c>，
/// 而那整个 <c>_Data</c> 文件夹**每次重新打包都会被重建**。
/// 于是"保存 → 重新打包 → 设置没了"，而原因和保存逻辑毫无关系 ——
/// 这会让人去查一个完全错误的地方（顺带：装在 Program Files 时那里也是只读的）。
/// 所以跟 <c>config.json</c> 一样放到用户数据目录下面，按皮肤名分文件。
///
/// 【现在只存一样东西】牌子文字的坐标。以后要存别的（比如某个动作的 fps、缩放）就加字段。
/// </summary>
public static class SkinUserOverride
{
    /// <summary>
    /// ⚠️ <c>formatVersion</c> 不是装饰：JsonUtility **分不清"字段没写"和"字段写了 0"**。
    ///    所以不能用 <c>textAreaX == 0</c> 来判断"有没有覆盖"（用户完全可能真的把 x 调成 0）。
    ///    必须有一个"写过没有"的标记，这里就是它。0 = 没写过。
    ///    （同一个坑在 PetConfig.pollSeconds 上也踩过 —— 那里也是用 0 表示"没写"。）
    /// </summary>
    [Serializable]
    public class Data
    {
        public string _help;
        public int formatVersion;
        public int textAreaX;
        public int textAreaY;

        /// <summary>
        /// 余额数字的**字号倍率**（5×7 位图字放大几倍）。
        ///
        /// 0 = 没写 → 用 1（原大小）。**只允许整数倍** ——
        /// 位图字体非整数倍会出现"有的笔画两像素宽、有的三像素"，看起来就是糊了。
        /// 所以这个字段是 int，不是 float。
        /// </summary>
        public int fontScale;
    }

    /// <summary>这套皮肤的用户文件该在哪。皮肤没有目录名时返回 null。</summary>
    public static string PathFor(PetSkin skin)
    {
        if (skin == null) return null;

        // ⚠️ 用**目录名**（FolderName），不是显示名（Name）——
        //    显示名来自 skin.json，可以被作者改、也可能重名，
        //    用它的话"拷一份皮肤改个文件夹名"会让两套皮肤共用同一个文件。
        string key = skin.FolderName;
        if (string.IsNullOrEmpty(key)) return null;

        // 目录名理论上可能带非法文件名字符，稳妥起见换掉
        string safe = Sanitize(key);
        return Path.Combine(PetConfig.UserDataFolder, "skins", safe + ".user.json");
    }

    /// <summary>把皮肤名变成能当文件名的样子（只保留字母数字、下划线、连字符、汉字）。</summary>
    static string Sanitize(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                   || c == '_' || c == '-' || c > 0x7F;    // 汉字等非 ASCII 一律放行
            sb.Append(ok ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 读出来的用户覆盖（对"余额数字"的全部调整）。
    ///
    /// 用一个结构体而不是一堆 out 参数：这样以后再加字段（比如颜色、描边）
    /// 不用改签名，调用点也不会跟着变。
    /// </summary>
    public struct Override
    {
        /// <summary>数字的**水平中心**（帧坐标，从帧左边缘算）。</summary>
        public int X;
        /// <summary>数字的**底边**（帧坐标，从帧底边算）。</summary>
        public int Y;
        /// <summary>字号倍率（5×7 位图字放大几倍），至少 1。</summary>
        public int FontScale;
    }

    /// <summary>
    /// 读用户覆盖。<paramref name="note"/> 是给日志/界面用的一句话（中性的，不是错误）。
    ///
    /// 返回 false 的三种情况都**不是错误**，只是"用皮肤声明的值"：
    ///   · 文件不存在（最常见 —— 用户从来没调过）
    ///   · 文件里没有有效覆盖（formatVersion = 0）
    ///   · 文件坏了读不出来（这时 note 会说明，但程序照常跑）
    /// </summary>
    public static bool TryLoad(PetSkin skin, out Override ov, out string note)
    {
        ov = new Override();
        ov.FontScale = 1;
        note = null;

        string path = PathFor(skin);
        if (path == null) return false;

        string text, err;
        if (!JsonStore.TryReadText(path, out text, out err))
        {
            if (err != null) note = "skin.user.json 读不出来（" + err + "），用皮肤声明的值";
            return false;
        }

        Data d;
        try
        {
            d = JsonUtility.FromJson<Data>(text);
        }
        catch (Exception e)
        {
            note = "skin.user.json 内容不是合法 JSON（" + e.GetType().Name + "），用皮肤声明的值";
            return false;
        }

        if (d == null || d.formatVersion <= 0)
        {
            note = "skin.user.json 里没有可用的覆盖（formatVersion = 0），用皮肤声明的值";
            return false;
        }

        ov.X = d.textAreaX;
        ov.Y = d.textAreaY;
        ov.FontScale = d.fontScale > 0 ? d.fontScale : 1;    // 0 = 没写 = 原大小
        note = "余额数字的位置/字号来自 skin.user.json：(" + ov.X + ", " + ov.Y
             + ") 字号 ×" + ov.FontScale;
        return true;
    }

    /// <summary>把用户的调整写进 skin.user.json。失败时 <paramref name="error"/> 是**给用户看的中文原因**。</summary>
    public static bool Save(PetSkin skin, int x, int y, int fontScale,
                            out string error, out string pathUsed)
    {
        error = null;
        pathUsed = PathFor(skin);

        if (pathUsed == null)
        {
            error = "这套皮肤没有名字，不知道该把它的用户设置存成哪个文件";
            return false;
        }

        if (fontScale < 1) fontScale = 1;

        var d = new Data();
        d._help = "这个文件是你在设置面板里调出来的，程序会覆盖它。"
                + "皮肤作者的文件是 skin.json —— 不要改那个，也不要把改动写进那个。"
                + "想恢复作者声明的值：删掉本文件即可。";
        d.formatVersion = 1;
        d.textAreaX = x;
        d.textAreaY = y;
        d.fontScale = fontScale;

        return JsonStore.Save(pathUsed, d, out error);
    }
}
