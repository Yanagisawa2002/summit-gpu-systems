using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ConfigureNYCGISGpuVegetation
{
    private const string ScenePath = "Assets/Scenes/NYCGISDemoFull.unity";
    private const string ObjectName = "NYCGIS Full-City GPU Vegetation";
    private const string ComputePath =
        "Assets/Shaders/NYCGISDemo/NYCGISVegetationGpuCull.compute";
    private const string MaterialPath =
        "Assets/Generated/NYCGISDemo/NYCGISGpuVegetation.mat";
    private const string ShaderName = "NYCGIS/GpuVegetationURP";
    private const string DetailCameraName = "Camera_Profile_200m_Zoom_Check";

    [MenuItem("Tools/NYC GIS Demo/Vegetation GPU/Install Full-City Renderer")]
    public static void Install()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (!string.Equals(scene.path, ScenePath, StringComparison.OrdinalIgnoreCase))
        {
            if (scene.isDirty)
            {
                throw new InvalidOperationException(
                    "The active scene has unsaved changes. Open NYCGISDemoFull manually " +
                    "before installing full-city GPU vegetation.");
            }
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        if (compute == null)
        {
            throw new FileNotFoundException("Vegetation compute shader is missing.", ComputePath);
        }
        Material material = EnsureMaterial();
        TerrainTileGridLoader terrain =
            UnityEngine.Object.FindAnyObjectByType<TerrainTileGridLoader>(
                FindObjectsInactive.Include);
        if (terrain == null)
        {
            throw new InvalidOperationException(
                "NYCGISDemoFull has no TerrainTileGridLoader for GIS alignment.");
        }

        Bfp2GpuIndirectRenderer buildings =
            UnityEngine.Object.FindAnyObjectByType<Bfp2GpuIndirectRenderer>(
                FindObjectsInactive.Include);
        Camera camera = buildings != null && buildings.cameraOverride != null
            ? buildings.cameraOverride
            : Camera.main;
        NYCGISDataRootConfig dataRoot =
            buildings != null ? buildings.dataRootConfig : terrain.dataRootConfig;

        GameObject host = GameObject.Find(ObjectName);
        if (host == null)
        {
            host = new GameObject(ObjectName);
            Undo.RegisterCreatedObjectUndo(host, "Create full-city GPU vegetation");
        }
        NYCGISGpuVegetationRenderer renderer =
            host.GetComponent<NYCGISGpuVegetationRenderer>();
        if (renderer == null)
        {
            renderer = Undo.AddComponent<NYCGISGpuVegetationRenderer>(host);
        }

        Undo.RecordObject(renderer, "Configure full-city GPU vegetation");
        renderer.useSharedDataRootConfig = true;
        renderer.dataRootConfig = dataRoot;
        renderer.manifestPath = string.Empty;
        renderer.loadInEditMode = true;
        renderer.maxUploadBytesPerFrame = 8 * 1024 * 1024;
        renderer.terrainLoader = terrain;
        renderer.cullCompute = compute;
        renderer.drawMaterial = material;
        renderer.cameraOverride = camera;
        renderer.useSceneViewCameraInEditor = true;
        renderer.drawInEditMode = true;
        renderer.renderVegetation = true;
        renderer.castShadows = true;
        renderer.receiveShadows = true;
        renderer.nearLodDistanceMeters = 220.0f;
        renderer.maximumDistanceMeters = 100000.0f;
        renderer.minimumScreenRadiusRatio = 0.0f;
        renderer.cameraMoveCullThresholdMeters = 0.5f;
        renderer.cameraRotateCullThresholdDegrees = 0.25f;
        renderer.crownTint = Color.white;
        renderer.trunkColor = new Color(0.20f, 0.095f, 0.035f, 1.0f);
        renderer.windStrength = 0.15f;
        renderer.windFrequency = 0.35f;
        renderer.showRuntimeHud = true;
        renderer.enableStatsReadback = true;
        renderer.statsReadbackIntervalSeconds = 0.5f;
        EditorUtility.SetDirty(renderer);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        renderer.BeginLoad();
        Selection.activeGameObject = host;

        Debug.Log(
            "Installed full-city GPU vegetation in NYCGISDemoFull. " +
            "Full residency, GPU cluster culling, indirect rendering, and URP shadows are enabled.");
    }

    [MenuItem("Tools/NYC GIS Demo/Vegetation GPU/Validate Full-City Renderer")]
    public static void Validate()
    {
        NYCGISGpuVegetationRenderer renderer =
            UnityEngine.Object.FindAnyObjectByType<NYCGISGpuVegetationRenderer>(
                FindObjectsInactive.Include);
        if (renderer == null)
        {
            throw new InvalidOperationException("Full-city GPU vegetation renderer is missing.");
        }
        if (renderer.cullCompute == null)
        {
            throw new InvalidOperationException("Vegetation compute shader is not assigned.");
        }
        if (renderer.drawMaterial == null ||
            renderer.drawMaterial.shader == null ||
            renderer.drawMaterial.shader.name != ShaderName)
        {
            throw new InvalidOperationException("Vegetation GPU material is missing or invalid.");
        }
        if (renderer.terrainLoader == null)
        {
            throw new InvalidOperationException("Vegetation terrain alignment is missing.");
        }
        if (!renderer.castShadows)
        {
            throw new InvalidOperationException("Vegetation real shadows are disabled.");
        }
        string path = renderer.ResolvedManifestPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Vegetation GPU manifest is missing.", path);
        }
        string json = File.ReadAllText(path);
        if (!json.Contains("\"recordCount\": 892947"))
        {
            throw new InvalidDataException(
                "Vegetation GPU manifest does not contain the expected 892,947 trees.");
        }

        Debug.Log(
            $"Full-city GPU vegetation validation passed. ready={renderer.IsReady}, " +
            $"trees={renderer.TotalTreeCount:N0}, clusters={renderer.ClusterCount:N0}, " +
            $"visible={renderer.VisibleNearCount + renderer.VisibleFarCount:N0}, " +
            $"GPU={renderer.EstimatedGpuBytes / (1024f * 1024f):F1} MiB, " +
            $"status={renderer.Status}");
    }

    [MenuItem("Tools/NYC GIS Demo/Vegetation GPU/Pause Rendering (Keep GPU Residency)")]
    public static void PauseRendering()
    {
        SetRenderingEnabled(false);
    }

    [MenuItem("Tools/NYC GIS Demo/Vegetation GPU/Resume Rendering")]
    public static void ResumeRendering()
    {
        SetRenderingEnabled(true);
    }

    private static void SetRenderingEnabled(bool enabled)
    {
        NYCGISGpuVegetationRenderer renderer =
            UnityEngine.Object.FindAnyObjectByType<NYCGISGpuVegetationRenderer>(
                FindObjectsInactive.Include);
        if (renderer == null)
        {
            throw new InvalidOperationException("Full-city GPU vegetation renderer is missing.");
        }
        renderer.SetRenderingEnabled(enabled);
        SceneView.RepaintAll();
        Debug.Log(
            enabled
                ? "Full-city GPU vegetation rendering resumed."
                : "Full-city GPU vegetation rendering paused; GPU buffers remain resident.");
    }

    [MenuItem("Tools/NYC GIS Demo/Vegetation GPU/Focus 200m Tree Detail")]
    public static void FocusTreeDetail()
    {
        GameObject cameraObject = GameObject.Find(DetailCameraName);
        Camera camera = cameraObject != null ? cameraObject.GetComponent<Camera>() : null;
        SceneView sceneView = SceneView.lastActiveSceneView;
        if (camera == null || sceneView == null)
        {
            throw new InvalidOperationException(
                $"The detail camera '{DetailCameraName}' or active Scene view is missing.");
        }

        Transform cameraTransform = camera.transform;
        float focusDistance = camera.orthographic
            ? Mathf.Max(100.0f, camera.orthographicSize)
            : 300.0f;
        Vector3 pivot = cameraTransform.position + cameraTransform.forward * focusDistance;
        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        Ray centerRay = new Ray(cameraTransform.position, cameraTransform.forward);
        if (groundPlane.Raycast(centerRay, out float groundDistance) && groundDistance > 0.0f)
        {
            focusDistance = groundDistance;
            pivot = centerRay.GetPoint(groundDistance);
        }

        sceneView.orthographic = camera.orthographic;
        sceneView.LookAt(
            pivot,
            cameraTransform.rotation,
            camera.orthographic
                ? camera.orthographicSize
                : Mathf.Clamp(focusDistance, 25.0f, 2000.0f));
        sceneView.Repaint();
        Debug.Log($"Focused Scene view on vegetation detail camera '{DetailCameraName}'.");
    }

    private static Material EnsureMaterial()
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            throw new InvalidOperationException(
                $"Shader.Find could not resolve '{ShaderName}'.");
        }
        if (material == null)
        {
            string directory = Path.GetDirectoryName(MaterialPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            material = new Material(shader)
            {
                name = "NYCGIS Full-City GPU Vegetation",
            };
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
            EditorUtility.SetDirty(material);
        }
        AssetDatabase.SaveAssets();
        return material;
    }
}
