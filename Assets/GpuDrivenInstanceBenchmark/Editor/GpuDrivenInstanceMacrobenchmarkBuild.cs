using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GpuDrivenInstanceMacrobenchmarkBuild
{
    private const string TemporaryScenePath =
        "Assets/GpuDrivenInstanceBenchmark/Generated/" +
        "GpuDrivenInstanceMacrobenchmark.generated.unity";
    private const string TemporaryMaterialPath =
        "Assets/GpuDrivenInstanceBenchmark/Generated/" +
        "GpuDrivenInstanceMacrobenchmark.generated.mat";
    private const string MacroShaderPath =
        "Assets/GpuDrivenInstanceBenchmark/Runtime/Resources/" +
        "GpuDrivenInstanceBenchmark/GpuDrivenInstanceMacro.shader";

    public static void PerformBuild()
    {
        string[] args = Environment.GetCommandLineArgs();
        string requestedPath = ReadString(
            args,
            "-gpu-driven-instance-macro-player-path",
            "Builds/GpuDrivenInstanceMacrobenchmark/" +
            "GpuDrivenInstanceMacrobenchmark.exe");
        string projectRoot =
            Directory.GetParent(Application.dataPath)?.FullName ??
            throw new InvalidOperationException(
                "Unable to resolve Unity project root.");
        string playerPath = Path.IsPathRooted(requestedPath)
            ? Path.GetFullPath(requestedPath)
            : Path.GetFullPath(Path.Combine(projectRoot, requestedPath));
        string playerDirectory =
            Path.GetDirectoryName(playerPath) ??
            throw new InvalidOperationException(
                "Player output directory is invalid.");
        Directory.CreateDirectory(playerDirectory);

        string generatedDirectory =
            Path.GetDirectoryName(TemporaryScenePath)?.Replace('\\', '/');
        bool createdGeneratedDirectory = false;
        if (!AssetDatabase.IsValidFolder(generatedDirectory))
        {
            AssetDatabase.CreateFolder(
                "Assets/GpuDrivenInstanceBenchmark",
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
            scene.name = "GpuDrivenInstanceMacrobenchmark";

            Shader macroShader = AssetDatabase.LoadAssetAtPath<Shader>(
                MacroShaderPath);
            if (macroShader == null)
            {
                throw new InvalidOperationException(
                    "Macrobenchmark shader asset could not be loaded.");
            }
            AssetDatabase.DeleteAsset(TemporaryMaterialPath);
            var instancingMaterial = new Material(macroShader)
            {
                name = "GPU Driven Macro Instancing Variant Anchor",
                enableInstancing = true
            };
            AssetDatabase.CreateAsset(
                instancingMaterial,
                TemporaryMaterialPath);
            GameObject variantAnchor =
                GameObject.CreatePrimitive(PrimitiveType.Cube);
            variantAnchor.name =
                "GPU Driven Macro Instancing Variant Anchor";
            variantAnchor.GetComponent<MeshRenderer>().sharedMaterial =
                instancingMaterial;
            if (!EditorSceneManager.SaveScene(scene, TemporaryScenePath))
            {
                throw new InvalidOperationException(
                    "Failed to save generated macrobenchmark scene.");
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
                playerDirectory,
                "macro-build-summary.txt");
            File.WriteAllLines(reportPath, new[]
            {
                "GPU driven-instance macrobenchmark Player build",
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
                    "GPU driven-instance macrobenchmark Player build " +
                    "failed: " + report.summary.result);
            }
        }
        finally
        {
            PlayerSettings.enableFrameTimingStats =
                previousFrameTimingStats;
            AssetDatabase.DeleteAsset(TemporaryScenePath);
            AssetDatabase.DeleteAsset(TemporaryMaterialPath);
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
