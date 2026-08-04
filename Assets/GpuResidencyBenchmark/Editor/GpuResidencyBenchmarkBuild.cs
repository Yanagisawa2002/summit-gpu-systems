using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GpuResidencyBenchmarkBuild
{
    private const string ScenePath =
        "Assets/GpuResidencyBenchmark/Generated/" +
        "GpuResidencyBenchmark.generated.unity";

    public static void PerformBuild()
    {
        string[] args = Environment.GetCommandLineArgs();
        string requested = ReadString(
            args,
            "-gpu-residency-player-path",
            "Builds/GpuResidencyBenchmark/GpuResidencyBenchmark.exe");
        string root = Directory.GetParent(Application.dataPath)?.FullName ??
            throw new InvalidOperationException("Project root unavailable.");
        string player = Path.IsPathRooted(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(Path.Combine(root, requested));
        Directory.CreateDirectory(Path.GetDirectoryName(player) ?? root);
        const string generated = "Assets/GpuResidencyBenchmark/Generated";
        bool created = false;
        if (!AssetDatabase.IsValidFolder(generated))
        {
            AssetDatabase.CreateFolder("Assets/GpuResidencyBenchmark", "Generated");
            created = true;
        }
        try
        {
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);
            scene.name = "GpuResidencyBenchmark";
            GameObject cameraHost = new GameObject("GPU Residency Camera");
            Camera camera = cameraHost.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 0;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                throw new InvalidOperationException("Temporary scene save failed.");
            }
            BuildReport report = BuildPipeline.BuildPlayer(
                new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = player,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development
                });
            File.WriteAllLines(
                Path.Combine(Path.GetDirectoryName(player) ?? root,
                    "build-summary.txt"),
                new[]
                {
                    "GPU residency benchmark Player build",
                    "result=" + report.summary.result,
                    "errors=" + report.summary.totalErrors,
                    "warnings=" + report.summary.totalWarnings,
                    "totalSizeBytes=" + report.summary.totalSize,
                    "elapsed=" + report.summary.totalTime,
                    "output=" + player
                });
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "GPU residency Player build failed: " +
                    report.summary.result);
            }
        }
        finally
        {
            AssetDatabase.DeleteAsset(ScenePath);
            if (created)
            {
                AssetDatabase.DeleteAsset(generated);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }

    private static string ReadString(
        string[] args, string name, string fallback)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(
                    args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return fallback;
    }
}
