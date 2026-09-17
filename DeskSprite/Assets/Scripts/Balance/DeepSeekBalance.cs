using System;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 查 DeepSeek 账户余额。
///
/// 接口（官方文档 https://api-docs.deepseek.com/zh-cn/api/get-user-balance/）：
/// <code>
///   GET https://api.deepseek.com/user/balance
///   Authorization: Bearer &lt;key&gt;
///   → {"is_available":true,
///      "balance_infos":[{"currency":"CNY","total_balance":"110.00",
///                        "granted_balance":"0.00","topped_up_balance":"110.00"}]}
/// </code>
///
/// 设计上刻意的几点：
///   · **失败要分类**，不要一个笼统的"出错了"。
///     Key 不对（401）和连不上网，用户该做的事完全不同 —— 前者去改配置，后者去查网络。
///   · **不轮询得太勤**。余额不是高频变化的东西，默认 5 分钟一次。
///   · **失败不覆盖上一次的好数据** —— 断网时牌子上宁可显示旧余额 + 一个"离线"标记，
///     也比显示 "--" 好。（B2 做牌子时用得上；现在先把状态分清楚。）
/// </summary>
public static class DeepSeekBalance
{
    public enum Status
    {
        Idle,          // 还没开始
        NoKey,         // 没有 API Key
        Disabled,      // 用户主动关掉了（config.json 里 hideBalance = true）
        Fetching,      // 请求进行中
        Ok,            // 拿到了
        AuthError,     // 401/403：Key 不对，或者没权限
        NetworkError,  // 连不上、超时、DNS 失败
        ParseError,    // 通了，但返回的内容看不懂
    }

    const string Endpoint = "https://api.deepseek.com/user/balance";
    const int TimeoutSeconds = 15;

    public static Status State { get; private set; } = Status.Idle;

    /// <summary>最近一次成功拿到的余额。**失败时不会清空它**（见类注释）。</summary>
    public static string Amount { get; private set; } = "--";
    public static string Currency { get; private set; } = "";
    public static DateTime LastSuccessUtc { get; private set; } = DateTime.MinValue;

    /// <summary>失败原因（中文长句，进日志和 Console；屏幕上不显示它）。</summary>
    public static string Detail { get; private set; } = "(还没请求)";

    static UnityWebRequest _req;

    // ⚠️ JsonUtility 的规矩：**字段名必须和 json 的键完全一致**。
    //    所以这里只能用 is_available / balance_infos 这种蛇形命名 —— 不是 C# 风格，
    //    但改成驼峰就读不出来了。（这个坑写在《皮肤契约设计.md》§5.5 同一类里。）
    [Serializable]
    class BalanceInfo
    {
        public string currency;
        public string total_balance;
        public string granted_balance;
        public string topped_up_balance;
    }

    [Serializable]
    class BalanceResponse
    {
        public bool is_available;
        public BalanceInfo[] balance_infos;
    }

    public static void Start()
    {
        PetConfig.Load();
        PetConfig.EnsureTemplate();
        Reevaluate("启动");
    }

    /// <summary>
    /// 按**当前配置**重新决定状态。**配置一变就要调它。**
    ///
    /// 为什么单独拎出来：这段逻辑原来长在 Start() 里，而 Start() 一辈子只调一次 ——
    /// 于是"运行中改配置"永远生效不了：用户在面板里填了新 Key，State 还是 NoKey，
    /// Update() 第一行就 return，程序像死了一样，而且**什么都不报**。
    ///
    /// 这正是 S3b 要解决的事：**改完当场生效，不用重启。**
    /// </summary>
    public static void Reevaluate(string why)
    {
        // 在途请求作废 —— 它带的是**旧** Key，回来也不能算数。
        // 不做这一步的话，改完 Key 之后旧请求的结果会把新状态覆盖掉（而且看不出原因）。
        if (_req != null)
        {
            try { _req.Dispose(); } catch { /* 已经销毁了就算了 */ }
            _req = null;
        }

        // 用户主动关掉了：**连请求都不发**。
        // 不需要 Key、不联网、不写余额日志 —— 这比"查了但不显示"干净得多。
        if (PetConfig.HideBalance)
        {
            State = Status.Disabled;
            Detail = "已在配置里关掉（hideBalance = true）";
            Debug.Log("[DeskSprite] balance(" + why + "): 已关闭");
            return;
        }

        if (string.IsNullOrEmpty(PetConfig.ApiKey))
        {
            State = Status.NoKey;
            Detail = "没有 API Key（" + PetConfig.Diagnostics + "）\n配置位置：" + PetConfig.ConfigPath;
            Debug.LogWarning("[DeskSprite] balance(" + why + "): " + Detail);
            return;
        }

        Debug.Log(string.Format("[DeskSprite] balance({0}): {1}；只在你点她的时候查",
                                why, PetConfig.Diagnostics));
        State = Status.Idle;

        // 进入"可查"状态时先查一次，这样牌子一上来就有数、而不是 "--"。
        // **这是一次性的，不是轮询** —— 详见 RefreshNow 和 Update 的注释。
        RefreshNow();
    }

    /// <summary>
    /// 现在就查（**唯一的触发方式**：启动时一次、用户点她、以及面板里打开余额开关）。
    ///
    /// 【为什么不做定时轮询】这是**用户定的 API 使用策略**：
    ///   余额不是高频变化的东西，而定时查意味着"你不动它，它也在花你的 API"。
    ///   改成"点她 = 查一次"之后，**每一次请求都是用户主动要的** ✓，
    ///   而且这个功能本身变得可解释（"我想知道余额 -> 我点一下"）。
    ///
    /// 三条守卫：
    ///   · 关掉了 / 没 Key → **什么都不做**（连 Key 都不读）
    ///   · 已经有一个请求在飞 → 什么都不做（连点不会打出连发请求）
    ///   · 失败**不清空**上一次的好数据（见类注释）
    /// </summary>
    public static void RefreshNow()
    {
        if (State == Status.NoKey || State == Status.Disabled) return;
        if (_req != null) return;          // 在途请求还没回来，别叠
        Begin();
    }

    /// <summary>
    /// 每帧被 PetRunner 调一次。**它只负责推进"在途的那个请求"，不会自己发起查询。**
    ///
    /// （原来这里有一个 `if (DateTime.UtcNow >= _nextFetchUtc) Begin();` ——
    ///   那就是定时轮询，已经按用户的 API 使用策略去掉了。）
    /// </summary>
    public static void Update()
    {
        if (State == Status.NoKey || State == Status.Disabled) return;

        if (_req != null)
        {
            if (!_req.isDone) return;
            Finish();
        }
    }

    static void Begin()
    {
        State = Status.Fetching;
        Detail = "请求中…";

        try
        {
            _req = UnityWebRequest.Get(Endpoint);
            _req.SetRequestHeader("Authorization", "Bearer " + PetConfig.ApiKey);
            _req.SetRequestHeader("Accept", "application/json");
            _req.timeout = TimeoutSeconds;
            _req.SendWebRequest();
        }
        catch (Exception e)
        {
            _req = null;
            State = Status.NetworkError;
            Detail = "发请求时抛异常：" + e.Message;
        }
    }

    static void Finish()
    {
        UnityWebRequest req = _req;
        _req = null;

        try
        {
            if (req.result == UnityWebRequest.Result.Success)
            {
                Parse(req.downloadHandler != null ? req.downloadHandler.text : null);
            }
            else if (req.responseCode == 401 || req.responseCode == 403)
            {
                // 这一类是"用户能自己修"的：去改配置里的 Key
                State = Status.AuthError;
                Detail = string.Format("HTTP {0} —— API Key 不对，或者这个 Key 没有权限", req.responseCode);
            }
            else
            {
                // 这一类是"环境问题"：断网、超时、DNS、服务端 5xx
                State = Status.NetworkError;
                Detail = string.Format("HTTP {0}，{1}", req.responseCode, req.error);
            }
        }
        catch (Exception e)
        {
            State = Status.ParseError;
            Detail = "处理响应时异常：" + e.Message;
        }
        finally
        {
            req.Dispose();
        }

        Debug.Log("[DeskSprite] balance -> " + State + " | " + Detail);
    }

    static void Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            State = Status.ParseError;
            Detail = "响应是空的";
            return;
        }

        BalanceResponse r = null;
        try
        {
            r = JsonUtility.FromJson<BalanceResponse>(text);
        }
        catch (Exception e)
        {
            State = Status.ParseError;
            Detail = "JSON 解析失败：" + e.Message + "\n原文前 200 字：" + Head(text, 200);
            return;
        }

        if (r == null || r.balance_infos == null || r.balance_infos.Length == 0)
        {
            State = Status.ParseError;
            Detail = "响应里没有 balance_infos。原文前 200 字：" + Head(text, 200);
            return;
        }

        // 优先人民币；没有 CNY 就用第一条
        BalanceInfo pick = null;
        for (int i = 0; i < r.balance_infos.Length; i++)
        {
            BalanceInfo b = r.balance_infos[i];
            if (b == null) continue;
            if (pick == null) pick = b;
            if (!string.IsNullOrEmpty(b.currency)
                && b.currency.Equals("CNY", StringComparison.OrdinalIgnoreCase))
            {
                pick = b;
                break;
            }
        }

        if (pick == null || string.IsNullOrEmpty(pick.total_balance))
        {
            State = Status.ParseError;
            Detail = "balance_infos 里没有可用的 total_balance";
            return;
        }

        Amount = pick.total_balance;
        Currency = pick.currency;
        LastSuccessUtc = DateTime.UtcNow;
        State = Status.Ok;
        Detail = string.Format("更新于 {0}（可用={1}）", DateTime.Now.ToString("HH:mm:ss"), r.is_available);
    }

    static string Head(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    /// <summary>
    /// 给屏幕诊断面板用的一行。**严格 ASCII** ——
    /// 打包后 Unity 内置字体不一定有中文字形，中文会变方块。
    /// 详细的中文原因在 <see cref="Detail"/> 里，进日志。
    /// </summary>
    public static string PanelLine()
    {
        switch (State)
        {
            // 没有 Key 是"用户能自己修"的情况，所以直接把热键写在脸上 —— 比让他去翻日志强
            case Status.NoKey:        return "bal --   NO KEY   C+S+O=config";
            case Status.Disabled:     return "bal --   disabled (hideBalance)";
            case Status.Fetching:     return "bal ...  fetching";
            case Status.Ok:           return "bal " + Amount + " " + Currency + "   " + AgeText();
            case Status.AuthError:    return "bal --   AUTH 401/403";
            case Status.NetworkError: return "bal --   NETWORK";
            case Status.ParseError:   return "bal --   BAD RESPONSE";
            default:                  return "bal --   idle";
        }
    }

    /// <summary>距离上次成功拿到余额多久了。</summary>
    static string AgeText()
    {
        if (LastSuccessUtc == DateTime.MinValue) return "";
        TimeSpan age = DateTime.UtcNow - LastSuccessUtc;
        if (age.TotalSeconds < 90) return "now";
        if (age.TotalMinutes < 90) return ((int)age.TotalMinutes) + "m";
        return ((int)age.TotalHours) + "h";
    }
}
