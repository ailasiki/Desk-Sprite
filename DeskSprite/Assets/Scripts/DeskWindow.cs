using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>窗口背景的透明方案。两套都实现了，因为哪套在你这台机器上生效取决于显卡驱动和 DXGI 设置。</summary>
public enum TransparencyMode
{
    /// <summary>DWM 逐像素透明：相机背景 alpha=0，交给桌面窗口管理器合成。能支持半透明边缘。</summary>
    DwmAlpha = 0,

    /// <summary>品红色键透明：把纯品红那一整片颜色抠掉。像素画的硬边绝不会有毛边，但抗锯齿的文字会带紫边。</summary>
    ColorKey = 1,
}

/// <summary>
/// 把 Unity 的窗口改造成"桌宠形态"：无边框、永远置顶、不进任务栏/Alt+Tab、背景透明。
///
/// ⚠️ 它**只在打包出来的 exe 里动手**。在 Editor 里它什么都不做 ——
/// 因为 Editor 里"游戏画面"只是 Unity 编辑器窗口里的一个面板，它没有自己的窗口。
/// 在那里改窗口样式，改的是 **Unity 编辑器本身**，整个编辑器会变成无边框置顶窗口。
/// </summary>
public static class DeskWindow
{
    /// <summary>我们自己窗口的句柄。找到一次就记住，不用每次重新找。</summary>
    public static IntPtr Handle { get; private set; } = IntPtr.Zero;

    /// <summary>改造到底做了没有。Editor 里永远是 false。</summary>
    public static bool Applied { get; private set; }

    /// <summary>
    /// 鼠标穿透是否开着。
    ///
    /// 这不是让用户手动切的开关 —— 而是**每帧根据"鼠标是不是压在她画到的像素上"自动决定**
    /// （见 <see cref="SetClickThrough"/> 和 PetRunner 里的用法）。
    /// 目的：她周围的透明区域不该吃掉桌面图标的点击。
    /// </summary>
    public static bool ClickThrough { get; private set; }

    /// <summary>当前透明方案。F1 切换。</summary>
    public static TransparencyMode Mode { get; private set; } = TransparencyMode.DwmAlpha;

    /// <summary>色键用的品红。相机背景色必须和它**一模一样**，才能被整片抠掉。</summary>
    public static readonly Color32 KeyColor = new Color32(255, 0, 255, 255);

    /// <summary>最近一次透明设置的调用结果（成功/失败 + 错误码），诊断用。</summary>
    public static string LastDetail { get; private set; } = "(还没执行)";

    /// <summary>给 Debug.Log 和屏幕诊断面板看的一行字。</summary>
    public static string Diagnostics { get; private set; } = "(还没执行)";

    // 改造前的原始样式：先读，再改。
    // 留着它是为了以后能"临时变回正常窗口"——比如打开设置面板时，
    // 我们会希望它短暂地像一个正常窗口那样有边框、能被系统拖动。
    static uint _originalStyle;
    static uint _originalExStyle;
    static bool _originalSaved;

    /// <summary>相机该用什么背景色。这个值必须和当前透明方案配套，改方案时也要同步改相机。</summary>
    public static Color CameraBackground
    {
        get
        {
            // Editor 里我们不动窗口，所以用一个不透明的深灰当"假透明"，好让你看得见画面里的东西
            if (Application.isEditor) return new Color(0.10f, 0.10f, 0.12f, 1f);

            // DwmAlpha：背景清成"透明的黑"（alpha=0），那块区域就透出桌面
            // ColorKey ：背景清成实心品红，Windows 会把这个颜色整片抠掉
            return Mode == TransparencyMode.DwmAlpha
                ? new Color(0f, 0f, 0f, 0f)
                : (Color)KeyColor;
        }
    }

    /// <summary>换一套透明方案并立刻重新应用。</summary>
    public static void ToggleMode()
    {
        Mode = Mode == TransparencyMode.DwmAlpha ? TransparencyMode.ColorKey : TransparencyMode.DwmAlpha;
        Apply();
    }

    /// <summary>
    /// 把当前窗口改造成桌宠形态。可以在运行时反复调用（幂等 —— 调一百次和调一次结果一样）。
    /// </summary>
    public static void Apply()
    {
        // ── 0) Editor 里绝不动手 ──
        // 用 if (Application.isEditor) 而不是 #if UNITY_EDITOR，是为了在 Editor 里
        // 也能把 Diagnostics 填上 —— 屏幕上那个诊断面板要靠它显示"为什么什么都没发生"。
        if (Application.isEditor)
        {
            Applied = false;
            LastDetail = "(Editor 里不执行)";
            Diagnostics = "Editor 模式：故意不改窗口（这里没有我们自己的窗口）";
            return;
        }

        // ── 1) 找到自己的窗口 ──
        if (!EnsureHandle())
        {
            // 注意这里**不抛异常**，只记录原因然后返回。
            // 判断标准：桌宠透明失败，它还能当个普通小窗口用 —— 属于"功能降级但可用"，
            // 那就该记录 + 继续跑，让失败变成"看得见的信息"而不是"崩溃"。
            Applied = false;
            Diagnostics = "失败：EnumWindows 没找到属于本进程的可见窗口";
            return;
        }

        // ── 2) 先读原始样式，再改 ──
        if (!_originalSaved)
        {
            _originalStyle = Win32.GetWindowStyle(Handle, Win32.GWL_STYLE);
            _originalExStyle = Win32.GetWindowStyle(Handle, Win32.GWL_EXSTYLE);
            _originalSaved = true;
        }

        // ── 3) 去掉边框：整套**换掉**，不是叠加 ──
        // 写成 旧值 | WS_POPUP 的话，WS_CAPTION（标题栏）那一位还亮着，边框不会消失。
        Win32.SetWindowStyle(Handle, Win32.GWL_STYLE, Win32.WS_POPUP | Win32.WS_VISIBLE);

        // ── 4) 先摘掉 LAYERED，再重新戴上 ──
        // 为什么要绕这一下：分层窗口的透明参数（色键 / 统一 alpha）一旦设定，
        // 另一套机制不一定能覆盖它。摘掉再戴上能强制 Windows 重置分层状态，
        // 这样 F1 来回切换才更可能双向都生效。
        Win32.SetWindowStyle(Handle, Win32.GWL_EXSTYLE, Win32.WS_EX_TOOLWINDOW);
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);

        Win32.SetWindowStyle(Handle, Win32.GWL_EXSTYLE,
            Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW
            | (ClickThrough ? Win32.WS_EX_TRANSPARENT : 0u));

        // ── 5) 按当前方案设置透明 ──
        if (Mode == TransparencyMode.DwmAlpha)
        {
            // 把整扇窗声明成"一块玻璃"：DWM 不再往里面画不透明底板，
            // 于是 backbuffer 里 alpha=0 的地方就真的透出桌面。
            var margins = new Win32.MARGINS
            {
                cxLeftWidth = -1,
                cyTopHeight = -1,
                cxRightWidth = -1,
                cyBottomHeight = -1,
            };
            int hr = Win32.DwmExtendFrameIntoClientArea(Handle, ref margins);
            LastDetail = "DWM hr=0x" + hr.ToString("X8") + (hr == 0 ? " (OK)" : " ← 失败");
        }
        else
        {
            // 先把可能残留的"玻璃"取消掉（margin 全 0 = 不要玻璃），否则两套机制会打架。
            var zero = new Win32.MARGINS();
            Win32.DwmExtendFrameIntoClientArea(Handle, ref zero);

            // COLORREF 是 0x00BBGGRR，不是 RGB —— 手算很容易把顺序搞反，所以用 Win32.ToColorRef。
            uint colorRef = Win32.ToColorRef(KeyColor.r, KeyColor.g, KeyColor.b);
            bool ok = Win32.SetLayeredWindowAttributes(Handle, colorRef, 0, Win32.LWA_COLORKEY);
            LastDetail = "COLORKEY 0x" + colorRef.ToString("X6")
                       + (ok ? " (OK)" : " ← 失败 err=" + Marshal.GetLastWin32Error());
        }

        // ── 6) 置顶 ──
        // 顺序有讲究：上一步把 exstyle 整个换掉，顺带清掉了可能存在的 TOPMOST 位，
        // 所以必须在这之后用 SetWindowPos 重新钉上去。
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);

        Applied = true;
        Diagnostics = string.Format(
            "hwnd=0x{0:X}   style 0x{1:X8}→0x{2:X8}   exstyle 0x{3:X8}→0x{4:X8}\n{5}",
            Handle.ToInt64(),
            _originalStyle, Win32.GetWindowStyle(Handle, Win32.GWL_STYLE),
            _originalExStyle, Win32.GetWindowStyle(Handle, Win32.GWL_EXSTYLE),
            LastDetail);
    }

    /// <summary>
    /// 确保我们已经拿到自己窗口的句柄。没有就去找一次。
    ///
    /// 为什么单独抽出来？因为原来 ResizeTo / MoveTo 是这么写的：
    ///     if (Handle == IntPtr.Zero) return;
    /// —— 结果在"句柄还没找到"的时候，它们**一声不吭地什么都没做**。
    /// 我们就是被这个坑到的：第 2 帧调用 ResizeTo 时句柄还是空的，尺寸设置静默失败，
    /// 一直等到第 12 帧 Apply() 才第一次把句柄找出来。
    ///
    /// 教训：宁可"自己去找一次"，也不要"条件不满足就静默返回"。
    /// </summary>
    public static bool EnsureHandle()
    {
        if (Application.isEditor) return false;
        if (Handle != IntPtr.Zero) return true;
        Handle = FindOwnWindow();
        return Handle != IntPtr.Zero;
    }

    /// <summary>
    /// 客户区左上角在**屏幕**上的坐标（物理像素）。
    ///
    /// 为什么不用 GetWindowRect 相减？因为窗口可能有**看不见的边框**（Windows 10+ 的特性）——
    /// 那时候"窗口矩形"和"客户区"不是一回事，相减会算偏。
    /// `ClientToScreen` 传 (0,0) 进去得到的，就是客户区左上角，精确。
    ///
    /// 拖动时用它把"鼠标屏幕坐标"换算成"窗口该被放到哪里"。
    /// </summary>
    public static bool GetClientOrigin(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!EnsureHandle()) return false;

        var p = new Win32.POINT();
        p.x = 0;
        p.y = 0;
        if (!Win32.ClientToScreen(Handle, ref p)) return false;

        x = p.x;
        y = p.y;
        return true;
    }

    /// <summary>
    /// 只改"鼠标穿透"这**一个位**，不重做整套窗口样式。
    ///
    /// 为什么不复用 <see cref="Apply"/>？因为 Apply 会摘掉再戴上 WS_EX_LAYERED、
    /// 还会 SetWindowPos —— 那是"改窗口形态"用的，**一帧调一次太重**。
    /// 按像素穿透需要每帧根据鼠标位置决定穿不穿，所以必须有一个便宜的路子。
    ///
    /// 而且"没变就不动"：省掉每帧一次 Win32 调用（虽然不贵，但没必要）。
    /// </summary>
    public static void SetClickThrough(bool on)
    {
        if (Application.isEditor || Handle == IntPtr.Zero) return;
        if (ClickThrough == on) return;

        ClickThrough = on;

        uint ex = Win32.GetWindowStyle(Handle, Win32.GWL_EXSTYLE);
        if (on) ex |= Win32.WS_EX_TRANSPARENT;
        else ex &= ~Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowStyle(Handle, Win32.GWL_EXSTYLE, ex);
    }

    /// <summary>
    /// 当前窗口所在显示器的**工作区**（显示器整块区域去掉任务栏之后的那部分）。
    /// 单位物理像素，左上角为原点。
    ///
    /// 用 <c>MonitorFromWindow(MONITOR_DEFAULTTONEAREST)</c> 而不是"主显示器"：
    /// 多显示器时窗口在哪块屏上，就该用哪块屏的工作区 ——
    /// 拿主屏的去夹副屏上的窗口，会把窗口挪到另一块屏上去。
    /// </summary>
    public static bool GetWorkArea(out int left, out int top, out int right, out int bottom)
    {
        left = 0; top = 0; right = 0; bottom = 0;
        if (!EnsureHandle()) return false;

        IntPtr mon = Win32.MonitorFromWindow(Handle, Win32.MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;

        var mi = new Win32.MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(Win32.MONITORINFO));   // ⚠️ 不填这个，调用直接失败
        if (!Win32.GetMonitorInfo(mon, ref mi)) return false;

        left = mi.rcWork.left;
        top = mi.rcWork.top;
        right = mi.rcWork.right;
        bottom = mi.rcWork.bottom;
        return true;
    }

    /// <summary>
    /// 客户区**底边中点**在屏幕上的位置。单位物理像素、左上角为原点、y 向下为正。
    ///
    /// 为什么是这个点：她贴在窗口底边、**水平居中**（这是锚点约定 (0.5, 0) 的直接后果）。
    /// 所以「她在屏幕上没动」⟺「窗口底边中点没动」。
    ///
    /// ⚠️ 这里踩过一次：只固定窗口的**某一条边**是不够的 ——
    ///    窗口从 192 宽变成 480 宽时，固定左边缘会让窗口中心右移 144 像素，
    ///    而她跟着窗口中心走，于是**她右移 144 像素**。
    ///    用户看到的就是"一开设置她就往右跳一下"。固定边 ≠ 固定她。
    /// </summary>
    public static bool GetClientBottomCenter(out int cx, out int cy)
    {
        cx = 0; cy = 0;
        if (!EnsureHandle()) return false;

        Win32.RECT cur;
        if (!Win32.GetClientRect(Handle, out cur)) return false;
        int w = cur.right - cur.left;
        int h = cur.bottom - cur.top;

        int x, y;
        if (!GetClientOrigin(out x, out y)) return false;

        cx = x + w / 2;
        cy = y + h;        // y 向下为正，所以"底边"是 y + h
        return true;
    }

    /// <summary>
    /// 把客户区改成 width × height，让**底边中点仍然落在 (px, py)**，
    /// 最后把整个窗口夹回显示器工作区。
    ///
    /// 为什么锚点要由调用方传进来，而不是内部取"当前底边中点"：
    /// 关闭设置时要**还回打开之前的位置**（打开时可能因为放不下而被夹走过），
    /// 那个位置已经不是"当前"的了。所以锚点必须由调用方决定 ——
    /// 这个函数只管"把指定的一点摆到指定尺寸的窗口底边中点上"。
    ///
    /// 夹取放在最后：窗口贴着屏幕边缘时 480×540 就是放不进原位置，只能把她挪开（无解）。
    /// **但只要放得下，她一步都不会动。**
    ///
    /// 位置和尺寸**一次 SetWindowPos 给完**：分两次会看到窗口跳一下。
    ///
    /// ⚠️ 前提：SetWindowPos 收的是**窗口矩形**坐标，而我们要摆的是**客户区**。
    ///    对我们的窗口两者相等 —— 它是 WS_POPUP、没有 WS_THICKFRAME，
    ///    没有 Win10+ 那种"看不见的调整边框"。这一点早前实测过（GetWindowRect == GetClientRect）。
    ///    **哪天给窗口加上可调整大小的边框，这个前提就破了** ——
    ///    那时要用 GetClientOrigin 反测一次把差值补偿进去（这个类已经因为同样的原因
    ///    特意没用 GetWindowRect 相减，见 GetClientOrigin 的注释）。
    /// </summary>
    public static void ResizeAroundBottomCenter(int px, int py, int width, int height)
    {
        if (!EnsureHandle())
        {
            Diagnostics = "ResizeAroundBottomCenter 失败：还没找到自己的窗口句柄";
            return;
        }

        // 当前尺寸只为打日志用（看着"从多大变成多大"最容易发现异常）。
        // 真正的算法**不需要**当前尺寸 —— 因为锚点是调用方给的绝对值。
        Win32.RECT cur;
        int curW = 0, curH = 0;
        if (Win32.GetClientRect(Handle, out cur))
        {
            curW = cur.right - cur.left;
            curH = cur.bottom - cur.top;
        }

        int newX = px - width / 2;
        int newY = py - height;

        int wl, wt, wr, wb;
        bool clamped = false;
        if (GetWorkArea(out wl, out wt, out wr, out wb))
        {
            int ox = newX, oy = newY;
            if (newX + width > wr) newX = wr - width;
            if (newX < wl) newX = wl;
            if (newY + height > wb) newY = wb - height;
            if (newY < wt) newY = wt;      // 窗口比工作区还高时只能贴上边（无解，下边溢出去）
            clamped = (ox != newX || oy != newY);
        }
        else
        {
            Diagnostics = "ResizeAroundBottomCenter：拿不到工作区，没做夹取";
        }

        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, newX, newY, width, height,
            Win32.SWP_SHOWWINDOW);

        LastDetail = string.Format(
            "resize {0}x{1}  锚({2},{3}) -> 窗口({4},{5})  原 {6}x{7}{8}",
            width, height, px, py, newX, newY, curW, curH, clamped ? "  [夹过]" : "");
    }

    /// <summary>
    /// 只改窗口尺寸、不动位置。**正常情况下只用在启动阶段**（第 2/12 帧）——
    /// 那时"她在屏幕哪里"还没有含义，保持左上角不动才是确定的行为。
    /// 运行期改尺寸请用 <see cref="ResizeAroundBottomCenter"/>，否则她会跟着窗口中心漂。
    /// </summary>
    public static void ResizeTo(int width, int height)
    {
        if (!EnsureHandle())
        {
            Diagnostics = "ResizeTo 失败：还没找到自己的窗口句柄";
            return;
        }
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, width, height,
            Win32.SWP_NOMOVE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// 把窗口搬到屏幕坐标 (x, y)。左上角为原点，单位是物理像素。
    /// 下一步"用鼠标把它拖走"就要用这个。
    /// </summary>
    public static void MoveTo(int x, int y)
    {
        if (!EnsureHandle())
        {
            Diagnostics = "MoveTo 失败：还没找到自己的窗口句柄";
            return;
        }
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, x, y, 0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// 找到"我们自己进程"的窗口。
    ///
    /// 为什么不学网上教程用 GetActiveWindow()？
    /// 因为它返回的是"当前前台窗口"。如果用户在启动那一瞬间切到了别的程序，
    /// 你拿到的就是**别人家的窗口句柄** —— 然后你会把一个无辜的记事本
    /// 改成无边框置顶窗口。这个 bug 极难查，而且非常讨人厌。
    /// 所以这里按 PID 过滤，只认自己的窗口。
    /// </summary>
    static IntPtr FindOwnWindow()
    {
        uint ownPid = Win32.GetCurrentProcessId();
        IntPtr found = IntPtr.Zero;

        // EnumWindows 会把桌面上每一个顶层窗口都喂给这个 lambda。
        //   返回 true  = "这个不要，继续给我下一个"
        //   返回 false = "就它了，停止枚举"
        Win32.EnumWindows((hWnd, lParam) =>
        {
            uint pid;
            Win32.GetWindowThreadProcessId(hWnd, out pid);

            if (pid != ownPid) return true;                          // 不是自己的进程 → 跳过
            if (!Win32.IsWindowVisible(hWnd)) return true;           // 隐藏的辅助窗口 → 跳过
            if (Win32.GetWindowTextLength(hWnd) == 0) return true;   // 没标题的内部窗口 → 跳过

            found = hWnd;
            return false;                                            // 命中，停止枚举
        }, IntPtr.Zero);

        return found;
    }
}
