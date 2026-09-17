#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeskSprite.EditorTools
{
    /// <summary>
    /// 给 Unity 菜单加两个按钮，顺便让命令行也能调（batchmode 打包要靠它）。
    ///
    /// 命令行用法（我在这边打包时会用）：
    ///   Unity.exe -batchmode -quit -projectPath "D:\DSH WorkSpace\Desk Sprite" `
    ///             -executeMethod DeskSprite.EditorTools.BuildTools.CompileCheck -logFile -
    /// </summary>
    public static class BuildTools
    {
        const string ScenesFolder = "Assets/Scenes";
        const string ScenePath = ScenesFolder + "/Main.unity";
        const string BuildFolder = "Build";
        const string ExeName = "DeskSprite.exe";

        [MenuItem("Desk Sprite/1. 创建启动场景", false, 10)]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (!Directory.Exists(ScenesFolder)) Directory.CreateDirectory(ScenesFolder);

            // 故意建一个**空**场景：所有东西都由 PetBootstrap 在运行时造出来。
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[DeskSprite] 已创建启动场景: " + ScenePath);
        }

        [MenuItem("Desk Sprite/2. 打包 Windows exe", false, 11)]
        public static void BuildWindows()
        {
            EnsureScene();

            if (!Directory.Exists(BuildFolder)) Directory.CreateDirectory(BuildFolder);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(BuildFolder, ExeName),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log(string.Format("[DeskSprite] 打包成功: {0}  ({1:F1} MB, {2:F0} 秒)",
                    options.locationPathName,
                    summary.totalSize / 1024f / 1024f,
                    summary.totalTime.TotalSeconds));
            }
            else
            {
                Debug.LogError("[DeskSprite] 打包失败: " + summary.result + " 错误数=" + summary.totalErrors);
            }
        }

        [MenuItem("Desk Sprite/3. 打开 exe 所在文件夹", false, 12)]
        public static void RevealBuild()
        {
            string full = Path.GetFullPath(BuildFolder);
            if (Directory.Exists(full)) EditorUtility.RevealInFinder(full);
            else Debug.LogWarning("[DeskSprite] 还没打包过，找不到 " + full);
        }

        /// <summary>命令行专用：只检查项目能不能编译通过。能走到这里，就说明没有编译错误。</summary>
        public static void CompileCheck()
        {
            Debug.Log("[DeskSprite] CompileCheck: 编译通过，没有错误。");
            EditorApplication.Exit(0);
        }

        /// <summary>命令行专用：建场景 + 打包，成功退 0，失败退 1。</summary>
        public static void BuildFromCommandLine()
        {
            try
            {
                CreateScene();
                BuildWindows();
                bool ok = File.Exists(Path.Combine(BuildFolder, ExeName));
                EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[DeskSprite] 打包异常: " + e);
                EditorApplication.Exit(1);
            }
        }

        static void EnsureScene()
        {
            if (!File.Exists(ScenePath))
            {
                CreateScene();
                return;
            }

            // 确保场景在 Build Settings 里
            bool listed = false;
            foreach (var s in EditorBuildSettings.scenes)
                if (s.path == ScenePath) { listed = true; break; }

            if (!listed)
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                Debug.Log("[DeskSprite] 已把 " + ScenePath + " 加进 Build Settings");
            }
        }
    }
}
#endif
