using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GpuDirectBinningBenchmarkBuild
{
    private const string TemporaryScenePath =
        "Assets/GpuDirectBinningBenchmark/Generated/" +
        "GpuDirectBinningBenchmark.generated.unity";

    public static void PerformBuild()
    {
        string[] args = Environment.GetCommandLineArgs();
        string requestedPath = ReadString(
            args,
            "-gpu-direct-binning-player-path",
            "Builds/GpuDirectBinningBenchmark/" +
            "GpuDirectBinningBenchmark.exe");
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

        string generatedDirectory =
            Path.GetDirectoryName(TemporaryScenePath)?.Replace('\\', '/');
        bool createdGeneratedDirectory = false;
        if (!AssetDatabase.IsValidFolder(generatedDirectory))
        {
            AssetDatabase.CreateFolder(
                "Assets/GpuDirectBinningBenchmark",
                "Generated");
            createdGeneratedDirectory = true;
        }

        bool previousFrameTimingStats =
            PlayerSettings.enableFrameTimingStats;
        try
        {
            PlayerSettings.enableFrameTimingStats = true;
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);
            scene.name = "GpuDirectBinningBenchmark";
            GameObject cameraHost =
                new GameObject("GPU Direct Binning Benchmark Camera");
            Camera camera = cameraHost.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 0;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.depthTextureMode = DepthTextureMode.None;
            cameraHost.transform.position =
                new Vector3(0.0f, 0.0f, -10.0f);

            if (!EditorSceneManager.SaveScene(
                    scene,
                    TemporaryScenePath))
            {
                throw new InvalidOperationException(
                    "Failed to save generated benchmark scene.");
            }

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = new[] { TemporaryScenePath },
                locationPathName = playerPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            string reportPath = Path.Combine(
                Path.GetDirectoryName(playerPath) ?? projectRoot,
                "build-summary.txt");
            File.WriteAllLines(reportPath, new[]
            {
                "GPU direct-binning benchmark Player build",
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
                    "GPU direct-binning Player build failed: " +
                    report.summary.result);
            }
        }
        finally
        {
            PlayerSettings.enableFrameTimingStats =
                previousFrameTimingStats;
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
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(
                    args[i],
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return fallback;
    }
}
