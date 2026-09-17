using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace DeskSprite
{
    /// <summary>窗口背景的透明方案。两套都实现了，因为哪套在你的机器上生效取决于显卡驱动和 DXGI 设置。</summary>
    public enum TransparencyMode
    {
        /// <summary>DWM 逐像素透明：相机背景 alpha=0，交给桌面窗口管理器合成。支持半透明边缘（以后能做影子）。</summary>
        DwmAlpha = 0,

        /// <summary>色键透明：把品红那一整片颜色抠掉。像素画的硬边绝不会有毛边，但抗锯齿的文字和半透明会脏。</summary>
        ColorKey = 1,
    }

    /// <summary>
    /// 把 Unity 窗口改造成"桌面宠物"形态：无边框、永远置顶、不进任务栏/Alt-Tab、背景可透明。
    ///
    /// 注意：这个类只在**打包出来的 exe** 里动手。在 Editor 里它什么都不做，
    /// 因为 Editor 的"游戏画面"是嵌在 Unity 编辑器窗口里的一个面板，
    /// 一旦对窗口动手就会把整个 Unity 编辑器改成无边框置顶窗口 —— 那是灾难。
    /// Editor 里想看美术效果用棋盘格背景代替（见 PetBootstrap）。
    /// </summary>
    public static class DeskWindow
    {
        // ---------- Win32 常量 ----------
        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;

        const uint WS_POPUP = 0x80000000;
        const uint WS_VISIBLE = 0x10000000;

        const uint WS_EX_LAYERED = 0x00080000;
        const uint WS_EX_TRANSPARENT = 0x00000020;
        const uint WS_EX_TOOLWINDOW = 0x00000080;

        const uint LWA_COLORKEY = 0x00000001;

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_SHOWWINDOW = 0x0040;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // ---------- Win32 导入 ----------
        // 64 位下必须用 SetWindowLongPtr，用 SetWindowLong 在 x64 上是错的（虽然常常"看起来能跑"）。
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

        static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, value)
                : (IntPtr)SetWindowLong32(hWnd, nIndex, value.ToInt32());
        }

        static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : (IntPtr)GetWindowLong32(hWnd, nIndex);
        }

        // ---------- 对外状态 ----------
        /// <summary>色键模式用的品红。相机背景色必须和它一模一样，才能被整片抠掉。</summary>
        public static readonly Color32 KeyColor = new Color32(255, 0, 255, 255);

        public static TransparencyMode Mode { get; private set; } = TransparencyMode.DwmAlpha;
        public static IntPtr Handle { get; private set; } = IntPtr.Zero;
        /// <summary>窗口改造是否真的执行过（Editor 里永远是 false）。</summary>
        public static bool Applied { get; private set; }
        /// <summary>点它会不会穿透。开了之后鼠标事件会落到桌面上，宠物就收不到点击了。</summary>
        public static bool ClickThrough { get; private set; }

        public static string LastError { get; private set; } = "";
        /// <summary>给 OnGUI 显示的诊断信息，M0 阶段全靠它看结果。</summary>
        public static string Diagnostics { get; private set; } = "(还没执行)";

        /// <summary>相机该用的背景色。DwmAlpha 要 alpha=0；色键模式要实心品红。</summary>
        public static Color CameraBackground
        {
            get
            {
                if (Application.isEditor) return new Color(0.10f, 0.10f, 0.12f, 1f);
                return Mode == TransparencyMode.DwmAlpha
                    ? new Color(0f, 0f, 0f, 0f)
                    : (Color)KeyColor;
            }
        }

        // ---------- 主流程 ----------

        /// <summary>把当前模式重新应用到窗口上。可以在运行时反复调用（切换热键就靠它）。</summary>
        public static bool Apply()
        {
            if (Application.isEditor)
            {
                Applied = false;
                Diagnostics = "Editor 模式：故意不改窗口";
                return false;
            }

            if (Handle == IntPtr.Zero)
            {
                Handle = FindOwnWindow();
                if (Handle == IntPtr.Zero)
                {
                    LastError = "EnumWindows 没找到本进程的可见窗口";
                    Diagnostics = "错误：" + LastError;
                    return false;
                }
            }

            try
            {
                // 1) 去掉标题栏和边框。WS_POPUP 是"弹出式窗口"，天然没有系统装饰。
                SetWindowLongPtr(Handle, GWL_STYLE, (IntPtr)(WS_POPUP | WS_VISIBLE));

                // 2) 分层窗口（透明的前提）+ TOOLWINDOW（不进任务栏、不进 Alt-Tab）
                uint exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                if (ClickThrough) exStyle |= WS_EX_TRANSPARENT;
                SetWindowLongPtr(Handle, GWL_EXSTYLE, (IntPtr)exStyle);

                string detail;
                if (Mode == TransparencyMode.DwmAlpha)
                {
                    // 把整扇窗变成"一块玻璃"：DWM 不再往里面画不透明底板，
                    // 于是 backbuffer 的 alpha=0 区域就真的透出桌面。
                    var margins = new MARGINS
                    {
                        cxLeftWidth = -1,
                        cyTopHeight = -1,
                        cxRightWidth = -1,
                        cyBottomHeight = -1,
                    };
                    int hr = DwmExtendFrameIntoClientArea(Handle, ref margins);
                    detail = "DwmExtendFrameIntoClientArea hr=0x" + hr.ToString("X8")
                             + (hr == 0 ? " (OK)" : " ← 失败");
                }
                else
                {
                    // COLORREF 是 0x00BBGGRR，不是 RGB。这里是最容易写错的地方。
                    uint colorRef = (uint)((KeyColor.b << 16) | (KeyColor.g << 8) | KeyColor.r);
                    bool ok = SetLayeredWindowAttributes(Handle, colorRef, 0, LWA_COLORKEY);
                    detail = "SetLayeredWindowAttributes key=0x" + colorRef.ToString("X6")
                             + (ok ? " (OK)" : " ← 失败, err=" + Marshal.GetLastWin32Error());
                }

                // 3) 永远置顶
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

                Applied = true;
                LastError = "";
                Diagnostics = string.Format(
                    "hwnd=0x{0:X}  模式={1}  穿透={2}\n{3}",
                    Handle.ToInt64(), Mode, ClickThrough ? "开" : "关", detail);
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Diagnostics = "异常：" + e.Message;
                return false;
            }
        }

        /// <summary>换一套透明方案并立刻重新应用。</summary>
        public static void ToggleMode()
        {
            Mode = Mode == TransparencyMode.DwmAlpha ? TransparencyMode.ColorKey : TransparencyMode.DwmAlpha;
            Apply();
        }

        public static void ToggleClickThrough()
        {
            ClickThrough = !ClickThrough;
            Apply();
        }

        /// <summary>把窗口挪到屏幕坐标 (x, y)，单位是物理像素，左上角为原点。</summary>
        public static void MoveTo(int x, int y)
        {
            if (Handle == IntPtr.Zero || Application.isEditor) return;
            SetWindowPos(Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        public static void Focus()
        {
            if (Handle != IntPtr.Zero && !Application.isEditor) SetForegroundWindow(Handle);
        }

        /// <summary>
        /// 找到"自己这个进程"的可见主窗口。
        ///
        /// 为什么不用网上教程里那句 GetActiveWindow()？因为它返回的是**当前前台窗口**，
        /// 如果玩家在启动的瞬间切到了别的程序，你拿到的就是别人家的窗口句柄 ——
        /// 然后你会把一个无辜的记事本改成无边框置顶的透明窗口。这个 bug 很难查，也很讨人厌。
        /// 所以这里按 PID 过滤，只认自己的窗口。
        /// </summary>
        static IntPtr FindOwnWindow()
        {
            // 故意不 using System.Diagnostics，否则 Debug 会和 UnityEngine.Debug 撞名。
            uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != ownPid) return true;          // 不是自己的进程，跳过
                if (!IsWindowVisible(hWnd)) return true; // 隐藏的辅助窗口，跳过
                if (GetWindowTextLength(hWnd) == 0) return true; // 没标题的，跳过
                found = hWnd;
                return false;                            // 找到了，停止枚举
            }, IntPtr.Zero);

            return found;
        }
    }
}
