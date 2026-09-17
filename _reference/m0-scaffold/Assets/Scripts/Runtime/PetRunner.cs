using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DeskSprite
{
    /// <summary>
    /// M0 阶段的司机：负责在正确的时机去改窗口，以及响应几个测试热键。
    ///
    /// 为什么改窗口要挑时机？因为窗口刚创建时，Unity 还没把它显示出来，
    /// 也还没应用屏幕分辨率。抢在它前面改样式，很容易被之后的重建覆盖掉。
    /// 所以这里按"第几帧"来安排，而不是在 Awake 里一口气做完。
    /// 这类"等两帧再做"的技巧在 Unity 里到处都是，值得记住。
    /// </summary>
    public class PetRunner : MonoBehaviour
    {
        public Camera PetCamera;

        int _frame;

        // M0 用的窗口尺寸，故意小一点，免得挡住你干活
        const int TestWindowWidth = 480;
        const int TestWindowHeight = 320;
        const int TestWindowX = 80;
        const int TestWindowY = 80;

        void Update()
        {
            _frame++;

            // 第 2 帧：让窗口变成我们要的尺寸（必须在改窗口样式之前）
            if (_frame == 2 && !Application.isEditor)
            {
                Screen.SetResolution(TestWindowWidth, TestWindowHeight, FullScreenMode.Windowed);
            }

            // 第 12 帧：分辨率已经生效，此刻再动窗口样式
            if (_frame == 12)
            {
                DeskWindow.Apply();
                DeskWindow.MoveTo(TestWindowX, TestWindowY);
                DeskWindow.Focus();
                SyncCameraBackground();
                Debug.Log("[DeskSprite] M0 窗口诊断: " + DeskWindow.Diagnostics);
            }

            if (Input.GetKeyDown(KeyCode.F1)) // 换透明方案
            {
                DeskWindow.ToggleMode();
                SyncCameraBackground();
                Debug.Log("[DeskSprite] 切换透明方案 -> " + DeskWindow.Mode + " | " + DeskWindow.Diagnostics);
            }

            if (Input.GetKeyDown(KeyCode.F2)) // 切换鼠标穿透
            {
                DeskWindow.ToggleClickThrough();
                Debug.Log("[DeskSprite] 鼠标穿透 -> " + (DeskWindow.ClickThrough ? "开" : "关"));
            }

            if (Input.GetKeyDown(KeyCode.Escape)) // 退出
            {
                Quit();
            }
        }

        void SyncCameraBackground()
        {
            if (PetCamera != null) PetCamera.backgroundColor = DeskWindow.CameraBackground;
        }

        /// <summary>
        /// 诊断面板。注意这里**故意只用 ASCII 字符**：
        /// Unity 内置的 GUI 字体在打包后不一定有中文字形，会显示成方块。
        /// 中文放在注释和文档里，屏幕上的字用英文 —— 这是打包后一定要检查的坑。
        /// </summary>
        void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.label);
            style.fontSize = 12;
            style.richText = false;
            style.wordWrap = true;

            var head = new GUIStyle(style);
            head.fontStyle = FontStyle.Bold;

            var area = new Rect(8f, 8f, Screen.width - 16f, 108f);
            GUI.Box(area, GUIContent.none);

            string mode = DeskWindow.Mode == TransparencyMode.DwmAlpha ? "DWM-alpha (per-pixel)" : "color-key (magenta)";
            string state = Application.isEditor
                ? "EDITOR: window untouched (checkerboard = fake transparency)"
                : (DeskWindow.Applied ? "PLAYER: window restyled OK" : "PLAYER: NOT applied yet");

            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 6f, area.width - 16f, area.height - 12f));
            GUILayout.Label("DeskSprite M0  |  " + state, head);
            GUILayout.Label("transparency: " + mode + "     click-through: " + (DeskWindow.ClickThrough ? "ON" : "OFF"), style);
            GUILayout.Label("frame " + _frame + "   screen " + Screen.width + "x" + Screen.height, style);
            GUILayout.Label(DeskWindow.Diagnostics, style);
            GUILayout.Label("F1 = switch transparency   F2 = click-through   ESC = quit", style);
            GUILayout.EndArea();
        }

        void Quit()
        {
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
