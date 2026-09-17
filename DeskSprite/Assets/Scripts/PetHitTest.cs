using System;
using UnityEngine;

/// <summary>
/// 按像素命中测试：鼠标现在压在**她画到的像素**上吗？
///
/// 为什么不用 Unity 的 `Input.mousePosition`？两个原因：
///   ① 它需要窗口有焦点 —— 而桌面宠物几乎永远没有焦点
///   ② 它的坐标系是 Unity 的虚拟屏幕，我们真正需要的是**操作系统级的真实鼠标位置**
/// 所以整条链都走 Win32：`GetCursorPos` + `ClientToScreen`。
///
/// ⚠️ 这条坐标链**只要错一环，表现就是"永远命中"或"永远不命中"**。
/// 所以 `Diagnostics` 会把中间值都带出来，并且第一次进窗口时会往日志里打一整行。
///
/// 坐标链（六步）：
/// <code>
///   GetCursorPos            鼠标在**屏幕**上的物理像素坐标
///   - ClientToScreen(0,0)   减去窗口客户区左上角 → Windows 客户区坐标（原点左上、y 向下）
///   → Unity 屏幕坐标         原点左下、y 向上，所以 y 要翻过来
///   → 世界坐标               除以 Z，再减去半个屏幕
///   → 帧内像素坐标           减去**她当前的世界坐标**，再按锚点修正
///   → 查 alpha               命中 = 不透明
/// </code>
///
/// ⚠️⚠️ 最后一步是踩过大坑的地方，写清楚免得再犯：
///   原来这里写的是 <c>ty = wy + FrameHeight * 0.5f</c>，意思是"她的原点在可见区域的竖直中心"。
///   这只在 **窗口高 == 帧高 × Z** 时成立。正常模式正好满足（264 = 88×3），
///   所以怎么测都是对的；设置模式把窗口放大到 480 之后，帧中心跑到 wy = −36 而不是 0，
///   于是算出来的贴图 y 一律小 36 —— 表现是**她的下半截对鼠标完全没反应**
///   （脚和腿算出来是负数，落在帧外），而上半截是错位命中。
///
///   教训：不要用"只在某种尺寸下碰巧相等"的量去推算。**直接问她现在在哪。**
///   （这和之前"她浮到窗口中间"那次是同一类错误：那次是拿 −FrameHeight/2 当她的位置。）
/// </summary>
public static class PetHitTest
{
    /// <summary>v1 硬编码 1：给命中区域膨胀一圈，防边缘处穿透状态反复开合（见 PetAction.IsOpaque）。</summary>
    public const int HitPadding = 1;

    /// <summary>鼠标现在压在她画到的像素上吗？</summary>
    public static bool CursorOnPet { get; private set; }

    /// <summary>鼠标左键现在按着吗？**全局**读取，不需要窗口有焦点。</summary>
    public static bool LeftButtonDown { get; private set; }

    /// <summary>鼠标右键现在按着吗？（同样全局读）—— "右键点她打开设置"要用它。</summary>
    public static bool RightButtonDown { get; private set; }

    /// <summary>换算出来的贴图像素坐标（带小数，也可能越界）。调试用。</summary>
    public static Vector2 CursorTexel { get; private set; }

    /// <summary>给面板看的短诊断。</summary>
    public static string Diagnostics { get; private set; } = "(还没测)";

    // 只在**窗口尺寸变化时**重新打一次坐标链，而不是一辈子只打一次。
    // 为什么？因为这次的 bug 正是"只在某个窗口尺寸下才出现"的 ——
    // 一次性日志永远只在启动时那个尺寸下留下证据，等于没有证据。
    static int _loggedW = -1;
    static int _loggedH = -1;

    /// <param name="petWorld">她当前的世界坐标 —— 也就是**帧的底边中点**（锚点约定）。</param>
    public static void Update(PetSkin skin, PetAction action, int frameIndex, Vector3 petWorld)
    {
        CursorOnPet = false;
        LeftButtonDown = Win32.IsKeyDown(Win32.VK_LBUTTON);
        RightButtonDown = Win32.IsKeyDown(Win32.VK_RBUTTON);

        if (skin == null || action == null || action.Alphas == null || action.FrameCount == 0)
        {
            Diagnostics = "no skin";
            return;
        }

        // ⚠️ Handle 只有在打包后的 exe 里才有值。
        //    Editor 里 DeskWindow 故意不动窗口，所以这里也测不了 —— 命中测试**必须打包才能验**。
        IntPtr h = DeskWindow.Handle;
        if (h == IntPtr.Zero)
        {
            Diagnostics = "no hwnd (Editor? need a build)";
            return;
        }

        Win32.POINT p;
        if (!Win32.GetCursorPos(out p))
        {
            Diagnostics = "GetCursorPos failed";
            return;
        }

        var origin = new Win32.POINT();
        origin.x = 0;
        origin.y = 0;
        if (!Win32.ClientToScreen(h, ref origin))
        {
            Diagnostics = "ClientToScreen failed";
            return;
        }

        int cx = p.x - origin.x;
        int cy = p.y - origin.y;

        // ── Windows 客户区（原点左上、y 向下）→ Unity 屏幕坐标（原点左下、y 向上）──
        // 加 0.5 是按"像素中心"对齐。不加的话整条链会偏半个像素 —— 平时看不出来，
        // 但在边缘像素上会让命中判定随机地差一格。
        float ux = cx + 0.5f;
        float uy = Screen.height - cy - 0.5f;

        float z = skin.Scale;

        // Unity 屏幕像素 → 世界单位。
        // 这里假设**相机在世界坐标 (0,0)**，这是 PetBootstrap 保证的（2D 模板的 Main Camera，
        // 我们从来没动过它的 x/y）。可见世界范围 = (Screen.width/Z) × (Screen.height/Z)，
        // 屏幕中心 = 世界原点。注意：**这半个屏幕是"相机"的一半，不是"她"的一半**。
        float wx = (ux - Screen.width * 0.5f) / z;
        float wy = (uy - Screen.height * 0.5f) / z;

        // 世界单位 → 帧内像素。PPU = 1，所以 1 世界单位 = 1 贴图像素。
        // 锚点是帧的**底边中点**，所以：
        //   x：她原点的左右各半个帧宽  → 减去她的 x，再补回半个帧宽
        //   y：她原点就是帧的**底边**  → 直接减去她的 y（不要加半个帧高！）
        float tx = wx - petWorld.x + skin.FrameWidth * 0.5f;
        float ty = wy - petWorld.y;
        CursorTexel = new Vector2(tx, ty);

        // 注意：这里**不能**因为"看起来在帧外"就提前 return ——
        // 膨胀会让帧外一圈也算命中，提前返回会把那圈的效果吃掉。
        CursorOnPet = action.IsOpaque(frameIndex, Mathf.FloorToInt(tx), Mathf.FloorToInt(ty), HitPadding);

        // 鼠标第一次进到窗口里（或者窗口尺寸变了）时，把整条链的中间值打进日志。
        // 命中出问题时，光看"HIT/miss"分不清是哪一环错的 —— 有了这行就能一眼定位。
        bool insideWindow = (cx >= 0 && cy >= 0 && cx < Screen.width && cy < Screen.height);
        if (insideWindow && (Screen.width != _loggedW || Screen.height != _loggedH))
        {
            _loggedW = Screen.width;
            _loggedH = Screen.height;
            Debug.Log(string.Format(
                "[DeskSprite] hit chain: cursor=({0},{1}) clientOrigin=({2},{3}) client=({4},{5}) "
                + "unity=({6:F1},{7:F1}) world=({8:F2},{9:F2}) pet=({10:F2},{11:F2}) "
                + "texel=({12:F2},{13:F2}) frame={14}x{15} Z={16} screen={17}x{18}",
                p.x, p.y, origin.x, origin.y, cx, cy, ux, uy, wx, wy,
                petWorld.x, petWorld.y, tx, ty,
                skin.FrameWidth, skin.FrameHeight, z, Screen.width, Screen.height));
        }

        Diagnostics = string.Format("{0} ({1:F1},{2:F1})",
                                    CursorOnPet ? "HIT" : "miss", tx, ty);
    }
}
