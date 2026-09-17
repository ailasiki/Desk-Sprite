using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 把一个小对象写成 UTF-8 的 JSON 文件，**原子地**。
///
/// 【为什么要原子】
/// <c>File.WriteAllText</c> 的行为是"先把文件清空，再逐段写"。写到一半断电、崩溃、
/// 或者被杀进程，留下的是**半个文件**。而 config.json 里存着 API Key ——
/// 半个文件等于 Key 丢了，而且下次读不出来时程序会当成"没配置过"，
/// 用户看到的是"我的 Key 怎么没了"，不是"文件坏了"。两个后果都很难查。
///
/// 所以：先写临时文件，写完整了再一次性顶上目标。
/// **替换要么全成、要么全不成**，不存在"半个文件"这个状态。
///
/// 【为什么写带 BOM 的 UTF-8】
/// Windows 上记事本和不少编辑器靠 BOM 判断"这是 UTF-8"。不带 BOM 的话，
/// 文件里的中文（我们的 `_help` 字段）会被当成 ANSI 打开，变成乱码。
/// 我们自己的读取是显式 UTF-8，加不加 BOM 都能读；加 BOM 纯粹是**为了用户**用记事本打开时能看懂。
/// </summary>
public static class JsonStore
{
    /// <summary>
    /// 把 <paramref name="data"/> 序列化成 JSON 写到 <paramref name="path"/>。
    /// 失败时返回 false 并给出**中文原因**（这个原因会进日志、也会进设置面板）。
    /// </summary>
    public static bool Save<T>(string path, T data, out string error)
    {
        error = null;
        string tmp = path + ".tmp";

        try
        {
            string json = JsonUtility.ToJson(data, true);   // true = 带缩进，用户要能看懂

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // ⚠️ 必须用 FileStream 显式指定 BOM，不能用 File.WriteAllText 的默认重载
            //    （那个写的是不带 BOM 的 UTF-8）。
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new UTF8Encoding(true)))
            {
                w.Write(json);
            }

            Replace(tmp, path);
            return true;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + "：" + e.Message;
            TryDelete(tmp);
            return false;
        }
    }

    /// <summary>读一个 JSON 文件。文件不存在时返回 false 且 <paramref name="error"/> 为 null（那是正常情况，不是错误）。</summary>
    public static bool TryReadText(string path, out string text, out string error)
    {
        text = null;
        error = null;

        try
        {
            if (!File.Exists(path)) return false;
            text = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + "：" + e.Message;
            return false;
        }
    }

    /// <summary>
    /// 把临时文件顶上目标位置。
    ///
    /// 首选 <c>File.Replace</c>：它是操作系统级的原子替换。
    /// 但它在某些文件系统/网盘同步目录上会抛异常（那些地方不支持 ReplaceFile 语义），
    /// 这时退化成"删掉旧的再改名" —— **那一步不原子**，所以要在日志里说清楚走过这条路，
    /// 免得以后出问题时以为是原子的。
    /// </summary>
    static void Replace(string tmp, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }

        try
        {
            File.Replace(tmp, path, null);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DeskSprite] File.Replace 失败（" + e.GetType().Name
                           + "），退化成「先删再改名」—— 这一步**不是原子的**，"
                           + "万一此刻断电，目标文件可能不存在。原因：" + e.Message);
            File.Delete(path);
            File.Move(tmp, path);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 删不掉就算了，反正它只是个 .tmp */ }
    }
}
