using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GpuSystemsShowcaseBuild
{
    private const string TemporaryScenePath =
        "Assets/GpuDrivenInstanceBenchmark/Generated/" +
        "GpuSystemsShowcase.generated.unity";
    private const string TemporaryMaterialPath =
        "Assets/GpuDrivenInstanceBenchmark/Generated/" +
        "GpuSystemsShowcase.generated.mat";
    private const string MacroShaderPath =
        "Assets/GpuDrivenInstanceBenchmark/Runtime/Resources/" +
        "GpuDrivenInstanceBenchmark/GpuDrivenInstanceMacro.shader";

    public static void PerformBuild()
    {
        string[] args = Environment.GetCommandLineArgs();
        string requestedPath = ReadString(
            args,
            "-gpu-systems-showcase-player-path",
            "Builds/GpuSystemsShowcase/GpuSystemsShowcase.exe");
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

        int previousWidth = PlayerSettings.defaultScreenWidth;
        int previousHeight = PlayerSettings.defaultScreenHeight;
        FullScreenMode previousMode = PlayerSettings.fullScreenMode;
        bool previousRunInBackground = PlayerSettings.runInBackground;
        try
        {
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.runInBackground = true;

            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single);
            scene.name = "GpuSystemsShowcase";

            Shader macroShader = AssetDatabase.LoadAssetAtPath<Shader>(
                MacroShaderPath);
            if (macroShader == null)
            {
                throw new InvalidOperationException(
                    "Showcase shader asset could not be loaded.");
            }
            AssetDatabase.DeleteAsset(TemporaryMaterialPath);
            var material = new Material(macroShader)
            {
                name = "GPU Systems Showcase Variant Anchor",
                enableInstancing = true
            };
            AssetDatabase.CreateAsset(material, TemporaryMaterialPath);
            GameObject variantAnchor =
                GameObject.CreatePrimitive(PrimitiveType.Cube);
            variantAnchor.name = "GPU Systems Showcase Variant Anchor";
            variantAnchor.GetComponent<MeshRenderer>().sharedMaterial = material;
            variantAnchor.SetActive(false);

            var host = new GameObject("GPU Systems Showcase Controller");
            host.AddComponent<GpuSystemsShowcaseController>();
            if (!EditorSceneManager.SaveScene(scene, TemporaryScenePath))
            {
                throw new InvalidOperationException(
                    "Failed to save generated showcase scene.");
            }

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = new[] { TemporaryScenePath },
                locationPathName = playerPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            File.WriteAllLines(
                Path.Combine(playerDirectory, "showcase-build-summary.txt"),
                new[]
                {
                    "GPU Systems Toolkit visible showcase Player build",
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
                    "GPU Systems showcase Player build failed: " +
                    report.summary.result);
            }
        }
        finally
        {
            PlayerSettings.defaultScreenWidth = previousWidth;
            PlayerSettings.defaultScreenHeight = previousHeight;
            PlayerSettings.fullScreenMode = previousMode;
            PlayerSettings.runInBackground = previousRunInBackground;
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
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return fallback;
    }
}
