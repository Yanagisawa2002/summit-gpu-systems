using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class NYCGISFullCityWeatherPerformanceQA
{
    public const string SourceScenePath = "Assets/Scenes/NYCGISDemoFull.unity";
    public const string ScenePath = "Assets/Scenes/NYCGISDemoFull_PortablePilot.unity";
    public const string BuildFolder = "Builds/Validation/FullCityWeather";
    public const string ExecutablePath = BuildFolder + "/NYCGISFullCityWeatherQA.exe";

    [MenuItem("Tools/NYC GIS Demo/Weather/Validation/Build Full City 1-4-6 Camera QA Player", priority = 30)]
    public static void BuildInteractive()
    {
        try
        {
            Debug.Log(BuildPlayer());
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    public static void BuildFullCityWeatherPerformancePlayerBatch()
    {
        int exitCode = 0;
        try
        {
            Debug.Log(BuildPlayer());
        }
        catch (Exception exception)
        {
            exitCode = 1;
            Debug.LogException(exception);
        }
        finally
        {
            EditorApplication.Exit(exitCode);
        }
    }

    public static string BuildPlayer()
    {
        if (!File.Exists(SourceScenePath))
        {
            throw new FileNotFoundException("Full-city source scene is missing.", SourceScenePath);
        }

        // The canonical scene contains editor-generated terrain/water meshes that make the
        // standalone level file hundreds of megabytes and can exceed Unity's reliable scene
        // serialization path. Build a disposable portable copy; the runtime loaders restore
        // those meshes from NYCGISDataRoot without changing the canonical scene.
        PreparePortableNYCGISScene.BuildPortablePilot();
        RemoveOptionalBoatNavigationFromQaScene();
        if (!File.Exists(ScenePath))
        {
            throw new FileNotFoundException("Portable full-city QA scene was not generated.", ScenePath);
        }
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        string absoluteExecutable = Path.GetFullPath(Path.Combine(projectRoot ?? string.Empty, ExecutablePath));
        string absoluteBuildFolder = Path.GetDirectoryName(absoluteExecutable) ??
                                     Path.GetFullPath(Path.Combine(projectRoot ?? string.Empty, BuildFolder));
        if (Directory.Exists(absoluteBuildFolder))
        {
            Directory.Delete(absoluteBuildFolder, true);
        }
        Directory.CreateDirectory(absoluteBuildFolder);

        bool previousRunInBackground = PlayerSettings.runInBackground;
        bool previousFrameTimingStats = PlayerSettings.enableFrameTimingStats;
        bool previousUseDefaultGraphicsApis =
            PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
        GraphicsDeviceType[] previousGraphicsApis =
            PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        BuildReport report;
        try
        {
            PlayerSettings.runInBackground = true;
            PlayerSettings.enableFrameTimingStats = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(
                BuildTarget.StandaloneWindows64,
                new[] { GraphicsDeviceType.Direct3D12 });
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = absoluteExecutable,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            });
        }
        finally
        {
            PlayerSettings.runInBackground = previousRunInBackground;
            PlayerSettings.enableFrameTimingStats = previousFrameTimingStats;
            PlayerSettings.SetUseDefaultGraphicsAPIs(
                BuildTarget.StandaloneWindows64,
                previousUseDefaultGraphicsApis);
            if (!previousUseDefaultGraphicsApis)
            {
                PlayerSettings.SetGraphicsAPIs(
                    BuildTarget.StandaloneWindows64,
                    previousGraphicsApis);
            }
        }
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Full-city weather QA build failed: {report.summary.result}; " +
                $"errors={report.summary.totalErrors}; warnings={report.summary.totalWarnings}.");
        }
        return $"Full-city weather QA player built: {absoluteExecutable}; " +
               $"size={report.summary.totalSize}; time={report.summary.totalTime.TotalSeconds:F1}s.";
    }

    private static void RemoveOptionalBoatNavigationFromQaScene()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        WaterPilotBoat[] boats = UnityEngine.Object.FindObjectsByType<WaterPilotBoat>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        WaterNavPilotSurface[] navigationSurfaces = UnityEngine.Object.FindObjectsByType<WaterNavPilotSurface>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        for (int i = 0; i < boats.Length; i++)
        {
            if (boats[i] != null)
            {
                UnityEngine.Object.DestroyImmediate(boats[i].gameObject);
            }
        }
        for (int i = 0; i < navigationSurfaces.Length; i++)
        {
            if (navigationSurfaces[i] != null)
            {
                UnityEngine.Object.DestroyImmediate(navigationSurfaces[i].gameObject);
            }
        }

        Transform[] transforms = UnityEngine.Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        for (int i = 0; i < transforms.Length; i++)
        {
            GameObject candidate = transforms[i] != null ? transforms[i].gameObject : null;
            if (candidate != null && candidate.scene == scene &&
                (string.Equals(candidate.name, "WaterNav_NYC", StringComparison.Ordinal) ||
                 string.Equals(candidate.name, "Boat_Test_Target", StringComparison.Ordinal)))
            {
                UnityEngine.Object.DestroyImmediate(candidate);
            }
        }

        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        for (int i = 0; i < allObjects.Length; i++)
        {
            GameObject candidate = allObjects[i];
            if (candidate != null && candidate.scene == scene &&
                (string.Equals(candidate.name, "WaterNav_NYC", StringComparison.Ordinal) ||
                 string.Equals(candidate.name, "Boat_Test_Target", StringComparison.Ordinal)))
            {
                UnityEngine.Object.DestroyImmediate(candidate);
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene, ScenePath))
        {
            throw new IOException("Could not save sanitized full-city QA scene at " + ScenePath);
        }

        string sceneYaml = File.ReadAllText(ScenePath);
        if (sceneYaml.Contains("m_Name: WaterNav_NYC", StringComparison.Ordinal) ||
            sceneYaml.Contains("m_Name: Boat_Test_Target", StringComparison.Ordinal) ||
            sceneYaml.Contains("--- !u!115", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Full-city QA scene still contains optional embedded boat scripts after sanitization.");
        }
    }
}
