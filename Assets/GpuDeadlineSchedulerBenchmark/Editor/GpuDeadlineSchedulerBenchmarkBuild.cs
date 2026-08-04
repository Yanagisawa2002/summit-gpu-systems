using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GpuDeadlineSchedulerBenchmarkBuild
{
    private const string TemporaryScenePath =
        "Assets/GpuDeadlineSchedulerBenchmark/Generated/" +
        "GpuDeadlineSchedulerBenchmark.generated.unity";

    public static void PerformBuild()
    {
        string[] args = Environment.GetCommandLineArgs();
        string requestedPath = ReadString(
            args,
            "-gpu-deadline-player-path",
            "Builds/GpuDeadlineSchedulerBenchmark/" +
            "GpuDeadlineSchedulerBenchmark.exe");
        string projectRoot =
            Directory.GetParent(Application.dataPath)?.FullName ??
            throw new InvalidOperationException(
                "Unable to resolve Unity project root.");
        string playerPath = Path.IsPathRooted(requestedPath)
            ? Path.GetFullPath(requestedPath)
            : Path.GetFullPath(Path.Combine(projectRoot, requestedPath));
        Directory.CreateDirectory(
            Path.GetDirectoryName(playerPath) ??
            throw new InvalidOperationException(
                "Player output directory is invalid."));

        const string generatedDirectory =
            "Assets/GpuDeadlineSchedulerBenchmark/Generated";
        bool createdGeneratedDirectory = false;
        if (!AssetDatabase.IsValidFolder(generatedDirectory))
        {
            AssetDatabase.CreateFolder(
                "Assets/GpuDeadlineSchedulerBenchmark",
                "Generated");
            createdGeneratedDirectory = true;
        }

        bool previousFrameTimingStats = PlayerSettings.enableFrameTimingStats;
        try
        {
            PlayerSettings.enableFrameTimingStats = true;
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);
            scene.name = "GpuDeadlineSchedulerBenchmark";
            GameObject cameraHost = new GameObject(
                "GPU Deadline Scheduler Benchmark Camera");
            Camera camera = cameraHost.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 0;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.depthTextureMode = DepthTextureMode.None;

            if (!EditorSceneManager.SaveScene(scene, TemporaryScenePath))
            {
                throw new InvalidOperationException(
                    "Failed to save the generated benchmark scene.");
            }

            BuildReport report = BuildPipeline.BuildPlayer(
                new BuildPlayerOptions
                {
                    scenes = new[] { TemporaryScenePath },
                    locationPathName = playerPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development
                });
            string summaryPath = Path.Combine(
                Path.GetDirectoryName(playerPath) ?? projectRoot,
                "build-summary.txt");
            File.WriteAllLines(summaryPath, new[]
            {
                "GPU deadline scheduler benchmark Player build",
                "result=" + report.summary.result,
                "errors=" + report.summary.totalErrors,
                "warnings=" + report.summary.totalWarnings,
                "totalSizeBytes=" + report.summary.totalSize,
                "elapsed=" + report.summary.totalTime,
                "output=" + playerPath
            });
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "GPU deadline scheduler Player build failed: " +
                    report.summary.result);
            }
        }
        finally
        {
            PlayerSettings.enableFrameTimingStats = previousFrameTimingStats;
            AssetDatabase.DeleteAsset(TemporaryScenePath);
            if (createdGeneratedDirectory)
            {
                AssetDatabase.DeleteAsset(generatedDirectory);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }

    private static string ReadString(
        string[] args,
        string name,
        string fallback)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(
                    args[index],
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return fallback;
    }
}
