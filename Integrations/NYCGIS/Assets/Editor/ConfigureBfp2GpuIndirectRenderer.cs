using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ConfigureBfp2GpuIndirectRenderer
{
    private const string DemoScenePath = NYCGISProductionBaseline.ProductionScenePath;
    private const string MaterialPath = "Assets/Generated/NYCGISDemo/BFP2GpuIndirect.mat";
    private const string ComputePath = "Assets/Shaders/NYCGISDemo/Bfp2GpuClusterCull.compute";

    [MenuItem("Tools/NYC GIS Demo/BFP2/Configure GPU Indirect Renderer")]
    public static void ConfigureCurrentScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("Exit Play Mode before configuring the BFP2 GPU indirect renderer.");
            return;
        }

        OpenDemoSceneIfNeeded();
        ConfigureLoadedScene();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Configured BFP2 GPU indirect renderer.");
    }

    public static void ConfigureDemoSceneBatch()
    {
        int exitCode = 0;
        try
        {
            EditorSceneManager.OpenScene(DemoScenePath);
            ConfigureLoadedScene();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            Debug.LogException(exception);
        }
        finally
        {
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(exitCode);
            }
        }
    }

    private static void OpenDemoSceneIfNeeded()
    {
        if (EditorSceneManager.GetActiveScene().path != DemoScenePath)
        {
            EditorSceneManager.OpenScene(DemoScenePath);
        }
    }

    public static void ConfigureLoadedScene()
    {
        NYCGISDataRootConfig dataRootConfig = NYCGISDataRootConfigEditorUtility.EnsureDefaultAsset();
        Texture2DArray facadeArray = Bfp2FacadeAssetBuilder.EnsureFacadeTextureArray(false);
        Material material = EnsureMaterial(facadeArray);
        ComputeShader waveCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Assets/Shaders/NYCGISDemo/Bfp2GpuClusterCullWave.compute");
        ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);

        GameObject host = GameObject.Find("Bfp2GpuIndirectRenderer");
        if (host == null)
        {
            host = new GameObject("Bfp2GpuIndirectRenderer");
        }

        Bfp2GpuIndirectRenderer renderer = host.GetComponent<Bfp2GpuIndirectRenderer>();
        if (renderer == null)
        {
            renderer = host.AddComponent<Bfp2GpuIndirectRenderer>();
        }

        host.SetActive(true);
        renderer.enabled = true;
        renderer.drawMaterial = material;
        renderer.cullCompute = compute;
        renderer.waveCullCompute = waveCompute;
        renderer.useSharedDataRootConfig = true;
        renderer.dataRootConfig = dataRootConfig;
        renderer.showBfp2 = true;
        renderer.drawInEditMode = true;
        renderer.streamInEditMode = true;
        renderer.scanOnEnable = true;
        renderer.loadAllEnabledPacksResident = false;
        renderer.drawAllResidentPacks = false;
        renderer.preferMegaPacks = false;
        renderer.gpuDrivenMegaLayerMode = false;
        renderer.requireMegaPacksInGpuDrivenMode = false;
        renderer.megaPackDirectoryName = "mega";
        renderer.megaPackMaxSourcePacks = 0;
        renderer.enableFrustumCulling = true;
        renderer.enableDistanceCulling = false;
        renderer.enableNormalConeBackfaceCulling = false;
        renderer.enableScreenSizeCulling = false;
        renderer.forceGpuClusterFrustumCullingInFullDraw = true;
        renderer.flipTriangleWinding = false;
        renderer.clusterCullAlgorithm = Bfp2ClusterCullAlgorithm.ScalarAoS;
        renderer.enableGpuProfilerMarkers = false;
        renderer.maxTotalResidentPacks = 96;
        renderer.maxUploadBytesPerFrame = 64 * 1024 * 1024;
        renderer.enableStagedUploadQueue = true;
        renderer.useBeginWriteUpload = true;
        renderer.maxUploadPacksPerFrame = 1;
        renderer.maxPendingUploadBytes = 512L * 1024L * 1024L;
        renderer.usePersistentGpuBufferPool = true;
        renderer.maxPooledGpuBytes = 256L * 1024L * 1024L;
        renderer.enableRuntimeShadows = true;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = true;
        renderer.submitDrawsToAllCamerasForShadows = true;
        renderer.pinLoadedPacks = false;
        renderer.residentFrustumPaddingMeters = 300.0f;
        renderer.enableRenderScheduler = false;
        renderer.renderSchedulerPacksPerGroup = 8;
        renderer.renderSchedulerMinPacksPerGroup = 2;
        renderer.renderSchedulerOneGroupPerLayer = true;
        renderer.renderSchedulerMaxPacksPerLayerGroup = 256;
        renderer.renderSchedulerMaxGroups = 8;
        renderer.renderSchedulerBuildingsOnly = false;
        renderer.renderSchedulerExcludeShadowCasters = true;
        renderer.renderSchedulerMaxBuildsStartedPerFrame = 1;
        renderer.renderSchedulerMaxUploadStagesPerFrame = 2;
        renderer.renderSchedulerMaxUploadBytesPerFrame = 32 * 1024 * 1024;
        renderer.renderSchedulerMaxExtraGpuBytes = 1024L * 1024L * 1024L;
        renderer.enableBatchedCameraFrameBuffer = true;
        renderer.maxBfp2CameraFrames = 6;
        renderer.enableRenderSchedulerPilot = false;
        renderer.schedulerPilotMaxPacks = 4;
        renderer.schedulerPilotBuildingsOnly = true;
        renderer.schedulerPilotExcludeShadowCasters = true;
        renderer.enableNearRealShadows = true;
        renderer.nearShadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        renderer.nearShadowBuildingLayersOnly = true;
        renderer.nearShadowRadiusMeters = 900.0f;
        renderer.maxNearShadowPacks = 8;
        renderer.maxNearShadowTriangles = 1200000;
        renderer.nearRealShadowStrength = 0.86f;
        renderer.useFacadeTextureArray = true;
        renderer.facadeTextureArray = facadeArray;
        renderer.facadeStrength = 0.88f;
        renderer.facadeWidthMeters = 42.0f;
        renderer.facadeHeightMeters = 72.0f;
        renderer.facadeRandomCellSizeMeters = 16.0f;
        renderer.facadeBuildingLayerIndex = 0;
        renderer.useRoofOrthophoto = true;
        renderer.roofOrthophotoStrength = 1.0f;
        renderer.productionMode = true;
        renderer.enableDetailedRuntimeStats = false;
        renderer.summaryStatsIntervalSeconds = 0.5f;
        renderer.showDebugPanel = false;
        renderer.enableDebugReadback = false;
        renderer.drawClusterBounds = false;
        ConfigureDefaultLayers(renderer, dataRootConfig);
        renderer.ApplyDataRootConfig();
        EnsureVirtualTextureTargetMaterial(material);

        DemoHUD hud = FindObjectIncludingInactive<DemoHUD>();
        if (hud != null)
        {
            hud.bfp2GpuIndirectRenderer = renderer;
            hud.productionMode = true;
            hud.allowHudInProduction = false;
            EditorUtility.SetDirty(hud);
        }

        // Generated assets and object references remain this tool's responsibility. All
        // production loading/culling/residency values are restored by the locked baseline.
        NYCGISProductionBaseline.ApplyScene(EditorSceneManager.GetActiveScene());
        EditorUtility.SetDirty(renderer);
    }

    private static void EnsureVirtualTextureTargetMaterial(Material material)
    {
        if (material == null)
        {
            return;
        }

        OrthophotoVirtualTextureController vt = FindObjectIncludingInactive<OrthophotoVirtualTextureController>();
        if (vt == null)
        {
            return;
        }

        Material[] existing = vt.additionalVirtualTextureMaterials ?? Array.Empty<Material>();
        for (int i = 0; i < existing.Length; i++)
        {
            if (existing[i] == material)
            {
                return;
            }
        }

        Material[] expanded = new Material[existing.Length + 1];
        Array.Copy(existing, expanded, existing.Length);
        expanded[expanded.Length - 1] = material;
        vt.additionalVirtualTextureMaterials = expanded;
        EditorUtility.SetDirty(vt);
    }

    private static void ConfigureDefaultLayers(Bfp2GpuIndirectRenderer renderer, NYCGISDataRootConfig dataRootConfig)
    {
        renderer.layers = new[]
        {
            new Bfp2GpuIndirectRenderer.Bfp2LayerSettings
            {
                name = "buildings",
                enabled = true,
                castShadows = true,
                fullLayerResidency = true,
                submitAllResidentPacks = true,
                directory = dataRootConfig.BuildingsBfp2Directory,
                tint = Color.white,
                maxResidentPacks = 0,
                maxLoadsPerFrame = 1,
                activeDistanceMeters = 100000.0f,
                maxResidentBytes = 0L
            },
            new Bfp2GpuIndirectRenderer.Bfp2LayerSettings
            {
                name = "roadbed",
                enabled = true,
                castShadows = false,
                fullLayerResidency = false,
                submitAllResidentPacks = false,
                directory = dataRootConfig.RoadbedBfp2Directory,
                tint = new Color(0.72f, 0.72f, 0.72f, 1.0f),
                maxResidentPacks = 8,
                maxLoadsPerFrame = 1,
                activeDistanceMeters = 90000.0f,
                maxResidentBytes = 256L * 1024L * 1024L
            },
            new Bfp2GpuIndirectRenderer.Bfp2LayerSettings
            {
                name = "parking-lot",
                enabled = true,
                castShadows = false,
                fullLayerResidency = false,
                submitAllResidentPacks = false,
                directory = dataRootConfig.ParkingLotBfp2Directory,
                tint = new Color(0.62f, 0.66f, 0.68f, 1.0f),
                maxResidentPacks = 4,
                maxLoadsPerFrame = 1,
                activeDistanceMeters = 90000.0f,
                maxResidentBytes = 128L * 1024L * 1024L
            },
            new Bfp2GpuIndirectRenderer.Bfp2LayerSettings
            {
                name = "entrance",
                enabled = true,
                castShadows = false,
                fullLayerResidency = false,
                submitAllResidentPacks = false,
                directory = dataRootConfig.EntranceBfp2Directory,
                tint = new Color(0.74f, 0.70f, 0.64f, 1.0f),
                maxResidentPacks = 2,
                maxLoadsPerFrame = 1,
                activeDistanceMeters = 90000.0f,
                maxResidentBytes = 64L * 1024L * 1024L
            }
        };
    }

    private static Material EnsureMaterial(Texture2DArray facadeArray)
    {
        Directory.CreateDirectory("Assets/Generated/NYCGISDemo");
        Shader shader = Shader.Find("NYCGIS/BFP2 Gpu Indirect URP");
        if (shader == null)
        {
            throw new InvalidOperationException("BFP2 shader not found.");
        }

        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(shader)
            {
                name = "BFP2GpuIndirect"
            };
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        else
        {
            material.shader = shader;
        }

        material.SetFloat("_AmbientLift", 0.28f);
        material.SetFloat("_DiffuseBoost", 0.95f);
        Bfp2FacadeAssetBuilder.ApplyFacadeDefaults(material, facadeArray);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static T FindObjectIncludingInactive<T>() where T : UnityEngine.Object
    {
        T[] objects = Resources.FindObjectsOfTypeAll<T>();
        foreach (T obj in objects)
        {
            if (obj == null)
            {
                continue;
            }

            if (obj is Component component && component.gameObject.scene.IsValid())
            {
                return obj;
            }

            if (obj is GameObject go && go.scene.IsValid())
            {
                return obj;
            }
        }

        return null;
    }
}

[InitializeOnLoad]
public static class NYCGISMapVisualEditorStreamingTicker
{
    private const double TickIntervalSeconds = 1.0 / 15.0;
    private static double nextTickTime;
    private static bool isTicking;

    static NYCGISMapVisualEditorStreamingTicker()
    {
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    private static void Tick()
    {
        if (isTicking
            || Application.isPlaying
            || EditorApplication.isCompiling
            || EditorApplication.isUpdating
            || EditorApplication.timeSinceStartup < nextTickTime)
        {
            return;
        }

        nextTickTime = EditorApplication.timeSinceStartup + TickIntervalSeconds;
        bool repaint = false;
        isTicking = true;
        try
        {
            Bfp2GpuIndirectRenderer[] bfp2Renderers =
                Resources.FindObjectsOfTypeAll<Bfp2GpuIndirectRenderer>();
            for (int i = 0; i < bfp2Renderers.Length; i++)
            {
                Bfp2GpuIndirectRenderer renderer = bfp2Renderers[i];
                if (renderer == null
                    || !renderer.gameObject.scene.IsValid()
                    || !renderer.NeedsEditorStreamingTick)
                {
                    continue;
                }

                renderer.EditorStreamingTick();
                repaint = true;
            }

            OrthophotoVirtualTextureController[] orthophotoControllers =
                Resources.FindObjectsOfTypeAll<OrthophotoVirtualTextureController>();
            for (int i = 0; i < orthophotoControllers.Length; i++)
            {
                OrthophotoVirtualTextureController controller = orthophotoControllers[i];
                if (controller == null
                    || !controller.gameObject.scene.IsValid()
                    || !controller.NeedsEditorStreamingTick)
                {
                    continue;
                }

                controller.EditorStreamingTick();
                repaint = true;
            }
        }
        finally
        {
            isTicking = false;
        }

        if (repaint)
        {
            SceneView.RepaintAll();
        }
    }
}

[InitializeOnLoad]
public static class Bfp2FacadeAssetBuilder
{
    public const string FacadeArrayPath = "Assets/Generated/NYCGISDemo/BFP2FacadePilot_18_26.asset";

    private const string SourceFolder = "Assets/Resources/NYCGISDemo/FacadePilot";
    private const string MaterialPath = "Assets/Generated/NYCGISDemo/BFP2GpuIndirect.mat";
    private const string ShaderName = "NYCGIS/BFP2 Gpu Indirect URP";

    private static readonly int FacadeArrayId = Shader.PropertyToID("_Bfp2FacadeArray");
    private static readonly int FacadeEnabledId = Shader.PropertyToID("_UseBfp2FacadeArray");
    private static readonly int FacadeStrengthId = Shader.PropertyToID("_Bfp2FacadeStrength");
    private static readonly int FacadeWidthId = Shader.PropertyToID("_Bfp2FacadeWidthMeters");
    private static readonly int FacadeHeightId = Shader.PropertyToID("_Bfp2FacadeHeightMeters");
    private static readonly int FacadeCellSizeId = Shader.PropertyToID("_Bfp2FacadeCellSizeMeters");
    private static readonly int FacadeTextureCountId = Shader.PropertyToID("_Bfp2FacadeTextureCount");
    private static readonly int RoofOrthophotoEnabledId = Shader.PropertyToID("_UseBfp2RoofOrthophoto");
    private static readonly int RoofOrthophotoStrengthId = Shader.PropertyToID("_Bfp2RoofOrthophotoStrength");

    static Bfp2FacadeAssetBuilder()
    {
        EditorApplication.delayCall += AutoBuildIfMissing;
    }

    [MenuItem("Tools/NYC GIS Demo/BFP2/Build Facade Texture Array 18-26")]
    public static void BuildFacadeTextureArrayMenu()
    {
        Texture2DArray array = EnsureFacadeTextureArray(true);
        Material material = EnsureBfp2Material(array);
        Debug.Log("Built BFP2 facade texture array: " + FacadeArrayPath + " (" + array.depth + " slices).", material);
    }

    public static void BuildFacadeTextureArrayBatch()
    {
        int exitCode = 0;
        try
        {
            BuildFacadeTextureArrayMenu();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            Debug.LogException(exception);
        }
        finally
        {
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(exitCode);
            }
        }
    }

    public static Texture2DArray EnsureFacadeTextureArray(bool forceRebuild = false)
    {
        Directory.CreateDirectory("Assets/Generated/NYCGISDemo");

        Texture2DArray existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(FacadeArrayPath);
        List<Texture2D> sources = LoadSourceTextures();
        if (!forceRebuild && existing != null && existing.depth == sources.Count)
        {
            return existing;
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("No facade source textures were found under " + SourceFolder + ".");
        }

        int width = sources[0].width;
        int height = sources[0].height;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].width != width || sources[i].height != height)
            {
                throw new InvalidOperationException("Facade texture sizes must match for Texture2DArray: " + sources[i].name);
            }
        }

        Texture2DArray array = new Texture2DArray(width, height, sources.Count, TextureFormat.RGBA32, true, false)
        {
            name = "BFP2FacadePilot_18_26",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 4
        };

        List<ImporterRestoreState> restoreStates = MakeSourcesReadable(sources);
        try
        {
            for (int slice = 0; slice < sources.Count; slice++)
            {
                Texture2D source = sources[slice];
                int mipCount = Mathf.Min(source.mipmapCount, array.mipmapCount);
                for (int mip = 0; mip < mipCount; mip++)
                {
                    array.SetPixels32(source.GetPixels32(mip), slice, mip);
                }
            }
        }
        finally
        {
            RestoreImporters(restoreStates);
        }

        array.Apply(true, true);

        if (existing != null)
        {
            AssetDatabase.DeleteAsset(FacadeArrayPath);
        }
        AssetDatabase.CreateAsset(array, FacadeArrayPath);
        AssetDatabase.ImportAsset(FacadeArrayPath);
        return AssetDatabase.LoadAssetAtPath<Texture2DArray>(FacadeArrayPath);
    }

    public static Material EnsureBfp2Material(Texture2DArray facadeArray)
    {
        Directory.CreateDirectory("Assets/Generated/NYCGISDemo");
        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            throw new InvalidOperationException("BFP2 shader not found: " + ShaderName);
        }

        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(shader)
            {
                name = "BFP2GpuIndirect"
            };
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        else
        {
            material.shader = shader;
        }

        ApplyFacadeDefaults(material, facadeArray);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        return material;
    }

    public static void ApplyFacadeDefaults(Material material, Texture2DArray facadeArray)
    {
        if (material == null)
        {
            return;
        }

        int textureCount = facadeArray != null ? facadeArray.depth : 0;
        material.SetTexture(FacadeArrayId, facadeArray);
        material.SetFloat(FacadeEnabledId, textureCount > 0 ? 1.0f : 0.0f);
        material.SetFloat(FacadeStrengthId, 0.88f);
        material.SetFloat(FacadeWidthId, 42.0f);
        material.SetFloat(FacadeHeightId, 72.0f);
        material.SetFloat(FacadeCellSizeId, 16.0f);
        material.SetFloat(FacadeTextureCountId, textureCount);
        material.SetFloat(RoofOrthophotoEnabledId, 1.0f);
        material.SetFloat(RoofOrthophotoStrengthId, 1.0f);
    }

    private static void AutoBuildIfMissing()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            return;
        }

        if (AssetDatabase.LoadAssetAtPath<Texture2DArray>(FacadeArrayPath) != null)
        {
            return;
        }

        try
        {
            Texture2DArray array = EnsureFacadeTextureArray(false);
            EnsureBfp2Material(array);
            Debug.Log("Auto-built missing BFP2 facade texture array: " + FacadeArrayPath + ".");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not auto-build BFP2 facade texture array: " + exception.Message);
        }
    }

    private static List<Texture2D> LoadSourceTextures()
    {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { SourceFolder });
        List<Texture2D> textures = new List<Texture2D>(guids.Length);
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture != null)
            {
                textures.Add(texture);
            }
        }

        textures.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return textures;
    }

    private static List<ImporterRestoreState> MakeSourcesReadable(List<Texture2D> sources)
    {
        List<ImporterRestoreState> states = new List<ImporterRestoreState>(sources.Count);
        foreach (Texture2D source in sources)
        {
            string path = AssetDatabase.GetAssetPath(source);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                continue;
            }

            ImporterRestoreState state = new ImporterRestoreState
            {
                path = path,
                isReadable = importer.isReadable,
                compression = importer.textureCompression
            };
            states.Add(state);

            if (!importer.isReadable || importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.isReadable = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
        }

        return states;
    }

    private static void RestoreImporters(List<ImporterRestoreState> states)
    {
        foreach (ImporterRestoreState state in states)
        {
            TextureImporter importer = AssetImporter.GetAtPath(state.path) as TextureImporter;
            if (importer == null)
            {
                continue;
            }

            if (importer.isReadable != state.isReadable || importer.textureCompression != state.compression)
            {
                importer.isReadable = state.isReadable;
                importer.textureCompression = state.compression;
                importer.SaveAndReimport();
            }
        }
    }

    private struct ImporterRestoreState
    {
        public string path;
        public bool isReadable;
        public TextureImporterCompression compression;
    }
}
