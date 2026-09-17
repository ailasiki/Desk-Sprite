using System;
using System.Runtime.InteropServices;

/// <summary>
/// Windows 原生 API 的"声明层"。
///
/// 这个文件里**没有任何逻辑**，只有两样东西：
///   1. 常量 —— 就是你说的那些"开关"。每个常量后面都注明了它拨的是哪一位。
///   2. [DllImport] 声明 —— 告诉 .NET："这个函数不在 C# 里，去某个 DLL 里找。"
///
/// 为什么要单独放一个文件？两个理由：
///   · 这些声明的正确签名和内存布局是"查出来的"，不是"想出来的"。写错了编译器不报错，
///     运行时会静默出错或者直接崩。把它们集中在一处、一次写对，比散落在业务代码里安全得多。
///   · 业务代码里就只剩"什么时候动窗口"这种有思考含量的东西，读起来干净。
///
/// 组合这些常量时**永远用按位或 | ，永远不用 +**。
/// 原因：| 是幂等的（拨一百次等于拨一次），+ 会进位（同一个开关拨两次就窜到隔壁位去了）。
/// </summary>
public static class Win32
{
    // ═══════════════════════════════════════════════════════════════
    //  常量：窗口样式（GWL_STYLE）—— "这个窗口长什么样子"
    // ═══════════════════════════════════════════════════════════════

    /// <summary>弹出式窗口：没有标题栏、没有边框、没有系统菜单。桌宠要的就是它。</summary>
    public const uint WS_POPUP = 0x80000000;

    /// <summary>可见。窗口不写这一位就是隐藏的。</summary>
    public const uint WS_VISIBLE = 0x10000000;

    // 下面这些是"普通窗口"身上的装饰。我们不是"加上 WS_POPUP"，
    // 而是"换掉整套样式"——所以认识它们是为了知道我们丢掉了什么。
    /// <summary>标题栏（含关闭按钮）</summary>
    public const uint WS_CAPTION = 0x00C00000;
    /// <summary>细边框</summary>
    public const uint WS_BORDER = 0x00800000;
    /// <summary>对话框外框</summary>
    public const uint WS_DLGFRAME = 0x00400000;
    /// <summary>可以用鼠标拖拽调整大小的厚边框</summary>
    public const uint WS_THICKFRAME = 0x00040000;
    /// <summary>左上角系统菜单（点它出"移动/大小/关闭"）</summary>
    public const uint WS_SYSMENU = 0x00080000;
    /// <summary>最小化按钮</summary>
    public const uint WS_MINIMIZEBOX = 0x00020000;
    /// <summary>最大化按钮</summary>
    public const uint WS_MAXIMIZEBOX = 0x00010000;

    // ═══════════════════════════════════════════════════════════════
    //  常量：扩展样式（GWL_EXSTYLE）—— "这个窗口有什么特殊行为"
    // ═══════════════════════════════════════════════════════════════

    /// <summary>分层窗口。**这是透明的法律前提**：不写这一位，任何透明设置都无效。</summary>
    public const uint WS_EX_LAYERED = 0x00080000;

    /// <summary>鼠标穿透：这块区域的鼠标事件交给下面的窗口。桌宠"空白处不挡路"靠它。</summary>
    public const uint WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>工具窗口：不进任务栏、不进 Alt+Tab 列表。桌宠不该在任务栏里占一格。</summary>
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>置顶。注意这个位只是"申请"，实际置顶靠 SetWindowPos 更可靠。</summary>
    public const uint WS_EX_TOPMOST = 0x00000008;

    /// <summary>强制出现在任务栏。和 TOOLWINDOW 效果相反，同时写两个时这个赢。</summary>
    public const uint WS_EX_APPWINDOW = 0x00040000;

    // ═══════════════════════════════════════════════════════════════
    //  常量：读写窗口样式时的"索引"
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get/SetWindowLongPtr 的 nIndex：我要读写"样式"</summary>
    public const int GWL_STYLE = -16;

    /// <summary>Get/SetWindowLongPtr 的 nIndex：我要读写"扩展样式"</summary>
    public const int GWL_EXSTYLE = -20;

    // ═══════════════════════════════════════════════════════════════
    //  常量：SetLayeredWindowAttributes 的开关
    // ═══════════════════════════════════════════════════════════════

    /// <summary>色键透明：把 crKey 这个颜色**整片抠掉**（B 方案，配品红用）</summary>
    public const uint LWA_COLORKEY = 0x00000001;

    /// <summary>整扇窗统一半透明（我们不用它，它会让整只宠物变淡而不是变透明）</summary>
    public const uint LWA_ALPHA = 0x00000002;

    // ═══════════════════════════════════════════════════════════════
    //  常量：SetWindowPos 的开关
    // ═══════════════════════════════════════════════════════════════

    /// <summary>别动尺寸</summary>
    public const uint SWP_NOSIZE = 0x0001;
    /// <summary>别动位置</summary>
    public const uint SWP_NOMOVE = 0x0002;
    /// <summary>别动层级顺序</summary>
    public const uint SWP_NOZORDER = 0x0004;
    /// <summary>不要抢焦点！桌宠抢焦点就是骚扰用户。</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>告诉 Windows "边框变了，重新算一遍"。去掉边框后不加它，外观有时不刷新。</summary>
    public const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>顺便把窗口显示出来</summary>
    public const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>SetWindowPos 的"插到谁后面"：钉在最上层</summary>
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    /// <summary>取消置顶</summary>
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    // ═══════════════════════════════════════════════════════════════
    //  结构体：布局必须和 C 语言那边**逐字节一致**
    // ═══════════════════════════════════════════════════════════════

    /// <summary>DwmExtendFrameIntoClientArea 的参数。四个 -1 表示"整扇窗都算玻璃"。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    /// <summary>屏幕坐标（左上角为原点，单位是物理像素）</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    /// <summary>
    /// 一个矩形。用**物理像素**、左上角为原点。
    ///
    /// 注意 Windows 里 RECT 的惯例是 right/bottom 是**开区间**：
    /// 宽度 = right − left，高度 = bottom − top，不要写 (right − left + 1)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /// <summary>
    /// GetMonitorInfo 的入参/出参。
    ///
    /// ⚠️ <c>cbSize</c> 必须由调用方填成这个结构的大小（64 位下是 40 字节）。
    ///    Windows 靠它在结构演进时区分版本；填 0 或者填错，GetMonitorInfo 会**直接返回 false**。
    ///    而它失败的表现是"拿不到工作区" —— 很容易被误判成"这台机器没有显示器"之类，
    ///    然后往完全错误的方向查。所以 DeskWindow.GetWorkArea 里用 Marshal.SizeOf 填。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;   // 显示器整块区域（含任务栏盖住的部分）
        public RECT rcWork;      // **工作区**：去掉任务栏之后用户真正能用的那块
        public uint dwFlags;
    }

    // ═══════════════════════════════════════════════════════════════
    //  函数声明
    // ═══════════════════════════════════════════════════════════════

    // ---- 读写窗口样式 ----
    // 这里故意声明了两套：64 位系统必须用 SetWindowLongPtr，32 位系统只有 SetWindowLong。
    // 用 32 位那个去写 64 位的窗口样式"看起来能跑"，但它是在错误的宽度上写内存 ——
    // 属于"今天没事、明天炸"的那类 bug。下面的包装函数替你按平台选对的。

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// 读窗口样式（传 GWL_STYLE 或 GWL_EXSTYLE）。
    /// 返回值用 uint：因为样式就是一堆位掩码，用有符号数读出来会变成负数，看着像 bug。
    /// </summary>
    public static uint GetWindowStyle(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? unchecked((uint)GetWindowLongPtr64(hWnd, nIndex).ToInt64())
            : unchecked((uint)GetWindowLong32(hWnd, nIndex));
    }

    /// <summary>写窗口样式（传 GWL_STYLE 或 GWL_EXSTYLE）。</summary>
    public static void SetWindowStyle(IntPtr hWnd, int nIndex, uint value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, (IntPtr)unchecked((long)value));
        else SetWindowLong32(hWnd, nIndex, unchecked((int)value));
    }

    // ---- 位置 / 尺寸 / 层级 ----
    /// <summary>改窗口的位置、尺寸、层级。只改其中一样时，用 SWP_NOMOVE / SWP_NOSIZE 把其它锁住。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int X, int Y, int cx, int cy, uint uFlags);

    // ---- 分层窗口透明 ----
    /// <summary>
    /// 设置分层窗口的透明方式。
    /// crKey 是 COLORREF 格式（0x00BBGGRR，注意不是 RGB！），配合 LWA_COLORKEY 使用。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>把 RGB 转成 Windows 的 COLORREF（0x00BBGGRR）。自己手算很容易搞错顺序。</summary>
    public static uint ToColorRef(byte r, byte g, byte b)
    {
        return (uint)((b << 16) | (g << 8) | r);
    }

    // ---- 找到"我自己的窗口" ----
    /// <summary>EnumWindows 的回调。返回 false 表示"够了，停止枚举"。</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>遍历当前桌面上所有顶层窗口</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>问一个窗口属于哪个进程。这是我们能"只认自己"的关键。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>这个窗口现在可见吗（隐藏的辅助窗口要排除掉）</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>窗口标题有多长。用来排除那些没标题的内部窗口。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    /// <summary>当前进程的 PID。比 System.Diagnostics.Process 轻，而且不会和 UnityEngine.Debug 撞名。</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();

    // ---- 焦点 / 鼠标 ----
    /// <summary>把焦点抢过来。M0 测试阶段用来确保热键能用。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 拿鼠标在屏幕上的位置。**穿透状态下 Unity 的 Input 会冻住，只有它能用。**
    /// 以后做"按像素命中测试"就靠它。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// 把"客户区坐标"换算成"屏幕坐标"（原地改写传入的点）。
    ///
    /// 为什么用它而不是 GetWindowRect？因为窗口可能有**看不见的边框**，
    /// 那时候"窗口矩形"和"客户区"不是一回事，直接相减会算偏。
    /// 传 (0,0) 进去得到的，就是客户区左上角的屏幕坐标 —— 这个换算是精确的。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    // ---- 窗口/显示器几何 ----

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>MONITOR_DEFAULTTONEAREST：窗口跨屏或不在任何屏上时，取离它最近的那块显示器。</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    // CharSet.Auto 会让 .NET 自动去找 GetMonitorInfoW —— user32 里**没有**叫
    // "GetMonitorInfo" 的导出，只有 A/W 两个版本。写成 CharSet.None 就会运行时找不到入口点。
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ── 文件夹选择对话框（shell32）──
    //
    // 为什么用 SHBrowseForFolder 而不是现代的 IFileOpenDialog：
    //   后者要手写 COM 接口的 vtable 顺序，**排错一个字段就是崩溃**，而且没法在这里测试。
    //   前者只是一次 P/Invoke + 一个结构体：UI 旧一点（经典目录树），但错了不会死。
    //   等它跑通了再升级成现代对话框也不难。

    /// <summary>
    /// SHBrowseForFolder 的参数。
    /// ⚠️ 字段顺序必须和 C 头文件里**逐字节一致** —— 这是整个调用风险最大的地方。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BROWSEINFOW
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        /// <summary>输出缓冲。**由调用方分配**（长度 MAX_PATH）—— 用 IntPtr 而不是 string，
        /// 因为结构体里的 string 在 .NET 里是"只读输入"，填不回来。</summary>
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    public const uint BIF_RETURNONLYFSDIRS = 0x00000001;   // 只能选目录，不能选文件
    public const uint BIF_EDITBOX = 0x00000010;            // 带一个可以直接打路径的输入框
    public const uint BIF_NEWDIALOGSTYLE = 0x00000040;     // 可调整大小 + 能新建文件夹
    public const uint BIF_USENEWUI = BIF_NEWDIALOGSTYLE | BIF_EDITBOX;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SHBrowseForFolder(ref BROWSEINFOW lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SHGetPathFromIDList(IntPtr pidl, System.Text.StringBuilder pszPath);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    /// <summary>BIF_NEWDIALOGSTYLE 要求调用线程初始化过 COM —— 见 ShellOpen.PickFolder。</summary>
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    // ---- 全局按键状态（逃生舱热键用） ----

    // 虚拟键码。Windows 给每个键编了个号，这几个是我们可能用到的。
    // 字母和数字键的码就是它的 ASCII 大写值：'A'=0x41, 'Q'=0x51, '0'=0x30 …
    public const int VK_LBUTTON = 0x01;   // 鼠标左键
    public const int VK_RBUTTON = 0x02;   // 鼠标右键
    public const int VK_SHIFT = 0x10;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;    // Alt
    public const int VK_ESCAPE = 0x1B;
    public const int VK_C = 0x43;
    public const int VK_L = 0x4C;
    public const int VK_M = 0x4D;
    public const int VK_O = 0x4F;
    public const int VK_Q = 0x51;
    public const int VK_S = 0x53;
    public const int VK_F12 = 0x7B;

    /// <summary>
    /// 问系统："这个键现在**物理上**按着吗？"—— 全局的，不管我们的窗口有没有焦点。
    ///
    /// 返回值是 short，最高位（0x8000）为 1 表示"当前按下"。
    /// 低位的含义是"自上次调用以来被按过"，我们不用它。
    ///
    /// 为什么不用 RegisterHotKey：那个靠往窗口消息队列投递 WM_HOTKEY，
    /// 而 Unity 不给我们消息循环 —— 消息投进来了没人取，我们永远收不到。
    /// GetAsyncKeyState 不需要消息循环，直接问系统。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>把"最高位是不是 1"这件事藏起来，调用处就写成 Win32.IsKeyDown(Win32.VK_Q)</summary>
    public static bool IsKeyDown(int vKey)
    {
        return (GetAsyncKeyState(vKey) & 0x8000) != 0;
    }

    // ---- DWM（桌面窗口管理器）：逐像素透明 ----
    /// <summary>
    /// 把窗口的客户区声明成"一块玻璃"，DWM 就不再往里面画不透明底板。
    /// 于是 backbuffer 里 alpha=0 的地方就真的透出桌面了。
    /// 返回 HRESULT：0 表示成功。所以它不返回 bool，而是返回 int。
    /// </summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);
}
