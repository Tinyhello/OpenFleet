using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace OpenFleet.Unity.Editor
{
    /// <summary>
    /// 建置 Windows 桌面版（Development build）。Editor 不在前景時 Play Mode 幾乎不更新畫面，所以驗證用 Player。
    /// 也可以從命令列執行：Unity -batchmode -quit -projectPath unity/Openfleet -executeMethod OpenFleet.Unity.Editor.PlayerBuilder.BuildWindows
    /// </summary>
    internal static class PlayerBuilder
    {
        private const string OutputPath = "Builds/Windows/OpenFleet.exe";

        [MenuItem("OpenFleet/Build Windows Player")]
        public static void BuildWindows()
        {
            FactorySceneBuilder.Build();

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { FactorySceneBuilder.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development,
            });

            if (report.summary.result != BuildResult.Succeeded)
            {
                // 命令列建置時讓程序以非 0 結束，呼叫端才知道失敗。
                Debug.LogError($"[OpenFleet] Player build failed: {report.summary.result}, {report.summary.totalErrors} error(s).");
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }

                return;
            }

            Debug.Log($"[OpenFleet] Player built: {OutputPath} ({report.summary.totalSize / (1024 * 1024)} MB)");
        }
    }
}
