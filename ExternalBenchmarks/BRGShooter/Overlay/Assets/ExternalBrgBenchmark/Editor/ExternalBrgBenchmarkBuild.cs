using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class ExternalBrgBenchmarkBuild
{
    private const string GeneratedDirectory =
        "Assets/ExternalBrgBenchmark/Generated";
    private const string GeneratedScene =
        GeneratedDirectory + "/ExternalBrgBenchmark.generated.unity";
    private const string UpstreamScene =
        "Assets/Scenes/brg_shooter.unity";

    public static void PerformBenchmarkBuild()
    {
        string playerPath = ResolvePlayerPath(
            "-external-brg-player-path",
            "Builds/ExternalBrgBenchmark/ExternalBrgBenchmark.exe");
        EnsureGeneratedDirectory();

        bool previousFrameTimingStats =
            PlayerSettings.enableFrameTimingStats;
        GraphicsDeviceType[] previousApis =
            PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        try
        {
            PlayerSettings.enableFrameTimingStats = true;
            PlayerSettings.SetGraphicsAPIs(
                BuildTarget.StandaloneWindows64,
                new[] { GraphicsDeviceType.Direct3D12 });

            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);
            scene.name = "ExternalBrgBenchmark";
            var host = new GameObject("External BRG Benchmark Controller");
            host.AddComponent<ExternalBrgBenchmarkController>();
            if (!EditorSceneManager.SaveScene(scene, GeneratedScene))
            {
                throw new InvalidOperationException(
                    "Failed to save the generated benchmark scene.");
            }

            Build(
                playerPath,
                new[] { GeneratedScene },
                "external-brg-build-summary.txt");
        }
        finally
        {
            PlayerSettings.enableFrameTimingStats =
                previousFrameTimingStats;
            PlayerSettings.SetGraphicsAPIs(
                BuildTarget.StandaloneWindows64,
                previousApis);
            AssetDatabase.DeleteAsset(GeneratedScene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }

    public static void PerformDemoBuild()
    {
        string playerPath = ResolvePlayerPath(
            "-external-brg-demo-player-path",
            "Builds/ExternalBrgDemo/ExternalBrgDemo.exe");
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(UpstreamScene) == null)
        {
            throw new FileNotFoundException(
                "The pinned upstream scene is missing.",
                UpstreamScene);
        }
        Build(
            playerPath,
            new[] { UpstreamScene },
            "external-brg-demo-build-summary.txt");
    }

    private static void Build(
        string playerPath,
        string[] scenes,
        string summaryFileName)
    {
        string directory = Path.GetDirectoryName(playerPath) ??
            throw new InvalidOperationException(
                "The Player output directory is invalid.");
        Directory.CreateDirectory(directory);

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = playerPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        string summaryPath = Path.Combine(directory, summaryFileName);
        string[] lines =
        {
            "External BRG Shooter integration build",
            "result=" + report.summary.result,
            "errors=" + report.summary.totalErrors,
            "warnings=" + report.summary.totalWarnings,
            "totalSizeBytes=" + report.summary.totalSize,
            "elapsed=" + report.summary.totalTime,
            "unity=" + Application.unityVersion,
            "output=" + playerPath
        };
        File.WriteAllLines(
            summaryPath,
            lines,
            new UTF8Encoding(false));
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                "External BRG Player build failed: " +
                report.summary.result);
        }
    }

    private static void EnsureGeneratedDirectory()
    {
        if (!AssetDatabase.IsValidFolder(GeneratedDirectory))
        {
            AssetDatabase.CreateFolder(
                "Assets/ExternalBrgBenchmark",
                "Generated");
        }
    }

    private static string ResolvePlayerPath(
        string argument,
        string fallback)
    {
        string requested = ReadString(
            Environment.GetCommandLineArgs(),
            argument,
            fallback);
        string projectRoot = Directory.GetParent(Application.dataPath)
            ?.FullName ?? throw new InvalidOperationException(
                "Unable to resolve the Unity project root.");
        return Path.IsPathRooted(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(Path.Combine(projectRoot, requested));
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
