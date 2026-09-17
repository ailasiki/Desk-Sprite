using System;
using System.IO;
using UnityEngine;

// ⚠️ 这里**故意不写** `using System.Diagnostics;`。
//    因为那个命名空间里也有一个 Debug，会和 UnityEngine.Debug 撞名（CS0104 歧义）。
//    所以下面用全名 System.Diagnostics.Process。
//    —— 这个坑我在 M0 的 DeskWindow.cs 里专门写过注释，结果自己又踩了一遍。

/// <summary>
/// 用资源管理器打开文件/文件夹。
///
/// 为什么需要它？因为我们要给用户看的两样东西**都在隐藏文件夹里**：
///   · 配置：%APPDATA%\Roaming\...  （AppData 默认是隐藏的）
///   · 日志：%APPDATA%\LocalLow\... （同上）
/// 光靠文字告诉路径没用 —— 用户找不到。
/// 一个热键直接把文件夹打开、并把文件选中，才是最省事的做法。
/// </summary>
public static class ShellOpen
{
    /// <summary>在资源管理器里定位到某个文件（会选中它）。文件不存在就打开它所在的文件夹。</summary>
    public static bool Reveal(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            if (File.Exists(path))
            {
                // /select,"完整路径" —— 注意引号，路径里有空格时不加就会解析错
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                return true;
            }

            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DeskSprite] 打开资源管理器失败：" + e.Message + "  路径：" + path);
            return false;
        }
    }

    /// <summary>打包后 Player.log / 编辑器里 Editor.log 的路径。</summary>
    public static string LogPath
    {
        get
        {
            try
            {
                string p = Application.consoleLogPath;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            return "";
        }
    }

    /// <summary>
    /// 弹出系统的"选择文件夹"对话框。用户取消就返回 null（**不是错误**）。
    /// 真出错时 <paramref name="error"/> 给一句中文原因。
    ///
    /// ⚠️ **把我们的窗口句柄传给对话框当 owner** —— 这一步不能省：
    ///    我们的窗口是 `HWND_TOPMOST`（永远置顶）。对话框如果没有 owner，
    ///    就会**跑到我们窗口后面**，用户看到的是"点了没反应" ✗。
    ///    有了 owner 关系，Windows 会保证对话框显示在 owner 之上 ✓。
    ///
    /// ⚠️ 另外它是**模态**的：调用期间会**阻塞 Unity 的主循环**，她会僵住不动。
    ///    这是模态对话框的正常行为（"程序在等你"），选完就恢复。
    ///    代价：如果此时正好有一个余额请求在飞，它可能因此超时 —— 点她一下就能重查 ✓。
    ///
    /// 为什么用 SHBrowseForFolder 而不是现代的 IFileOpenDialog：见 Win32.BROWSEINFOW 的注释。
    /// </summary>
    public static string PickFolder(string title, out string error)
    {
        error = null;

        IntPtr hwnd = DeskWindow.Handle;     // 可能为 Zero（编辑器里）—— 那就没有 owner，将就
        IntPtr nameBuf = IntPtr.Zero;
        IntPtr pidl = IntPtr.Zero;

        // BIF_NEWDIALOGSTYLE 要求调用线程初始化过 COM。
        // 返回值：0 = S_OK（我们负责反初始化）、1 = S_FALSE（已经初始化过，**不要**反初始化）、
        // 其它（比如 RPC_E_CHANGED_MODE 0x80010106）= 用了别的线程模型，同样不要反初始化。
        int hr = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED);
        bool weInitialized = (hr == 0);

        try
        {
            const int MAX_PATH = 260;
            nameBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal(MAX_PATH * 2);

            var bi = new Win32.BROWSEINFOW();
            bi.hwndOwner = hwnd;
            bi.pszDisplayName = nameBuf;
            bi.lpszTitle = title;
            bi.ulFlags = Win32.BIF_RETURNONLYFSDIRS | Win32.BIF_USENEWUI;

            pidl = Win32.SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero)
            {
                // 用户点了取消 —— 这不是错误
                Debug.Log("[DeskSprite] 选择文件夹：用户取消了");
                return null;
            }

            var sb = new System.Text.StringBuilder(MAX_PATH);
            if (!Win32.SHGetPathFromIDList(pidl, sb))
            {
                error = "拿到了你选的文件夹，但读不出它的路径";
                return null;
            }

            string path = sb.ToString();
            Debug.Log("[DeskSprite] 选择文件夹 -> " + path);
            return path;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + "：" + e.Message;
            Debug.LogWarning("[DeskSprite] 弹出文件夹选择框失败：" + error);
            return null;
        }
        finally
        {
            // pidl 是 shell 分配的，必须还回去；nameBuf 是我们分配的，自己释放
            if (pidl != IntPtr.Zero) Win32.CoTaskMemFree(pidl);
            if (nameBuf != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(nameBuf);
            if (weInitialized) Win32.CoUninitialize();
        }
    }
}
