using System.Collections.Generic;
using UnityEngine;

namespace DeskSprite
{
    /// <summary>
    /// 启动引导。
    ///
    /// 关键点：为什么不用场景文件？
    /// Unity 的 .unity 场景是 YAML，手写容易写坏，而且在聊天窗口里改场景等于用文本编辑器
    /// 拼一个二进制结构。用 [RuntimeInitializeOnLoadMethod] 就不一样了：
    /// 空场景按 Play，这个方法会在场景加载完后自己跑，把相机、精灵、控制器全部凭空造出来。
    /// 好处是"整个游戏"就是几个 .cs 文件，可以 diff、可以 code review、可以随时重建。
    ///
    /// 等以后内容复杂了（多个场景、预制体），我们再回到场景文件，那时你就知道该怎么取舍了。
    /// </summary>
    public static class PetBootstrap
    {
        /// <summary>1 个世界单位 = 1 个屏幕像素。以后要给宠物放大，改这个数就行。</summary>
        public const float PixelsPerUnit = 1f;

        /// <summary>
        /// 刻晴的"神之眼"配色小宝石，16×16。
        /// 这是 M1 那套像素画流程的试机样品 —— 证明"字符网格 → 贴图 → 精灵"这条路是通的。
        /// 等到画刻晴本人，就是把这个网格换成 48×64 或者 64×96 的。
        /// </summary>
        static readonly string[] GemGrid =
        {
            "................",
            ".......11.......",
            "......1331......",
            ".....133331.....",
            "....13322331....",
            "...1332222331...",
            "..133222222331..",
            ".13322222222331.",
            ".13222222222231.",
            "..132222222231..",
            "...1322222231...",
            "....13222231....",
            ".....132231.....",
            "......1331......",
            ".......11.......",
            "................",
        };

        static readonly Dictionary<char, Color32> GemPalette = new Dictionary<char, Color32>
        {
            { '1', new Color32(0x3A, 0x1A, 0x5E, 255) }, // 深紫：描边
            { '2', new Color32(0x7A, 0x3F, 0xD1, 255) }, // 主紫：雷元素
            { '3', new Color32(0xC9, 0xA8, 0xFF, 255) }, // 淡紫：高光
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            // 桌宠最重要的一条：失焦之后必须继续动。不然你一点别的窗口它就僵住了。
            Application.runInBackground = true;

            // 30 帧够了。像素动画一秒 8~12 帧，60 帧只是白烧 CPU 和电池。
            // 以后要拖拽更顺滑可以调到 60，但先养成"算清楚要多少帧"的习惯。
            Application.targetFrameRate = 30;

            // 关掉 MSAA：多重采样会让 DWM 透明那套打法失效（边缘被 resolve 过）。
            QualitySettings.antiAliasing = 0;

            var root = new GameObject("DeskSprite");
            Object.DontDestroyOnLoad(root);

            var cam = BuildCamera(root.transform);
            BuildScene(root.transform);

            var runner = root.AddComponent<PetRunner>();
            runner.PetCamera = cam;

            Debug.Log("[DeskSprite] Boot 完成。Editor=" + Application.isEditor);
        }

        static Camera BuildCamera(Transform parent)
        {
            // 空场景里本来就有一台 Main Camera（还有一盏平行光）。留着它会白渲染一遍，
            // 而且谁最后渲染谁说话，容易让人困惑 —— 所以先全部关掉。
            foreach (var existing in Object.FindObjectsOfType<Camera>())
                existing.enabled = false;

            var go = new GameObject("PetCamera");
            go.transform.SetParent(parent, false);
            go.tag = "MainCamera";

            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = Screen.height / (2f * PixelsPerUnit); // 1 单位 = 1 像素
            cam.nearClipPlane = -10f;
            cam.farClipPlane = 100f;
            cam.transform.position = new Vector3(0f, 0f, -10f);

            // 背景就是"桌面透出来的地方"，颜色由 DeskWindow 按透明方案决定。
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = DeskWindow.CameraBackground;

            // HDR 会改变 backbuffer 的格式，alpha 通道可能就保不住了。
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.depth = 100f;

            return cam;
        }

        static void BuildScene(Transform parent)
        {
            // Editor 里背景故意做成不透明的棋盘格，模拟"这里是透明的"。
            // 因为在 Editor 里我们不敢碰窗口，否则会把 Unity 编辑器本身改成无边框置顶窗口。
            if (Application.isEditor)
            {
                int w = Mathf.Min(Screen.width, 2048);
                int h = Mathf.Min(Screen.height, 2048);
                var checker = PixelArt.Checkerboard(w, h, 8,
                    new Color32(0x2A, 0x2A, 0x30, 255),
                    new Color32(0x22, 0x22, 0x28, 255));
                var sr = MakeRenderer("Checkerboard", checker, parent);
                sr.sortingOrder = -100;
            }

            // 试机样品：神之眼宝石，放大 4 倍看清楚每个像素
            var gemTex = PixelArt.FromGrid(GemGrid, GemPalette, new Color32(0, 0, 0, 0));
            var gem = MakeRenderer("Gem", gemTex, parent);
            gem.transform.position = new Vector3(-96f, 0f, 0f);
            gem.transform.localScale = Vector3.one * 4f;

            // 体检表：贴在右边，1:1 原始大小（64 像素就是 64 像素）
            var cardTex = PixelArt.DiagnosticCard(64);
            var card = MakeRenderer("DiagnosticCard", cardTex, parent);
            card.transform.position = new Vector3(32f, 0f, 0f);
        }

        static SpriteRenderer MakeRenderer(string name, Texture2D tex, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = PixelArt.ToSprite(tex, PixelsPerUnit);
            return sr;
        }
    }
}
