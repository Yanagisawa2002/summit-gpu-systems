using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class NYCGISGpuVegetationRenderer : MonoBehaviour
{
    private const string ExpectedSchema = "nycgis-gpu-vegetation-v1";
    private const int WordsPerTree = 8;
    private const int WordsPerCluster = 8;
    private const int DrawArgsWords = 12;
    private const int TrunkVertexCount = 48;
    private const int NearCrownVertexCount = 300;
    private const int FarCrownVertexCount = 24;

    private static readonly int TreeWordsId = Shader.PropertyToID("_VegetationTreeWords");
    private static readonly int ClusterWordsId = Shader.PropertyToID("_VegetationClusterWords");
    private static readonly int NearIndicesId = Shader.PropertyToID("_VegetationNearIndices");
    private static readonly int FarIndicesId = Shader.PropertyToID("_VegetationFarIndices");
    private static readonly int VisibleIndicesId = Shader.PropertyToID("_VegetationVisibleIndices");
    private static readonly int DrawArgsId = Shader.PropertyToID("_VegetationDrawArgs");
    private static readonly int StatsId = Shader.PropertyToID("_VegetationStats");
    private static readonly int RecordCountId = Shader.PropertyToID("_VegetationRecordCount");
    private static readonly int ClusterCountId = Shader.PropertyToID("_VegetationClusterCount");
    private static readonly int FrustumPlanesId = Shader.PropertyToID("_VegetationFrustumPlanes");
    private static readonly int CameraPositionId = Shader.PropertyToID("_VegetationCameraPositionWS");
    private static readonly int NearDistanceId = Shader.PropertyToID("_VegetationNearDistance");
    private static readonly int MaxDistanceId = Shader.PropertyToID("_VegetationMaxDistance");
    private static readonly int MinimumScreenRatioId = Shader.PropertyToID("_VegetationMinimumScreenRatio");
    private static readonly int DataToWorldId = Shader.PropertyToID("_VegetationDataToWorld");
    private static readonly int GeometryModeId = Shader.PropertyToID("_VegetationGeometryMode");
    private static readonly int CrownTintId = Shader.PropertyToID("_VegetationCrownTint");
    private static readonly int TrunkColorId = Shader.PropertyToID("_VegetationTrunkColor");
    private static readonly int WindId = Shader.PropertyToID("_VegetationWind");

    [Header("Data")]
    public bool useSharedDataRootConfig = true;
    public NYCGISDataRootConfig dataRootConfig;
    public string manifestPath;
    public bool loadInEditMode = true;
    [Min(1024 * 1024)] public int maxUploadBytesPerFrame = 8 * 1024 * 1024;

    [Header("World Alignment")]
    public TerrainTileGridLoader terrainLoader;

    [Header("GPU Rendering")]
    public ComputeShader cullCompute;
    public Material drawMaterial;
    public Camera cameraOverride;
    public bool useSceneViewCameraInEditor = true;
    public bool drawInEditMode = true;
    public bool renderVegetation = true;
    public bool castShadows = true;
    public bool receiveShadows = true;
    [Min(10.0f)] public float nearLodDistanceMeters = 220.0f;
    [Min(100.0f)] public float maximumDistanceMeters = 100000.0f;
    [Min(0.0f)] public float minimumScreenRadiusRatio;
    [Min(0.0f)] public float cameraMoveCullThresholdMeters = 0.5f;
    [Min(0.0f)] public float cameraRotateCullThresholdDegrees = 0.25f;
    public Color crownTint = Color.white;
    public Color trunkColor = new Color(0.20f, 0.095f, 0.035f, 1.0f);
    [Range(0.0f, 1.0f)] public float windStrength = 0.15f;
    [Min(0.01f)] public float windFrequency = 0.35f;

    [Header("Diagnostics")]
    public bool showRuntimeHud = true;
    public bool enableStatsReadback = true;
    [Min(0.1f)] public float statsReadbackIntervalSeconds = 0.5f;

    private readonly Plane[] frustumPlanes = new Plane[6];
    private readonly Vector4[] frustumPlaneVectors = new Vector4[6];
    private MaterialPropertyBlock trunkProperties;
    private MaterialPropertyBlock nearCrownProperties;
    private MaterialPropertyBlock farCrownProperties;

    private VegetationManifest manifest;
    private Task<LoadResult> loadTask;
    private int loadGeneration;
    private uint[] pendingTreeWords;
    private uint[] pendingClusterWords;
    private int uploadedTreeWords;
    private int uploadedClusterWords;
    private GraphicsBuffer treeWordsBuffer;
    private GraphicsBuffer clusterWordsBuffer;
    private GraphicsBuffer nearVisibleBuffer;
    private GraphicsBuffer farVisibleBuffer;
    private GraphicsBuffer drawArgsBuffer;
    private GraphicsBuffer statsBuffer;
    private int clearKernel = -1;
    private int cullKernel = -1;
    private bool buffersReady;
    private bool cullDirty = true;
    private bool hasCameraSample;
    private Vector3 lastCameraPosition;
    private Quaternion lastCameraRotation;
    private float lastFieldOfView;
    private float lastAspect;
    private float lastOrthoSize;
    private Matrix4x4 dataToWorld = Matrix4x4.identity;
    private Bounds worldBounds = new Bounds(Vector3.zero, Vector3.one);
    private bool statsReadbackPending;
    private float nextStatsReadbackTime;
    private int visibleNearCount;
    private int visibleFarCount;
    private string status = "GPU vegetation idle";

    public int TotalTreeCount => manifest != null ? manifest.recordCount : 0;
    public int ClusterCount => manifest != null ? manifest.clusterCount : 0;
    public int VisibleNearCount => visibleNearCount;
    public int VisibleFarCount => visibleFarCount;
    public long EstimatedGpuBytes =>
        (long)TotalTreeCount * (WordsPerTree + 2L) * sizeof(uint) +
        (long)ClusterCount * WordsPerCluster * sizeof(uint) +
        DrawArgsWords * sizeof(uint) +
        4L * sizeof(uint);
    public bool IsReady => buffersReady;
    public string Status => status;
    public string ResolvedManifestPath => ResolveManifestPath();

    private void OnEnable()
    {
        EnsureDrawProperties();
        ValidateSettings();
        ResolveReferences();
        ResolveKernels();
        if (Application.isPlaying || loadInEditMode)
        {
            BeginLoad();
        }
#if UNITY_EDITOR
        EditorApplication.update += EditorTick;
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
#endif
        loadGeneration++;
        ReleaseBuffers();
    }

    private void OnDestroy()
    {
        ReleaseBuffers();
    }

    private void OnValidate()
    {
        ValidateSettings();
        cullDirty = true;
    }

    private void LateUpdate()
    {
        Tick();
    }

#if UNITY_EDITOR
    private void EditorTick()
    {
        if (Application.isPlaying || !isActiveAndEnabled)
        {
            return;
        }
        Tick();
        if (buffersReady && drawInEditMode)
        {
            SceneView.RepaintAll();
        }
    }
#endif

    private void Tick()
    {
        if (!Application.isPlaying && !loadInEditMode)
        {
            return;
        }

        ResolveCompletedLoad();
        ProcessUploads();
        if (!buffersReady)
        {
            return;
        }
        if (!renderVegetation)
        {
            status = $"GPU vegetation resident; rendering paused ({EstimatedGpuBytes / (1024f * 1024f):F1} MiB)";
            return;
        }
        if ((clearKernel < 0 || cullKernel < 0) && cullCompute != null)
        {
            ResolveKernels();
        }

        ResolveDataToWorld();
        Camera camera = ResolveCamera();
        if (camera == null)
        {
            status = "GPU vegetation ready; waiting for a camera";
            return;
        }

        if (ShouldRefreshCull(camera))
        {
            DispatchCull(camera);
        }
        if (Application.isPlaying || drawInEditMode)
        {
            SubmitDraws();
        }
    }

    [ContextMenu("Reload Full-City GPU Vegetation")]
    public void BeginLoad()
    {
        ReleaseBuffers();
        ResolveKernels();
        manifest = null;
        string resolvedManifestPath = ResolveManifestPath();
        if (!File.Exists(resolvedManifestPath))
        {
            status = "GPU vegetation manifest missing: " + resolvedManifestPath;
            Debug.LogWarning(status, this);
            return;
        }

        try
        {
            manifest = JsonUtility.FromJson<VegetationManifest>(
                File.ReadAllText(resolvedManifestPath));
        }
        catch (Exception exception)
        {
            status = "GPU vegetation manifest parse failed: " + exception.Message;
            Debug.LogError(status, this);
            return;
        }

        if (!ValidateManifest(manifest, out string validationError))
        {
            status = validationError;
            Debug.LogError(status, this);
            manifest = null;
            return;
        }

        string manifestDirectory = Path.GetDirectoryName(resolvedManifestPath) ?? string.Empty;
        string treePath = NYCGISDataRootConfig.Combine(manifestDirectory, manifest.treeFile);
        string clusterPath = NYCGISDataRootConfig.Combine(manifestDirectory, manifest.clusterFile);
        int generation = ++loadGeneration;
        status = $"Reading {manifest.recordCount:N0} trees on a background thread";
        loadTask = Task.Run(() => LoadPayload(
            generation,
            treePath,
            clusterPath,
            manifest.recordCount,
            manifest.recordStrideBytes,
            manifest.clusterCount,
            manifest.clusterStrideBytes));
    }

    public void SetRenderingEnabled(bool enabled)
    {
        renderVegetation = enabled;
        cullDirty = true;
        if (enabled && buffersReady)
        {
            status =
                $"GPU vegetation ready: {TotalTreeCount:N0} trees / " +
                $"{ClusterCount:N0} clusters / {EstimatedGpuBytes / (1024f * 1024f):F1} MiB";
        }
    }

    private static LoadResult LoadPayload(
        int generation,
        string treePath,
        string clusterPath,
        int recordCount,
        int recordStride,
        int clusterCount,
        int clusterStride)
    {
        try
        {
            byte[] treeBytes = File.ReadAllBytes(treePath);
            byte[] clusterBytes = File.ReadAllBytes(clusterPath);
            long expectedTreeBytes = (long)recordCount * recordStride;
            long expectedClusterBytes = (long)clusterCount * clusterStride;
            if (treeBytes.LongLength != expectedTreeBytes)
            {
                return LoadResult.Failed(
                    generation,
                    $"Tree payload length {treeBytes.LongLength:N0} != {expectedTreeBytes:N0}");
            }
            if (clusterBytes.LongLength != expectedClusterBytes)
            {
                return LoadResult.Failed(
                    generation,
                    $"Cluster payload length {clusterBytes.LongLength:N0} != {expectedClusterBytes:N0}");
            }

            uint[] treeWords = new uint[treeBytes.Length / sizeof(uint)];
            uint[] clusterWords = new uint[clusterBytes.Length / sizeof(uint)];
            Buffer.BlockCopy(treeBytes, 0, treeWords, 0, treeBytes.Length);
            Buffer.BlockCopy(clusterBytes, 0, clusterWords, 0, clusterBytes.Length);
            return LoadResult.Succeeded(generation, treeWords, clusterWords);
        }
        catch (Exception exception)
        {
            return LoadResult.Failed(generation, exception.Message);
        }
    }

    private void ResolveCompletedLoad()
    {
        if (loadTask == null || !loadTask.IsCompleted)
        {
            return;
        }

        Task<LoadResult> completed = loadTask;
        loadTask = null;
        LoadResult result;
        try
        {
            result = completed.Result;
        }
        catch (Exception exception)
        {
            status = "GPU vegetation background load failed: " + exception.Message;
            Debug.LogError(status, this);
            return;
        }

        if (result.generation != loadGeneration)
        {
            return;
        }
        if (!string.IsNullOrEmpty(result.error))
        {
            status = "GPU vegetation load failed: " + result.error;
            Debug.LogError(status, this);
            return;
        }

        pendingTreeWords = result.treeWords;
        pendingClusterWords = result.clusterWords;
        uploadedTreeWords = 0;
        uploadedClusterWords = 0;
        AllocateBuffers();
        status = $"Uploading {manifest.recordCount:N0} trees to GPU in staged chunks";
    }

    private void AllocateBuffers()
    {
        ReleaseGpuBuffers();
        buffersReady = false;
        cullDirty = true;
        hasCameraSample = false;
        int treeWordCount = manifest.recordCount * WordsPerTree;
        int clusterWordCount = manifest.clusterCount * WordsPerCluster;
        treeWordsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, treeWordCount, sizeof(uint));
        clusterWordsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, clusterWordCount, sizeof(uint));
        nearVisibleBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            manifest.recordCount,
            sizeof(uint));
        farVisibleBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            manifest.recordCount,
            sizeof(uint));
        drawArgsBuffer = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
            DrawArgsWords,
            sizeof(uint));
        statsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint));
        pendingTreeWords ??= Array.Empty<uint>();
        pendingClusterWords ??= Array.Empty<uint>();
    }

    private void ProcessUploads()
    {
        if (treeWordsBuffer == null || clusterWordsBuffer == null ||
            pendingTreeWords == null || pendingClusterWords == null)
        {
            return;
        }

        int wordBudget = Mathf.Max(1, maxUploadBytesPerFrame / sizeof(uint));
        if (uploadedClusterWords < pendingClusterWords.Length)
        {
            int count = Mathf.Min(wordBudget, pendingClusterWords.Length - uploadedClusterWords);
            clusterWordsBuffer.SetData(
                pendingClusterWords,
                uploadedClusterWords,
                uploadedClusterWords,
                count);
            uploadedClusterWords += count;
            wordBudget -= count;
        }
        if (wordBudget > 0 && uploadedTreeWords < pendingTreeWords.Length)
        {
            int count = Mathf.Min(wordBudget, pendingTreeWords.Length - uploadedTreeWords);
            treeWordsBuffer.SetData(
                pendingTreeWords,
                uploadedTreeWords,
                uploadedTreeWords,
                count);
            uploadedTreeWords += count;
        }

        if (uploadedTreeWords < pendingTreeWords.Length ||
            uploadedClusterWords < pendingClusterWords.Length)
        {
            float progress = (uploadedTreeWords + uploadedClusterWords) /
                             (float)Mathf.Max(
                                 1,
                                 pendingTreeWords.Length + pendingClusterWords.Length);
            status = $"GPU vegetation staged upload {progress:P0}";
            return;
        }

        pendingTreeWords = null;
        pendingClusterWords = null;
        buffersReady = true;
        cullDirty = true;
        hasCameraSample = false;
        status =
            $"GPU vegetation ready: {manifest.recordCount:N0} trees / " +
            $"{manifest.clusterCount:N0} clusters / {EstimatedGpuBytes / (1024f * 1024f):F1} MiB";
        Debug.Log(status, this);
    }

    private void DispatchCull(Camera camera)
    {
        if (clearKernel < 0 || cullKernel < 0 || cullCompute == null)
        {
            return;
        }
        if (!NYCGISStreamingFrameContext.CopyFrustumPlanes(camera, frustumPlanes))
        {
            return;
        }
        for (int index = 0; index < frustumPlanes.Length; index++)
        {
            Plane plane = frustumPlanes[index];
            frustumPlaneVectors[index] =
                new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
        }

        cullCompute.SetBuffer(clearKernel, DrawArgsId, drawArgsBuffer);
        cullCompute.SetBuffer(clearKernel, StatsId, statsBuffer);
        cullCompute.Dispatch(clearKernel, 1, 1, 1);

        cullCompute.SetBuffer(cullKernel, TreeWordsId, treeWordsBuffer);
        cullCompute.SetBuffer(cullKernel, ClusterWordsId, clusterWordsBuffer);
        cullCompute.SetBuffer(cullKernel, NearIndicesId, nearVisibleBuffer);
        cullCompute.SetBuffer(cullKernel, FarIndicesId, farVisibleBuffer);
        cullCompute.SetBuffer(cullKernel, DrawArgsId, drawArgsBuffer);
        cullCompute.SetBuffer(cullKernel, StatsId, statsBuffer);
        cullCompute.SetInt(RecordCountId, manifest.recordCount);
        cullCompute.SetInt(ClusterCountId, manifest.clusterCount);
        cullCompute.SetVectorArray(FrustumPlanesId, frustumPlaneVectors);
        cullCompute.SetVector(CameraPositionId, camera.transform.position);
        cullCompute.SetFloat(NearDistanceId, nearLodDistanceMeters);
        cullCompute.SetFloat(MaxDistanceId, maximumDistanceMeters);
        cullCompute.SetFloat(MinimumScreenRatioId, minimumScreenRadiusRatio);
        cullCompute.SetMatrix(DataToWorldId, dataToWorld);
        cullCompute.Dispatch(cullKernel, manifest.clusterCount, 1, 1);

        cullDirty = false;
        hasCameraSample = true;
        lastCameraPosition = camera.transform.position;
        lastCameraRotation = camera.transform.rotation;
        lastFieldOfView = camera.fieldOfView;
        lastAspect = camera.aspect;
        lastOrthoSize = camera.orthographicSize;
        QueueStatsReadback();
    }

    private void SubmitDraws()
    {
        if (drawMaterial == null || drawArgsBuffer == null)
        {
            return;
        }
        EnsureDrawProperties();

        ShadowCastingMode shadowMode =
            castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        float windTime = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
        Vector4 wind = new Vector4(
            windStrength,
            windFrequency,
            windTime,
            0.0f);

        PrepareDrawProperties(trunkProperties, nearVisibleBuffer, 0, wind);
        PrepareDrawProperties(nearCrownProperties, nearVisibleBuffer, 1, wind);
        PrepareDrawProperties(farCrownProperties, farVisibleBuffer, 2, wind);

        Graphics.DrawProceduralIndirect(
            drawMaterial,
            worldBounds,
            MeshTopology.Triangles,
            drawArgsBuffer,
            0,
            null,
            trunkProperties,
            shadowMode,
            receiveShadows,
            gameObject.layer);
        Graphics.DrawProceduralIndirect(
            drawMaterial,
            worldBounds,
            MeshTopology.Triangles,
            drawArgsBuffer,
            sizeof(uint) * 4,
            null,
            nearCrownProperties,
            shadowMode,
            receiveShadows,
            gameObject.layer);
        Graphics.DrawProceduralIndirect(
            drawMaterial,
            worldBounds,
            MeshTopology.Triangles,
            drawArgsBuffer,
            sizeof(uint) * 8,
            null,
            farCrownProperties,
            shadowMode,
            receiveShadows,
            gameObject.layer);
    }

    private void PrepareDrawProperties(
        MaterialPropertyBlock properties,
        GraphicsBuffer visibleBuffer,
        int geometryMode,
        Vector4 wind)
    {
        properties.Clear();
        properties.SetBuffer(TreeWordsId, treeWordsBuffer);
        properties.SetBuffer(VisibleIndicesId, visibleBuffer);
        properties.SetMatrix(DataToWorldId, dataToWorld);
        properties.SetInt(GeometryModeId, geometryMode);
        properties.SetColor(CrownTintId, crownTint);
        properties.SetColor(TrunkColorId, trunkColor);
        properties.SetVector(WindId, wind);
    }

    private void ResolveDataToWorld()
    {
        ResolveReferences();
        Matrix4x4 next = Matrix4x4.identity;
        if (terrainLoader != null && manifest != null)
        {
            Vector3 origin = terrainLoader.ProjectedToWorld(
                new NYCGISProjectedPosition(
                    manifest.originEasting,
                    manifest.originNorthing,
                    0.0f));
            Vector3 x = terrainLoader.ProjectedToWorld(
                new NYCGISProjectedPosition(
                    manifest.originEasting + 1.0,
                    manifest.originNorthing,
                    0.0f)) - origin;
            Vector3 y = terrainLoader.ProjectedToWorld(
                new NYCGISProjectedPosition(
                    manifest.originEasting,
                    manifest.originNorthing,
                    1.0f)) - origin;
            Vector3 z = terrainLoader.ProjectedToWorld(
                new NYCGISProjectedPosition(
                    manifest.originEasting,
                    manifest.originNorthing + 1.0,
                    0.0f)) - origin;
            next.SetColumn(0, new Vector4(x.x, x.y, x.z, 0.0f));
            next.SetColumn(1, new Vector4(y.x, y.y, y.z, 0.0f));
            next.SetColumn(2, new Vector4(z.x, z.y, z.z, 0.0f));
            next.SetColumn(3, new Vector4(origin.x, origin.y, origin.z, 1.0f));
        }
        if (MatrixChanged(dataToWorld, next))
        {
            dataToWorld = next;
            cullDirty = true;
            RebuildWorldBounds();
        }
    }

    private void RebuildWorldBounds()
    {
        if (manifest == null || manifest.bounds == null)
        {
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 1000.0f);
            return;
        }

        float minX = (float)(manifest.bounds.minEasting - manifest.originEasting) - 12.0f;
        float maxX = (float)(manifest.bounds.maxEasting - manifest.originEasting) + 12.0f;
        float minZ = (float)(manifest.bounds.minNorthing - manifest.originNorthing) - 12.0f;
        float maxZ = (float)(manifest.bounds.maxNorthing - manifest.originNorthing) + 12.0f;
        float minY = manifest.bounds.minAltitudeMeters - 2.0f;
        float maxY = manifest.bounds.maxAltitudeMeters + 2.0f;
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new Vector3(
                (corner & 1) == 0 ? minX : maxX,
                (corner & 2) == 0 ? minY : maxY,
                (corner & 4) == 0 ? minZ : maxZ);
            Vector3 world = dataToWorld.MultiplyPoint3x4(local);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }
        worldBounds = new Bounds((min + max) * 0.5f, max - min);
    }

    private bool ShouldRefreshCull(Camera camera)
    {
        if (cullDirty || !hasCameraSample)
        {
            return true;
        }
        if (Vector3.Distance(camera.transform.position, lastCameraPosition) >=
            cameraMoveCullThresholdMeters)
        {
            return true;
        }
        if (Quaternion.Angle(camera.transform.rotation, lastCameraRotation) >=
            cameraRotateCullThresholdDegrees)
        {
            return true;
        }
        return !Mathf.Approximately(camera.fieldOfView, lastFieldOfView) ||
               !Mathf.Approximately(camera.aspect, lastAspect) ||
               !Mathf.Approximately(camera.orthographicSize, lastOrthoSize);
    }

    private void QueueStatsReadback()
    {
        if (!enableStatsReadback || statsReadbackPending ||
            Time.realtimeSinceStartup < nextStatsReadbackTime)
        {
            return;
        }
        statsReadbackPending = true;
        nextStatsReadbackTime = Time.realtimeSinceStartup + statsReadbackIntervalSeconds;
        AsyncGPUReadback.Request(statsBuffer, request =>
        {
            statsReadbackPending = false;
            if (request.hasError || !isActiveAndEnabled)
            {
                return;
            }
            var data = request.GetData<uint>();
            if (data.Length >= 4)
            {
                visibleNearCount = (int)data[1];
                visibleFarCount = (int)data[2];
            }
        });
    }

    private void ResolveReferences()
    {
        if (terrainLoader == null)
        {
            terrainLoader = FindAnyObjectByType<TerrainTileGridLoader>(FindObjectsInactive.Include);
        }
    }

    private void EnsureDrawProperties()
    {
        trunkProperties ??= new MaterialPropertyBlock();
        nearCrownProperties ??= new MaterialPropertyBlock();
        farCrownProperties ??= new MaterialPropertyBlock();
    }

    private Camera ResolveCamera()
    {
        if (cameraOverride != null && cameraOverride.isActiveAndEnabled)
        {
            return cameraOverride;
        }
        if (Application.isPlaying && Camera.main != null)
        {
            return Camera.main;
        }
#if UNITY_EDITOR
        if (useSceneViewCameraInEditor &&
            SceneView.lastActiveSceneView != null &&
            SceneView.lastActiveSceneView.camera != null)
        {
            return SceneView.lastActiveSceneView.camera;
        }
#endif
        return Camera.main;
    }

    private void ResolveKernels()
    {
        if (cullCompute == null)
        {
            clearKernel = -1;
            cullKernel = -1;
            return;
        }
        clearKernel = cullCompute.FindKernel("ClearVegetationArgs");
        cullKernel = cullCompute.FindKernel("CullVegetationClusters");
    }

    private string ResolveManifestPath()
    {
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            return NYCGISDataRootConfig.NormalizePath(manifestPath);
        }
        NYCGISDataRootConfig config = NYCGISDataRootConfig.Resolve(dataRootConfig);
        return NYCGISDataRootConfig.Combine(
            config.PipelineRootPath,
            @"data\vegetation_processed\nyc_tree_gpu_full_v1\manifest.json");
    }

    private static bool ValidateManifest(
        VegetationManifest value,
        out string error)
    {
        if (value == null)
        {
            error = "GPU vegetation manifest is empty.";
            return false;
        }
        if (!string.Equals(value.schema, ExpectedSchema, StringComparison.Ordinal))
        {
            error = $"GPU vegetation schema '{value.schema}' != '{ExpectedSchema}'.";
            return false;
        }
        if (value.recordCount <= 0 || value.recordStrideBytes != WordsPerTree * sizeof(uint))
        {
            error = "GPU vegetation tree count or stride is invalid.";
            return false;
        }
        if (value.clusterCount <= 0 ||
            value.clusterStrideBytes != WordsPerCluster * sizeof(uint))
        {
            error = "GPU vegetation cluster count or stride is invalid.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(value.treeFile) ||
            string.IsNullOrWhiteSpace(value.clusterFile))
        {
            error = "GPU vegetation payload paths are missing.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private void ValidateSettings()
    {
        maxUploadBytesPerFrame = Mathf.Max(1024 * 1024, maxUploadBytesPerFrame);
        nearLodDistanceMeters = Mathf.Max(10.0f, nearLodDistanceMeters);
        maximumDistanceMeters = Mathf.Max(nearLodDistanceMeters, maximumDistanceMeters);
        minimumScreenRadiusRatio = Mathf.Max(0.0f, minimumScreenRadiusRatio);
        cameraMoveCullThresholdMeters = Mathf.Max(0.0f, cameraMoveCullThresholdMeters);
        cameraRotateCullThresholdDegrees = Mathf.Max(0.0f, cameraRotateCullThresholdDegrees);
        statsReadbackIntervalSeconds = Mathf.Max(0.1f, statsReadbackIntervalSeconds);
        windFrequency = Mathf.Max(0.01f, windFrequency);
    }

    private void ReleaseBuffers()
    {
        buffersReady = false;
        cullDirty = true;
        hasCameraSample = false;
        pendingTreeWords = null;
        pendingClusterWords = null;
        uploadedTreeWords = 0;
        uploadedClusterWords = 0;
        ReleaseGpuBuffers();
    }

    private void ReleaseGpuBuffers()
    {
        treeWordsBuffer?.Release();
        clusterWordsBuffer?.Release();
        nearVisibleBuffer?.Release();
        farVisibleBuffer?.Release();
        drawArgsBuffer?.Release();
        statsBuffer?.Release();
        treeWordsBuffer = null;
        clusterWordsBuffer = null;
        nearVisibleBuffer = null;
        farVisibleBuffer = null;
        drawArgsBuffer = null;
        statsBuffer = null;
    }

    private static bool MatrixChanged(Matrix4x4 left, Matrix4x4 right)
    {
        for (int index = 0; index < 16; index++)
        {
            if (Mathf.Abs(left[index] - right[index]) > 0.00001f)
            {
                return true;
            }
        }
        return false;
    }

    private void OnGUI()
    {
        if (!showRuntimeHud || !Application.isPlaying)
        {
            return;
        }
        GUI.Box(new Rect(12.0f, 112.0f, 500.0f, 104.0f), string.Empty);
        GUI.Label(new Rect(24.0f, 120.0f, 470.0f, 22.0f),
            "NYC GPU Vegetation — full-city resident + compute culling");
        GUI.Label(new Rect(24.0f, 142.0f, 470.0f, 22.0f),
            $"loaded {TotalTreeCount:N0} | visible near {visibleNearCount:N0} | far {visibleFarCount:N0}");
        GUI.Label(new Rect(24.0f, 164.0f, 470.0f, 22.0f),
            $"GPU payload {EstimatedGpuBytes / (1024f * 1024f):F1} MiB | shadows {(castShadows ? "on" : "off")}");
        GUI.Label(new Rect(24.0f, 186.0f, 470.0f, 22.0f), status);
    }

    [Serializable]
    private sealed class VegetationManifest
    {
        public string schema;
        public int recordCount;
        public int recordStrideBytes;
        public int clusterCount;
        public int clusterStrideBytes;
        public double originEasting;
        public double originNorthing;
        public string treeFile;
        public string clusterFile;
        public VegetationBounds bounds;
    }

    [Serializable]
    private sealed class VegetationBounds
    {
        public double minEasting;
        public double minNorthing;
        public double maxEasting;
        public double maxNorthing;
        public float minAltitudeMeters;
        public float maxAltitudeMeters;
    }

    private sealed class LoadResult
    {
        public int generation;
        public uint[] treeWords;
        public uint[] clusterWords;
        public string error;

        public static LoadResult Succeeded(
            int generation,
            uint[] treeWords,
            uint[] clusterWords)
        {
            return new LoadResult
            {
                generation = generation,
                treeWords = treeWords,
                clusterWords = clusterWords,
                error = string.Empty,
            };
        }

        public static LoadResult Failed(int generation, string error)
        {
            return new LoadResult
            {
                generation = generation,
                treeWords = null,
                clusterWords = null,
                error = error ?? "unknown load failure",
            };
        }
    }
}
