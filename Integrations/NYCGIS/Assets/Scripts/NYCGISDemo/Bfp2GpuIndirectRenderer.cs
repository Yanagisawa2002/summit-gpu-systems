using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

public enum Bfp2ClusterCullAlgorithm
{
    ScalarAoS = 0,
    ScalarCompact = 1,
    WaveCompact = 2,
    WaveTile32 = 3,
    WaveTile64 = 4,
    WaveTile32Index16 = 5
}

public struct Bfp2CullValidationSnapshot
{
    public ulong PrimaryCullPassEpoch;
    public int PreparedCameraCount;
    public int PrimaryCameraInstanceId;
    public int DispatchFrame;
    public int DispatchedPackCount;
    public ulong CameraStateHash;
    public ulong DispatchedPackHash;
    public bool PackSetComplete;
    public int ResidentPacks;
    public ulong VisibleClusters;
    public ulong VisibleIndices;
    public ulong CulledClusters;
    public ulong OverflowClusters;
    public ulong DrawIndices;
    public ulong VisibleTiles;
    public ulong PaddedDrawIndices;
    public ulong PerPackHash;
}

[ExecuteAlways]
public sealed class Bfp2GpuIndirectRenderer : MonoBehaviour
{
    private struct PrimaryCullPassMetadata
    {
        public ulong Epoch;
        public int PreparedCameraCount;
        public int PrimaryCameraInstanceId;
        public int DispatchFrame;
        public int DispatchedPackCount;
        public ulong CameraStateHash;
        public ulong DispatchedPackHash;
        public bool PackSetComplete;
    }

    private const uint Bfp2Magic = 0x32504642u;
    private const int Bfp2Version = 2;
    private const int HeaderBytes = 256;
    private const int VertexStrideBytes = 16;
    private const int ClusterStrideBytes = 80;
    private const int CompactClusterWordsPerCluster = 10;
    private const int Tile32TriangleCount = 32;
    private const int Tile64TriangleCount = 64;
    private const int VisibleTile32DescriptorWords = 2;
    private const int VisibleTile32Index16DescriptorWords = 2;
    private const int DebugWindowId = 7230202;
    private const float PreviousFacadeBlockCellSizeMeters = 64.0f;
    private const float DefaultFacadeParcelCellSizeMeters = 16.0f;
    private const int DefaultSpatialMacroSourcePackCount = 8;
    private const string WaveCullComputeAssetPath =
        "Assets/Shaders/NYCGISDemo/Bfp2GpuClusterCullWave.compute";
    private const string ClusterCullMarker = "BFP2.ClusterCull";
    private const string ClearArgsMarker = "BFP2.ClusterCull.ClearArgs";
    private const string ScalarAosMarker = "BFP2.ClusterCull.ScalarAoS";
    private const string ScalarCompactMarker = "BFP2.ClusterCull.ScalarCompact";
    private const string WaveCompactMarker = "BFP2.ClusterCull.WaveCompact";
    private const string WaveTile32Marker = "BFP2.ClusterCull.WaveTile32";
    private const string WaveTile64Marker = "BFP2.ClusterCull.WaveTile64";
    private const string WaveTile32Index16Marker = "BFP2.ClusterCull.WaveTile32Index16";
    private const string PackCompactMarker = "BFP2.ClusterCull.PackCompactLayout";
    private static readonly uint[] InitialDrawArgsData = { 0u, 1u, 0u, 0u };
    private static readonly uint[] InitialStatsData = { 0u, 0u, 0u, 0u };
    private static readonly uint[] InitialCameraExpansionStatsData = { 0u, 0u };

    private static readonly int VertexWordsId = Shader.PropertyToID("_Bfp2VertexWords");
    private static readonly int IndicesId = Shader.PropertyToID("_Bfp2Indices");
    private static readonly int PackedIndices16Id = Shader.PropertyToID("_Bfp2PackedIndices16");
    private static readonly int ClusterIndexBases16Id = Shader.PropertyToID("_Bfp2ClusterIndexBases16");
    private static readonly int ClusterWordsId = Shader.PropertyToID("_Bfp2ClusterWords");
    private static readonly int CompactClusterWordsId = Shader.PropertyToID("_Bfp2CompactClusterWords");
    private static readonly int VisibleIndicesId = Shader.PropertyToID("_Bfp2VisibleIndices");
    private static readonly int VisibleTileWordsId = Shader.PropertyToID("_Bfp2VisibleTileWords");
    private static readonly int VisibleTileTrianglesId = Shader.PropertyToID("_Bfp2VisibleTileTriangles");
    private static readonly int VisibleTileDescriptorWordsId =
        Shader.PropertyToID("_Bfp2VisibleTileDescriptorWords");
    private static readonly int UseClusterLocalIndices16Id =
        Shader.PropertyToID("_Bfp2UseClusterLocalIndices16");
    private static readonly int DrawArgsId = Shader.PropertyToID("_Bfp2DrawArgs");
    private static readonly int StatsId = Shader.PropertyToID("_Bfp2Stats");
    private static readonly int ClusterCountId = Shader.PropertyToID("_Bfp2ClusterCount");
    private static readonly int MaxVisibleIndicesId = Shader.PropertyToID("_Bfp2MaxVisibleIndices");
    private static readonly int MaxVisibleTileWordsId = Shader.PropertyToID("_Bfp2MaxVisibleTileWords");
    private static readonly int FrustumPlanesId = Shader.PropertyToID("_Bfp2FrustumPlanes");
    private static readonly int CameraPositionOsId = Shader.PropertyToID("_Bfp2CameraPositionOS");
    private static readonly int CameraFrameCountId = Shader.PropertyToID("_Bfp2CameraFrameCount");
    private static readonly int CameraFrustumPlanesId = Shader.PropertyToID("_Bfp2CameraFrustumPlanes");
    private static readonly int CameraPositionsOsId = Shader.PropertyToID("_Bfp2CameraPositionsOS");
    private static readonly int CameraScreenParamsId = Shader.PropertyToID("_Bfp2CameraScreenParams");
    private static readonly int CameraExpansionStatsId = Shader.PropertyToID("_Bfp2CameraExpansionStats");
    private static readonly int MaxDistanceId = Shader.PropertyToID("_Bfp2MaxDistance");
    private static readonly int EnableFrustumCullingId = Shader.PropertyToID("_Bfp2EnableFrustumCulling");
    private static readonly int EnableDistanceCullingId = Shader.PropertyToID("_Bfp2EnableDistanceCulling");
    private static readonly int EnableNormalConeCullingId = Shader.PropertyToID("_Bfp2EnableNormalConeCulling");
    private static readonly int EnableScreenSizeCullingId = Shader.PropertyToID("_Bfp2EnableScreenSizeCulling");
    private static readonly int UseProjectedPixelScreenSizeCullingId =
        Shader.PropertyToID("_Bfp2UseProjectedPixelScreenSizeCulling");
    private static readonly int FlipTriangleWindingId = Shader.PropertyToID("_Bfp2FlipTriangleWinding");
    private static readonly int MinScreenRadiusRatioId = Shader.PropertyToID("_Bfp2MinScreenRadiusRatio");
    private static readonly int MinScreenRadiusPixelsId = Shader.PropertyToID("_Bfp2MinScreenRadiusPixels");
    private static readonly int QuantOriginId = Shader.PropertyToID("_Bfp2QuantOrigin");
    private static readonly int QuantScaleId = Shader.PropertyToID("_Bfp2QuantScale");
    private static readonly int LayerTintId = Shader.PropertyToID("_LayerTint");
    private static readonly int GlobalAmbientLiftId = Shader.PropertyToID("_Bfp2GlobalAmbientLift");
    private static readonly int GlobalDiffuseBoostId = Shader.PropertyToID("_Bfp2GlobalDiffuseBoost");
    private static readonly int GlobalFacadeEnabledId = Shader.PropertyToID("_Bfp2GlobalUseFacadeArray");
    private static readonly int GlobalFacadeStrengthId = Shader.PropertyToID("_Bfp2GlobalFacadeStrength");
    private static readonly int GlobalFacadeWidthId = Shader.PropertyToID("_Bfp2GlobalFacadeWidthMeters");
    private static readonly int GlobalFacadeHeightId = Shader.PropertyToID("_Bfp2GlobalFacadeHeightMeters");
    private static readonly int GlobalFacadeCellSizeId = Shader.PropertyToID("_Bfp2GlobalFacadeCellSizeMeters");
    private static readonly int GlobalFacadeTextureCountId = Shader.PropertyToID("_Bfp2GlobalFacadeTextureCount");
    private static readonly int GlobalFacadeLayerIndexId = Shader.PropertyToID("_Bfp2GlobalFacadeLayerIndex");
    private static readonly int GlobalRoofOrthophotoEnabledId = Shader.PropertyToID("_Bfp2GlobalUseRoofOrthophoto");
    private static readonly int GlobalRoofOrthophotoStrengthId = Shader.PropertyToID("_Bfp2GlobalRoofOrthophotoStrength");
    private static readonly int LayerIndexId = Shader.PropertyToID("_Bfp2LayerIndex");
    private static readonly int FacadeArrayId = Shader.PropertyToID("_Bfp2FacadeArray");

    private static readonly string PipelineDataRoot = NYCGISDataRootConfig.DefaultTilesRootPath;

    private const string Bfp2FacadeArrayAssetPath = "Assets/Generated/NYCGISDemo/BFP2FacadePilot_18_26.asset";
    private const string Bfp2FacadeResourcePath = "NYCGISDemo/FacadePilot";

    [Serializable]
    public sealed class Bfp2LayerSettings
    {
        public string name = "layer";
        public bool enabled = true;
        public bool castShadows = true;
        [Tooltip("Keep every source pack in this layer resident, independently of streaming budgets.")]
        public bool fullLayerResidency;
        [Tooltip("Submit every resident source pack in a full-residency layer. GPU cluster frustum culling remains active.")]
        public bool submitAllResidentPacks;
        public string directory;
        public Color tint = Color.white;
        [Tooltip("Resident pack limit for streamed layers. Ignored when Full Layer Residency is enabled.")]
        public int maxResidentPacks = 16;
        public int maxLoadsPerFrame = 1;
        public float activeDistanceMeters = 100000.0f;
        [Tooltip("Resident byte limit for streamed layers. Ignored when Full Layer Residency is enabled; zero means unlimited.")]
        public long maxResidentBytes = 1024L * 1024L * 1024L;
    }

    [Serializable]
    private sealed class Bfp2FormatMetadata
    {
#pragma warning disable 0649
        public string defaultFacing;
#pragma warning restore 0649
    }

    [Header("Data Root")]
    public bool useSharedDataRootConfig = true;
    public NYCGISDataRootConfig dataRootConfig;

    [Header("BFP2 Assets")]
    public Bfp2LayerSettings[] layers =
    {
        new Bfp2LayerSettings
        {
            name = "buildings",
            castShadows = true,
            fullLayerResidency = true,
            submitAllResidentPacks = true,
            directory = PipelineDataRoot + @"\tum-buildings-full-dem-grounded\binary\bfp2-clustered",
            tint = Color.white,
            maxResidentPacks = 0,
            maxLoadsPerFrame = 1,
            activeDistanceMeters = 100000.0f,
            maxResidentBytes = 0L
        },
        new Bfp2LayerSettings
        {
            name = "roadbed",
            castShadows = false,
            directory = PipelineDataRoot + @"\tum-streetspace-extra\roadbed\binary\bfp2-clustered",
            tint = new Color(0.72f, 0.72f, 0.72f, 1.0f),
            maxResidentPacks = 8,
            maxLoadsPerFrame = 2,
            activeDistanceMeters = 90000.0f,
            maxResidentBytes = 256L * 1024L * 1024L
        },
        new Bfp2LayerSettings
        {
            name = "parking-lot",
            castShadows = false,
            directory = PipelineDataRoot + @"\tum-streetspace-extra\parking-lot\binary\bfp2-clustered",
            tint = new Color(0.62f, 0.66f, 0.68f, 1.0f),
            maxResidentPacks = 2,
            maxLoadsPerFrame = 2,
            activeDistanceMeters = 90000.0f,
            maxResidentBytes = 256L * 1024L * 1024L
        },
        new Bfp2LayerSettings
        {
            name = "entrance",
            castShadows = false,
            directory = PipelineDataRoot + @"\tum-streetspace-extra\entrance\binary\bfp2-clustered",
            tint = new Color(0.74f, 0.70f, 0.64f, 1.0f),
            maxResidentPacks = 2,
            maxLoadsPerFrame = 2,
            activeDistanceMeters = 90000.0f,
            maxResidentBytes = 96L * 1024L * 1024L
        }
    };

    [Header("Rendering")]
    public ComputeShader cullCompute;
    public ComputeShader waveCullCompute;
    public Material drawMaterial;
    public Camera cameraOverride;
    [Tooltip("Bypass camera discovery and use cameraOverride directly. " +
             "Intended for deterministic validation and capture tools.")]
    public bool forceCameraOverride;
    [Tooltip("Submit every resident source pack and let GPU cluster culling " +
             "determine visibility. Intended for deterministic benchmarks; " +
             "production streaming defaults remain unchanged.")]
    public bool forceSubmitAllResidentPacks;
    public bool useActiveCameraWhenMissing = true;
    public bool useSceneViewCameraInEditor = true;
    public bool drawInEditMode = true;
    public ShadowCastingMode shadowCastingMode = ShadowCastingMode.Off;
    public bool receiveShadows = true;
    public bool submitDrawsToAllCamerasForShadows = true;
    public bool showBfp2 = true;
    [Range(0.0f, 2.0f)]
    public float skyAmbientStrength = 0.72f;
    [Range(0.0f, 3.0f)]
    public float sunDirectStrength = 1.0f;

    [Header("Near Real Shadows")]
    public bool enableNearRealShadows;
    public ShadowCastingMode nearShadowCastingMode = ShadowCastingMode.On;
    public bool nearShadowBuildingLayersOnly = true;
    [Min(1.0f)] public float nearShadowRadiusMeters = 900.0f;
    [Min(1)] public int maxNearShadowPacks = 8;
    [Min(1000)] public int maxNearShadowTriangles = 1200000;
    [Range(0.0f, 1.0f)] public float nearRealShadowStrength = 0.86f;

    [Header("BFP2 Facade Pilot")]
    public bool useFacadeTextureArray = true;
    public Texture2DArray facadeTextureArray;
    [Range(0.0f, 1.0f)]
    public float facadeStrength = 0.88f;
    [Min(2.0f)] public float facadeWidthMeters = 42.0f;
    [Min(2.0f)] public float facadeHeightMeters = 72.0f;
    [Min(4.0f)] public float facadeRandomCellSizeMeters = 16.0f;
    public int facadeBuildingLayerIndex = 0;

    [Header("BFP2 Roof Orthophoto")]
    public bool useRoofOrthophoto = true;
    [Range(0.0f, 1.0f)] public float roofOrthophotoStrength = 1.0f;

    [Header("Culling")]
    public bool enableFrustumCulling = true;
    public bool enableDistanceCulling;
    public bool enableNormalConeBackfaceCulling;
    public bool enableScreenSizeCulling;
    [Tooltip("Cull only when a cluster projects below the pixel threshold in every active camera. " +
             "Disable this to retain the legacy angular-radius ratio for comparison.")]
    public bool useProjectedPixelScreenSizeCulling = true;
    public bool forceGpuClusterFrustumCullingInFullDraw = true;
    public bool flipTriangleWinding;
    [Min(0.0f)] public float maxDrawDistanceMeters = 100000.0f;
    [Min(0.0f)] public float minScreenRadiusPixels = 1.0f;
    [Tooltip("Legacy angular-radius threshold used only when projected-pixel culling is disabled.")]
    [Min(0.0f)] public float minScreenRadiusRatio = 0.00003f;

    [Header("GPU Performance Engineering")]
    [Tooltip("ScalarAoS is the original 80-byte record fallback. ScalarCompact isolates the " +
              "40-byte hot-layout effect. WaveCompact batches full-index reservations per wave. " +
              "WaveTile32/64 emit only fixed-size visible-tile descriptors and fetch source " +
              "indices directly in the vertex shader. WaveTile32Index16 additionally decodes " +
              "two cluster-local uint16 indices from each source word.")]
    public Bfp2ClusterCullAlgorithm clusterCullAlgorithm = Bfp2ClusterCullAlgorithm.ScalarAoS;
    [Tooltip("Capture-only path that emits GPU samples around clear and cluster-cull dispatches.")]
    public bool enableGpuProfilerMarkers;

    [Header("Streaming")]
    public bool scanOnEnable = true;
    public bool streamInEditMode = true;
    public bool loadNearestWhenCameraMissing = true;
    public bool loadAllEnabledPacksResident;
    public bool drawAllResidentPacks;
    public bool pinLoadedPacks;
    // Source-pack streaming is the safe default. Legacy full-layer mega packs can exceed
    // the per-layer GPU residency budget and leave the layer with zero desired packs.
    public bool preferMegaPacks;
    public bool gpuDrivenMegaLayerMode;
    public bool requireMegaPacksInGpuDrivenMode;
    public string megaPackDirectoryName = "mega";
    [Min(0)] public int megaPackMaxSourcePacks;
    [Min(0.0f)] public float residentFrustumPaddingMeters = 300.0f;
    public bool enableFrustumPrefetch = true;
    [Min(0.0f)] public float residentPrefetchPaddingMeters = 2800.0f;
    public bool enableRecentVisiblePackRetention = true;
    [Min(0.0f)] public float recentlyVisibleRetentionSeconds = 8.0f;
    public bool usePersistentGpuBufferPool = true;
    public bool enableRuntimeShadows = true;
    [Min(0.0f)] public float refreshIntervalSeconds = 0.35f;
    public bool useCameraRefreshHysteresis = true;
    [Min(0.0f)] public float cameraMoveRefreshMeters = 180.0f;
    [Min(0.0f)] public float cameraRotateRefreshDegrees = 2.5f;
    [Min(1.0f)] public float cameraAltitudeRefreshTierMeters = 300.0f;
    [Min(0.25f)] public float forceRefreshIntervalSeconds = 2.0f;
    [Min(0.0f)] public float unloadDelaySeconds = 2.0f;
    [Min(1)] public int maxTotalResidentPacks = 96;
    [Min(0)] public int maxUploadBytesPerFrame = 64 * 1024 * 1024;
    public long maxPooledGpuBytes = 256L * 1024L * 1024L;

    [Header("Upload Pipeline")]
    public bool enableStagedUploadQueue = true;
    public bool useBeginWriteUpload = true;
    [Min(1)] public int maxUploadPacksPerFrame = 1;
    [Min(1)] public int maxUploadStagesPerFrame = 3;
    [Min(1024)] public int maxUploadStageBytes = 16 * 1024 * 1024;
    public long maxPendingUploadBytes = 512L * 1024L * 1024L;

    [Header("Render Scheduler")]
    public bool enableRenderScheduler;
    [Range(2, 32)] public int renderSchedulerPacksPerGroup = 8;
    [Min(2)] public int renderSchedulerMinPacksPerGroup = 2;
    public bool renderSchedulerOneGroupPerLayer = true;
    [Min(1)] public int renderSchedulerMaxPacksPerLayerGroup = 256;
    [Min(0)] public int renderSchedulerMaxGroups = 8;
    public bool renderSchedulerBuildingsOnly;
    public bool renderSchedulerExcludeShadowCasters = true;
    [Min(0)] public int renderSchedulerMaxBuildsStartedPerFrame = 1;
    [Min(1)] public int renderSchedulerMaxUploadStagesPerFrame = 2;
    [Min(1024)] public int renderSchedulerMaxUploadBytesPerFrame = 32 * 1024 * 1024;
    public long renderSchedulerMaxExtraGpuBytes = 1024L * 1024L * 1024L;

    [Header("Multi Camera Cull")]
    public bool enableBatchedCameraFrameBuffer;
    [Range(1, 8)] public int maxBfp2CameraFrames = 6;
    public Camera[] batchedCullCameras;
    public bool useWeatherCameraRegistry = true;
    public bool enableAdaptiveCameraCullGrouping = true;
    [Min(1.0f)] public float unionExpansionGroupThreshold = 2.5f;
    [Range(0.1f, 1.0f)] public float unionExpansionReleaseFraction = 0.8f;
    [Min(0.1f)] public float cameraExpansionSampleIntervalSeconds = 0.5f;

    [Header("Compatibility Render Scheduler Pilot")]
    public bool enableRenderSchedulerPilot;
    [Range(2, 8)] public int schedulerPilotMaxPacks = 4;
    public bool schedulerPilotBuildingsOnly = true;
    public bool schedulerPilotExcludeShadowCasters = true;

    [Header("Production")]
    public bool productionMode = true;
    public bool enableDetailedRuntimeStats;
    [Min(0.05f)] public float summaryStatsIntervalSeconds = 0.5f;

    [Header("Debug")]
    public bool showDebugPanel;
    public bool enableDebugReadback;
    [Min(0.05f)] public float debugReadbackIntervalSeconds = 0.5f;
    public bool drawClusterBounds;
    public Color clusterBoundsColor = new Color(0.0f, 0.85f, 1.0f, 0.35f);

    private readonly List<Bfp2PackInfo> packs = new List<Bfp2PackInfo>(256);
    private readonly List<Bfp2PackInfo> desiredScratch = new List<Bfp2PackInfo>(256);
    private readonly List<Bfp2PackInfo> loadCandidateScratch = new List<Bfp2PackInfo>(256);
    private readonly List<Bfp2PackInfo> evictionScratch = new List<Bfp2PackInfo>(256);
    private readonly List<Bfp2PackInfo> schedulerCandidateScratch = new List<Bfp2PackInfo>(32);
    private readonly HashSet<Bfp2PackInfo> schedulerDrawSkipSet = new HashSet<Bfp2PackInfo>();
    private readonly List<Bfp2ScheduledGroup> scheduledGroups = new List<Bfp2ScheduledGroup>(4);
    private readonly HashSet<string> schedulerDesiredSignatures = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<NearShadowPackCandidate> nearShadowCandidates = new List<NearShadowPackCandidate>(64);
    private readonly HashSet<Bfp2PackInfo> nearShadowPacks = new HashSet<Bfp2PackInfo>();
    private readonly List<PooledBuffer> bufferPool = new List<PooledBuffer>(64);
    private readonly Queue<Bfp2PackInfo> uploadQueue = new Queue<Bfp2PackInfo>(64);
    private readonly NYCGISStreamingCameraChangeGate desiredCameraGate = new NYCGISStreamingCameraChangeGate();
    private readonly NYCGISStreamingCameraChangeGate nearShadowCameraGate = new NYCGISStreamingCameraChangeGate();
    private readonly Bfp2CameraFrameBuffer cameraFrameBuffer = new Bfp2CameraFrameBuffer();
    private readonly Camera[] registeredCameraScratch = new Camera[8];
    private readonly Bfp2FileHandleCache fileHandleCache = new Bfp2FileHandleCache();
    private int[] residentCountScratch = Array.Empty<int>();
    private long[] residentBytesScratch = Array.Empty<long>();
    private int[] startedPerLayerScratch = Array.Empty<int>();
    private readonly Plane[] frustumPlanes = new Plane[6];
    private readonly Plane[] cameraFramePlaneScratch = new Plane[6];
    private readonly Vector4[] frustumPlaneVectors = new Vector4[6];
    private readonly Vector4[] cameraFrustumPlaneVectors = new Vector4[8 * 6];
    private readonly Vector4[] cameraPositionVectors = new Vector4[8];
    private readonly Vector4[] cameraScreenParamVectors = new Vector4[8];
    private MaterialPropertyBlock propertyBlock;
    private Rect debugPanelRect = new Rect(900.0f, 16.0f, 390.0f, 420.0f);

    private int clearKernel = -1;
    private int cameraExpansionKernel = -1;
    private int packCompactKernel = -1;
    private int cullKernel = -1;
    private int compactCullKernel = -1;
    private int waveCullKernel = -1;
    private int waveTile32Kernel = -1;
    private int waveTile64Kernel = -1;
    private int waveTile32Index16Kernel = -1;
    private CommandBuffer gpuProfilerCommandBuffer;
    private float nextRefreshTime;
    private float nextForcedDesiredRefreshTime;
    private float nextDebugReadbackTime;
    private float nextSummaryStatsTime;
    private Material runtimeMaterial;
    private Texture2DArray runtimeFacadeTextureArray;
    private bool warnedFacadeResourceFallbackFailure;
    private int lastScannedLayerCount = -1;
    private int totalPackCount;
    private int residentPackCount;
    private int loadingPackCount;
    private long residentGpuBytes;
    private long secondaryCullGpuBytes;
    private long pendingCpuBytes;
    private long stagedUploadBytes;
    private int stagedUploadPackCount;
    private long pooledGpuBytes;
    private ulong totalResidentClusters;
    private ulong visibleClusters;
    private ulong culledClusters;
    private ulong visibleTriangles;
    private ulong visibleIndices;
    private ulong overflowClusters;
    private PrimaryCullPassMetadata activePrimaryCullPass;
    private PrimaryCullPassMetadata completedPrimaryCullPass;
    private string lastStatus = "BFP2 idle";
    private bool usingMegaPacks;
    private int preparedCameraFrameCount;
    private GraphicsBuffer cameraExpansionStatsBuffer;
    private bool cameraExpansionReadbackPending;
    private AsyncGPUReadbackRequest cameraExpansionReadbackRequest;
    private float nextCameraExpansionSampleTime;
    private ulong primaryFrustumVisibleClusters;
    private ulong unionFrustumVisibleClusters;
    private float unionExpansionRatio = 1.0f;
    private bool adaptiveCullGroupingRequested;
    private bool usingAdaptiveCullGroups;
    private bool usingRegisteredCameraFrames;
    private int registeredScheduledCameraCount;
    private int nearShadowPackCount;
    private int nearShadowCandidateCount;
    private long nearShadowTriangleCount;
    private bool nearShadowDirty = true;
    private int schedulerPilotMergedPackCount;
    private int schedulerPilotGroupCount;
    private bool beginWriteUploadUnavailable;
    private bool lastScanUsedMissionSpatialPacks;
    private int observedMissionRevision = int.MinValue;
    private int missionSelectionRevision;
    private int missionEligiblePackCount;
    private int missionAcceptedPackCount;
    private long missionAcceptedGpuBytes;
    private int missionMissingSpatialLayerCount;
    private string missionMissingSpatialLayerNames = string.Empty;
    private readonly List<string> missionMissingSpatialLayerScratch = new List<string>(4);
    private int fullResidencyBaselinePackCount;
    private long fullResidencyBaselineGpuBytes;

    public int TotalPackCount => totalPackCount;
    public int ResidentPackCount => residentPackCount;
    public int LoadingPackCount => loadingPackCount;
    public long ResidentGpuBytes => residentGpuBytes;
    public long PendingCpuBytes => pendingCpuBytes;
    public long StagedUploadBytes => stagedUploadBytes;
    public int StagedUploadPackCount => stagedUploadPackCount;
    public long SecondaryCullGpuBytes => secondaryCullGpuBytes;
    public long PooledGpuBytes => pooledGpuBytes;
    public ulong TotalResidentClusters => totalResidentClusters;
    public ulong VisibleClusters => visibleClusters;
    public ulong CulledClusters => culledClusters;
    public ulong VisibleTriangles => visibleTriangles;
    public ulong VisibleIndices => visibleIndices;
    public ulong OverflowClusters => overflowClusters;
    public string LastStatus => lastStatus;
    public bool WaveCullSupported =>
        waveCullCompute != null &&
        waveCullKernel >= 0 &&
        SystemInfo.supportsComputeShaders &&
        SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
    public bool WaveTileCullSupported =>
        WaveCullSupported &&
        waveTile32Kernel >= 0 &&
        waveTile64Kernel >= 0;
    public bool WaveTileIndex16CullSupported =>
        WaveCullSupported &&
        waveTile32Index16Kernel >= 0;
    public int ClusterLocalIndex16ReadyPackCount =>
        CountClusterLocalIndex16ReadyPacks();
    public long ClusterLocalIndex32SourceBytes =>
        SumClusterLocalIndexBytes(usePacked16: false, includeBaseVertices: false);
    public long ClusterLocalIndex16PackedBytes =>
        SumClusterLocalIndexBytes(usePacked16: true, includeBaseVertices: false);
    public long ClusterLocalIndex16BaseVertexBytes =>
        SumClusterLocalIndexBytes(usePacked16: true, includeBaseVertices: true) -
        SumClusterLocalIndexBytes(usePacked16: true, includeBaseVertices: false);
    public uint ClusterLocalIndex16MaximumSpan =>
        FindClusterLocalIndex16MaximumSpan();
    public string ActiveClusterCullAlgorithmName
    {
        get
        {
            if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveTile32Index16 &&
                WaveTileIndex16CullSupported)
            {
                return nameof(Bfp2ClusterCullAlgorithm.WaveTile32Index16);
            }
            if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveTile32 &&
                WaveTileCullSupported)
            {
                return nameof(Bfp2ClusterCullAlgorithm.WaveTile32);
            }
            if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveTile64 &&
                WaveTileCullSupported)
            {
                return nameof(Bfp2ClusterCullAlgorithm.WaveTile64);
            }
            if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveCompact && WaveCullSupported)
            {
                return nameof(Bfp2ClusterCullAlgorithm.WaveCompact);
            }
            if (clusterCullAlgorithm != Bfp2ClusterCullAlgorithm.ScalarAoS &&
                compactCullKernel >= 0)
            {
                return nameof(Bfp2ClusterCullAlgorithm.ScalarCompact);
            }
            return nameof(Bfp2ClusterCullAlgorithm.ScalarAoS);
        }
    }
    public int NearShadowPackCount => nearShadowPackCount;
    public int NearShadowCandidateCount => nearShadowCandidateCount;
    public long NearShadowTriangleCount => nearShadowTriangleCount;
    public ulong PrimaryFrustumVisibleClusters => primaryFrustumVisibleClusters;
    public ulong UnionFrustumVisibleClusters => unionFrustumVisibleClusters;
    public float UnionExpansionRatio => unionExpansionRatio;
    public ulong PrimaryCullPassEpoch => completedPrimaryCullPass.Epoch;
    public int PrimaryCullPassCameraCount =>
        completedPrimaryCullPass.PreparedCameraCount;
    public int PrimaryCullPassPackCount =>
        completedPrimaryCullPass.DispatchedPackCount;
    public bool PrimaryCullPassPackSetComplete =>
        completedPrimaryCullPass.PackSetComplete;
    public int PrimaryCullPassDispatchFrame =>
        completedPrimaryCullPass.DispatchFrame;
    public ulong PrimaryCullPassTopologyHash =>
        HashPrimaryCullPassTopology(completedPrimaryCullPass);
    public bool UsingAdaptiveCullGroups => usingAdaptiveCullGroups;
    public int RegisteredScheduledCameraCount => registeredScheduledCameraCount;
    public bool UsingMissionSpatialPacks => lastScanUsedMissionSpatialPacks;
    public int MissionSelectionRevision => missionSelectionRevision;
    public int MissionEligiblePackCount => missionEligiblePackCount;
    public int MissionAcceptedPackCount => missionAcceptedPackCount;
    public long MissionAcceptedGpuBytes => missionAcceptedGpuBytes;
    public int FullResidencyBaselinePackCount => fullResidencyBaselinePackCount;
    public int FullResidencyResidentPackCount => CountFullResidencyResidentPacks();
    public long FullResidencyBaselineGpuBytes => fullResidencyBaselineGpuBytes;
    public bool NeedsEditorStreamingTick => !Application.isPlaying
                                            && streamInEditMode
                                            && isActiveAndEnabled
                                            && (!preferMegaPacks || megaPackMaxSourcePacks > 0);

    public bool TryCaptureCullValidationSnapshot(out Bfp2CullValidationSnapshot snapshot)
    {
        snapshot = default;
        PrimaryCullPassMetadata pass = completedPrimaryCullPass;
        snapshot.PrimaryCullPassEpoch = pass.Epoch;
        snapshot.PreparedCameraCount = pass.PreparedCameraCount;
        snapshot.PrimaryCameraInstanceId = pass.PrimaryCameraInstanceId;
        snapshot.DispatchFrame = pass.DispatchFrame;
        snapshot.DispatchedPackCount = pass.DispatchedPackCount;
        snapshot.CameraStateHash = pass.CameraStateHash;
        snapshot.DispatchedPackHash = pass.DispatchedPackHash;
        snapshot.PackSetComplete = pass.PackSetComplete;
        uint[] stats = new uint[4];
        uint[] drawArgs = new uint[4];
        ulong hash = 1469598103934665603UL;
        ulong packHash = 1469598103934665603UL;
        bool layoutValid = true;

        try
        {
            for (int i = 0; i < packs.Count; i++)
            {
                Bfp2PackInfo pack = packs[i];
                Bfp2GpuPack gpu = pack.Gpu;
                if (pack.LastPrimaryCullPassEpoch != pass.Epoch)
                {
                    continue;
                }
                if (gpu == null || gpu.StatsBuffer == null ||
                    gpu.DrawArgsBuffer == null)
                {
                    continue;
                }

                gpu.StatsBuffer.GetData(stats);
                gpu.DrawArgsBuffer.GetData(drawArgs);
                snapshot.ResidentPacks++;
                snapshot.VisibleClusters += stats[0];
                snapshot.VisibleIndices += stats[1];
                snapshot.CulledClusters += stats[2];
                snapshot.OverflowClusters += stats[3];
                snapshot.DrawIndices += drawArgs[0];

                int tileTriangles = gpu.GetCullOutputTileTriangleCount(0);
                if (tileTriangles > 0)
                {
                    uint tileIndexCount = (uint)(tileTriangles * 3);
                    if (drawArgs[0] < stats[1] || drawArgs[0] % tileIndexCount != 0u)
                    {
                        layoutValid = false;
                    }
                    else
                    {
                        snapshot.VisibleTiles += drawArgs[0] / tileIndexCount;
                        snapshot.PaddedDrawIndices += drawArgs[0] - stats[1];
                    }
                }
                else if (drawArgs[0] != stats[1])
                {
                    layoutValid = false;
                }

                packHash = HashCullValidationValue(packHash, (uint)i);
                packHash = HashCullValidationValue(packHash, (uint)pack.Header.ClusterCount);
                packHash = HashCullValidationValue(packHash, (uint)pack.Header.IndexCount);
                hash = HashCullValidationValue(hash, (uint)i);
                hash = HashCullValidationValue(hash, (uint)pack.Header.ClusterCount);
                hash = HashCullValidationValue(hash, (uint)pack.Header.IndexCount);
                hash = HashCullValidationValue(hash, stats[0]);
                hash = HashCullValidationValue(hash, stats[1]);
                hash = HashCullValidationValue(hash, stats[2]);
                hash = HashCullValidationValue(hash, stats[3]);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("BFP2 cull validation readback failed: " + exception.Message, this);
            snapshot = default;
            return false;
        }

        snapshot.PerPackHash = hash;
        return pass.Epoch > 0UL &&
               pass.PackSetComplete &&
               snapshot.ResidentPacks > 0 &&
               snapshot.ResidentPacks == pass.DispatchedPackCount &&
               packHash == pass.DispatchedPackHash &&
               snapshot.OverflowClusters == 0 &&
               layoutValid;
    }

    public bool TryCaptureFreshCullValidationSnapshot(
        ulong minimumExclusivePrimaryCullPassEpoch,
        Camera expectedPrimaryCamera,
        out Bfp2CullValidationSnapshot snapshot)
    {
        if (completedPrimaryCullPass.Epoch <=
            minimumExclusivePrimaryCullPassEpoch)
        {
            snapshot = default;
            return false;
        }
        return TryCaptureCullValidationSnapshot(out snapshot) &&
            snapshot.PrimaryCullPassEpoch >
                minimumExclusivePrimaryCullPassEpoch &&
            snapshot.PreparedCameraCount == 1 &&
            expectedPrimaryCamera != null &&
            snapshot.PrimaryCameraInstanceId ==
                expectedPrimaryCamera.StableId();
    }

    private static ulong HashCullValidationValue(ulong hash, uint value)
    {
        unchecked
        {
            hash ^= value;
            return hash * 1099511628211UL;
        }
    }

    private int CountClusterLocalIndex16ReadyPacks()
    {
        int count = 0;
        for (int i = 0; i < packs.Count; i++)
        {
            if (packs[i].Gpu != null && packs[i].Gpu.ClusterLocalIndex16Ready)
            {
                count++;
            }
        }
        for (int i = 0; i < scheduledGroups.Count; i++)
        {
            if (scheduledGroups[i].Gpu != null &&
                scheduledGroups[i].Gpu.ClusterLocalIndex16Ready)
            {
                count++;
            }
        }
        return count;
    }

    private long SumClusterLocalIndexBytes(bool usePacked16, bool includeBaseVertices)
    {
        long bytes = 0L;
        for (int i = 0; i < packs.Count; i++)
        {
            bytes += GetClusterLocalIndexBytes(
                packs[i].Gpu,
                usePacked16,
                includeBaseVertices);
        }
        for (int i = 0; i < scheduledGroups.Count; i++)
        {
            bytes += GetClusterLocalIndexBytes(
                scheduledGroups[i].Gpu,
                usePacked16,
                includeBaseVertices);
        }
        return bytes;
    }

    private static long GetClusterLocalIndexBytes(
        Bfp2GpuPack gpu,
        bool usePacked16,
        bool includeBaseVertices)
    {
        if (gpu == null)
        {
            return 0L;
        }
        if (!usePacked16)
        {
            return (long)gpu.IndexCount * sizeof(uint);
        }

        long bytes = (long)gpu.PackedIndex16WordCount * sizeof(uint);
        if (includeBaseVertices)
        {
            bytes += (long)gpu.ClusterIndexBaseCount * sizeof(uint);
        }
        return bytes;
    }

    private uint FindClusterLocalIndex16MaximumSpan()
    {
        uint maximum = 0u;
        for (int i = 0; i < packs.Count; i++)
        {
            if (packs[i].Gpu != null)
            {
                maximum = Math.Max(maximum, packs[i].Gpu.MaximumClusterIndexSpan16);
            }
        }
        for (int i = 0; i < scheduledGroups.Count; i++)
        {
            if (scheduledGroups[i].Gpu != null)
            {
                maximum = Math.Max(
                    maximum,
                    scheduledGroups[i].Gpu.MaximumClusterIndexSpan16);
            }
        }
        return maximum;
    }

    public void EditorStreamingTick()
    {
        if (NeedsEditorStreamingTick)
        {
            LateUpdate();
        }
    }

    private void OnEnable()
    {
        NYCGISProductionBaseline.ApplyIfProduction(this);
        ApplyDataRootConfig();
        ApplySurfaceDefaults();
        EnsureResources();
        if (scanOnEnable)
        {
            RescanPacks();
        }
    }

    private void OnDisable()
    {
        ClearNearShadowState();
        ReleaseAllPacks();
        ReleaseCameraExpansionStatsBuffer();
        ReleasePooledBuffers();
        ReleaseRuntimeMaterial();
        ReleaseRuntimeFacadeTextureArray();
        ReleaseGpuProfilerCommandBuffer();
        if (NYCGISMissionAreaResidencyContext.TryGetSnapshot(out NYCGISMissionAreaSnapshot missionSnapshot))
        {
            ReportMissionResidency(missionSnapshot, resourcesAvailable: false);
        }
    }

    private void OnDestroy()
    {
        ClearNearShadowState();
        ReleaseAllPacks();
        ReleaseCameraExpansionStatsBuffer();
        ReleasePooledBuffers();
        ReleaseRuntimeMaterial();
        ReleaseRuntimeFacadeTextureArray();
        ReleaseGpuProfilerCommandBuffer();
    }

    private void OnValidate()
    {
        NYCGISProductionBaseline.ApplyIfProduction(this);
        ApplyDataRootConfig();
        ApplySurfaceDefaults();
        refreshIntervalSeconds = Mathf.Max(0.0f, refreshIntervalSeconds);
        cameraMoveRefreshMeters = Mathf.Max(0.0f, cameraMoveRefreshMeters);
        cameraRotateRefreshDegrees = Mathf.Max(0.0f, cameraRotateRefreshDegrees);
        cameraAltitudeRefreshTierMeters = Mathf.Max(1.0f, cameraAltitudeRefreshTierMeters);
        forceRefreshIntervalSeconds = Mathf.Max(0.25f, forceRefreshIntervalSeconds);
        debugReadbackIntervalSeconds = Mathf.Max(0.05f, debugReadbackIntervalSeconds);
        summaryStatsIntervalSeconds = Mathf.Max(0.05f, summaryStatsIntervalSeconds);
        maxTotalResidentPacks = Mathf.Max(1, maxTotalResidentPacks);
        maxUploadBytesPerFrame = Mathf.Max(0, maxUploadBytesPerFrame);
        maxUploadPacksPerFrame = Mathf.Max(1, maxUploadPacksPerFrame);
        maxUploadStagesPerFrame = Mathf.Max(1, maxUploadStagesPerFrame);
        maxUploadStageBytes = Mathf.Max(1024, maxUploadStageBytes);
        maxPendingUploadBytes = Math.Max(0L, maxPendingUploadBytes);
        maxPooledGpuBytes = Math.Max(0L, maxPooledGpuBytes);
        ApplyGpuDrivenMegaLayerDefaults();
        renderSchedulerPacksPerGroup = Mathf.Clamp(renderSchedulerPacksPerGroup, 2, 32);
        renderSchedulerMinPacksPerGroup = Mathf.Clamp(renderSchedulerMinPacksPerGroup, 2, renderSchedulerPacksPerGroup);
        renderSchedulerMaxPacksPerLayerGroup = Mathf.Max(1, renderSchedulerMaxPacksPerLayerGroup);
        renderSchedulerMaxGroups = Mathf.Max(0, renderSchedulerMaxGroups);
        renderSchedulerMaxBuildsStartedPerFrame = Mathf.Max(0, renderSchedulerMaxBuildsStartedPerFrame);
        renderSchedulerMaxUploadStagesPerFrame = Mathf.Max(1, renderSchedulerMaxUploadStagesPerFrame);
        renderSchedulerMaxUploadBytesPerFrame = Mathf.Max(1024, renderSchedulerMaxUploadBytesPerFrame);
        renderSchedulerMaxExtraGpuBytes = Math.Max(0L, renderSchedulerMaxExtraGpuBytes);
        maxBfp2CameraFrames = Mathf.Clamp(maxBfp2CameraFrames, 1, 8);
        unionExpansionGroupThreshold = Mathf.Max(1.0f, unionExpansionGroupThreshold);
        unionExpansionReleaseFraction = Mathf.Clamp(unionExpansionReleaseFraction, 0.1f, 1.0f);
        cameraExpansionSampleIntervalSeconds = Mathf.Max(0.1f, cameraExpansionSampleIntervalSeconds);
        schedulerPilotMaxPacks = Mathf.Clamp(schedulerPilotMaxPacks, 2, 8);
        residentFrustumPaddingMeters = Mathf.Max(0.0f, residentFrustumPaddingMeters);
        residentPrefetchPaddingMeters = Mathf.Max(0.0f, residentPrefetchPaddingMeters);
        recentlyVisibleRetentionSeconds = Mathf.Max(0.0f, recentlyVisibleRetentionSeconds);
        nearShadowRadiusMeters = Mathf.Max(1.0f, nearShadowRadiusMeters);
        maxNearShadowPacks = Mathf.Max(1, maxNearShadowPacks);
        maxNearShadowTriangles = Mathf.Max(1000, maxNearShadowTriangles);
        nearRealShadowStrength = Mathf.Clamp01(nearRealShadowStrength);
        nearShadowDirty = true;
        facadeStrength = Mathf.Clamp01(facadeStrength);
        facadeWidthMeters = Mathf.Max(2.0f, facadeWidthMeters);
        facadeHeightMeters = Mathf.Max(2.0f, facadeHeightMeters);
        facadeRandomCellSizeMeters = Mathf.Max(4.0f, facadeRandomCellSizeMeters);
        roofOrthophotoStrength = Mathf.Clamp01(roofOrthophotoStrength);
        minScreenRadiusPixels = Mathf.Max(0.0f, minScreenRadiusPixels);
        minScreenRadiusRatio = Mathf.Max(0.0f, minScreenRadiusRatio);
        ApplyProductionDebugGuards();
    }

    public void ApplyDataRootConfig()
    {
        if (!useSharedDataRootConfig)
        {
            return;
        }

        NYCGISDataRootConfig config = NYCGISDataRootConfig.Resolve(dataRootConfig);
        if (config == null || layers == null)
        {
            return;
        }

        for (int i = 0; i < layers.Length; i++)
        {
            Bfp2LayerSettings layer = layers[i];
            if (layer == null)
            {
                continue;
            }

            switch ((layer.name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "buildings":
                    layer.directory = config.BuildingsBfp2Directory;
                    break;
                case "roadbed":
                    layer.directory = config.RoadbedBfp2Directory;
                    break;
                case "parking-lot":
                case "parking_lot":
                case "parkinglot":
                    layer.directory = config.ParkingLotBfp2Directory;
                    break;
                case "entrance":
                    layer.directory = config.EntranceBfp2Directory;
                    break;
            }
        }
    }

    private void ApplySurfaceDefaults()
    {
        if (showBfp2)
        {
            submitDrawsToAllCamerasForShadows = true;
            pinLoadedPacks = false;
            enableNearRealShadows = true;
            // Each pack currently has one procedural submission for both the visible and
            // shadow passes. ShadowsOnly would replace that visible submission and make a
            // pack disappear as soon as it enters the near-shadow radius.
            if (nearShadowCastingMode == ShadowCastingMode.ShadowsOnly)
            {
                nearShadowCastingMode = ShadowCastingMode.On;
            }
            useFacadeTextureArray = true;
            useRoofOrthophoto = true;
            if (facadeStrength <= 0.001f)
            {
                facadeStrength = 0.88f;
            }
            if (roofOrthophotoStrength <= 0.001f)
            {
                roofOrthophotoStrength = 1.0f;
            }
        }

        if (Mathf.Approximately(facadeRandomCellSizeMeters, PreviousFacadeBlockCellSizeMeters))
        {
            facadeRandomCellSizeMeters = DefaultFacadeParcelCellSizeMeters;
        }

        ApplyGpuDrivenMegaLayerDefaults();
    }

    private void ApplyGpuDrivenMegaLayerDefaults()
    {
        if (!gpuDrivenMegaLayerMode)
        {
            return;
        }

        preferMegaPacks = true;
        forceGpuClusterFrustumCullingInFullDraw = true;
        enableRenderScheduler = false;
        enableRenderSchedulerPilot = false;
    }

    private void LateUpdate()
    {
        ApplyProductionDebugGuards();

        if (!Application.isPlaying && !streamInEditMode)
        {
            return;
        }

        bool missionActive = NYCGISMissionAreaResidencyContext.TryGetSnapshot(
            out NYCGISMissionAreaSnapshot missionSnapshot);
        int missionRevision = missionActive ? missionSnapshot.revision : 0;
        bool missionRevisionChanged = observedMissionRevision != missionRevision;

        // Pooled buffers are intentionally outside the resident-pack accounting used by the
        // normal roaming streamer. A bounded mission has a strict GPU-byte ceiling, so discard
        // that roaming cache before any resources for the new revision can be allocated.
        if (missionActive && missionRevisionChanged)
        {
            ReleasePooledBuffers();
        }

        EnsureResources();
        if (cullCompute == null || ActiveMaterial == null)
        {
            lastStatus = "BFP2 missing material or compute shader";
            if (missionActive)
            {
                ReportMissionResidency(missionSnapshot, resourcesAvailable: false);
            }
            return;
        }

        bool missionLayoutMayDiffer = lastScanUsedMissionSpatialPacks != missionActive && preferMegaPacks;
        if (lastScannedLayerCount != (layers == null ? 0 : layers.Length) || missionLayoutMayDiffer)
        {
            RescanPacks();
        }
        else
        {
            // Source-pack mode uses the same files with or without a mission. Keep resident
            // full-layer baselines intact instead of releasing and reloading them on every
            // mission transition.
            lastScanUsedMissionSpatialPacks = missionActive;
        }

        if (missionRevisionChanged)
        {
            observedMissionRevision = missionRevision;
            desiredCameraGate.Reset();
            nearShadowCameraGate.Reset();
            nextForcedDesiredRefreshTime = 0.0f;
            nearShadowDirty = true;
        }

        Camera drawCamera = ResolveCamera();
        if (missionRevisionChanged || Time.realtimeSinceStartup >= nextRefreshTime)
        {
            nextRefreshTime = Time.realtimeSinceStartup + refreshIntervalSeconds;
            bool forceRefresh = Time.realtimeSinceStartup >= nextForcedDesiredRefreshTime;
            bool cameraDirty = !useCameraRefreshHysteresis || desiredCameraGate.ShouldRefresh(
                drawCamera,
                cameraMoveRefreshMeters,
                cameraRotateRefreshDegrees,
                cameraAltitudeRefreshTierMeters,
                force: forceRefresh);
            if (missionRevisionChanged || cameraDirty)
            {
                nextForcedDesiredRefreshTime = Time.realtimeSinceStartup + forceRefreshIntervalSeconds;
                UpdateDesiredPacks(drawCamera, missionActive, missionSnapshot);
            }
        }

        // On a mission revision, retire old resident buffers before completed loads or staged
        // uploads can allocate buffers for the replacement set. This avoids an old+new peak.
        if (missionActive)
        {
            UnloadExpiredPacks();
        }

        CompleteFinishedLoads();
        ProcessUploadQueue();
        EnforceResidentBudgets();
        StartPendingLoads();
        if (!missionActive && !pinLoadedPacks)
        {
            UnloadExpiredPacks();
        }
        EnforceResidentBudgets();

        MaybeRebuildSummaryStats();
        if (missionActive)
        {
            ReportMissionResidency(missionSnapshot, resourcesAvailable: true);
        }
        RebuildNearShadowPackSetIfNeeded(drawCamera);

        if (showBfp2 && (Application.isPlaying || drawInEditMode))
        {
            DrawLoadedPacks(drawCamera);
        }

    }

    private bool HasResidentBfp2BuildingLayer()
    {
        if (layers == null)
        {
            return false;
        }

        for (int i = 0; i < packs.Count; i++)
        {
            Bfp2PackInfo pack = packs[i];
            if (!IsPackResident(pack) || pack.LayerIndex < 0 || pack.LayerIndex >= layers.Length)
            {
                continue;
            }

            if (IsBuildingLayer(layers[pack.LayerIndex]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBuildingLayer(Bfp2LayerSettings layer)
    {
        return layer != null &&
               !string.IsNullOrWhiteSpace(layer.name) &&
               layer.name.IndexOf("building", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsFullResidencyPack(Bfp2PackInfo pack)
    {
        return pack != null &&
               layers != null &&
               pack.LayerIndex >= 0 &&
               pack.LayerIndex < layers.Length &&
               layers[pack.LayerIndex] != null &&
               layers[pack.LayerIndex].fullLayerResidency;
    }

    private bool ShouldSubmitAllResidentPacks(Bfp2PackInfo pack)
    {
        return IsFullResidencyPack(pack) &&
               layers[pack.LayerIndex].submitAllResidentPacks;
    }

    private int CountFullResidencyResidentPacks()
    {
        int count = 0;
        for (int i = 0; i < packs.Count; i++)
        {
            Bfp2PackInfo pack = packs[i];
            if (IsFullResidencyPack(pack) && IsPackResident(pack))
            {
                count++;
            }
        }

        return count;
    }

    private void OnGUI()
    {
        if (productionMode || !showDebugPanel)
        {
            return;
        }

        debugPanelRect = GUI.Window(DebugWindowId, debugPanelRect, DrawDebugPanel, "BFP2 GPU");
    }

    private void DrawDebugPanel(int windowId)
    {
        GUILayout.Label(BuildDebugLine());
        GUILayout.Label("Status: " + lastStatus);
        GUILayout.Space(4.0f);

        showBfp2 = GUILayout.Toggle(showBfp2, "Draw BFP2");
        drawAllResidentPacks = GUILayout.Toggle(drawAllResidentPacks, "Draw all resident packs");
        preferMegaPacks = GUILayout.Toggle(preferMegaPacks, "Prefer mega packs");
        gpuDrivenMegaLayerMode = GUILayout.Toggle(gpuDrivenMegaLayerMode, "GPU-driven mega layer mode");
        requireMegaPacksInGpuDrivenMode = GUILayout.Toggle(requireMegaPacksInGpuDrivenMode, "Require mega packs");
        enableFrustumCulling = GUILayout.Toggle(enableFrustumCulling, "Frustum culling");
        forceGpuClusterFrustumCullingInFullDraw = GUILayout.Toggle(forceGpuClusterFrustumCullingInFullDraw, "Force GPU cluster culling in full draw");
        enableDistanceCulling = GUILayout.Toggle(enableDistanceCulling, "Distance culling");
        enableNormalConeBackfaceCulling = GUILayout.Toggle(enableNormalConeBackfaceCulling, "Normal cone backface culling");
        enableScreenSizeCulling = GUILayout.Toggle(enableScreenSizeCulling, "Screen-size culling");
        flipTriangleWinding = GUILayout.Toggle(flipTriangleWinding, "Flip triangle winding");
        drawClusterBounds = GUILayout.Toggle(drawClusterBounds, "Cluster/chunk bounds gizmos");
        enableDebugReadback = GUILayout.Toggle(enableDebugReadback, "GPU stats readback");
        enableRenderScheduler = GUILayout.Toggle(enableRenderScheduler, "Render scheduler");
        renderSchedulerExcludeShadowCasters = GUILayout.Toggle(renderSchedulerExcludeShadowCasters, "Scheduler excludes shadow casters");
        renderSchedulerOneGroupPerLayer = GUILayout.Toggle(renderSchedulerOneGroupPerLayer, "Scheduler one group per layer");
        enableBatchedCameraFrameBuffer = GUILayout.Toggle(enableBatchedCameraFrameBuffer, "Batched camera frame buffer");
        useWeatherCameraRegistry = GUILayout.Toggle(useWeatherCameraRegistry, "Use scheduled weather feed cameras");
        enableAdaptiveCameraCullGrouping = GUILayout.Toggle(enableAdaptiveCameraCullGrouping, "Adaptive camera cull grouping");
        enableRenderSchedulerPilot = GUILayout.Toggle(enableRenderSchedulerPilot, "Compatibility scheduler pilot");
        enableStagedUploadQueue = GUILayout.Toggle(enableStagedUploadQueue, "Staged upload queue");
        useBeginWriteUpload = GUILayout.Toggle(useBeginWriteUpload, "BeginWrite GPU upload");

        GUILayout.Space(4.0f);
        GUILayout.Label("Layers");
        if (layers != null)
        {
            foreach (Bfp2LayerSettings layer in layers)
            {
                if (layer == null)
                {
                    continue;
                }

                GUILayout.BeginHorizontal();
                layer.enabled = GUILayout.Toggle(layer.enabled, layer.name);
                layer.castShadows = GUILayout.Toggle(layer.castShadows, "shadows", GUILayout.Width(82.0f));
                GUILayout.EndHorizontal();
            }
        }

        GUILayout.Space(4.0f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Rescan"))
        {
            RescanPacks();
        }
        if (GUILayout.Button("Release"))
        {
            ReleaseAllPacks();
        }
        GUILayout.EndHorizontal();

        GUI.DragWindow();
    }

    public void RescanPacks()
    {
        bool missionActive = NYCGISMissionAreaResidencyContext.TryGetSnapshot(out _);
        ReleaseAllPacks();
        packs.Clear();
        desiredCameraGate.Reset();
        nextForcedDesiredRefreshTime = 0.0f;
        lastScannedLayerCount = layers == null ? 0 : layers.Length;
        lastScanUsedMissionSpatialPacks = missionActive;
        usingMegaPacks = false;

        if (layers == null)
        {
            totalPackCount = 0;
            lastStatus = "BFP2 has no layers";
            return;
        }

        for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
        {
            Bfp2LayerSettings layer = layers[layerIndex];
            if (layer == null || string.IsNullOrWhiteSpace(layer.directory) || !Directory.Exists(layer.directory))
            {
                continue;
            }

            bool forceNoTriangleWindingFlip = ShouldForceNoTriangleWindingFlip(layer.directory);
            string[] files = GetRuntimePackFilesForLayer(layer, missionActive, out bool layerUsesMegaPacks);
            usingMegaPacks |= layerUsesMegaPacks;
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                try
                {
                    Bfp2Header header = ReadHeader(path);
                    packs.Add(new Bfp2PackInfo
                    {
                        LayerIndex = layerIndex,
                        Path = path,
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        Header = header,
                        Bounds = MakeBounds(header.BoundsMin, header.BoundsMax),
                        FileBytes = new FileInfo(path).Length,
                        ForceNoTriangleWindingFlip = forceNoTriangleWindingFlip
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("BFP2 header scan failed for " + path + ": " + ex.Message, this);
                }
            }
        }

        totalPackCount = packs.Count;
        lastStatus = "BFP2 scanned " + totalPackCount +
                     (missionActive ? " spatial mission packs" : " runtime packs");
    }

    public int BuildMegaPacksForEnabledLayers(bool overwriteExisting = true)
    {
        if (layers == null || layers.Length == 0)
        {
            lastStatus = "BFP2 mega build skipped: no layers";
            return 0;
        }

        int written = 0;
        for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
        {
            Bfp2LayerSettings layer = layers[layerIndex];
            if (layer == null || !layer.enabled || string.IsNullOrWhiteSpace(layer.directory) || !Directory.Exists(layer.directory))
            {
                continue;
            }

            written += BuildMegaPacksForLayer(layer, layerIndex, overwriteExisting);
        }

        lastStatus = "BFP2 mega build wrote " + written + " pack(s)";
        return written;
    }

    [ContextMenu("Build BFP2 Mega Packs For Enabled Layers")]
    private void BuildMegaPacksForEnabledLayersContextMenu()
    {
        int written = BuildMegaPacksForEnabledLayers(true);
        Debug.Log("BFP2 mega build wrote " + written + " pack(s).", this);
    }

    public string BuildDebugLine()
    {
        return "BFP2 GPU: packs " + residentPackCount + "/" + totalPackCount
               + (fullResidencyBaselinePackCount > 0
                   ? " baseline " + FullResidencyResidentPackCount + "/" + fullResidencyBaselinePackCount
                   : string.Empty)
               + " loading " + loadingPackCount
               + " upload staged " + stagedUploadPackCount + "/" + FormatBytes(stagedUploadBytes)
               + " gpu " + FormatBytes(residentGpuBytes)
               + (secondaryCullGpuBytes > 0 ? " secondary-cull " + FormatBytes(secondaryCullGpuBytes) : string.Empty)
               + " pool " + FormatBytes(pooledGpuBytes)
               + " clusters " + visibleClusters + "/" + totalResidentClusters
               + " tris " + visibleTriangles
               + " overflow " + overflowClusters
               + (usingMegaPacks ? " mega" : " packs")
               + (gpuDrivenMegaLayerMode ? " gpu-driven-layer" : string.Empty)
               + (drawAllResidentPacks ? " full-draw" : " frustum-draw")
               + (drawAllResidentPacks && forceGpuClusterFrustumCullingInFullDraw ? " gpu-cluster-cull" : string.Empty)
               + (enableScreenSizeCulling
                   ? (useProjectedPixelScreenSizeCulling
                       ? " screen-cull " + minScreenRadiusPixels.ToString("0.##") + "px"
                       : " screen-cull legacy")
                   : string.Empty)
               + (productionMode ? " prod-lite-stats" : string.Empty)
               + (renderSchedulerOneGroupPerLayer ? " layer-groups" : string.Empty)
               + (usingRegisteredCameraFrames
                   ? " registered-frames " + registeredScheduledCameraCount
                   : (enableBatchedCameraFrameBuffer ? " camera-buffer " + preparedCameraFrameCount : string.Empty))
               + (usingRegisteredCameraFrames && registeredScheduledCameraCount > 1
                   ? " union " + unionExpansionRatio.ToString("0.00") + "x" + (usingAdaptiveCullGroups ? " grouped" : string.Empty)
                   : string.Empty)
               + (missionSelectionRevision > 0
                   ? " mission r" + missionSelectionRevision + " " + missionAcceptedPackCount + "/" + missionEligiblePackCount
                   : string.Empty)
               + ", scheduler " + (enableRenderScheduler || enableRenderSchedulerPilot ? schedulerPilotMergedPackCount + "/" + schedulerPilotGroupCount : "off")
               + ", near shadows " + nearShadowPackCount + "/" + nearShadowCandidateCount
               + " packs " + nearShadowTriangleCount + " tris";
    }

    private void ReportMissionResidency(
        NYCGISMissionAreaSnapshot missionSnapshot,
        bool resourcesAvailable)
    {
        int desiredResidentCount = 0;
        int desiredPendingCount = 0;
        int outsideResidentCount = 0;
        int outsidePendingCount = 0;
        long totalResidentBytes = 0L;
        int budgetedResidentCount = 0;
        long budgetedResidentBytes = 0L;
        long budgetedPendingGpuBytes = 0L;

        for (int i = 0; i < packs.Count; i++)
        {
            Bfp2PackInfo pack = packs[i];
            bool fullResidency = IsFullResidencyPack(pack);
            bool resident = IsPackResident(pack);
            bool pending = pack.LoadTask != null ||
                           pack.PendingUploadCpu != null ||
                           pack.PendingUploadGpu != null ||
                           pack.UploadQueued;
            if (resident)
            {
                totalResidentBytes += pack.Gpu.EstimatedBytes;
                if (!fullResidency)
                {
                    budgetedResidentCount++;
                    budgetedResidentBytes += pack.Gpu.EstimatedBytes;
                }
                if (pack.Desired)
                {
                    desiredResidentCount++;
                }
                else if (!fullResidency)
                {
                    outsideResidentCount++;
                }
            }

            if (!fullResidency && pack.PendingUploadGpu != null)
            {
                budgetedPendingGpuBytes += pack.PendingUploadGpu.EstimatedBytes;
            }

            if (pack.Desired && !resident)
            {
                desiredPendingCount++;
            }
            else if (!fullResidency && !pack.Desired && pending)
            {
                outsidePendingCount++;
            }
        }

        bool bfp2Requested = missionSnapshot.Includes(NYCGISMissionDataLayers.Buildings) ||
                             missionSnapshot.Includes(NYCGISMissionDataLayers.StreetSpace);
        bool selectionCurrent = missionSelectionRevision == missionSnapshot.revision;
        bool usesSpatialPacks = lastScanUsedMissionSpatialPacks && !usingMegaPacks;
        bool capRejected = missionAcceptedPackCount < missionEligiblePackCount;
        int effectivePackLimit = GetEffectiveGlobalPackLimit(true, missionSnapshot);
        long effectiveByteLimit = GetEffectiveGlobalByteLimit(true, missionSnapshot);
        long budgetedAllocatedGpuBytes = budgetedResidentBytes + budgetedPendingGpuBytes + pooledGpuBytes;
        bool actualBudgetViolation = budgetedResidentCount > effectivePackLimit ||
                                     budgetedAllocatedGpuBytes > effectiveByteLimit;
        bool ready = resourcesAvailable &&
                     selectionCurrent &&
                     usesSpatialPacks &&
                     missionMissingSpatialLayerCount == 0 &&
                     !capRejected &&
                     !actualBudgetViolation &&
                     desiredResidentCount == missionAcceptedPackCount &&
                     desiredPendingCount == 0 &&
                     outsideResidentCount == 0 &&
                     outsidePendingCount == 0;

        string detail;
        if (!resourcesAvailable)
        {
            detail = "BFP2 renderer resources are unavailable";
        }
        else if (!selectionCurrent)
        {
            detail = "BFP2 mission selection has not refreshed";
        }
        else if (!usesSpatialPacks)
        {
            detail = "BFP2 mission mode rejected non-spatial full-city mega packs";
        }
        else if (missionMissingSpatialLayerCount > 0)
        {
            detail = "BFP2 spatial source packs are missing for requested layer(s): " +
                     missionMissingSpatialLayerNames +
                     ". Restore the Google Drive spatial-pack patch (227 packs total); " +
                     "mission-area residency remains fail-closed";
        }
        else if (actualBudgetViolation)
        {
            detail = "BFP2 allocated GPU buffers exceed the active mission hard cap";
        }
        else if (capRejected)
        {
            detail = "BFP2 mission bounds exceed the configured pack or GPU-byte hard cap";
        }
        else if (outsideResidentCount > 0 || outsidePendingCount > 0)
        {
            detail = "BFP2 is releasing data outside the active mission revision";
        }
        else if (desiredPendingCount > 0)
        {
            detail = "BFP2 mission packs are loading";
        }
        else if (!bfp2Requested)
        {
            detail = "BFP2 layers are not requested by this mission";
        }
        else
        {
            detail = "BFP2 mission residency converged";
        }

        NYCGISMissionAreaResidencyContext.Report(new NYCGISMissionAreaResidencyReport(
            NYCGISMissionAreaConsumer.Bfp2,
            missionSnapshot.revision,
            missionEligiblePackCount,
            desiredResidentCount,
            desiredPendingCount + outsidePendingCount,
            totalResidentBytes,
            ready,
            detail));
    }

    public void ApplyNearRealShadowPilotProfile()
    {
        enableNearRealShadows = true;
        nearShadowCastingMode = ShadowCastingMode.On;
        nearShadowBuildingLayersOnly = true;
        nearShadowRadiusMeters = 900.0f;
        maxNearShadowPacks = 8;
        maxNearShadowTriangles = 1200000;
        nearRealShadowStrength = 0.86f;
        receiveShadows = true;
        submitDrawsToAllCamerasForShadows = true;
        pinLoadedPacks = false;
        nearShadowDirty = true;
        RebuildNearShadowPackSet(ResolveCamera());
    }

    public void DisableNearRealShadows()
    {
        enableNearRealShadows = false;
        ClearNearShadowState();
    }

    private void EnsureResources()
    {
        if (cullCompute == null)
        {
#if UNITY_EDITOR
            cullCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Shaders/NYCGISDemo/Bfp2GpuClusterCull.compute");
#endif
        }

        if (waveCullCompute == null)
        {
#if UNITY_EDITOR
            waveCullCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(WaveCullComputeAssetPath);
#endif
        }

        if (cullCompute != null)
        {
            if (clearKernel < 0)
            {
                clearKernel = cullCompute.FindKernel("ClearBfp2Args");
            }
            if (cameraExpansionKernel < 0 && cullCompute.HasKernel("CountBfp2CameraExpansion"))
            {
                cameraExpansionKernel = cullCompute.FindKernel("CountBfp2CameraExpansion");
            }
            if (packCompactKernel < 0 && cullCompute.HasKernel("PackBfp2CompactCullClusters"))
            {
                packCompactKernel = cullCompute.FindKernel("PackBfp2CompactCullClusters");
            }
            if (cullKernel < 0)
            {
                cullKernel = cullCompute.FindKernel("CullAndCompactBfp2Clusters");
            }
            if (compactCullKernel < 0 &&
                cullCompute.HasKernel("CullAndCompactBfp2ClustersCompact"))
            {
                compactCullKernel = cullCompute.FindKernel("CullAndCompactBfp2ClustersCompact");
            }
        }

        if (waveCullCompute != null)
        {
            if (waveCullKernel < 0 &&
                waveCullCompute.HasKernel("CullAndCompactBfp2ClustersWave"))
            {
                waveCullKernel = waveCullCompute.FindKernel("CullAndCompactBfp2ClustersWave");
            }
            if (waveTile32Kernel < 0 &&
                waveCullCompute.HasKernel("CullAndBuildBfp2VisibleTiles32"))
            {
                waveTile32Kernel = waveCullCompute.FindKernel("CullAndBuildBfp2VisibleTiles32");
            }
            if (waveTile64Kernel < 0 &&
                waveCullCompute.HasKernel("CullAndBuildBfp2VisibleTiles64"))
            {
                waveTile64Kernel = waveCullCompute.FindKernel("CullAndBuildBfp2VisibleTiles64");
            }
            if (waveTile32Index16Kernel < 0 &&
                waveCullCompute.HasKernel("CullAndBuildBfp2VisibleTiles32Index16"))
            {
                waveTile32Index16Kernel =
                    waveCullCompute.FindKernel("CullAndBuildBfp2VisibleTiles32Index16");
            }
        }

        if (cameraExpansionStatsBuffer == null)
        {
            cameraExpansionStatsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, sizeof(uint));
            cameraExpansionStatsBuffer.SetData(InitialCameraExpansionStatsData);
        }

        if (drawMaterial == null && runtimeMaterial == null)
        {
            Shader shader = Shader.Find("NYCGIS/BFP2 Gpu Indirect URP");
            if (shader != null)
            {
                runtimeMaterial = new Material(shader)
                {
                    name = "BFP2 Gpu Indirect Runtime Material",
                    hideFlags = HideFlags.DontSave
                };
            }
        }

#if UNITY_EDITOR
        if (facadeTextureArray == null)
        {
            facadeTextureArray = AssetDatabase.LoadAssetAtPath<Texture2DArray>(Bfp2FacadeArrayAssetPath);
        }
#endif

        if (facadeTextureArray == null)
        {
            EnsureRuntimeFacadeTextureArray();
        }

        if (propertyBlock == null)
        {
            propertyBlock = new MaterialPropertyBlock();
        }
    }

    private Material ActiveMaterial => drawMaterial != null ? drawMaterial : runtimeMaterial;

    private void UpdateDesiredPacks(
        Camera drawCamera,
        bool missionActive,
        NYCGISMissionAreaSnapshot missionSnapshot)
    {
        foreach (Bfp2PackInfo pack in packs)
        {
            pack.Desired = false;
            pack.DrawDesired = false;
            pack.CandidateDrawDesired = false;
            pack.StreamPriority = int.MaxValue;
            pack.DistanceToCamera = float.PositiveInfinity;
        }

        Vector3 cameraPosition = Vector3.zero;
        bool hasCamera = drawCamera != null;
        bool hasFrustum = false;
        Matrix4x4 localToWorld = transform.localToWorldMatrix;
        if (hasCamera)
        {
            NYCGISStreamingFrame frame = ResolvePrimaryCameraFrame(drawCamera);
            cameraPosition = transform.InverseTransformPoint(frame.Position);
            Camera primaryCamera = frame.Camera != null ? frame.Camera : drawCamera;
            hasFrustum = NYCGISStreamingFrameContext.CopyFrustumPlanes(primaryCamera, frustumPlanes);
        }

        missionSelectionRevision = missionActive ? missionSnapshot.revision : 0;
        missionEligiblePackCount = 0;
        missionAcceptedPackCount = 0;
        missionAcceptedGpuBytes = 0L;
        missionMissingSpatialLayerCount = 0;
        fullResidencyBaselinePackCount = 0;
        fullResidencyBaselineGpuBytes = 0L;
        if (layers == null)
        {
            return;
        }

        desiredScratch.Clear();
        for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
        {
            Bfp2LayerSettings layer = layers[layerIndex];
            if (layer == null || !layer.enabled)
            {
                continue;
            }

            bool fullLayerResidency = layer.fullLayerResidency;

            foreach (Bfp2PackInfo pack in packs)
            {
                if (pack.LayerIndex != layerIndex)
                {
                    continue;
                }

                if (missionActive && !fullLayerResidency)
                {
                    if (!MissionIncludesLayer(missionSnapshot, layer))
                    {
                        continue;
                    }

                    Bounds worldBounds = TransformBounds(pack.Bounds, localToWorld);
                    if (!missionSnapshot.IntersectsResidentWorldBounds(worldBounds))
                    {
                        continue;
                    }

                    missionEligiblePackCount++;
                }
                else if (missionActive)
                {
                    // A full-layer baseline is present independently of the bounded mission.
                    // Count it in convergence, but add its capacity on top of the mission's
                    // incremental streaming budget below.
                    missionEligiblePackCount++;
                }

                if (fullLayerResidency)
                {
                    fullResidencyBaselinePackCount++;
                    fullResidencyBaselineGpuBytes += missionActive
                        ? EstimateMissionGpuBytes(pack.Header)
                        : EstimateGpuBytes(pack.Header);
                }

                float distance = hasCamera ? DistanceToBounds(pack.Bounds, cameraPosition) : 0.0f;
                pack.DistanceToCamera = distance;
                if (!fullLayerResidency &&
                    !missionActive &&
                    !hasCamera &&
                    !loadNearestWhenCameraMissing &&
                    !loadAllEnabledPacksResident)
                {
                    continue;
                }

                bool drawCandidate = true;
                int streamPriority = 0;
                if (hasCamera && hasFrustum)
                {
                    drawCandidate = drawAllResidentPacks || IsPackInResidentFrustum(pack, localToWorld, residentFrustumPaddingMeters);
                    if (!drawCandidate)
                    {
                        streamPriority = 1;
                        if (!fullLayerResidency && !missionActive && !loadAllEnabledPacksResident)
                        {
                            float prefetchPadding = Mathf.Max(residentFrustumPaddingMeters, residentPrefetchPaddingMeters);
                            if (!enableFrustumPrefetch ||
                                prefetchPadding <= residentFrustumPaddingMeters + 0.001f ||
                                !IsPackInResidentFrustum(pack, localToWorld, prefetchPadding))
                            {
                                continue;
                            }
                        }
                    }
                }

                if (fullLayerResidency ||
                    missionActive ||
                    loadAllEnabledPacksResident ||
                    layer.activeDistanceMeters <= 0.0f ||
                    distance <= layer.activeDistanceMeters)
                {
                    // Mission admission is camera-independent. Every accepted task-area pack
                    // must reach the scheduled-camera GPU union; filtering it here by only the
                    // selected/primary camera would make a secondary MLC feed lose geometry.
                    pack.CandidateDrawDesired = fullLayerResidency || missionActive || drawCandidate;
                    pack.StreamPriority = fullLayerResidency ? -1 : streamPriority;
                    desiredScratch.Add(pack);
                }
            }
        }

        if (missionActive)
        {
            missionMissingSpatialLayerCount = CountMissingMissionSpatialLayers(missionSnapshot);
        }

        desiredScratch.Sort(ComparePackDistance);
        EnsureResidentBudgetScratch();
        Array.Clear(residentCountScratch, 0, residentCountScratch.Length);
        Array.Clear(residentBytesScratch, 0, residentBytesScratch.Length);

        bool unrestrictedFullResidency = !missionActive && loadAllEnabledPacksResident;
        int globalPackLimit = GetEffectiveGlobalPackLimit(missionActive, missionSnapshot);
        long globalByteLimit = GetEffectiveGlobalByteLimit(missionActive, missionSnapshot);

        int budgetedDesired = 0;
        long budgetedDesiredBytes = 0L;
        int acceptedDesired = 0;
        long acceptedDesiredBytes = 0L;
        float now = Time.realtimeSinceStartup;
        for (int i = 0; i < desiredScratch.Count; i++)
        {
            Bfp2PackInfo pack = desiredScratch[i];
            int layerIndex = pack.LayerIndex;
            if (layerIndex < 0 || layerIndex >= layers.Length)
            {
                continue;
            }

            Bfp2LayerSettings layer = layers[layerIndex];
            bool fullLayerResidency = layer.fullLayerResidency;
            long estimatedBytes = missionActive
                ? EstimateMissionGpuBytes(pack.Header)
                : EstimateGpuBytes(pack.Header);

            // Full-layer baselines are intentionally outside all resident pack/byte limits.
            // The target workstations provision this memory separately; the limits below are
            // exclusively for camera/mission-streamed layers.
            if (!fullLayerResidency)
            {
                if (budgetedDesired >= globalPackLimit)
                {
                    continue;
                }

                int layerLimit = Mathf.Max(0, layer.maxResidentPacks);
                if (!unrestrictedFullResidency && residentCountScratch[layerIndex] >= layerLimit)
                {
                    continue;
                }

                if (!unrestrictedFullResidency &&
                    layer.maxResidentBytes > 0 &&
                    residentBytesScratch[layerIndex] + estimatedBytes > layer.maxResidentBytes)
                {
                    continue;
                }
                if (globalByteLimit != long.MaxValue &&
                    budgetedDesiredBytes + estimatedBytes > globalByteLimit)
                {
                    continue;
                }
            }

            pack.Desired = true;
            pack.DrawDesired = pack.CandidateDrawDesired;
            pack.LastDesiredTime = now;
            if (pack.DrawDesired)
            {
                pack.LastDrawDesiredTime = now;
            }

            if (!fullLayerResidency)
            {
                residentCountScratch[layerIndex]++;
                residentBytesScratch[layerIndex] += estimatedBytes;
                budgetedDesired++;
                budgetedDesiredBytes += estimatedBytes;
            }
            acceptedDesired++;
            acceptedDesiredBytes += estimatedBytes;
        }

        if (missionActive)
        {
            missionAcceptedPackCount = acceptedDesired;
            missionAcceptedGpuBytes = acceptedDesiredBytes;
        }
    }

    private int GetEffectiveGlobalPackLimit(
        bool missionActive,
        NYCGISMissionAreaSnapshot missionSnapshot)
    {
        if (!missionActive && loadAllEnabledPacksResident)
        {
            return int.MaxValue;
        }

        int rendererLimit = Mathf.Max(1, maxTotalResidentPacks);
        if (!missionActive)
        {
            return rendererLimit;
        }

        // Full-layer baselines are excluded by the caller. These limits apply only to
        // mission-streamed layers.
        NYCGISMissionResidencyBudget missionBudget = missionSnapshot.budget.Sanitized();
        return Mathf.Min(rendererLimit, Mathf.Max(1, missionBudget.maxBfp2Packs));
    }

    private long GetEffectiveGlobalByteLimit(
        bool missionActive,
        NYCGISMissionAreaSnapshot missionSnapshot)
    {
        if (!missionActive)
        {
            return long.MaxValue;
        }

        NYCGISMissionResidencyBudget missionBudget = missionSnapshot.budget.Sanitized();
        return (long)Mathf.Max(64, missionBudget.maxBfp2GpuMegabytes) * 1024L * 1024L;
    }

    private int CountMissingMissionSpatialLayers(NYCGISMissionAreaSnapshot missionSnapshot)
    {
        int missing = 0;
        missionMissingSpatialLayerScratch.Clear();
        bool sawBuildingLayer = false;
        bool sawStreetSpaceLayer = false;
        for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
        {
            Bfp2LayerSettings layer = layers[layerIndex];
            if (layer == null || !layer.enabled || !MissionIncludesLayer(missionSnapshot, layer))
            {
                continue;
            }

            if (IsBuildingLayer(layer))
            {
                sawBuildingLayer = true;
            }
            else
            {
                sawStreetSpaceLayer = true;
            }

            bool found = false;
            for (int packIndex = 0; packIndex < packs.Count; packIndex++)
            {
                if (packs[packIndex].LayerIndex == layerIndex)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                missing++;
                missionMissingSpatialLayerScratch.Add(
                    string.IsNullOrWhiteSpace(layer.name) ? $"layer[{layerIndex}]" : layer.name);
            }
        }

        if (missionSnapshot.Includes(NYCGISMissionDataLayers.Buildings) && !sawBuildingLayer)
        {
            missing++;
            missionMissingSpatialLayerScratch.Add("buildings (layer not configured)");
        }
        if (missionSnapshot.Includes(NYCGISMissionDataLayers.StreetSpace) && !sawStreetSpaceLayer)
        {
            missing++;
            missionMissingSpatialLayerScratch.Add("street-space (layer not configured)");
        }

        missionMissingSpatialLayerNames = missionMissingSpatialLayerScratch.Count > 0
            ? string.Join(", ", missionMissingSpatialLayerScratch)
            : string.Empty;
        return missing;
    }

    private static bool MissionIncludesLayer(
        NYCGISMissionAreaSnapshot missionSnapshot,
        Bfp2LayerSettings layer)
    {
        return IsBuildingLayer(layer)
            ? missionSnapshot.Includes(NYCGISMissionDataLayers.Buildings)
            : missionSnapshot.Includes(NYCGISMissionDataLayers.StreetSpace);
    }

    private bool IsPackInResidentFrustum(Bfp2PackInfo pack, Matrix4x4 localToWorld, float paddingMeters)
    {
        Bounds worldBounds = TransformBounds(pack.Bounds, localToWorld);
        float padding = Mathf.Max(0.0f, paddingMeters);
        if (padding > 0.0f)
        {
            worldBounds.Expand(padding * 2.0f);
        }
        return GeometryUtility.TestPlanesAABB(frustumPlanes, worldBounds);
    }

    private void StartPendingLoads()
    {
        if (layers == null)
        {
            return;
        }

        EnsureStartedPerLayerScratch();
        Array.Clear(startedPerLayerScratch, 0, startedPerLayerScratch.Length);
        int activeLoadCount = 0;
        long pendingPipelineBytes = stagedUploadBytes;
        foreach (Bfp2PackInfo pack in packs)
        {
            if (pack.LoadTask != null)
            {
                activeLoadCount++;
                pendingPipelineBytes += EstimateGpuBytes(pack.Header);
            }
        }
        int globalLoadStartBudget = int.MaxValue;
        if (NYCGISMissionAreaResidencyContext.TryGetSnapshot(out NYCGISMissionAreaSnapshot missionSnapshot))
        {
            globalLoadStartBudget = Mathf.Max(
                0,
                missionSnapshot.budget.maxConcurrentIoRequests - activeLoadCount);
        }
        int globalLoadsStarted = 0;

        loadCandidateScratch.Clear();
        foreach (Bfp2PackInfo pack in packs)
        {
            if (!pack.Desired || IsPackResident(pack) || pack.LoadTask != null || pack.PendingUploadCpu != null)
            {
                continue;
            }

            loadCandidateScratch.Add(pack);
        }

        loadCandidateScratch.Sort(ComparePackDistance);
        foreach (Bfp2PackInfo pack in loadCandidateScratch)
        {
            if (globalLoadsStarted >= globalLoadStartBudget)
            {
                break;
            }

            Bfp2LayerSettings layer = layers[pack.LayerIndex];
            int maxLayerLoads = Mathf.Max(1, layer.maxLoadsPerFrame);
            if (startedPerLayerScratch[pack.LayerIndex] >= maxLayerLoads)
            {
                continue;
            }

            long estimate = EstimateGpuBytes(pack.Header);
            if (maxPendingUploadBytes > 0 && pendingPipelineBytes >= maxPendingUploadBytes)
            {
                break;
            }

            if (maxPendingUploadBytes > 0 &&
                pendingPipelineBytes + estimate > maxPendingUploadBytes &&
                startedPerLayerScratch[pack.LayerIndex] > 0)
            {
                continue;
            }

            pendingPipelineBytes += estimate;
            startedPerLayerScratch[pack.LayerIndex]++;
            globalLoadsStarted++;
            pack.LoadTask = Task.Run(() => ReadPackPayloadCached(pack.Path, pack.Header));
            pack.LoadStartTime = Time.realtimeSinceStartup;
        }
    }

    private void EnsureStartedPerLayerScratch()
    {
        int layerCount = layers != null ? layers.Length : 0;
        if (startedPerLayerScratch.Length != layerCount)
        {
            startedPerLayerScratch = new int[layerCount];
        }
    }

    private void CompleteFinishedLoads()
    {
        bool missionActive = NYCGISMissionAreaResidencyContext.HasActiveMissionArea;
        foreach (Bfp2PackInfo pack in packs)
        {
            Task<Bfp2CpuPack> task = pack.LoadTask;
            if (task == null || !task.IsCompleted)
            {
                continue;
            }

            pack.LoadTask = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                string message = task.Exception != null ? task.Exception.GetBaseException().Message : "canceled";
                lastStatus = "BFP2 load failed: " + pack.Name + " " + message;
                Debug.LogWarning(lastStatus, this);
                continue;
            }

            if (!pack.Desired && (!pinLoadedPacks || missionActive))
            {
                continue;
            }

            try
            {
                if (enableStagedUploadQueue)
                {
                    EnqueueUpload(pack, task.Result);
                    lastStatus = "BFP2 staged " + pack.Name;
                }
                else
                {
                    UploadPack(pack, task.Result);
                    pack.LastDesiredTime = Time.realtimeSinceStartup;
                    lastStatus = "BFP2 loaded " + pack.Name;
                }
            }
            catch (Exception ex)
            {
                lastStatus = "BFP2 upload failed: " + pack.Name + " " + ex.Message;
                Debug.LogWarning(lastStatus, this);
            }
        }
    }

    private void EnqueueUpload(Bfp2PackInfo pack, Bfp2CpuPack cpu)
    {
        if (pack == null || cpu == null)
        {
            return;
        }

        ClearPendingUpload(pack);
        pack.PendingUploadCpu = cpu;
        pack.PendingUploadBytes = EstimateUploadBytes(cpu);
        pack.UploadStage = Bfp2UploadStage.AllocateBuffers;
        pack.PendingVertexUploadWordOffset = 0;
        pack.PendingIndexUploadOffset = 0;
        pack.PendingPackedIndex16UploadOffset = 0;
        pack.PendingClusterIndexBase16UploadOffset = 0;
        pack.PendingClusterUploadWordOffset = 0;
        stagedUploadBytes += pack.PendingUploadBytes;
        stagedUploadPackCount++;

        if (!pack.UploadQueued)
        {
            pack.UploadQueued = true;
            uploadQueue.Enqueue(pack);
        }
    }

    private void ProcessUploadQueue()
    {
        if (uploadQueue.Count == 0)
        {
            return;
        }

        int uploadPackBudget = enableStagedUploadQueue ? Mathf.Max(1, maxUploadPacksPerFrame) : int.MaxValue;
        long uploadByteBudget = !enableStagedUploadQueue || maxUploadBytesPerFrame <= 0 ? long.MaxValue : maxUploadBytesPerFrame;
        double mainThreadDeadline = double.PositiveInfinity;
        if (NYCGISMissionAreaResidencyContext.TryGetSnapshot(out NYCGISMissionAreaSnapshot missionSnapshot))
        {
            long missionUploadBudget = (long)(Mathf.Max(0.0f, missionSnapshot.budget.maxUploadMegabytesPerFrame) * 1024.0f * 1024.0f);
            uploadByteBudget = uploadByteBudget == long.MaxValue
                ? missionUploadBudget
                : Math.Min(uploadByteBudget, missionUploadBudget);
            mainThreadDeadline = Time.realtimeSinceStartupAsDouble +
                                 Mathf.Max(0.1f, missionSnapshot.budget.maxStreamingMainThreadMilliseconds) / 1000.0;
        }
        int processedPacks = 0;
        int processedStages = 0;
        long uploadedBytesThisFrame = 0L;
        int guard = uploadQueue.Count;
        bool missionActive = NYCGISMissionAreaResidencyContext.HasActiveMissionArea;

        while (uploadQueue.Count > 0 &&
               processedPacks < uploadPackBudget &&
               processedStages < Mathf.Max(1, maxUploadStagesPerFrame) &&
               Time.realtimeSinceStartupAsDouble <= mainThreadDeadline &&
               guard-- > 0)
        {
            if (uploadByteBudget != long.MaxValue && uploadedBytesThisFrame >= uploadByteBudget)
            {
                break;
            }

            Bfp2PackInfo pack = uploadQueue.Dequeue();
            pack.UploadQueued = false;

            Bfp2CpuPack cpu = pack.PendingUploadCpu;
            if (cpu == null)
            {
                continue;
            }

            if (!pack.Desired && (!pinLoadedPacks || missionActive))
            {
                ClearPendingUpload(pack);
                continue;
            }

            if (pack.Gpu != null)
            {
                ClearPendingUpload(pack);
                continue;
            }

            try
            {
                bool complete = false;
                while (pack.PendingUploadCpu != null &&
                       pack.Gpu == null &&
                       processedStages < Mathf.Max(1, maxUploadStagesPerFrame) &&
                       Time.realtimeSinceStartupAsDouble <= mainThreadDeadline)
                {
                    long remainingBytes = uploadByteBudget == long.MaxValue ? long.MaxValue : uploadByteBudget - uploadedBytesThisFrame;
                    if (remainingBytes <= 0 && processedStages > 0)
                    {
                        break;
                    }

                    complete = ProcessPendingUploadStage(pack, remainingBytes <= 0 ? long.MaxValue : remainingBytes, out long uploadedBytes);
                    processedStages++;
                    uploadedBytesThisFrame += Math.Max(0L, uploadedBytes);

                    if (complete)
                    {
                        nearShadowDirty = true;
                        pack.LastDesiredTime = Time.realtimeSinceStartup;
                        lastStatus = "BFP2 uploaded " + pack.Name;
                        break;
                    }

                    if (uploadByteBudget != long.MaxValue && uploadedBytesThisFrame >= uploadByteBudget)
                    {
                        break;
                    }
                }

                if (!complete && pack.PendingUploadCpu != null && pack.Gpu == null)
                {
                    pack.UploadQueued = true;
                    uploadQueue.Enqueue(pack);
                    lastStatus = "BFP2 uploading " + pack.Name + " " + pack.UploadStage;
                }
            }
            catch (Exception ex)
            {
                ClearPendingUpload(pack);
                lastStatus = "BFP2 upload failed: " + pack.Name + " " + ex.Message;
                Debug.LogWarning(lastStatus, this);
            }

            processedPacks++;
        }
    }

    private void ClearPendingUpload(Bfp2PackInfo pack, bool releasePendingGpu = true)
    {
        if (pack == null)
        {
            return;
        }

        if (releasePendingGpu && pack.PendingUploadGpu != null)
        {
            pack.PendingUploadGpu.Release(this);
            pack.PendingUploadGpu = null;
        }

        if (pack.PendingUploadCpu != null)
        {
            stagedUploadBytes = Math.Max(0L, stagedUploadBytes - Math.Max(0L, pack.PendingUploadBytes));
            stagedUploadPackCount = Mathf.Max(0, stagedUploadPackCount - 1);
            pack.PendingUploadCpu = null;
            pack.PendingUploadBytes = 0;
        }

        pack.PendingUploadGpu = null;
        pack.UploadStage = Bfp2UploadStage.None;
        pack.PendingVertexUploadWordOffset = 0;
        pack.PendingIndexUploadOffset = 0;
        pack.PendingPackedIndex16UploadOffset = 0;
        pack.PendingClusterIndexBase16UploadOffset = 0;
        pack.PendingClusterUploadWordOffset = 0;
        pack.UploadQueued = false;
    }

    private void UnloadExpiredPacks()
    {
        bool missionActive = NYCGISMissionAreaResidencyContext.HasActiveMissionArea;
        float now = Time.realtimeSinceStartup;
        foreach (Bfp2PackInfo pack in packs)
        {
            if (!IsPackResident(pack) || pack.Desired)
            {
                continue;
            }

            float delaySeconds = missionActive ? 0.0f : Mathf.Max(0.0f, unloadDelaySeconds);
            if (!missionActive && enableRecentVisiblePackRetention && pack.LastDrawDesiredTime > 0.0f)
            {
                delaySeconds = Mathf.Max(delaySeconds, recentlyVisibleRetentionSeconds);
            }

            if (now - pack.LastDesiredTime >= delaySeconds)
            {
                ClearPendingUpload(pack);
                pack.ReleaseGpu(this);
            }
        }
    }

    private void EnforceResidentBudgets()
    {
        if (!NYCGISMissionAreaResidencyContext.HasActiveMissionArea && loadAllEnabledPacksResident)
        {
            return;
        }

        if (layers == null || layers.Length == 0 || packs.Count == 0)
        {
            return;
        }

        EnsureResidentBudgetScratch();
        Array.Clear(residentCountScratch, 0, residentCountScratch.Length);
        Array.Clear(residentBytesScratch, 0, residentBytesScratch.Length);
        evictionScratch.Clear();

        int totalResident = 0;
        long totalResidentBytes = 0L;
        for (int i = 0; i < packs.Count; i++)
        {
            Bfp2PackInfo pack = packs[i];
            Bfp2GpuPack gpu = pack.Gpu;
            if (gpu == null)
            {
                continue;
            }

            if (IsFullResidencyPack(pack))
            {
                continue;
            }

            totalResident++;
            totalResidentBytes += gpu.EstimatedBytes;
            if (pack.LayerIndex >= 0 && pack.LayerIndex < layers.Length)
            {
                residentCountScratch[pack.LayerIndex]++;
                residentBytesScratch[pack.LayerIndex] += gpu.EstimatedBytes;
            }

            if (!pack.Desired)
            {
                evictionScratch.Add(pack);
            }
        }

        if (!IsOverResidentBudget(totalResident, totalResidentBytes, residentCountScratch, residentBytesScratch))
        {
            return;
        }

        evictionScratch.Sort(ComparePackDistanceDescending);
        for (int i = 0; i < evictionScratch.Count; i++)
        {
            if (!IsOverResidentBudget(totalResident, totalResidentBytes, residentCountScratch, residentBytesScratch))
            {
                break;
            }

            Bfp2PackInfo pack = evictionScratch[i];
            Bfp2GpuPack gpu = pack.Gpu;
            if (gpu == null || pack.Desired)
            {
                continue;
            }

            long releasedBytes = gpu.EstimatedBytes;
            int layerIndex = pack.LayerIndex;
            pack.ReleaseGpu(this);
            totalResident = Mathf.Max(0, totalResident - 1);
            totalResidentBytes = Math.Max(0L, totalResidentBytes - releasedBytes);
            if (layerIndex >= 0 && layerIndex < layers.Length)
            {
                residentCountScratch[layerIndex] = Mathf.Max(0, residentCountScratch[layerIndex] - 1);
                residentBytesScratch[layerIndex] = Math.Max(0L, residentBytesScratch[layerIndex] - releasedBytes);
            }
        }
    }

    private void EnsureResidentBudgetScratch()
    {
        int layerCount = layers != null ? layers.Length : 0;
        if (residentCountScratch.Length == layerCount && residentBytesScratch.Length == layerCount)
        {
            return;
        }

        residentCountScratch = new int[layerCount];
        residentBytesScratch = new long[layerCount];
    }

    private bool IsOverResidentBudget(
        int totalResident,
        long totalResidentBytes,
        int[] layerResidentCounts,
        long[] layerResidentBytes)
    {
        int globalPackLimit = Mathf.Max(1, maxTotalResidentPacks);
        long globalByteLimit = long.MaxValue;
        if (NYCGISMissionAreaResidencyContext.TryGetSnapshot(out NYCGISMissionAreaSnapshot missionSnapshot))
        {
            globalPackLimit = GetEffectiveGlobalPackLimit(true, missionSnapshot);
            globalByteLimit = GetEffectiveGlobalByteLimit(true, missionSnapshot);
        }

        if (totalResident > globalPackLimit || totalResidentBytes > globalByteLimit)
        {
            return true;
        }

        for (int i = 0; i < layers.Length; i++)
        {
            Bfp2LayerSettings layer = layers[i];
            if (layer == null || layer.fullLayerResidency)
            {
                continue;
            }

            int layerPackLimit = Mathf.Max(0, layer.maxResidentPacks);
            if (layerResidentCounts[i] > layerPackLimit)
            {
                return true;
            }

            if (layer.maxResidentBytes > 0 && layerResidentBytes[i] > layer.maxResidentBytes)
            {
                return true;
            }
        }

        return false;
    }

    private void DrawLoadedPacks(Camera drawCamera)
    {
        ProcessCameraExpansionReadback();
        if (clearKernel < 0 || cullKernel < 0)
        {
            return;
        }

        Matrix4x4 localToWorld = transform.localToWorldMatrix;
        Matrix4x4 worldToLocal = transform.worldToLocalMatrix;
        Matrix4x4 planeTransform = Matrix4x4.Transpose(localToWorld);
        if (!PopulateCameraFrameBuffer(drawCamera))
        {
            adaptiveCullGroupingRequested = false;
            usingAdaptiveCullGroups = false;
            return;
        }

        Camera primaryDrawCamera = cameraFrameBuffer.Frames[0].Camera;

        bool shouldQueueStatsReadback = IsDebugReadbackActive() && Time.realtimeSinceStartup >= nextDebugReadbackTime;
        if (shouldQueueStatsReadback)
        {
            nextDebugReadbackTime = Time.realtimeSinceStartup + debugReadbackIntervalSeconds;
        }

        if (ShouldUseRuntimeRenderScheduler())
        {
            UpdateRenderScheduler(primaryDrawCamera);
        }
        else
        {
            schedulerDrawSkipSet.Clear();
            schedulerPilotMergedPackCount = 0;
            schedulerPilotGroupCount = 0;
            ReleaseScheduledGroups();
        }
        ApplyGlobalMaterialConstants();

        if (ShouldSampleCameraExpansion() &&
            PrepareCameraCullFrameRange(0, cameraFrameBuffer.Count, worldToLocal, planeTransform, out _))
        {
            SampleCameraExpansion();
        }

        bool useCullGroups = ShouldUseAdaptiveCullGroups() && EnsureSecondaryCullOutputs();
        usingAdaptiveCullGroups = useCullGroups;
        if (useCullGroups)
        {
            if (PrepareCameraCullFrameRange(0, 1, worldToLocal, planeTransform, out Vector3 primaryCameraPositionOS))
            {
                BeginPrimaryCullPass(0, 1);
                DrawCullGroup(primaryDrawCamera, primaryCameraPositionOS, 0, 0, 1);
                CompletePrimaryCullPass();
            }

            int secondaryCameraCount = cameraFrameBuffer.Count - 1;
            if (secondaryCameraCount > 0 &&
                PrepareCameraCullFrameRange(1, secondaryCameraCount, worldToLocal, planeTransform, out Vector3 secondaryCameraPositionOS))
            {
                DrawCullGroup(cameraFrameBuffer.Frames[1].Camera, secondaryCameraPositionOS, 1, 1, secondaryCameraCount);
            }
        }
        else if (PrepareCameraCullFrameRange(0, cameraFrameBuffer.Count, worldToLocal, planeTransform, out Vector3 unionCameraPositionOS))
        {
            BeginPrimaryCullPass(0, cameraFrameBuffer.Count);
            DrawCullGroup(primaryDrawCamera, unionCameraPositionOS, 0, 0, cameraFrameBuffer.Count);
            CompletePrimaryCullPass();
        }

        if (shouldQueueStatsReadback)
        {
            QueueStatsReadbacks();
        }
    }

    private void DrawCullGroup(
        Camera fallbackDrawCamera,
        Vector3 cameraPositionOS,
        int cullOutputIndex,
        int targetFrameStart,
        int targetFrameCount)
    {
        DrawScheduledGroups(fallbackDrawCamera, cullOutputIndex, targetFrameStart, targetFrameCount, cameraPositionOS);

        for (int packIndex = 0; packIndex < packs.Count; packIndex++)
        {
            Bfp2PackInfo pack = packs[packIndex];
            if (pack.Gpu == null || pack.Header.ClusterCount == 0 || pack.Header.IndexCount == 0)
            {
                continue;
            }

            if ((!forceSubmitAllResidentPacks && !pack.DrawDesired) ||
                schedulerDrawSkipSet.Contains(pack))
            {
                continue;
            }

            // A full-residency layer may opt into submitting every pack (the production
            // buildings default). Otherwise reject pack AABBs outside this camera group before
            // dispatching the finer GPU cluster test.
            if (!forceSubmitAllResidentPacks &&
                IsFullResidencyPack(pack) &&
                !ShouldSubmitAllResidentPacks(pack) &&
                !IntersectsPreparedCameraFrustums(pack.Bounds, residentFrustumPaddingMeters))
            {
                continue;
            }

            DispatchCull(pack, cameraPositionOS, cullOutputIndex);
            if (cullOutputIndex == 0)
            {
                RecordPrimaryCullPack(pack, packIndex);
            }
            DrawPack(pack, fallbackDrawCamera, cullOutputIndex, targetFrameStart, targetFrameCount);
        }
    }

    private void BeginPrimaryCullPass(
        int targetFrameStart,
        int targetFrameCount)
    {
        ulong nextEpoch = completedPrimaryCullPass.Epoch + 1UL;
        if (nextEpoch == 0UL)
        {
            nextEpoch = 1UL;
        }
        int clampedStart = Mathf.Clamp(
            targetFrameStart,
            0,
            cameraFrameBuffer.Count);
        int clampedCount = Mathf.Clamp(
            targetFrameCount,
            0,
            cameraFrameBuffer.Count - clampedStart);
        ulong cameraHash = 1469598103934665603UL;
        Camera primaryCamera = null;
        for (int i = 0; i < clampedCount; i++)
        {
            Camera camera = cameraFrameBuffer.Frames[
                clampedStart + i].Camera;
            if (i == 0)
            {
                primaryCamera = camera;
            }
            cameraHash = HashCameraValidationState(
                cameraHash,
                camera);
        }
        activePrimaryCullPass = new PrimaryCullPassMetadata
        {
            Epoch = nextEpoch,
            PreparedCameraCount = clampedCount,
            PrimaryCameraInstanceId = primaryCamera != null
                ? primaryCamera.StableId()
                : 0,
            DispatchFrame = Time.frameCount,
            DispatchedPackCount = 0,
            CameraStateHash = cameraHash,
            DispatchedPackHash = 1469598103934665603UL,
            PackSetComplete = scheduledGroups.Count == 0 &&
                schedulerDrawSkipSet.Count == 0
        };
    }

    private void RecordPrimaryCullPack(
        Bfp2PackInfo pack,
        int packIndex)
    {
        pack.LastPrimaryCullPassEpoch = activePrimaryCullPass.Epoch;
        activePrimaryCullPass.DispatchedPackCount++;
        activePrimaryCullPass.DispatchedPackHash =
            HashCullValidationValue(
                activePrimaryCullPass.DispatchedPackHash,
                (uint)packIndex);
        activePrimaryCullPass.DispatchedPackHash =
            HashCullValidationValue(
                activePrimaryCullPass.DispatchedPackHash,
                (uint)pack.Header.ClusterCount);
        activePrimaryCullPass.DispatchedPackHash =
            HashCullValidationValue(
                activePrimaryCullPass.DispatchedPackHash,
                (uint)pack.Header.IndexCount);
    }

    private void CompletePrimaryCullPass()
    {
        completedPrimaryCullPass = activePrimaryCullPass;
    }

    private static ulong HashCameraValidationState(
        ulong hash,
        Camera camera)
    {
        if (camera == null)
        {
            return HashCullValidationValue(hash, 0u);
        }
        Vector3 position = camera.transform.position;
        Quaternion rotation = camera.transform.rotation;
        hash = HashFloatValidationValue(hash, position.x);
        hash = HashFloatValidationValue(hash, position.y);
        hash = HashFloatValidationValue(hash, position.z);
        hash = HashFloatValidationValue(hash, rotation.x);
        hash = HashFloatValidationValue(hash, rotation.y);
        hash = HashFloatValidationValue(hash, rotation.z);
        hash = HashFloatValidationValue(hash, rotation.w);
        hash = HashCullValidationValue(
            hash,
            camera.orthographic ? 1u : 0u);
        hash = HashFloatValidationValue(hash, camera.fieldOfView);
        hash = HashFloatValidationValue(hash, camera.orthographicSize);
        hash = HashFloatValidationValue(hash, camera.aspect);
        hash = HashFloatValidationValue(hash, camera.nearClipPlane);
        hash = HashFloatValidationValue(hash, camera.farClipPlane);
        int width = camera.targetTexture != null
            ? camera.targetTexture.width
            : camera.pixelWidth;
        int height = camera.targetTexture != null
            ? camera.targetTexture.height
            : camera.pixelHeight;
        hash = HashCullValidationValue(hash, (uint)Mathf.Max(0, width));
        return HashCullValidationValue(hash, (uint)Mathf.Max(0, height));
    }

    private static ulong HashFloatValidationValue(
        ulong hash,
        float value)
    {
        return HashCullValidationValue(
            hash,
            unchecked((uint)BitConverter.SingleToInt32Bits(value)));
    }

    private static ulong HashPrimaryCullPassTopology(
        PrimaryCullPassMetadata pass)
    {
        ulong hash = 1469598103934665603UL;
        hash = HashCullValidationValue(
            hash,
            (uint)pass.PreparedCameraCount);
        hash = HashCullValidationValue(
            hash,
            (uint)pass.DispatchedPackCount);
        hash = HashCullValidationValue(
            hash,
            pass.PackSetComplete ? 1u : 0u);
        hash = HashCullValidationValue(
            hash,
            (uint)(pass.CameraStateHash & uint.MaxValue));
        hash = HashCullValidationValue(
            hash,
            (uint)(pass.CameraStateHash >> 32));
        hash = HashCullValidationValue(
            hash,
            (uint)(pass.DispatchedPackHash & uint.MaxValue));
        return HashCullValidationValue(
            hash,
            (uint)(pass.DispatchedPackHash >> 32));
    }

    private bool IntersectsPreparedCameraFrustums(Bounds bounds, float paddingMeters)
    {
        if (preparedCameraFrameCount <= 0)
        {
            return true;
        }

        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents + Vector3.one * Mathf.Max(0.0f, paddingMeters);
        for (int cameraIndex = 0; cameraIndex < preparedCameraFrameCount; cameraIndex++)
        {
            bool intersects = true;
            int planeOffset = cameraIndex * 6;
            for (int planeIndex = 0; planeIndex < 6; planeIndex++)
            {
                Vector4 plane = cameraFrustumPlaneVectors[planeOffset + planeIndex];
                float signedDistance = plane.x * center.x +
                                       plane.y * center.y +
                                       plane.z * center.z +
                                       plane.w;
                float projectedRadius = Mathf.Abs(plane.x) * extents.x +
                                        Mathf.Abs(plane.y) * extents.y +
                                        Mathf.Abs(plane.z) * extents.z;
                if (signedDistance + projectedRadius < 0.0f)
                {
                    intersects = false;
                    break;
                }
            }

            if (intersects)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPackResident(Bfp2PackInfo pack)
    {
        return pack != null && pack.Gpu != null;
    }

    private void DispatchCull(Bfp2PackInfo pack, Vector3 cameraPositionOS, int cullOutputIndex)
    {
        DispatchCull(pack.Gpu, pack.Header, pack.ForceNoTriangleWindingFlip, cameraPositionOS, cullOutputIndex);
    }

    private void DispatchCull(
        Bfp2GpuPack gpu,
        Bfp2Header header,
        bool forceNoTriangleWindingFlip,
        Vector3 cameraPositionOS,
        int cullOutputIndex)
    {
        GraphicsBuffer visibleIndexBuffer = gpu.GetVisibleIndexBuffer(cullOutputIndex);
        GraphicsBuffer drawArgsBuffer = gpu.GetDrawArgsBuffer(cullOutputIndex);
        GraphicsBuffer statsBuffer = gpu.GetStatsBuffer(cullOutputIndex);

        bool requestsCompact = clusterCullAlgorithm != Bfp2ClusterCullAlgorithm.ScalarAoS;
        if (requestsCompact)
        {
            InitializeCompactClusterData(gpu, header.ClusterCount);
        }

        ComputeShader activeCompute = cullCompute;
        int activeKernel = cullKernel;
        bool useCompactLayout = false;
        int tileTriangleCount = 0;
        bool useClusterLocalIndices16 = false;
        string markerName = ScalarAosMarker;
        if (requestsCompact && gpu.CompactClusterDataReady && compactCullKernel >= 0)
        {
            activeKernel = compactCullKernel;
            useCompactLayout = true;
            markerName = ScalarCompactMarker;
        }
        if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveCompact &&
            gpu.CompactClusterDataReady &&
            WaveCullSupported)
        {
            activeCompute = waveCullCompute;
            activeKernel = waveCullKernel;
            useCompactLayout = true;
            markerName = WaveCompactMarker;
        }
        else if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveTile32Index16 &&
                 gpu.CompactClusterDataReady &&
                 gpu.ClusterLocalIndex16Ready &&
                 WaveTileIndex16CullSupported)
        {
            activeCompute = waveCullCompute;
            activeKernel = waveTile32Index16Kernel;
            useCompactLayout = true;
            tileTriangleCount = Tile32TriangleCount;
            useClusterLocalIndices16 = true;
            markerName = WaveTile32Index16Marker;
        }
        else if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveTile32Index16 &&
                 gpu.CompactClusterDataReady &&
                 WaveTileCullSupported)
        {
            // Preserve rendering on a pack that failed the uint16 eligibility contract.
            activeCompute = waveCullCompute;
            activeKernel = waveTile32Kernel;
            useCompactLayout = true;
            tileTriangleCount = Tile32TriangleCount;
            markerName = WaveTile32Marker;
        }
        else if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveTile32 &&
                 gpu.CompactClusterDataReady &&
                 WaveTileCullSupported)
        {
            activeCompute = waveCullCompute;
            activeKernel = waveTile32Kernel;
            useCompactLayout = true;
            tileTriangleCount = Tile32TriangleCount;
            markerName = WaveTile32Marker;
        }
        else if (clusterCullAlgorithm == Bfp2ClusterCullAlgorithm.WaveTile64 &&
                 gpu.CompactClusterDataReady &&
                 WaveTileCullSupported)
        {
            activeCompute = waveCullCompute;
            activeKernel = waveTile64Kernel;
            useCompactLayout = true;
            tileTriangleCount = Tile64TriangleCount;
            markerName = WaveTile64Marker;
        }

        gpu.SetCullOutputTileTriangleCount(cullOutputIndex, tileTriangleCount);
        gpu.SetCullOutputUsesClusterLocalIndex16(
            cullOutputIndex,
            useClusterLocalIndices16);

        int groups = Mathf.Max(1, Mathf.CeilToInt(header.ClusterCount / 128.0f));
        if (enableGpuProfilerMarkers)
        {
            DispatchCullWithGpuMarkers(
                activeCompute,
                activeKernel,
                markerName,
                useCompactLayout,
                tileTriangleCount,
                useClusterLocalIndices16,
                gpu,
                header,
                forceNoTriangleWindingFlip,
                cameraPositionOS,
                visibleIndexBuffer,
                drawArgsBuffer,
                statsBuffer,
                groups);
            return;
        }

        cullCompute.SetBuffer(clearKernel, DrawArgsId, drawArgsBuffer);
        cullCompute.SetBuffer(clearKernel, StatsId, statsBuffer);
        cullCompute.Dispatch(clearKernel, 1, 1, 1);

        BindCullKernelDirect(
            activeCompute,
            activeKernel,
            useCompactLayout,
            tileTriangleCount,
            useClusterLocalIndices16,
            gpu,
            header,
            forceNoTriangleWindingFlip,
            cameraPositionOS,
            visibleIndexBuffer,
            drawArgsBuffer,
            statsBuffer);
        activeCompute.Dispatch(activeKernel, groups, 1, 1);
    }

    private void BindCullKernelDirect(
        ComputeShader compute,
        int kernel,
        bool useCompactLayout,
        int tileTriangleCount,
        bool useClusterLocalIndices16,
        Bfp2GpuPack gpu,
        Bfp2Header header,
        bool forceNoTriangleWindingFlip,
        Vector3 cameraPositionOS,
        GraphicsBuffer visibleIndexBuffer,
        GraphicsBuffer drawArgsBuffer,
        GraphicsBuffer statsBuffer)
    {
        compute.SetBuffer(kernel, IndicesId, gpu.IndexBuffer);
        if (useClusterLocalIndices16)
        {
            compute.SetBuffer(
                kernel,
                ClusterIndexBases16Id,
                gpu.ClusterIndexBases16Buffer);
        }
        if (useCompactLayout)
        {
            compute.SetBuffer(kernel, CompactClusterWordsId, gpu.CompactClusterWordsBuffer);
        }
        else
        {
            compute.SetBuffer(kernel, ClusterWordsId, gpu.ClusterWordsBuffer);
        }
        if (tileTriangleCount > 0)
        {
            compute.SetBuffer(kernel, VisibleTileWordsId, visibleIndexBuffer);
            compute.SetInt(MaxVisibleTileWordsId, gpu.VisibleIndexCapacity);
        }
        else
        {
            compute.SetBuffer(kernel, VisibleIndicesId, visibleIndexBuffer);
            compute.SetInt(MaxVisibleIndicesId, gpu.VisibleIndexCapacity);
        }
        compute.SetBuffer(kernel, DrawArgsId, drawArgsBuffer);
        compute.SetBuffer(kernel, StatsId, statsBuffer);
        compute.SetInt(ClusterCountId, header.ClusterCount);
        compute.SetVectorArray(FrustumPlanesId, frustumPlaneVectors);
        compute.SetVector(CameraPositionOsId, cameraPositionOS);
        compute.SetInt(CameraFrameCountId, Mathf.Clamp(preparedCameraFrameCount, 0, 8));
        compute.SetVectorArray(CameraFrustumPlanesId, cameraFrustumPlaneVectors);
        compute.SetVectorArray(CameraPositionsOsId, cameraPositionVectors);
        compute.SetVectorArray(CameraScreenParamsId, cameraScreenParamVectors);
        compute.SetFloat(MaxDistanceId, maxDrawDistanceMeters);
        compute.SetInt(EnableFrustumCullingId, IsGpuFrustumCullingEnabled() ? 1 : 0);
        compute.SetInt(EnableDistanceCullingId, enableDistanceCulling ? 1 : 0);
        compute.SetInt(EnableNormalConeCullingId, enableNormalConeBackfaceCulling ? 1 : 0);
        compute.SetInt(EnableScreenSizeCullingId, enableScreenSizeCulling ? 1 : 0);
        compute.SetInt(
            UseProjectedPixelScreenSizeCullingId,
            useProjectedPixelScreenSizeCulling ? 1 : 0);
        bool shouldFlipTriangleWinding = flipTriangleWinding && !forceNoTriangleWindingFlip;
        compute.SetInt(FlipTriangleWindingId, shouldFlipTriangleWinding ? 1 : 0);
        compute.SetFloat(MinScreenRadiusRatioId, minScreenRadiusRatio);
        compute.SetFloat(MinScreenRadiusPixelsId, minScreenRadiusPixels);
    }

    private void DispatchCullWithGpuMarkers(
        ComputeShader activeCompute,
        int activeKernel,
        string markerName,
        bool useCompactLayout,
        int tileTriangleCount,
        bool useClusterLocalIndices16,
        Bfp2GpuPack gpu,
        Bfp2Header header,
        bool forceNoTriangleWindingFlip,
        Vector3 cameraPositionOS,
        GraphicsBuffer visibleIndexBuffer,
        GraphicsBuffer drawArgsBuffer,
        GraphicsBuffer statsBuffer,
        int groups)
    {
        CommandBuffer command = GetGpuProfilerCommandBuffer();
        command.Clear();
        command.BeginSample(ClusterCullMarker);
        command.BeginSample(ClearArgsMarker);
        command.SetComputeBufferParam(cullCompute, clearKernel, DrawArgsId, drawArgsBuffer);
        command.SetComputeBufferParam(cullCompute, clearKernel, StatsId, statsBuffer);
        command.DispatchCompute(cullCompute, clearKernel, 1, 1, 1);
        command.EndSample(ClearArgsMarker);

        command.BeginSample(markerName);
        BindCullKernelCommand(
            command,
            activeCompute,
            activeKernel,
            useCompactLayout,
            tileTriangleCount,
            useClusterLocalIndices16,
            gpu,
            header,
            forceNoTriangleWindingFlip,
            cameraPositionOS,
            visibleIndexBuffer,
            drawArgsBuffer,
            statsBuffer);
        command.DispatchCompute(activeCompute, activeKernel, groups, 1, 1);
        command.EndSample(markerName);
        command.EndSample(ClusterCullMarker);
        Graphics.ExecuteCommandBuffer(command);
    }

    private void BindCullKernelCommand(
        CommandBuffer command,
        ComputeShader compute,
        int kernel,
        bool useCompactLayout,
        int tileTriangleCount,
        bool useClusterLocalIndices16,
        Bfp2GpuPack gpu,
        Bfp2Header header,
        bool forceNoTriangleWindingFlip,
        Vector3 cameraPositionOS,
        GraphicsBuffer visibleIndexBuffer,
        GraphicsBuffer drawArgsBuffer,
        GraphicsBuffer statsBuffer)
    {
        command.SetComputeBufferParam(compute, kernel, IndicesId, gpu.IndexBuffer);
        if (useClusterLocalIndices16)
        {
            command.SetComputeBufferParam(
                compute,
                kernel,
                ClusterIndexBases16Id,
                gpu.ClusterIndexBases16Buffer);
        }
        command.SetComputeBufferParam(
            compute,
            kernel,
            useCompactLayout ? CompactClusterWordsId : ClusterWordsId,
            useCompactLayout ? gpu.CompactClusterWordsBuffer : gpu.ClusterWordsBuffer);
        if (tileTriangleCount > 0)
        {
            command.SetComputeBufferParam(
                compute,
                kernel,
                VisibleTileWordsId,
                visibleIndexBuffer);
            command.SetComputeIntParam(compute, MaxVisibleTileWordsId, gpu.VisibleIndexCapacity);
        }
        else
        {
            command.SetComputeBufferParam(compute, kernel, VisibleIndicesId, visibleIndexBuffer);
            command.SetComputeIntParam(compute, MaxVisibleIndicesId, gpu.VisibleIndexCapacity);
        }
        command.SetComputeBufferParam(compute, kernel, DrawArgsId, drawArgsBuffer);
        command.SetComputeBufferParam(compute, kernel, StatsId, statsBuffer);
        command.SetComputeIntParam(compute, ClusterCountId, header.ClusterCount);
        command.SetComputeVectorArrayParam(compute, FrustumPlanesId, frustumPlaneVectors);
        command.SetComputeVectorParam(compute, CameraPositionOsId, cameraPositionOS);
        command.SetComputeIntParam(compute, CameraFrameCountId, Mathf.Clamp(preparedCameraFrameCount, 0, 8));
        command.SetComputeVectorArrayParam(compute, CameraFrustumPlanesId, cameraFrustumPlaneVectors);
        command.SetComputeVectorArrayParam(compute, CameraPositionsOsId, cameraPositionVectors);
        command.SetComputeVectorArrayParam(compute, CameraScreenParamsId, cameraScreenParamVectors);
        command.SetComputeFloatParam(compute, MaxDistanceId, maxDrawDistanceMeters);
        command.SetComputeIntParam(compute, EnableFrustumCullingId, IsGpuFrustumCullingEnabled() ? 1 : 0);
        command.SetComputeIntParam(compute, EnableDistanceCullingId, enableDistanceCulling ? 1 : 0);
        command.SetComputeIntParam(compute, EnableNormalConeCullingId, enableNormalConeBackfaceCulling ? 1 : 0);
        command.SetComputeIntParam(compute, EnableScreenSizeCullingId, enableScreenSizeCulling ? 1 : 0);
        command.SetComputeIntParam(
            compute,
            UseProjectedPixelScreenSizeCullingId,
            useProjectedPixelScreenSizeCulling ? 1 : 0);
        bool shouldFlipTriangleWinding = flipTriangleWinding && !forceNoTriangleWindingFlip;
        command.SetComputeIntParam(compute, FlipTriangleWindingId, shouldFlipTriangleWinding ? 1 : 0);
        command.SetComputeFloatParam(compute, MinScreenRadiusRatioId, minScreenRadiusRatio);
        command.SetComputeFloatParam(compute, MinScreenRadiusPixelsId, minScreenRadiusPixels);
    }

    private void InitializeCompactClusterData(Bfp2GpuPack gpu, int clusterCount)
    {
        if (gpu == null ||
            gpu.CompactClusterDataReady ||
            packCompactKernel < 0 ||
            gpu.ClusterWordsBuffer == null ||
            gpu.CompactClusterWordsBuffer == null ||
            clusterCount <= 0)
        {
            return;
        }

        int groups = Mathf.Max(1, Mathf.CeilToInt(clusterCount / 128.0f));
        if (enableGpuProfilerMarkers)
        {
            CommandBuffer command = GetGpuProfilerCommandBuffer();
            command.Clear();
            command.BeginSample(PackCompactMarker);
            command.SetComputeBufferParam(
                cullCompute,
                packCompactKernel,
                ClusterWordsId,
                gpu.ClusterWordsBuffer);
            command.SetComputeBufferParam(
                cullCompute,
                packCompactKernel,
                CompactClusterWordsId,
                gpu.CompactClusterWordsBuffer);
            command.SetComputeIntParam(cullCompute, ClusterCountId, clusterCount);
            command.DispatchCompute(cullCompute, packCompactKernel, groups, 1, 1);
            command.EndSample(PackCompactMarker);
            Graphics.ExecuteCommandBuffer(command);
        }
        else
        {
            cullCompute.SetBuffer(packCompactKernel, ClusterWordsId, gpu.ClusterWordsBuffer);
            cullCompute.SetBuffer(
                packCompactKernel,
                CompactClusterWordsId,
                gpu.CompactClusterWordsBuffer);
            cullCompute.SetInt(ClusterCountId, clusterCount);
            cullCompute.Dispatch(packCompactKernel, groups, 1, 1);
        }
        gpu.CompactClusterDataReady = true;
    }

    private CommandBuffer GetGpuProfilerCommandBuffer()
    {
        if (gpuProfilerCommandBuffer == null)
        {
            gpuProfilerCommandBuffer = new CommandBuffer
            {
                name = "BFP2 GPU Cluster Culling Markers"
            };
        }
        return gpuProfilerCommandBuffer;
    }

    private void ReleaseGpuProfilerCommandBuffer()
    {
        if (gpuProfilerCommandBuffer == null)
        {
            return;
        }
        gpuProfilerCommandBuffer.Release();
        gpuProfilerCommandBuffer = null;
    }

    private void BindDrawGeometry(
        Bfp2GpuPack gpu,
        int cullOutputIndex,
        bool forceNoTriangleWindingFlip)
    {
        GraphicsBuffer visibleOutput = gpu.GetVisibleIndexBuffer(cullOutputIndex);
        int tileTriangles = gpu.GetCullOutputTileTriangleCount(cullOutputIndex);
        bool useClusterLocalIndices16 =
            gpu.GetCullOutputUsesClusterLocalIndex16(cullOutputIndex);
        propertyBlock.SetBuffer(VertexWordsId, gpu.VertexWordsBuffer);
        propertyBlock.SetBuffer(IndicesId, gpu.IndexBuffer);
        propertyBlock.SetBuffer(
            PackedIndices16Id,
            gpu.PackedIndex16Buffer ?? gpu.IndexBuffer);
        propertyBlock.SetBuffer(
            ClusterIndexBases16Id,
            gpu.ClusterIndexBases16Buffer ?? gpu.IndexBuffer);
        propertyBlock.SetBuffer(VisibleIndicesId, visibleOutput);
        propertyBlock.SetBuffer(VisibleTileWordsId, visibleOutput);
        propertyBlock.SetInt(VisibleTileTrianglesId, tileTriangles);
        propertyBlock.SetInt(
            VisibleTileDescriptorWordsId,
            useClusterLocalIndices16
                ? VisibleTile32Index16DescriptorWords
                : VisibleTile32DescriptorWords);
        propertyBlock.SetInt(
            UseClusterLocalIndices16Id,
            useClusterLocalIndices16 ? 1 : 0);
        bool shouldFlipTriangleWinding =
            flipTriangleWinding && !forceNoTriangleWindingFlip;
        propertyBlock.SetInt(FlipTriangleWindingId, shouldFlipTriangleWinding ? 1 : 0);
    }

    private void DrawPack(
        Bfp2PackInfo pack,
        Camera drawCamera,
        int cullOutputIndex,
        int targetFrameStart,
        int targetFrameCount)
    {
        Bfp2GpuPack gpu = pack.Gpu;
        Bfp2LayerSettings layer = layers[pack.LayerIndex];

        propertyBlock.Clear();
        BindDrawGeometry(gpu, cullOutputIndex, pack.ForceNoTriangleWindingFlip);
        propertyBlock.SetVector(QuantOriginId, pack.Header.QuantOrigin);
        propertyBlock.SetVector(QuantScaleId, pack.Header.QuantScale);
        propertyBlock.SetColor(LayerTintId, layer.tint);
        propertyBlock.SetFloat(LayerIndexId, pack.LayerIndex);
        if (facadeTextureArray != null)
        {
            propertyBlock.SetTexture(FacadeArrayId, facadeTextureArray);
        }

        Bounds worldBounds = TransformBounds(pack.Bounds, transform.localToWorldMatrix);
        ShadowCastingMode packShadowCastingMode = GetPackShadowCastingMode(pack, layer);
        SubmitProceduralDraw(
            worldBounds,
            gpu.GetDrawArgsBuffer(cullOutputIndex),
            drawCamera,
            propertyBlock,
            packShadowCastingMode,
            targetFrameStart,
            targetFrameCount);
    }

    private void SubmitProceduralDraw(
        Bounds worldBounds,
        GraphicsBuffer drawArgsBuffer,
        Camera fallbackDrawCamera,
        MaterialPropertyBlock drawPropertyBlock,
        ShadowCastingMode drawShadowCastingMode,
        int targetFrameStart,
        int targetFrameCount)
    {
        if (usingRegisteredCameraFrames)
        {
            int frameEnd = Mathf.Min(cameraFrameBuffer.Count, targetFrameStart + targetFrameCount);
            for (int frameIndex = Mathf.Max(0, targetFrameStart); frameIndex < frameEnd; frameIndex++)
            {
                Camera targetCamera = cameraFrameBuffer.Frames[frameIndex].Camera;
                if (targetCamera == null)
                {
                    continue;
                }

                Graphics.DrawProceduralIndirect(
                    ActiveMaterial,
                    worldBounds,
                    MeshTopology.Triangles,
                    drawArgsBuffer,
                    0,
                    targetCamera,
                    drawPropertyBlock,
                    drawShadowCastingMode,
                    receiveShadows,
                    gameObject.layer);
            }
            return;
        }

        Graphics.DrawProceduralIndirect(
            ActiveMaterial,
            worldBounds,
            MeshTopology.Triangles,
            drawArgsBuffer,
            0,
            submitDrawsToAllCamerasForShadows ? null : fallbackDrawCamera,
            drawPropertyBlock,
            drawShadowCastingMode,
            receiveShadows,
            gameObject.layer);
    }

    private bool ShouldUseRuntimeRenderScheduler()
    {
        // Scheduler groups duplicate source-pack GPU payloads. Mission residency keeps the
        // existing indirect/union/adaptive-cull path, but suppresses those duplicate groups so
        // the mission pack/byte budget remains a true hard ceiling.
        if (gpuDrivenMegaLayerMode || NYCGISMissionAreaResidencyContext.HasActiveMissionArea)
        {
            return false;
        }

        return enableRenderScheduler || enableRenderSchedulerPilot;
    }

    private void UpdateRenderScheduler(Camera drawCamera)
    {
        schedulerDrawSkipSet.Clear();
        schedulerPilotMergedPackCount = 0;
        schedulerPilotGroupCount = 0;

        if (!enableRenderScheduler)
        {
            UpdateRenderSchedulerPilot(drawCamera);
            return;
        }

        if (drawCamera == null || layers == null || layers.Length == 0)
        {
            ReleaseScheduledGroups();
            return;
        }

        CompleteScheduledGroupBuilds();
        ProcessScheduledGroupUploads();
        EnsureRenderSchedulerGroups();
        MarkReadyScheduledGroups();
    }

    private void EnsureRenderSchedulerGroups()
    {
        schedulerDesiredSignatures.Clear();
        if (!enableRenderScheduler)
        {
            return;
        }

        int groupsStarted = 0;
        int groupLimit = renderSchedulerMaxGroups > 0 ? renderSchedulerMaxGroups : int.MaxValue;
        long scheduledBytes = EstimateScheduledGroupGpuBytes();

        for (int layerIndex = 0; layerIndex < layers.Length && schedulerDesiredSignatures.Count < groupLimit; layerIndex++)
        {
            Bfp2LayerSettings layer = layers[layerIndex];
            if (layer == null || !layer.enabled)
            {
                continue;
            }

            if (renderSchedulerBuildingsOnly && !IsBuildingLayer(layer))
            {
                continue;
            }

            for (int forceNoFlipPass = 0; forceNoFlipPass < 2 && schedulerDesiredSignatures.Count < groupLimit; forceNoFlipPass++)
            {
                bool forceNoFlip = forceNoFlipPass != 0;
                for (int shadowPass = 0; shadowPass < 4 && schedulerDesiredSignatures.Count < groupLimit; shadowPass++)
                {
                    ShadowCastingMode shadowMode = (ShadowCastingMode)shadowPass;
                    if (renderSchedulerExcludeShadowCasters && shadowMode != ShadowCastingMode.Off)
                    {
                        continue;
                    }

                    BuildSchedulerCandidateBucket(layerIndex, forceNoFlip, shadowMode);
                    int groupSize = GetRenderSchedulerGroupSize();
                    int minGroupSize = renderSchedulerOneGroupPerLayer
                        ? 1
                        : Mathf.Clamp(renderSchedulerMinPacksPerGroup, 2, groupSize);
                    for (int offset = 0; offset < schedulerCandidateScratch.Count && schedulerDesiredSignatures.Count < groupLimit; offset += groupSize)
                    {
                        int count = Mathf.Min(groupSize, schedulerCandidateScratch.Count - offset);
                        if (count < minGroupSize)
                        {
                            break;
                        }

                        string signature = BuildScheduledGroupSignature(schedulerCandidateScratch, offset, count, layerIndex, forceNoFlip, shadowMode);
                        schedulerDesiredSignatures.Add(signature);
                        Bfp2ScheduledGroup group = FindScheduledGroup(signature);
                        if (group == null)
                        {
                            long estimatedBytes = EstimateScheduledGroupGpuBytes(schedulerCandidateScratch, offset, count);
                            if (renderSchedulerMaxExtraGpuBytes > 0 && scheduledBytes + estimatedBytes > renderSchedulerMaxExtraGpuBytes)
                            {
                                continue;
                            }

                            if (groupsStarted >= renderSchedulerMaxBuildsStartedPerFrame)
                            {
                                continue;
                            }

                            group = StartScheduledGroupBuild(schedulerCandidateScratch, offset, count, layerIndex, forceNoFlip, shadowMode, signature, estimatedBytes);
                            if (group != null)
                            {
                                groupsStarted++;
                                scheduledBytes += estimatedBytes;
                            }
                        }
                    }
                }
            }
        }

        ReleaseUndesiredScheduledGroups();
    }

    private int GetRenderSchedulerGroupSize()
    {
        if (renderSchedulerOneGroupPerLayer)
        {
            return Mathf.Max(1, renderSchedulerMaxPacksPerLayerGroup);
        }

        return Mathf.Clamp(renderSchedulerPacksPerGroup, 2, 32);
    }

    private NYCGISStreamingFrame ResolvePrimaryCameraFrame(Camera drawCamera)
    {
        if (Application.isPlaying &&
            useWeatherCameraRegistry &&
            NYCGISWeatherCameraRegistry.HasAnyRegisteredFeeds &&
            NYCGISWeatherCameraRegistry.TryGetSelectedFeed(out NYCGISWeatherCameraFeedSnapshot selectedFeed) &&
            selectedFeed.Camera != null)
        {
            return NYCGISStreamingFrameContext.GetFrame(selectedFeed.Camera);
        }

        if (!enableBatchedCameraFrameBuffer)
        {
            return NYCGISStreamingFrameContext.GetFrame(drawCamera);
        }

        cameraFrameBuffer.Clear(Mathf.Clamp(maxBfp2CameraFrames, 1, 8));
        cameraFrameBuffer.Add(drawCamera);
        return cameraFrameBuffer.Count > 0
            ? cameraFrameBuffer.Frames[0]
            : new NYCGISStreamingFrame(null);
    }

    private bool PopulateCameraFrameBuffer(Camera drawCamera)
    {
        int frameLimit = Mathf.Clamp(maxBfp2CameraFrames, 1, 8);
        usingRegisteredCameraFrames = false;
        registeredScheduledCameraCount = 0;

        if (TryPopulateRegisteredCameraFrames(frameLimit))
        {
            usingRegisteredCameraFrames = true;
            registeredScheduledCameraCount = cameraFrameBuffer.Count;
            return cameraFrameBuffer.Count > 0;
        }

        cameraFrameBuffer.Clear(enableBatchedCameraFrameBuffer ? frameLimit : 1);
        cameraFrameBuffer.Add(drawCamera);

        if (enableBatchedCameraFrameBuffer && batchedCullCameras != null)
        {
            for (int i = 0; i < batchedCullCameras.Length; i++)
            {
                cameraFrameBuffer.Add(batchedCullCameras[i]);
            }
        }

        return cameraFrameBuffer.Count > 0;
    }

    private bool TryPopulateRegisteredCameraFrames(int frameLimit)
    {
        if (!Application.isPlaying ||
            !useWeatherCameraRegistry ||
            !NYCGISWeatherCameraRegistry.HasAnyRegisteredFeeds)
        {
            return false;
        }

        cameraFrameBuffer.Clear(frameLimit);
        int frameIndex = Time.frameCount;
        if (NYCGISWeatherCameraRegistry.CurrentScheduleFrame != frameIndex)
        {
            return true;
        }

        if (NYCGISWeatherCameraRegistry.TryGetSelectedFeed(out NYCGISWeatherCameraFeedSnapshot selectedFeed) &&
            selectedFeed.Camera != null &&
            selectedFeed.ScheduledThisFrame &&
            selectedFeed.ScheduledFrame == frameIndex)
        {
            cameraFrameBuffer.Add(selectedFeed.Camera);
        }

        int copiedCameraCount = NYCGISWeatherCameraRegistry.CopyScheduledCamerasNonAlloc(registeredCameraScratch);
        for (int cameraIndex = 0; cameraIndex < copiedCameraCount; cameraIndex++)
        {
            cameraFrameBuffer.Add(registeredCameraScratch[cameraIndex]);
        }

        return true;
    }

    private bool PrepareCameraCullFrameRange(
        int frameStart,
        int frameCount,
        Matrix4x4 worldToLocal,
        Matrix4x4 planeTransform,
        out Vector3 primaryCameraPositionOS)
    {
        primaryCameraPositionOS = Vector3.zero;
        preparedCameraFrameCount = 0;
        int safeFrameStart = Mathf.Clamp(frameStart, 0, cameraFrameBuffer.Count);
        int safeFrameEnd = Mathf.Clamp(safeFrameStart + Mathf.Max(0, frameCount), safeFrameStart, cameraFrameBuffer.Count);
        Vector3 lossyScale = transform.lossyScale;
        float maxObjectScale = Mathf.Max(
            Mathf.Abs(lossyScale.x),
            Mathf.Abs(lossyScale.y),
            Mathf.Abs(lossyScale.z));

        for (int frameIndex = safeFrameStart; frameIndex < safeFrameEnd && preparedCameraFrameCount < 8; frameIndex++)
        {
            NYCGISStreamingFrame frame = cameraFrameBuffer.Frames[frameIndex];
            Camera camera = frame.Camera;
            if (!NYCGISStreamingFrameContext.CopyFrustumPlanes(camera, cameraFramePlaneScratch))
            {
                continue;
            }

            Vector3 cameraPositionOS = worldToLocal.MultiplyPoint3x4(frame.Position);
            cameraPositionVectors[preparedCameraFrameCount] = new Vector4(cameraPositionOS.x, cameraPositionOS.y, cameraPositionOS.z, 1.0f);
            float pixelHeight = Mathf.Max(1.0f, camera.scaledPixelHeight);
            float projectionScale = 0.5f * pixelHeight * Mathf.Abs(camera.projectionMatrix.m11);
            cameraScreenParamVectors[preparedCameraFrameCount] = new Vector4(
                projectionScale,
                camera.orthographic ? 1.0f : 0.0f,
                Mathf.Max(maxObjectScale, 0.000001f),
                0.0f);

            int planeOffset = preparedCameraFrameCount * 6;
            for (int planeIndex = 0; planeIndex < 6; planeIndex++)
            {
                Plane plane = cameraFramePlaneScratch[planeIndex];
                Vector4 worldPlane = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
                Vector4 localPlane = planeTransform * worldPlane;
                cameraFrustumPlaneVectors[planeOffset + planeIndex] = localPlane;
                if (preparedCameraFrameCount == 0)
                {
                    frustumPlaneVectors[planeIndex] = localPlane;
                    frustumPlanes[planeIndex] = plane;
                }
            }

            if (preparedCameraFrameCount == 0)
            {
                primaryCameraPositionOS = cameraPositionOS;
            }

            preparedCameraFrameCount++;
        }

        return preparedCameraFrameCount > 0;
    }

    private bool ShouldSampleCameraExpansion()
    {
        return usingRegisteredCameraFrames &&
               cameraFrameBuffer.Count > 1 &&
               IsGpuFrustumCullingEnabled() &&
               cameraExpansionKernel >= 0 &&
               cameraExpansionStatsBuffer != null &&
               !cameraExpansionReadbackPending &&
               Time.realtimeSinceStartup >= nextCameraExpansionSampleTime;
    }

    private void SampleCameraExpansion()
    {
        nextCameraExpansionSampleTime = Time.realtimeSinceStartup + cameraExpansionSampleIntervalSeconds;
        cameraExpansionStatsBuffer.SetData(InitialCameraExpansionStatsData);

        int dispatchCount = 0;
        for (int packIndex = 0; packIndex < packs.Count; packIndex++)
        {
            Bfp2PackInfo pack = packs[packIndex];
            if (pack.Gpu == null || !pack.DrawDesired || pack.Header.ClusterCount <= 0)
            {
                continue;
            }

            cullCompute.SetBuffer(cameraExpansionKernel, ClusterWordsId, pack.Gpu.ClusterWordsBuffer);
            cullCompute.SetBuffer(cameraExpansionKernel, CameraExpansionStatsId, cameraExpansionStatsBuffer);
            cullCompute.SetInt(ClusterCountId, pack.Header.ClusterCount);
            cullCompute.SetInt(CameraFrameCountId, Mathf.Clamp(preparedCameraFrameCount, 0, 8));
            cullCompute.SetVectorArray(CameraFrustumPlanesId, cameraFrustumPlaneVectors);
            int groups = Mathf.CeilToInt(pack.Header.ClusterCount / 128.0f);
            cullCompute.Dispatch(cameraExpansionKernel, Mathf.Max(1, groups), 1, 1);
            dispatchCount++;
        }

        if (dispatchCount == 0)
        {
            return;
        }

        try
        {
            cameraExpansionReadbackRequest = AsyncGPUReadback.Request(cameraExpansionStatsBuffer);
            cameraExpansionReadbackPending = true;
        }
        catch (Exception exception)
        {
            cameraExpansionReadbackPending = false;
            lastStatus = "BFP2 camera expansion readback failed: " + exception.Message;
        }
    }

    private void ProcessCameraExpansionReadback()
    {
        if (!cameraExpansionReadbackPending || !cameraExpansionReadbackRequest.done)
        {
            return;
        }

        cameraExpansionReadbackPending = false;
        if (cameraExpansionReadbackRequest.hasError)
        {
            return;
        }

        NativeArray<uint> data = cameraExpansionReadbackRequest.GetData<uint>();
        if (data.Length < 2)
        {
            return;
        }

        primaryFrustumVisibleClusters = data[0];
        unionFrustumVisibleClusters = data[1];
        unionExpansionRatio = unionFrustumVisibleClusters <= primaryFrustumVisibleClusters
            ? 1.0f
            : unionFrustumVisibleClusters / (float)Math.Max(1UL, primaryFrustumVisibleClusters);

        float groupThreshold = Mathf.Max(1.0f, unionExpansionGroupThreshold);
        float releaseThreshold = Mathf.Max(1.0f, groupThreshold * unionExpansionReleaseFraction);
        if (unionExpansionRatio > groupThreshold)
        {
            adaptiveCullGroupingRequested = true;
        }
        else if (unionExpansionRatio <= releaseThreshold)
        {
            adaptiveCullGroupingRequested = false;
        }
    }

    private bool ShouldUseAdaptiveCullGroups()
    {
        if (!enableAdaptiveCameraCullGrouping ||
            !IsGpuFrustumCullingEnabled() ||
            !usingRegisteredCameraFrames ||
            cameraFrameBuffer.Count <= 1)
        {
            adaptiveCullGroupingRequested = false;
            return false;
        }

        return adaptiveCullGroupingRequested;
    }

    private bool IsGpuFrustumCullingEnabled()
    {
        return enableFrustumCulling || (drawAllResidentPacks && forceGpuClusterFrustumCullingInFullDraw);
    }

    private bool EnsureSecondaryCullOutputs()
    {
        try
        {
            for (int groupIndex = 0; groupIndex < scheduledGroups.Count; groupIndex++)
            {
                Bfp2ScheduledGroup group = scheduledGroups[groupIndex];
                if (group.Gpu != null)
                {
                    EnsureSecondaryCullOutput(group.Gpu);
                }
            }

            for (int packIndex = 0; packIndex < packs.Count; packIndex++)
            {
                Bfp2PackInfo pack = packs[packIndex];
                if (pack.Gpu != null && pack.DrawDesired && !schedulerDrawSkipSet.Contains(pack))
                {
                    EnsureSecondaryCullOutput(pack.Gpu);
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            adaptiveCullGroupingRequested = false;
            lastStatus = "BFP2 secondary cull buffer allocation failed: " + exception.Message;
            Debug.LogWarning(lastStatus, this);
            return false;
        }
    }

    private void EnsureSecondaryCullOutput(Bfp2GpuPack gpu)
    {
        if (gpu == null || gpu.SecondaryVisibleIndexBuffer != null)
        {
            return;
        }

        try
        {
            gpu.SecondaryVisibleIndexBuffer = AcquireBuffer(GraphicsBuffer.Target.Structured, gpu.VisibleIndexCapacity, sizeof(uint));
            gpu.SecondaryDrawArgsBuffer = AcquireBuffer(
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
                4,
                sizeof(uint));
            gpu.SecondaryStatsBuffer = AcquireBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint));
            gpu.SecondaryEstimatedBytes = (long)gpu.VisibleIndexCapacity * sizeof(uint) + 8L * sizeof(uint);
            gpu.EstimatedBytes += gpu.SecondaryEstimatedBytes;
            secondaryCullGpuBytes += gpu.SecondaryEstimatedBytes;
        }
        catch
        {
            RecycleBuffer(ref gpu.SecondaryVisibleIndexBuffer, GraphicsBuffer.Target.Structured, gpu.VisibleIndexCapacity, sizeof(uint));
            RecycleBuffer(
                ref gpu.SecondaryDrawArgsBuffer,
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
                4,
                sizeof(uint));
            RecycleBuffer(ref gpu.SecondaryStatsBuffer, GraphicsBuffer.Target.Structured, 4, sizeof(uint));
            gpu.SecondaryEstimatedBytes = 0L;
            throw;
        }
    }

    private void BuildSchedulerCandidateBucket(int layerIndex, bool forceNoFlip, ShadowCastingMode shadowMode)
    {
        schedulerCandidateScratch.Clear();
        for (int i = 0; i < packs.Count; i++)
        {
            Bfp2PackInfo pack = packs[i];
            if (!IsPackResident(pack) || !pack.DrawDesired || pack.LayerIndex != layerIndex)
            {
                continue;
            }

            if (pack.Header.ClusterCount == 0 || pack.Header.IndexCount == 0)
            {
                continue;
            }

            Bfp2LayerSettings layer = layers[layerIndex];
            if (layer == null || layer.fullLayerResidency)
            {
                // Keep the always-resident baseline in its source buffers. Scheduler groups
                // would duplicate that full-city payload without changing cluster visibility.
                continue;
            }
            ShadowCastingMode packShadowMode = GetPackShadowCastingMode(pack, layer);
            if (pack.ForceNoTriangleWindingFlip != forceNoFlip || packShadowMode != shadowMode)
            {
                continue;
            }

            schedulerCandidateScratch.Add(pack);
        }
    }

    private Bfp2ScheduledGroup StartScheduledGroupBuild(
        List<Bfp2PackInfo> candidates,
        int offset,
        int count,
        int layerIndex,
        bool forceNoTriangleWindingFlip,
        ShadowCastingMode shadowCastingMode,
        string signature,
        long estimatedBytes)
    {
        Bfp2PackSnapshot[] snapshots = new Bfp2PackSnapshot[count];
        List<Bfp2PackInfo> groupPacks = new List<Bfp2PackInfo>(count);
        for (int i = 0; i < count; i++)
        {
            Bfp2PackInfo pack = candidates[offset + i];
            groupPacks.Add(pack);
            snapshots[i] = new Bfp2PackSnapshot(pack);
        }

        Bfp2ScheduledGroup group = new Bfp2ScheduledGroup
        {
            Signature = signature,
            LayerIndex = layerIndex,
            ForceNoTriangleWindingFlip = forceNoTriangleWindingFlip,
            ShadowCastingMode = shadowCastingMode,
            Packs = groupPacks,
            EstimatedBytes = estimatedBytes,
            BuildTask = Task.Run(() => BuildScheduledGroupCpu(snapshots))
        };
        scheduledGroups.Add(group);
        lastStatus = "BFP2 scheduler building group " + scheduledGroups.Count + " (" + count + " packs)";
        return group;
    }

    private void ProcessScheduledGroupUploads()
    {
        int processedStages = 0;
        long uploadedBytesThisFrame = 0L;
        long byteBudget = renderSchedulerMaxUploadBytesPerFrame <= 0 ? long.MaxValue : renderSchedulerMaxUploadBytesPerFrame;
        for (int i = 0; i < scheduledGroups.Count &&
                        processedStages < Mathf.Max(1, renderSchedulerMaxUploadStagesPerFrame) &&
                        uploadedBytesThisFrame < byteBudget; i++)
        {
            Bfp2ScheduledGroup group = scheduledGroups[i];
            if (group.Gpu != null || group.PendingUploadCpu == null)
            {
                continue;
            }

            long remainingBudget = byteBudget == long.MaxValue ? long.MaxValue : Math.Max(1L, byteBudget - uploadedBytesThisFrame);
            if (ProcessPendingScheduledGroupUploadStage(group, remainingBudget, out long uploadedBytes))
            {
                group.Gpu = group.PendingUploadGpu;
                group.PendingUploadGpu = null;
                group.PendingUploadCpu = null;
                group.UploadStage = Bfp2UploadStage.None;
                group.PendingVertexUploadWordOffset = 0;
                group.PendingIndexUploadOffset = 0;
                group.PendingPackedIndex16UploadOffset = 0;
                group.PendingClusterIndexBase16UploadOffset = 0;
                group.PendingClusterUploadWordOffset = 0;
                lastStatus = "BFP2 scheduler ready " + group.Packs.Count + " packs";
            }

            uploadedBytesThisFrame += Math.Max(0L, uploadedBytes);
            processedStages++;
        }
    }

    private bool ProcessPendingScheduledGroupUploadStage(Bfp2ScheduledGroup group, long byteBudget, out long uploadedBytes)
    {
        uploadedBytes = 0L;
        Bfp2CpuPack cpu = group.PendingUploadCpu;
        if (cpu == null)
        {
            return true;
        }

        switch (group.UploadStage)
        {
            case Bfp2UploadStage.None:
            case Bfp2UploadStage.AllocateBuffers:
                group.PendingUploadGpu = AllocateGpuPackBuffers(cpu);
                group.UploadStage = Bfp2UploadStage.UploadVertices;
                return false;

            case Bfp2UploadStage.UploadVertices:
                if (UploadBufferDataChunked(
                        group.PendingUploadGpu.VertexWordsBuffer,
                        cpu.VertexWords,
                        ref group.PendingVertexUploadWordOffset,
                        byteBudget,
                        out uploadedBytes))
                {
                    group.UploadStage = Bfp2UploadStage.UploadIndices;
                }
                return false;

            case Bfp2UploadStage.UploadIndices:
                if (UploadBufferDataChunked(
                        group.PendingUploadGpu.IndexBuffer,
                        cpu.Indices,
                        ref group.PendingIndexUploadOffset,
                        byteBudget,
                        out uploadedBytes))
                {
                    group.UploadStage = Bfp2UploadStage.UploadPackedIndices16;
                }
                return false;

            case Bfp2UploadStage.UploadPackedIndices16:
                if (UploadBufferDataChunked(
                        group.PendingUploadGpu.PackedIndex16Buffer,
                        cpu.PackedIndices16,
                        ref group.PendingPackedIndex16UploadOffset,
                        byteBudget,
                        out uploadedBytes))
                {
                    group.UploadStage = Bfp2UploadStage.UploadClusterIndexBases16;
                }
                return false;

            case Bfp2UploadStage.UploadClusterIndexBases16:
                if (UploadBufferDataChunked(
                        group.PendingUploadGpu.ClusterIndexBases16Buffer,
                        cpu.ClusterIndexBases16,
                        ref group.PendingClusterIndexBase16UploadOffset,
                        byteBudget,
                        out uploadedBytes))
                {
                    group.UploadStage = Bfp2UploadStage.UploadClusters;
                }
                return false;

            case Bfp2UploadStage.UploadClusters:
                if (UploadBufferDataChunked(
                        group.PendingUploadGpu.ClusterWordsBuffer,
                        cpu.ClusterWords,
                        ref group.PendingClusterUploadWordOffset,
                        byteBudget,
                        out uploadedBytes))
                {
                    group.UploadStage = Bfp2UploadStage.InitArgsAndStats;
                }
                return false;

            case Bfp2UploadStage.InitArgsAndStats:
                InitializeCompactClusterData(
                    group.PendingUploadGpu,
                    cpu.ClusterWords.Length / 20);
                UploadBufferData(group.PendingUploadGpu.DrawArgsBuffer, InitialDrawArgsData);
                UploadBufferData(group.PendingUploadGpu.StatsBuffer, InitialStatsData);
                group.PendingUploadGpu.ClusterLocalIndex16Ready =
                    cpu.ClusterLocalIndex16Ready &&
                    group.PendingUploadGpu.PackedIndex16Buffer != null &&
                    group.PendingUploadGpu.ClusterIndexBases16Buffer != null;
                uploadedBytes = (InitialDrawArgsData.Length + InitialStatsData.Length) * (long)sizeof(uint);
                return true;

            default:
                throw new InvalidOperationException("Unsupported BFP2 scheduler upload stage " + group.UploadStage);
        }
    }

    private void MarkReadyScheduledGroups()
    {
        for (int i = 0; i < scheduledGroups.Count; i++)
        {
            Bfp2ScheduledGroup group = scheduledGroups[i];
            if (group.Gpu == null || group.Packs == null)
            {
                continue;
            }

            if (group.LayerIndex < 0 || group.LayerIndex >= layers.Length)
            {
                continue;
            }

            bool allReady = true;
            for (int j = 0; j < group.Packs.Count; j++)
            {
                Bfp2PackInfo pack = group.Packs[j];
                if (!IsPackResident(pack) || !pack.DrawDesired)
                {
                    allReady = false;
                    break;
                }
            }

            if (!allReady)
            {
                continue;
            }

            for (int j = 0; j < group.Packs.Count; j++)
            {
                schedulerDrawSkipSet.Add(group.Packs[j]);
            }

            schedulerPilotGroupCount++;
            schedulerPilotMergedPackCount += group.Packs.Count;
        }
    }

    private void ReleaseUndesiredScheduledGroups()
    {
        for (int i = scheduledGroups.Count - 1; i >= 0; i--)
        {
            Bfp2ScheduledGroup group = scheduledGroups[i];
            if (group == null || string.IsNullOrEmpty(group.Signature) || !schedulerDesiredSignatures.Contains(group.Signature))
            {
                group?.Release(this);
                scheduledGroups.RemoveAt(i);
            }
        }
    }

    private Bfp2ScheduledGroup FindScheduledGroup(string signature)
    {
        for (int i = 0; i < scheduledGroups.Count; i++)
        {
            Bfp2ScheduledGroup group = scheduledGroups[i];
            if (group != null && group.Signature == signature)
            {
                return group;
            }
        }

        return null;
    }

    private long EstimateScheduledGroupGpuBytes()
    {
        long bytes = 0L;
        for (int i = 0; i < scheduledGroups.Count; i++)
        {
            bytes += Math.Max(0L, scheduledGroups[i].EstimatedBytes);
        }
        return bytes;
    }

    private static long EstimateScheduledGroupGpuBytes(List<Bfp2PackInfo> candidates, int offset, int count)
    {
        long bytes = 0L;
        for (int i = 0; i < count; i++)
        {
            bytes += EstimateGpuBytes(candidates[offset + i].Header);
        }
        return bytes;
    }

    private void UpdateRenderSchedulerPilot(Camera drawCamera)
    {
        schedulerDrawSkipSet.Clear();
        schedulerPilotMergedPackCount = 0;
        schedulerPilotGroupCount = 0;

        if (!enableRenderSchedulerPilot || drawCamera == null || layers == null || layers.Length == 0)
        {
            ReleaseScheduledGroups();
            return;
        }

        CompleteScheduledGroupBuilds();
        Bfp2ScheduledGroup group = EnsureSchedulerPilotGroup();
        if (group == null || group.Gpu == null)
        {
            return;
        }

        if (group.LayerIndex < 0 || group.LayerIndex >= layers.Length)
        {
            ReleaseScheduledGroups();
            return;
        }

        for (int i = 0; i < group.Packs.Count; i++)
        {
            Bfp2PackInfo pack = group.Packs[i];
            if (!IsPackResident(pack) || !pack.DrawDesired)
            {
                ReleaseScheduledGroups();
                return;
            }
        }

        for (int i = 0; i < group.Packs.Count; i++)
        {
            schedulerDrawSkipSet.Add(group.Packs[i]);
        }

        schedulerPilotGroupCount = 1;
        schedulerPilotMergedPackCount = group.Packs.Count;
    }

    private Bfp2ScheduledGroup EnsureSchedulerPilotGroup()
    {
        schedulerCandidateScratch.Clear();
        int selectedLayer = -1;
        bool selectedForceNoFlip = false;
        ShadowCastingMode selectedShadowMode = ShadowCastingMode.Off;

        for (int i = 0; i < packs.Count; i++)
        {
            Bfp2PackInfo pack = packs[i];
            if (!IsPackResident(pack) || !pack.DrawDesired || pack.LayerIndex < 0 || pack.LayerIndex >= layers.Length)
            {
                continue;
            }

            Bfp2LayerSettings layer = layers[pack.LayerIndex];
            if (layer == null || !layer.enabled || layer.fullLayerResidency)
            {
                continue;
            }

            if (schedulerPilotBuildingsOnly && !IsBuildingLayer(layer))
            {
                continue;
            }

            ShadowCastingMode shadowMode = GetPackShadowCastingMode(pack, layer);
            if (schedulerPilotExcludeShadowCasters && shadowMode != ShadowCastingMode.Off)
            {
                continue;
            }

            if (selectedLayer < 0)
            {
                selectedLayer = pack.LayerIndex;
                selectedForceNoFlip = pack.ForceNoTriangleWindingFlip;
                selectedShadowMode = shadowMode;
            }

            if (pack.LayerIndex != selectedLayer ||
                pack.ForceNoTriangleWindingFlip != selectedForceNoFlip ||
                shadowMode != selectedShadowMode)
            {
                continue;
            }

            schedulerCandidateScratch.Add(pack);
        }

        int packLimit = Mathf.Clamp(schedulerPilotMaxPacks, 2, 8);
        if (schedulerCandidateScratch.Count < 2)
        {
            ReleaseScheduledGroups();
            return null;
        }

        schedulerCandidateScratch.Sort(ComparePackDistance);
        int selectedCount = Mathf.Min(packLimit, schedulerCandidateScratch.Count);
        string signature = BuildScheduledGroupSignature(schedulerCandidateScratch, selectedCount, selectedLayer, selectedForceNoFlip, selectedShadowMode);
        if (scheduledGroups.Count == 1 && scheduledGroups[0].Signature == signature)
        {
            return scheduledGroups[0];
        }

        if (scheduledGroups.Count == 1 && scheduledGroups[0].BuildTask != null)
        {
            return scheduledGroups[0];
        }

        ReleaseScheduledGroups();
        Bfp2PackSnapshot[] snapshots = new Bfp2PackSnapshot[selectedCount];
        List<Bfp2PackInfo> groupPacks = new List<Bfp2PackInfo>(selectedCount);
        for (int i = 0; i < selectedCount; i++)
        {
            Bfp2PackInfo pack = schedulerCandidateScratch[i];
            groupPacks.Add(pack);
            snapshots[i] = new Bfp2PackSnapshot(pack);
        }

        Bfp2ScheduledGroup group = new Bfp2ScheduledGroup
        {
            Signature = signature,
            LayerIndex = selectedLayer,
            ForceNoTriangleWindingFlip = selectedForceNoFlip,
            ShadowCastingMode = selectedShadowMode,
            Packs = groupPacks,
            BuildTask = Task.Run(() => BuildScheduledGroupCpu(snapshots))
        };
        scheduledGroups.Add(group);
        lastStatus = "BFP2 scheduler building " + selectedCount + " packs";
        return group;
    }

    private void CompleteScheduledGroupBuilds()
    {
        for (int i = scheduledGroups.Count - 1; i >= 0; i--)
        {
            Bfp2ScheduledGroup group = scheduledGroups[i];
            Task<Bfp2ScheduledBuildResult> task = group.BuildTask;
            if (task == null || !task.IsCompleted)
            {
                continue;
            }

            group.BuildTask = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                string message = task.Exception != null ? task.Exception.GetBaseException().Message : "canceled";
                lastStatus = "BFP2 scheduler build failed: " + message;
                Debug.LogWarning(lastStatus, this);
                group.Release(this);
                scheduledGroups.RemoveAt(i);
                continue;
            }

            try
            {
                Bfp2ScheduledBuildResult result = task.Result;
                group.Header = result.Header;
                group.Bounds = result.Bounds;
                group.Name = result.Name;
                group.PendingUploadCpu = result.Cpu;
                group.PendingUploadGpu = null;
                group.UploadStage = Bfp2UploadStage.AllocateBuffers;
                group.PendingVertexUploadWordOffset = 0;
                group.PendingIndexUploadOffset = 0;
                group.PendingPackedIndex16UploadOffset = 0;
                group.PendingClusterIndexBase16UploadOffset = 0;
                group.PendingClusterUploadWordOffset = 0;
                lastStatus = "BFP2 scheduler staged " + group.Packs.Count + " packs";
            }
            catch (Exception exception)
            {
                lastStatus = "BFP2 scheduler upload failed: " + exception.Message;
                Debug.LogWarning(lastStatus, this);
                group.Release(this);
                scheduledGroups.RemoveAt(i);
            }
        }
    }

    private void DrawScheduledGroups(
        Camera drawCamera,
        int cullOutputIndex,
        int targetFrameStart,
        int targetFrameCount,
        Vector3 cameraPositionOS)
    {
        for (int i = 0; i < scheduledGroups.Count; i++)
        {
            Bfp2ScheduledGroup group = scheduledGroups[i];
            if (group.Gpu == null || group.LayerIndex < 0 || group.LayerIndex >= layers.Length)
            {
                continue;
            }

            Bfp2LayerSettings layer = layers[group.LayerIndex];
            if (layer == null)
            {
                continue;
            }

            DispatchCull(group.Gpu, group.Header, group.ForceNoTriangleWindingFlip, cameraPositionOS, cullOutputIndex);

            propertyBlock.Clear();
            BindDrawGeometry(
                group.Gpu,
                cullOutputIndex,
                group.ForceNoTriangleWindingFlip);
            propertyBlock.SetVector(QuantOriginId, group.Header.QuantOrigin);
            propertyBlock.SetVector(QuantScaleId, group.Header.QuantScale);
            propertyBlock.SetColor(LayerTintId, layer.tint);
            propertyBlock.SetFloat(LayerIndexId, group.LayerIndex);
            if (facadeTextureArray != null)
            {
                propertyBlock.SetTexture(FacadeArrayId, facadeTextureArray);
            }

            Bounds worldBounds = TransformBounds(group.Bounds, transform.localToWorldMatrix);
            SubmitProceduralDraw(
                worldBounds,
                group.Gpu.GetDrawArgsBuffer(cullOutputIndex),
                drawCamera,
                propertyBlock,
                group.ShadowCastingMode,
                targetFrameStart,
                targetFrameCount);
        }
    }

    private void ReleaseScheduledGroups()
    {
        for (int i = 0; i < scheduledGroups.Count; i++)
        {
            scheduledGroups[i].Release(this);
        }
        scheduledGroups.Clear();
        schedulerDrawSkipSet.Clear();
        schedulerPilotMergedPackCount = 0;
        schedulerPilotGroupCount = 0;
    }

    private static string BuildScheduledGroupSignature(
        List<Bfp2PackInfo> candidates,
        int selectedCount,
        int layerIndex,
        bool forceNoTriangleWindingFlip,
        ShadowCastingMode shadowCastingMode)
    {
        return BuildScheduledGroupSignature(candidates, 0, selectedCount, layerIndex, forceNoTriangleWindingFlip, shadowCastingMode);
    }

    private static string BuildScheduledGroupSignature(
        List<Bfp2PackInfo> candidates,
        int offset,
        int selectedCount,
        int layerIndex,
        bool forceNoTriangleWindingFlip,
        ShadowCastingMode shadowCastingMode)
    {
        string signature = layerIndex.ToString() + ":" + forceNoTriangleWindingFlip + ":" + shadowCastingMode;
        for (int i = 0; i < selectedCount; i++)
        {
            signature += "|" + candidates[offset + i].Path;
        }
        return signature;
    }

    private ShadowCastingMode GetPackShadowCastingMode(Bfp2PackInfo pack, Bfp2LayerSettings layer)
    {
        if (pack == null || layer == null || !layer.castShadows)
        {
            return ShadowCastingMode.Off;
        }

        if (enableNearRealShadows)
        {
            return nearShadowPacks.Contains(pack) ? nearShadowCastingMode : ShadowCastingMode.Off;
        }

        return shadowCastingMode;
    }

    private void RebuildNearShadowPackSet(Camera drawCamera)
    {
        nearShadowCandidates.Clear();
        nearShadowPacks.Clear();
        nearShadowPackCount = 0;
        nearShadowCandidateCount = 0;
        nearShadowTriangleCount = 0;

        if (!enableNearRealShadows || !showBfp2 || drawCamera == null || packs.Count == 0 || layers == null)
        {
            ClearNearShadowShaderGlobals();
            return;
        }

        Vector3 cameraPositionOS = transform.InverseTransformPoint(drawCamera.transform.position);
        float radius = Mathf.Max(1.0f, nearShadowRadiusMeters);
        for (int i = 0; i < packs.Count; i++)
        {
            Bfp2PackInfo pack = packs[i];
            if (!IsPackResident(pack) || !pack.DrawDesired || pack.LayerIndex < 0 || pack.LayerIndex >= layers.Length)
            {
                continue;
            }

            Bfp2LayerSettings layer = layers[pack.LayerIndex];
            if (layer == null || !layer.enabled || !layer.castShadows)
            {
                continue;
            }

            if (nearShadowBuildingLayersOnly && !IsBuildingLayer(layer))
            {
                continue;
            }

            if (gpuDrivenMegaLayerMode && IsMegaPackPath(pack.Path))
            {
                continue;
            }

            float distance = DistanceToBounds(pack.Bounds, cameraPositionOS);
            if (distance > radius)
            {
                continue;
            }

            nearShadowCandidates.Add(new NearShadowPackCandidate(pack, distance));
        }

        nearShadowCandidates.Sort(CompareNearShadowCandidateDistance);
        nearShadowCandidateCount = nearShadowCandidates.Count;

        int maxPacks = Mathf.Max(1, maxNearShadowPacks);
        long triangleBudget = Math.Max(1000, maxNearShadowTriangles);
        for (int i = 0; i < nearShadowCandidates.Count; i++)
        {
            if (nearShadowPackCount >= maxPacks)
            {
                break;
            }

            Bfp2PackInfo pack = nearShadowCandidates[i].pack;
            long packTriangles = Math.Max(0, pack.Header.TriangleCount);
            if (nearShadowPackCount > 0 && nearShadowTriangleCount + packTriangles > triangleBudget)
            {
                continue;
            }

            nearShadowPacks.Add(pack);
            nearShadowPackCount++;
            nearShadowTriangleCount += packTriangles;
        }

        if (nearShadowPackCount > 0)
        {
            Shader.SetGlobalFloat("_NYCGIS_NearRealShadowEnabled", 1.0f);
            Shader.SetGlobalFloat(
                "_NYCGIS_NearRealShadowStrength",
                Mathf.Max(Shader.GetGlobalFloat("_NYCGIS_NearRealShadowStrength"), Mathf.Clamp01(nearRealShadowStrength)));
        }
        else
        {
            ClearNearShadowShaderGlobals();
        }
    }

    private void RebuildNearShadowPackSetIfNeeded(Camera drawCamera)
    {
        if (!enableNearRealShadows || !showBfp2)
        {
            if (nearShadowPackCount > 0 || nearShadowCandidateCount > 0 || nearShadowPacks.Count > 0)
            {
                ClearNearShadowState();
            }
            return;
        }

        bool cameraDirty = nearShadowDirty || !nearShadowCameraGate.HasSample || nearShadowCameraGate.ShouldRefresh(
            drawCamera,
            Mathf.Max(10.0f, cameraMoveRefreshMeters),
            180.0f,
            cameraAltitudeRefreshTierMeters,
            includePixelRect: false,
            force: nearShadowDirty);
        if (!cameraDirty)
        {
            return;
        }

        nearShadowDirty = false;
        RebuildNearShadowPackSet(drawCamera);
    }

    private void ClearNearShadowState()
    {
        nearShadowCandidates.Clear();
        nearShadowPacks.Clear();
        nearShadowPackCount = 0;
        nearShadowCandidateCount = 0;
        nearShadowTriangleCount = 0;
        nearShadowCameraGate.Reset();
        nearShadowDirty = true;
        ClearNearShadowShaderGlobals();
    }

    private static void ClearNearShadowShaderGlobals()
    {
        Shader.SetGlobalFloat("_NYCGIS_NearRealShadowEnabled", 0.0f);
        Shader.SetGlobalFloat("_NYCGIS_NearRealShadowStrength", 0.0f);
    }

    private void ApplyGlobalMaterialConstants()
    {
        int textureCount = facadeTextureArray != null ? Mathf.Max(1, facadeTextureArray.depth) : 0;
        bool facadeEnabled = useFacadeTextureArray && facadeTextureArray != null && textureCount > 0;

        Shader.SetGlobalFloat(GlobalAmbientLiftId, skyAmbientStrength);
        Shader.SetGlobalFloat(GlobalDiffuseBoostId, sunDirectStrength);
        Shader.SetGlobalFloat(GlobalFacadeEnabledId, facadeEnabled ? 1.0f : 0.0f);
        Shader.SetGlobalFloat(GlobalFacadeStrengthId, Mathf.Clamp01(facadeStrength));
        Shader.SetGlobalFloat(GlobalFacadeWidthId, Mathf.Max(2.0f, facadeWidthMeters));
        Shader.SetGlobalFloat(GlobalFacadeHeightId, Mathf.Max(2.0f, facadeHeightMeters));
        Shader.SetGlobalFloat(GlobalFacadeCellSizeId, Mathf.Max(4.0f, facadeRandomCellSizeMeters));
        Shader.SetGlobalFloat(GlobalFacadeTextureCountId, textureCount);
        Shader.SetGlobalFloat(GlobalFacadeLayerIndexId, facadeBuildingLayerIndex);
        Shader.SetGlobalFloat(GlobalRoofOrthophotoEnabledId, useRoofOrthophoto ? 1.0f : 0.0f);
        Shader.SetGlobalFloat(GlobalRoofOrthophotoStrengthId, Mathf.Clamp01(roofOrthophotoStrength));

        if (facadeEnabled)
        {
            Shader.SetGlobalTexture(FacadeArrayId, facadeTextureArray);
        }
    }

    private void UploadPack(Bfp2PackInfo pack, Bfp2CpuPack cpu)
    {
        pack.Gpu = CreateGpuPackFromCpu(cpu);
    }

    private Bfp2GpuPack CreateGpuPackFromCpu(Bfp2CpuPack cpu)
    {
        Bfp2GpuPack gpu = AllocateGpuPackBuffers(cpu);

        UploadBufferData(gpu.VertexWordsBuffer, cpu.VertexWords);
        UploadBufferData(gpu.IndexBuffer, cpu.Indices);
        UploadBufferData(gpu.PackedIndex16Buffer, cpu.PackedIndices16);
        UploadBufferData(gpu.ClusterIndexBases16Buffer, cpu.ClusterIndexBases16);
        UploadBufferData(gpu.ClusterWordsBuffer, cpu.ClusterWords);
        InitializeCompactClusterData(gpu, gpu.ClusterWordsCount / 20);
        UploadBufferData(gpu.DrawArgsBuffer, InitialDrawArgsData);
        UploadBufferData(gpu.StatsBuffer, InitialStatsData);
        gpu.ClusterLocalIndex16Ready =
            cpu.ClusterLocalIndex16Ready &&
            gpu.PackedIndex16Buffer != null &&
            gpu.ClusterIndexBases16Buffer != null;

        gpu.EstimatedBytes = EstimateUploadBytes(cpu);

        return gpu;
    }

    private Bfp2GpuPack AllocateGpuPackBuffers(Bfp2CpuPack cpu)
    {
        if (cpu == null)
        {
            throw new ArgumentNullException(nameof(cpu));
        }

        Bfp2GpuPack gpu = new Bfp2GpuPack();
        gpu.VertexWordsCount = Mathf.Max(1, cpu.VertexWords != null ? cpu.VertexWords.Length : 0);
        gpu.IndexCount = Mathf.Max(1, cpu.Indices != null ? cpu.Indices.Length : 0);
        gpu.PackedIndex16WordCount =
            cpu.ClusterLocalIndex16Ready && cpu.PackedIndices16 != null
                ? Mathf.Max(1, cpu.PackedIndices16.Length)
                : 0;
        gpu.ClusterIndexBaseCount =
            cpu.ClusterLocalIndex16Ready && cpu.ClusterIndexBases16 != null
                ? Mathf.Max(1, cpu.ClusterIndexBases16.Length)
                : 0;
        gpu.MaximumClusterIndexSpan16 = cpu.MaximumClusterIndexSpan16;
        gpu.ClusterWordsCount = Mathf.Max(1, cpu.ClusterWords != null ? cpu.ClusterWords.Length : 0);
        int clusterCount = gpu.ClusterWordsCount / 20;
        gpu.CompactClusterWordsCount =
            Mathf.Max(1, clusterCount * CompactClusterWordsPerCluster);
        gpu.VertexWordsBuffer = AcquireBuffer(GraphicsBuffer.Target.Structured, gpu.VertexWordsCount, sizeof(uint));
        gpu.IndexBuffer = AcquireBuffer(GraphicsBuffer.Target.Structured, gpu.IndexCount, sizeof(uint));
        if (gpu.PackedIndex16WordCount > 0 && gpu.ClusterIndexBaseCount > 0)
        {
            gpu.PackedIndex16Buffer =
                AcquireBuffer(
                    GraphicsBuffer.Target.Structured,
                    gpu.PackedIndex16WordCount,
                    sizeof(uint));
            gpu.ClusterIndexBases16Buffer =
                AcquireBuffer(
                    GraphicsBuffer.Target.Structured,
                    gpu.ClusterIndexBaseCount,
                    sizeof(uint));
        }
        gpu.ClusterWordsBuffer = AcquireBuffer(GraphicsBuffer.Target.Structured, gpu.ClusterWordsCount, sizeof(uint));
        gpu.CompactClusterWordsBuffer =
            AcquireBuffer(GraphicsBuffer.Target.Structured, gpu.CompactClusterWordsCount, sizeof(uint));
        gpu.VisibleIndexCapacity = gpu.IndexCount;
        gpu.VisibleIndexBuffer = AcquireBuffer(GraphicsBuffer.Target.Structured, gpu.VisibleIndexCapacity, sizeof(uint));
        gpu.DrawArgsBuffer = AcquireBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, 4, sizeof(uint));
        gpu.StatsBuffer = AcquireBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint));
        gpu.EstimatedBytes = EstimateUploadBytes(cpu);
        return gpu;
    }

    private bool ProcessPendingUploadStage(Bfp2PackInfo pack, long byteBudget, out long uploadedBytes)
    {
        uploadedBytes = 0L;
        if (pack == null || pack.PendingUploadCpu == null)
        {
            return true;
        }

        Bfp2CpuPack cpu = pack.PendingUploadCpu;
        switch (pack.UploadStage)
        {
            case Bfp2UploadStage.None:
            case Bfp2UploadStage.AllocateBuffers:
                pack.PendingUploadGpu = AllocateGpuPackBuffers(cpu);
                pack.UploadStage = Bfp2UploadStage.UploadVertices;
                return false;

            case Bfp2UploadStage.UploadVertices:
                if (UploadBufferDataChunked(
                    pack.PendingUploadGpu.VertexWordsBuffer,
                    cpu.VertexWords,
                    ref pack.PendingVertexUploadWordOffset,
                    byteBudget,
                    out uploadedBytes))
                {
                    pack.UploadStage = Bfp2UploadStage.UploadIndices;
                }
                return false;

            case Bfp2UploadStage.UploadIndices:
                if (UploadBufferDataChunked(
                    pack.PendingUploadGpu.IndexBuffer,
                    cpu.Indices,
                    ref pack.PendingIndexUploadOffset,
                    byteBudget,
                    out uploadedBytes))
                {
                    pack.UploadStage = Bfp2UploadStage.UploadPackedIndices16;
                }
                return false;

            case Bfp2UploadStage.UploadPackedIndices16:
                if (UploadBufferDataChunked(
                    pack.PendingUploadGpu.PackedIndex16Buffer,
                    cpu.PackedIndices16,
                    ref pack.PendingPackedIndex16UploadOffset,
                    byteBudget,
                    out uploadedBytes))
                {
                    pack.UploadStage = Bfp2UploadStage.UploadClusterIndexBases16;
                }
                return false;

            case Bfp2UploadStage.UploadClusterIndexBases16:
                if (UploadBufferDataChunked(
                    pack.PendingUploadGpu.ClusterIndexBases16Buffer,
                    cpu.ClusterIndexBases16,
                    ref pack.PendingClusterIndexBase16UploadOffset,
                    byteBudget,
                    out uploadedBytes))
                {
                    pack.UploadStage = Bfp2UploadStage.UploadClusters;
                }
                return false;

            case Bfp2UploadStage.UploadClusters:
                if (UploadBufferDataChunked(
                    pack.PendingUploadGpu.ClusterWordsBuffer,
                    cpu.ClusterWords,
                    ref pack.PendingClusterUploadWordOffset,
                    byteBudget,
                    out uploadedBytes))
                {
                    pack.UploadStage = Bfp2UploadStage.InitArgsAndStats;
                }
                return false;

            case Bfp2UploadStage.InitArgsAndStats:
                InitializeCompactClusterData(
                    pack.PendingUploadGpu,
                    cpu.ClusterWords.Length / 20);
                UploadBufferData(pack.PendingUploadGpu.DrawArgsBuffer, InitialDrawArgsData);
                UploadBufferData(pack.PendingUploadGpu.StatsBuffer, InitialStatsData);
                pack.PendingUploadGpu.ClusterLocalIndex16Ready =
                    cpu.ClusterLocalIndex16Ready &&
                    pack.PendingUploadGpu.PackedIndex16Buffer != null &&
                    pack.PendingUploadGpu.ClusterIndexBases16Buffer != null;
                uploadedBytes = (InitialDrawArgsData.Length + InitialStatsData.Length) * (long)sizeof(uint);
                pack.Gpu = pack.PendingUploadGpu;
                pack.PendingUploadGpu = null;
                ClearPendingUpload(pack, releasePendingGpu: false);
                return true;

            default:
                throw new InvalidOperationException("Unsupported BFP2 upload stage " + pack.UploadStage);
        }
    }

    private void UploadBufferData(GraphicsBuffer buffer, uint[] data)
    {
        if (buffer == null || data == null || data.Length == 0)
        {
            return;
        }

        if (useBeginWriteUpload && !beginWriteUploadUnavailable)
        {
            if (TryUploadBufferDataWithBeginWrite(buffer, data))
            {
                return;
            }
        }

        buffer.SetData(data);
    }

    private bool UploadBufferDataChunked(GraphicsBuffer buffer, uint[] data, ref int wordOffset, long byteBudget, out long uploadedBytes)
    {
        uploadedBytes = 0L;
        if (buffer == null || data == null || data.Length == 0)
        {
            wordOffset = 0;
            return true;
        }

        if (wordOffset < 0 || wordOffset > data.Length)
        {
            wordOffset = 0;
        }

        int remainingWords = data.Length - wordOffset;
        if (remainingWords <= 0)
        {
            return true;
        }

        long stageByteLimit = maxUploadStageBytes > 0 ? maxUploadStageBytes : long.MaxValue;
        long effectiveByteBudget = byteBudget > 0 && byteBudget != long.MaxValue
            ? Math.Min(byteBudget, stageByteLimit)
            : stageByteLimit;
        int wordBudget = effectiveByteBudget == long.MaxValue
            ? remainingWords
            : Mathf.Max(1, (int)Math.Min(remainingWords, effectiveByteBudget / sizeof(uint)));
        int wordCount = Mathf.Min(remainingWords, wordBudget);
        buffer.SetData(data, wordOffset, wordOffset, wordCount);
        wordOffset += wordCount;
        uploadedBytes = (long)wordCount * sizeof(uint);
        return wordOffset >= data.Length;
    }

    private bool TryUploadBufferDataWithBeginWrite(GraphicsBuffer buffer, uint[] data)
    {
        try
        {
            System.Reflection.MethodInfo beginWrite = null;
            System.Reflection.MethodInfo endWrite = null;
            System.Reflection.MethodInfo[] methods = typeof(GraphicsBuffer).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                System.Reflection.MethodInfo method = methods[i];
                if (method.Name == "BeginWrite" && method.IsGenericMethodDefinition)
                {
                    System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(int))
                    {
                        beginWrite = method.MakeGenericMethod(typeof(uint));
                    }
                }
                else if (method.Name == "EndWrite" && method.IsGenericMethodDefinition)
                {
                    System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                    {
                        endWrite = method.MakeGenericMethod(typeof(uint));
                    }
                }
            }

            if (beginWrite == null || endWrite == null)
            {
                beginWriteUploadUnavailable = true;
                return false;
            }

            object targetObject = beginWrite.Invoke(buffer, new object[] { 0, data.Length });
            NativeArray<uint> target = (NativeArray<uint>)targetObject;
            target.CopyFrom(data);
            endWrite.Invoke(buffer, new object[] { data.Length });
            return true;
        }
        catch (Exception ex)
        {
            beginWriteUploadUnavailable = true;
            Debug.LogWarning("BFP2 BeginWrite upload unavailable, falling back to SetData: " + ex.Message, this);
            return false;
        }
    }

    private static long EstimateUploadBytes(Bfp2CpuPack cpu)
    {
        if (cpu == null)
        {
            return 0L;
        }

        int clusterWordCount = cpu.ClusterWords != null ? cpu.ClusterWords.Length : 0;
        long compactClusterBytes =
            (long)(clusterWordCount / 20) * CompactClusterWordsPerCluster * sizeof(uint);
        long packedIndex16Bytes =
            (long)(cpu.PackedIndices16 != null ? cpu.PackedIndices16.Length : 0) * sizeof(uint);
        long clusterIndexBase16Bytes =
            (long)(cpu.ClusterIndexBases16 != null ? cpu.ClusterIndexBases16.Length : 0) * sizeof(uint);
        if (cpu.EstimatedUploadBytes > 0)
        {
            return cpu.EstimatedUploadBytes +
                   compactClusterBytes +
                   packedIndex16Bytes +
                   clusterIndexBase16Bytes;
        }

        int indexCount = cpu.Indices != null ? cpu.Indices.Length : 0;
        return (long)(cpu.VertexWords != null ? cpu.VertexWords.Length : 0) * sizeof(uint)
               + (long)indexCount * sizeof(uint)
               + (long)clusterWordCount * sizeof(uint)
               + compactClusterBytes
               + packedIndex16Bytes
               + clusterIndexBase16Bytes
               + (long)indexCount * sizeof(uint)
               + 32L;
    }

    private void ApplyProductionDebugGuards()
    {
        if (!productionMode)
        {
            return;
        }

        showDebugPanel = false;
        enableDebugReadback = false;
        drawClusterBounds = false;
        enableDetailedRuntimeStats = false;
    }

    private bool IsDebugReadbackActive()
    {
        return !productionMode && enableDebugReadback;
    }

    private bool IsDetailedStatsActive()
    {
        return !productionMode && (enableDetailedRuntimeStats || enableDebugReadback || showDebugPanel);
    }

    private void MaybeRebuildSummaryStats()
    {
        if (Time.realtimeSinceStartup < nextSummaryStatsTime && !IsDetailedStatsActive())
        {
            return;
        }

        nextSummaryStatsTime = Time.realtimeSinceStartup + summaryStatsIntervalSeconds;
        RebuildSummaryStats(false, IsDetailedStatsActive());
    }

    private void RebuildSummaryStats(bool readback, bool detailedStats)
    {
        if (readback && IsDebugReadbackActive())
        {
            QueueStatsReadbacks();
        }

        if (detailedStats || IsDebugReadbackActive())
        {
            ProcessStatsReadbacks();
        }

        residentPackCount = 0;
        loadingPackCount = 0;
        residentGpuBytes = 0;
        pendingCpuBytes = 0;
        stagedUploadBytes = 0;
        stagedUploadPackCount = 0;
        totalResidentClusters = 0;
        visibleClusters = 0;
        visibleIndices = 0;
        visibleTriangles = 0;
        culledClusters = 0;
        overflowClusters = 0;

        foreach (Bfp2PackInfo pack in packs)
        {
            if (pack.LoadTask != null)
            {
                loadingPackCount++;
                pendingCpuBytes += pack.FileBytes;
            }

            if (pack.PendingUploadCpu != null)
            {
                loadingPackCount++;
                pendingCpuBytes += Math.Max(0L, pack.PendingUploadBytes);
                stagedUploadBytes += Math.Max(0L, pack.PendingUploadBytes);
                stagedUploadPackCount++;
            }

            if (pack.Gpu == null)
            {
                continue;
            }

            residentPackCount++;
            residentGpuBytes += pack.Gpu.EstimatedBytes;
            totalResidentClusters += (ulong)pack.Header.ClusterCount;

            if (detailedStats)
            {
                visibleClusters += pack.LastVisibleClusters;
                visibleIndices += pack.LastVisibleIndices;
                visibleTriangles += pack.LastVisibleIndices / 3u;
                culledClusters += pack.LastCulledClusters;
                overflowClusters += pack.LastOverflowClusters;
            }
        }

        if (residentPackCount > 0 || loadingPackCount > 0)
        {
            lastStatus = "BFP2 resident " + residentPackCount + ", loading " + loadingPackCount;
        }
    }

    private void QueueStatsReadbacks()
    {
        foreach (Bfp2PackInfo pack in packs)
        {
            Bfp2GpuPack gpu = pack.Gpu;
            if (gpu == null || gpu.StatsBuffer == null || gpu.StatsReadbackPending)
            {
                continue;
            }

            try
            {
                gpu.StatsReadbackRequest = AsyncGPUReadback.Request(gpu.StatsBuffer);
                gpu.StatsReadbackPending = true;
            }
            catch (Exception ex)
            {
                gpu.StatsReadbackPending = false;
                lastStatus = "BFP2 async stats readback failed: " + ex.Message;
            }
        }
    }

    private void ProcessStatsReadbacks()
    {
        foreach (Bfp2PackInfo pack in packs)
        {
            Bfp2GpuPack gpu = pack.Gpu;
            if (gpu == null || !gpu.StatsReadbackPending || !gpu.StatsReadbackRequest.done)
            {
                continue;
            }

            gpu.StatsReadbackPending = false;
            if (gpu.StatsReadbackRequest.hasError)
            {
                continue;
            }

            Unity.Collections.NativeArray<uint> data = gpu.StatsReadbackRequest.GetData<uint>();
            if (data.Length < 4)
            {
                continue;
            }

            pack.LastVisibleClusters = data[0];
            pack.LastVisibleIndices = data[1];
            pack.LastCulledClusters = data[2];
            pack.LastOverflowClusters = data[3];
        }
    }

    private Camera ResolveCamera()
    {
        if (forceCameraOverride && cameraOverride != null)
        {
            return cameraOverride;
        }

        if (Application.isPlaying &&
            useWeatherCameraRegistry &&
            NYCGISWeatherCameraRegistry.HasAnyRegisteredFeeds &&
            NYCGISWeatherCameraRegistry.TryGetSelectedFeed(out NYCGISWeatherCameraFeedSnapshot selectedFeed) &&
            selectedFeed.Camera != null)
        {
            return selectedFeed.Camera;
        }

        Camera resolvedCamera = NYCGISStreamingCameraResolver.Resolve(cameraOverride, useSceneViewCameraInEditor);
        if (resolvedCamera != null)
        {
            return resolvedCamera;
        }

        if (useActiveCameraWhenMissing && Camera.current != null)
        {
            return Camera.current;
        }

        return null;
    }

    private void ReleaseAllPacks()
    {
        ReleaseScheduledGroups();
        uploadQueue.Clear();
        foreach (Bfp2PackInfo pack in packs)
        {
            ClearPendingUpload(pack);
            pack.ReleaseGpu(this);
            pack.LoadTask = null;
        }
        fileHandleCache.CloseAll();
        stagedUploadBytes = 0;
        stagedUploadPackCount = 0;
        secondaryCullGpuBytes = 0L;
        RebuildSummaryStats(false, false);
    }

    private GraphicsBuffer AcquireBuffer(GraphicsBuffer.Target target, int count, int stride)
    {
        if (usePersistentGpuBufferPool)
        {
            for (int i = 0; i < bufferPool.Count; i++)
            {
                PooledBuffer pooled = bufferPool[i];
                if (pooled.Target == target && pooled.Count == count && pooled.Stride == stride)
                {
                    bufferPool.RemoveAt(i);
                    pooledGpuBytes -= pooled.EstimatedBytes;
                    return pooled.Buffer;
                }
            }
        }

        return new GraphicsBuffer(target, count, stride);
    }

    private void RecycleBuffer(ref GraphicsBuffer buffer, GraphicsBuffer.Target target, int count, int stride)
    {
        if (buffer == null)
        {
            return;
        }

        long estimatedBytes = (long)count * stride;
        if (usePersistentGpuBufferPool &&
            !NYCGISMissionAreaResidencyContext.HasActiveMissionArea &&
            maxPooledGpuBytes > 0 &&
            pooledGpuBytes + estimatedBytes <= maxPooledGpuBytes)
        {
            bufferPool.Add(new PooledBuffer
            {
                Buffer = buffer,
                Target = target,
                Count = count,
                Stride = stride,
                EstimatedBytes = estimatedBytes
            });
            pooledGpuBytes += estimatedBytes;
            buffer = null;
            return;
        }

        buffer.Release();
        buffer = null;
    }

    private void ReleasePooledBuffers()
    {
        foreach (PooledBuffer pooled in bufferPool)
        {
            pooled.Buffer?.Release();
        }
        bufferPool.Clear();
        pooledGpuBytes = 0;
    }

    private void ReleaseCameraExpansionStatsBuffer()
    {
        cameraExpansionReadbackPending = false;
        if (cameraExpansionStatsBuffer == null)
        {
            return;
        }

        cameraExpansionStatsBuffer.Release();
        cameraExpansionStatsBuffer = null;
    }

    private void ReleaseRuntimeMaterial()
    {
        if (runtimeMaterial == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(runtimeMaterial);
        }
        else
        {
            DestroyImmediate(runtimeMaterial);
        }
        runtimeMaterial = null;
    }

    private void EnsureRuntimeFacadeTextureArray()
    {
        if (runtimeFacadeTextureArray != null)
        {
            facadeTextureArray = runtimeFacadeTextureArray;
            return;
        }

        Texture2D[] sources = Resources.LoadAll<Texture2D>(Bfp2FacadeResourcePath);
        if (sources == null || sources.Length == 0)
        {
            WarnFacadeResourceFallbackOnce(
                "No facade PNG textures were found in Resources/" + Bfp2FacadeResourcePath + ".");
            return;
        }

        Array.Sort(sources, (left, right) => string.CompareOrdinal(left.name, right.name));
        Texture2D first = sources[0];
        int width = first.width;
        int height = first.height;
        TextureFormat format = first.format;
        int mipCount = first.mipmapCount;
        for (int i = 1; i < sources.Length; i++)
        {
            Texture2D source = sources[i];
            if (source.width != width || source.height != height ||
                source.format != format || source.mipmapCount != mipCount)
            {
                WarnFacadeResourceFallbackOnce(
                    "Facade PNG imports must have matching size, format, and mip count. " +
                    source.name + " does not match " + first.name + ".");
                return;
            }
        }

        Texture2DArray array = null;
        try
        {
            array = new Texture2DArray(width, height, sources.Length, format, mipCount > 1, false)
            {
                name = "BFP2 Facade PNG Runtime Array",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4
            };

            for (int slice = 0; slice < sources.Length; slice++)
            {
                for (int mip = 0; mip < mipCount; mip++)
                {
                    Graphics.CopyTexture(sources[slice], 0, mip, array, slice, mip);
                }
            }

            runtimeFacadeTextureArray = array;
            facadeTextureArray = array;
            Debug.Log(
                "[NYCGIS] Loaded " + sources.Length +
                " Git-managed facade PNG textures into the runtime texture array.",
                this);
        }
        catch (Exception exception)
        {
            if (array != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(array);
                }
                else
                {
                    DestroyImmediate(array);
                }
            }

            WarnFacadeResourceFallbackOnce(
                "Could not create the facade PNG runtime texture array: " + exception.Message);
        }
    }

    private void WarnFacadeResourceFallbackOnce(string detail)
    {
        if (warnedFacadeResourceFallbackFailure)
        {
            return;
        }

        warnedFacadeResourceFallbackFailure = true;
        Debug.LogWarning(
            "[NYCGIS] Building facade textures are unavailable. " + detail +
            " Buildings will use the untextured fallback until the PNG sources are restored.",
            this);
    }

    private void ReleaseRuntimeFacadeTextureArray()
    {
        if (runtimeFacadeTextureArray == null)
        {
            return;
        }

        if (facadeTextureArray == runtimeFacadeTextureArray)
        {
            facadeTextureArray = null;
        }

        if (Application.isPlaying)
        {
            Destroy(runtimeFacadeTextureArray);
        }
        else
        {
            DestroyImmediate(runtimeFacadeTextureArray);
        }

        runtimeFacadeTextureArray = null;
        warnedFacadeResourceFallbackFailure = false;
    }

    private string[] GetRuntimePackFilesForLayer(
        Bfp2LayerSettings layer,
        bool missionActive,
        out bool layerUsesMegaPacks)
    {
        layerUsesMegaPacks = false;
        if (layer == null || string.IsNullOrWhiteSpace(layer.directory) || !Directory.Exists(layer.directory))
        {
            return Array.Empty<string>();
        }

        // A full-residency layer deliberately keeps spatial source packs as independent draw
        // and cluster-cull units. This avoids replacing the production baseline with one
        // monolithic full-city mega buffer.
        if (layer.fullLayerResidency)
        {
            return GetSourceBfp2Files(layer.directory);
        }

        // A legacy per-layer mega pack has one full-city AABB. It cannot prove spatial admission
        // for a bounded mission, so mission residency always uses the original spatial source
        // packs. GPU indirect rendering and scheduled-camera culling work for either file layout.
        if (missionActive)
        {
            return GetSourceBfp2Files(layer.directory);
        }

        if (preferMegaPacks)
        {
            string[] megaFiles = GetMegaPackFilesForLayer(layer);
            if (megaFiles.Length > 0)
            {
                layerUsesMegaPacks = true;
                return megaFiles;
            }

            if (gpuDrivenMegaLayerMode && requireMegaPacksInGpuDrivenMode)
            {
                lastStatus = "BFP2 missing mega pack for layer " + layer.name;
                Debug.LogWarning(lastStatus + " in " + GetMegaPackDirectory(layer) + ". Build mega packs before using strict GPU-driven layer mode.", this);
                return Array.Empty<string>();
            }
        }

        return GetSourceBfp2Files(layer.directory);
    }

    private string[] GetMegaPackFilesForLayer(Bfp2LayerSettings layer)
    {
        string directory = GetMegaPackDirectory(layer);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        string[] files = Directory.GetFiles(directory, "*.megabfp2", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private string GetMegaPackDirectory(Bfp2LayerSettings layer)
    {
        if (layer == null || string.IsNullOrWhiteSpace(layer.directory))
        {
            return null;
        }

        string folderName = string.IsNullOrWhiteSpace(megaPackDirectoryName) ? "mega" : megaPackDirectoryName.Trim();
        return System.IO.Path.Combine(layer.directory, folderName);
    }

    private int BuildMegaPacksForLayer(Bfp2LayerSettings layer, int layerIndex, bool overwriteExisting)
    {
        string[] sourceFiles = GetSourceBfp2Files(layer.directory);
        Array.Sort(sourceFiles, StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Length == 0)
        {
            return 0;
        }

        string outputDirectory = GetMegaPackDirectory(layer);
        Directory.CreateDirectory(outputDirectory);
        if (overwriteExisting)
        {
            string[] staleMacroPacks = Directory.GetFiles(
                outputDirectory,
                "*.megabfp2",
                SearchOption.TopDirectoryOnly);
            for (int i = 0; i < staleMacroPacks.Length; i++)
            {
                File.Delete(staleMacroPacks[i]);
            }
        }

        Bfp2PackSnapshot[] orderedSources = new Bfp2PackSnapshot[sourceFiles.Length];
        for (int i = 0; i < sourceFiles.Length; i++)
        {
            string path = sourceFiles[i];
            orderedSources[i] = new Bfp2PackSnapshot(
                path,
                System.IO.Path.GetFileNameWithoutExtension(path),
                ReadHeader(path));
        }
        SortSnapshotsBySpatialMortonKey(orderedSources);

        int maxSourcesPerMega = megaPackMaxSourcePacks <= 0
            ? DefaultSpatialMacroSourcePackCount
            : Mathf.Max(1, megaPackMaxSourcePacks);
        int written = 0;
        for (int offset = 0; offset < orderedSources.Length; offset += maxSourcesPerMega)
        {
            int count = Mathf.Min(maxSourcesPerMega, orderedSources.Length - offset);
            int macroIndex = offset / maxSourcesPerMega;
            string suffix = orderedSources.Length <= maxSourcesPerMega ? string.Empty : "_" + macroIndex.ToString("000");
            string outputPath = System.IO.Path.Combine(outputDirectory, MakeSafeFileName(layer.name) + suffix + ".megabfp2");
            if (File.Exists(outputPath) && !overwriteExisting)
            {
                continue;
            }

            Bfp2PackSnapshot[] snapshots = new Bfp2PackSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                snapshots[i] = orderedSources[offset + i];
            }

            Bfp2ScheduledBuildResult result = BuildScheduledGroupCpu(snapshots);
            result.Name = MakeSafeFileName(layer.name) + suffix;
            WriteBfp2MegaPack(outputPath, result.Header, result.Cpu);
            written++;
            lastStatus = "BFP2 mega built " + result.Name + " (" + count + " source packs)";
        }

        return written;
    }

    private static void SortSnapshotsBySpatialMortonKey(Bfp2PackSnapshot[] snapshots)
    {
        if (snapshots == null || snapshots.Length < 2)
        {
            return;
        }

        float minX = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;
        for (int i = 0; i < snapshots.Length; i++)
        {
            Vector3 center = (snapshots[i].Header.BoundsMin + snapshots[i].Header.BoundsMax) * 0.5f;
            minX = Mathf.Min(minX, center.x);
            minZ = Mathf.Min(minZ, center.z);
            maxX = Mathf.Max(maxX, center.x);
            maxZ = Mathf.Max(maxZ, center.z);
        }

        Array.Sort(snapshots, (left, right) =>
        {
            ulong leftKey = BuildSpatialMortonKey(left.Header, minX, minZ, maxX, maxZ);
            ulong rightKey = BuildSpatialMortonKey(right.Header, minX, minZ, maxX, maxZ);
            int keyCompare = leftKey.CompareTo(rightKey);
            return keyCompare != 0 ? keyCompare : string.CompareOrdinal(left.Path, right.Path);
        });
    }

    private static ulong BuildSpatialMortonKey(
        Bfp2Header header,
        float minX,
        float minZ,
        float maxX,
        float maxZ)
    {
        Vector3 center = (header.BoundsMin + header.BoundsMax) * 0.5f;
        float normalizedX = Mathf.InverseLerp(minX, maxX, center.x);
        float normalizedZ = Mathf.InverseLerp(minZ, maxZ, center.z);
        uint x = (uint)Mathf.Clamp(Mathf.RoundToInt(normalizedX * 65535.0f), 0, 65535);
        uint z = (uint)Mathf.Clamp(Mathf.RoundToInt(normalizedZ * 65535.0f), 0, 65535);
        return SpreadMortonBits(x) | (SpreadMortonBits(z) << 1);
    }

    private static ulong SpreadMortonBits(uint value)
    {
        ulong bits = value & 0xFFFFu;
        bits = (bits | (bits << 8)) & 0x00FF00FFUL;
        bits = (bits | (bits << 4)) & 0x0F0F0F0FUL;
        bits = (bits | (bits << 2)) & 0x33333333UL;
        bits = (bits | (bits << 1)) & 0x55555555UL;
        return bits;
    }

    private static string[] GetSourceBfp2Files(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        string[] files = Directory.GetFiles(directory, "*.bfp2", SearchOption.AllDirectories);
        List<string> sourceFiles = new List<string>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            if (path.EndsWith(".megabfp2", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sourceFiles.Add(path);
        }

        sourceFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return sourceFiles.ToArray();
    }

    private static bool IsMegaPackPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               path.EndsWith(".megabfp2", StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeSafeFileName(string value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "layer" : value.Trim();
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        for (int i = 0; i < invalid.Length; i++)
        {
            text = text.Replace(invalid[i], '_');
        }

        return text.Replace(' ', '_');
    }

    private static void WriteBfp2MegaPack(string outputPath, Bfp2Header header, Bfp2CpuPack cpu)
    {
        if (cpu == null || cpu.VertexWords == null || cpu.Indices == null || cpu.ClusterWords == null)
        {
            throw new InvalidDataException("Cannot write an empty BFP2 mega pack.");
        }

        string directory = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Bfp2Header writeHeader = header;
        writeHeader.VertexDataOffset = HeaderBytes;
        writeHeader.IndexDataOffset = writeHeader.VertexDataOffset + (long)cpu.VertexWords.Length * sizeof(uint);
        writeHeader.ClusterDataOffset = writeHeader.IndexDataOffset + (long)cpu.Indices.Length * sizeof(uint);
        writeHeader.BatchDataOffset = 0;
        writeHeader.IndirectArgsOffset = 0;
        writeHeader.BatchCount = 0;
        writeHeader.VertexCount = cpu.VertexWords.Length / Mathf.Max(1, writeHeader.VertexStride / sizeof(uint));
        writeHeader.IndexCount = cpu.Indices.Length;
        writeHeader.ClusterCount = cpu.ClusterWords.Length / Mathf.Max(1, writeHeader.ClusterStride / sizeof(uint));
        writeHeader.TriangleCount = Mathf.Max(0, writeHeader.IndexCount / 3);

        string tempPath = outputPath + ".tmp";
        using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            WriteBfp2Header(writer, writeHeader);
            WriteUIntArray(writer, cpu.VertexWords);
            WriteUIntArray(writer, cpu.Indices);
            WriteUIntArray(writer, cpu.ClusterWords);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        File.Move(tempPath, outputPath);
    }

    private static void WriteBfp2Header(BinaryWriter writer, Bfp2Header header)
    {
        writer.Write(Bfp2Magic);
        writer.Write(Bfp2Version);
        writer.Write(header.Flags);
        writer.Write(header.VertexFormat);
        writer.Write(header.IndexFormat);
        writer.Write(header.ClusterFormat);
        writer.Write(header.BatchFormat);
        writer.Write(header.IndirectArgsFormat);
        writer.Write((uint)header.VertexStride);
        writer.Write((uint)header.ClusterStride);
        writer.Write((uint)header.BatchStride);
        writer.Write((uint)header.IndirectArgsStride);
        writer.Write((ulong)Math.Max(0, header.BatchCount));
        writer.Write((ulong)Math.Max(0, header.ClusterCount));
        writer.Write((ulong)Math.Max(0, header.VertexCount));
        writer.Write((ulong)Math.Max(0, header.IndexCount));
        writer.Write((ulong)Math.Max(0, header.TriangleCount));
        writer.Write((ulong)Math.Max(0L, header.VertexDataOffset));
        writer.Write((ulong)Math.Max(0L, header.IndexDataOffset));
        writer.Write((ulong)Math.Max(0L, header.ClusterDataOffset));
        writer.Write((ulong)Math.Max(0L, header.BatchDataOffset));
        writer.Write((ulong)Math.Max(0L, header.IndirectArgsOffset));
        writer.Write(header.BoundsMin.x);
        writer.Write(header.BoundsMin.y);
        writer.Write(header.BoundsMin.z);
        writer.Write(header.BoundsMax.x);
        writer.Write(header.BoundsMax.y);
        writer.Write(header.BoundsMax.z);
        writer.Write(header.QuantOrigin.x);
        writer.Write(header.QuantOrigin.y);
        writer.Write(header.QuantOrigin.z);
        writer.Write(header.QuantScale.x);
        writer.Write(header.QuantScale.y);
        writer.Write(header.QuantScale.z);
        writer.Write((uint)Math.Max(0, header.ClusterTriangleTarget));
        writer.Write((uint)Math.Max(0, header.ClusterVertexTarget));

        long padding = HeaderBytes - writer.BaseStream.Position;
        if (padding < 0)
        {
            throw new InvalidDataException("BFP2 header exceeded reserved header size.");
        }

        for (long i = 0; i < padding; i++)
        {
            writer.Write((byte)0);
        }
    }

    private static void WriteUIntArray(BinaryWriter writer, uint[] values)
    {
        const int maxWordsPerChunk = 1024 * 1024;
        byte[] bytes = new byte[maxWordsPerChunk * sizeof(uint)];
        int offset = 0;
        while (offset < values.Length)
        {
            int count = Math.Min(maxWordsPerChunk, values.Length - offset);
            int byteCount = count * sizeof(uint);
            Buffer.BlockCopy(values, offset * sizeof(uint), bytes, 0, byteCount);
            writer.Write(bytes, 0, byteCount);
            offset += count;
        }
    }

    private Bfp2CpuPack ReadPackPayloadCached(string path, Bfp2Header header)
    {
        return fileHandleCache.ReadPack(path, header);
    }

    private static Bfp2CpuPack ReadPackPayload(string path, Bfp2Header header)
    {
        using (FileStream stream = OpenPackReadStream(path))
        {
            return ReadPackPayloadFromStream(path, header, stream);
        }
    }

    private static Bfp2CpuPack ReadPackPayloadFromStream(string path, Bfp2Header header, FileStream stream)
    {
        byte[] vertexBytes = ReadBytesAt(stream, path, header.VertexDataOffset, checked(header.VertexCount * header.VertexStride));
        byte[] indexBytes = ReadBytesAt(stream, path, header.IndexDataOffset, checked(header.IndexCount * sizeof(uint)));
        byte[] clusterBytes = ReadBytesAt(stream, path, header.ClusterDataOffset, checked(header.ClusterCount * header.ClusterStride));
        Bfp2CpuPack cpu = new Bfp2CpuPack
        {
            VertexWords = BytesToUInts(vertexBytes),
            Indices = BytesToUInts(indexBytes),
            ClusterWords = BytesToUInts(clusterBytes),
            EstimatedUploadBytes =
                (long)vertexBytes.Length
                + indexBytes.Length
                + clusterBytes.Length
                + indexBytes.Length
                + 32L
        };
        PrepareClusterLocalIndex16(cpu);
        return cpu;
    }

    private static void PrepareClusterLocalIndex16(Bfp2CpuPack cpu)
    {
        if (cpu == null)
        {
            return;
        }

        cpu.ClusterLocalIndex16Ready = Bfp2ClusterLocalIndex16.TryEncode(
            cpu.Indices,
            cpu.ClusterWords,
            out cpu.PackedIndices16,
            out cpu.ClusterIndexBases16,
            out cpu.MaximumClusterIndexSpan16,
            out cpu.ClusterLocalIndex16Error);
        if (!cpu.ClusterLocalIndex16Ready)
        {
            cpu.PackedIndices16 = null;
            cpu.ClusterIndexBases16 = null;
        }
    }

    private static FileStream OpenPackReadStream(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.RandomAccess);
    }

    private static byte[] ReadBytesAt(FileStream stream, string path, long offset, int count)
    {
        byte[] data = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(data, read, count - read);
            if (n <= 0)
            {
                throw new EndOfStreamException("Unexpected EOF in " + path);
            }
            read += n;
        }
        return data;
    }

    private static uint[] BytesToUInts(byte[] bytes)
    {
        if ((bytes.Length & 3) != 0)
        {
            throw new InvalidDataException("BFP2 byte range is not uint aligned.");
        }

        uint[] values = new uint[bytes.Length / sizeof(uint)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static Bfp2ScheduledBuildResult BuildScheduledGroupCpu(Bfp2PackSnapshot[] snapshots)
    {
        if (snapshots == null || snapshots.Length < 1)
        {
            throw new InvalidDataException("BFP2 merge needs at least one pack.");
        }

        Bfp2Header firstHeader = snapshots[0].Header;
        int wordsPerVertex = Mathf.Max(1, firstHeader.VertexStride / sizeof(uint));
        int wordsPerCluster = Mathf.Max(1, firstHeader.ClusterStride / sizeof(uint));
        if (wordsPerVertex != 4 || wordsPerCluster != 20)
        {
            throw new InvalidDataException("BFP2 scheduler only supports the current 4-word vertex and 20-word cluster format.");
        }

        Vector3 boundsMin = firstHeader.BoundsMin;
        Vector3 boundsMax = firstHeader.BoundsMax;
        int totalVertexCount = 0;
        int totalIndexCount = 0;
        int totalClusterCount = 0;
        int totalTriangleCount = 0;
        Bfp2CpuPack[] cpuPacks = new Bfp2CpuPack[snapshots.Length];

        for (int i = 0; i < snapshots.Length; i++)
        {
            Bfp2Header header = snapshots[i].Header;
            if (header.VertexStride != firstHeader.VertexStride ||
                header.ClusterStride != firstHeader.ClusterStride ||
                header.IndexFormat != firstHeader.IndexFormat ||
                header.VertexFormat != firstHeader.VertexFormat ||
                header.ClusterFormat != firstHeader.ClusterFormat)
            {
                throw new InvalidDataException("BFP2 scheduler cannot merge packs with different binary formats.");
            }

            boundsMin = Vector3.Min(boundsMin, header.BoundsMin);
            boundsMax = Vector3.Max(boundsMax, header.BoundsMax);
            totalVertexCount = checked(totalVertexCount + header.VertexCount);
            totalIndexCount = checked(totalIndexCount + header.IndexCount);
            totalClusterCount = checked(totalClusterCount + header.ClusterCount);
            totalTriangleCount = checked(totalTriangleCount + header.TriangleCount);
            cpuPacks[i] = ReadPackPayload(snapshots[i].Path, header);
        }

        Vector3 quantOrigin = boundsMin;
        Vector3 size = boundsMax - boundsMin;
        Vector3 quantScale = new Vector3(
            Mathf.Max(size.x / 65535.0f, 0.000001f),
            Mathf.Max(size.y / 65535.0f, 0.000001f),
            Mathf.Max(size.z / 65535.0f, 0.000001f));

        uint[] vertexWords = new uint[checked(totalVertexCount * wordsPerVertex)];
        uint[] indices = new uint[totalIndexCount];
        uint[] clusterWords = new uint[checked(totalClusterCount * wordsPerCluster)];

        int vertexOffset = 0;
        int vertexWordOffset = 0;
        int indexOffset = 0;
        int clusterWordOffset = 0;
        for (int packIndex = 0; packIndex < snapshots.Length; packIndex++)
        {
            Bfp2Header header = snapshots[packIndex].Header;
            Bfp2CpuPack cpu = cpuPacks[packIndex];
            if (cpu.VertexWords.Length < header.VertexCount * wordsPerVertex ||
                cpu.Indices.Length < header.IndexCount ||
                cpu.ClusterWords.Length < header.ClusterCount * wordsPerCluster)
            {
                throw new InvalidDataException("BFP2 scheduler source payload is smaller than the scanned header.");
            }

            for (int vertex = 0; vertex < header.VertexCount; vertex++)
            {
                int src = vertex * wordsPerVertex;
                int dst = vertexWordOffset + vertex * wordsPerVertex;
                uint w0 = cpu.VertexWords[src + 0];
                uint w1 = cpu.VertexWords[src + 1];
                uint qx = w0 & 0xffffu;
                uint qy = (w0 >> 16) & 0xffffu;
                uint qz = w1 & 0xffffu;
                Vector3 position = header.QuantOrigin + new Vector3(qx * header.QuantScale.x, qy * header.QuantScale.y, qz * header.QuantScale.z);
                uint mergedQx = QuantizeUInt16(position.x, quantOrigin.x, quantScale.x);
                uint mergedQy = QuantizeUInt16(position.y, quantOrigin.y, quantScale.y);
                uint mergedQz = QuantizeUInt16(position.z, quantOrigin.z, quantScale.z);

                vertexWords[dst + 0] = (mergedQx & 0xffffu) | ((mergedQy & 0xffffu) << 16);
                vertexWords[dst + 1] = (w1 & 0xffff0000u) | (mergedQz & 0xffffu);
                vertexWords[dst + 2] = cpu.VertexWords[src + 2];
                vertexWords[dst + 3] = cpu.VertexWords[src + 3];
            }

            for (int index = 0; index < header.IndexCount; index++)
            {
                indices[indexOffset + index] = checked(cpu.Indices[index] + (uint)vertexOffset);
            }

            for (int cluster = 0; cluster < header.ClusterCount; cluster++)
            {
                int src = cluster * wordsPerCluster;
                int dst = clusterWordOffset + cluster * wordsPerCluster;
                Array.Copy(cpu.ClusterWords, src, clusterWords, dst, wordsPerCluster);
                clusterWords[dst] = checked(clusterWords[dst] + (uint)indexOffset);
            }

            vertexOffset += header.VertexCount;
            vertexWordOffset += header.VertexCount * wordsPerVertex;
            indexOffset += header.IndexCount;
            clusterWordOffset += header.ClusterCount * wordsPerCluster;
        }

        Bfp2Header mergedHeader = firstHeader;
        mergedHeader.VertexCount = totalVertexCount;
        mergedHeader.IndexCount = totalIndexCount;
        mergedHeader.ClusterCount = totalClusterCount;
        mergedHeader.TriangleCount = totalTriangleCount;
        mergedHeader.BoundsMin = boundsMin;
        mergedHeader.BoundsMax = boundsMax;
        mergedHeader.QuantOrigin = quantOrigin;
        mergedHeader.QuantScale = quantScale;
        mergedHeader.VertexDataOffset = 0;
        mergedHeader.IndexDataOffset = 0;
        mergedHeader.ClusterDataOffset = 0;
        mergedHeader.BatchDataOffset = 0;
        mergedHeader.IndirectArgsOffset = 0;

        Bfp2CpuPack mergedCpu = new Bfp2CpuPack
        {
            VertexWords = vertexWords,
            Indices = indices,
            ClusterWords = clusterWords,
            EstimatedUploadBytes =
                (long)vertexWords.Length * sizeof(uint)
                + (long)indices.Length * sizeof(uint) * 2L
                + (long)clusterWords.Length * sizeof(uint)
                + 32L
        };
        PrepareClusterLocalIndex16(mergedCpu);

        return new Bfp2ScheduledBuildResult
        {
            Header = mergedHeader,
            Bounds = MakeBounds(boundsMin, boundsMax),
            Name = "scheduler-" + snapshots.Length + "-packs",
            Cpu = mergedCpu
        };
    }

    private static uint QuantizeUInt16(float value, float origin, float scale)
    {
        int quantized = Mathf.RoundToInt((value - origin) / Mathf.Max(scale, 0.000001f));
        return (uint)Mathf.Clamp(quantized, 0, 65535);
    }

    private static Bfp2Header ReadHeader(string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            uint magic = reader.ReadUInt32();
            int version = reader.ReadInt32();
            if (magic != Bfp2Magic || version != Bfp2Version)
            {
                throw new InvalidDataException("Not a supported BFP2 file.");
            }

            Bfp2Header header = new Bfp2Header
            {
                Flags = reader.ReadUInt32(),
                VertexFormat = reader.ReadUInt32(),
                IndexFormat = reader.ReadUInt32(),
                ClusterFormat = reader.ReadUInt32(),
                BatchFormat = reader.ReadUInt32(),
                IndirectArgsFormat = reader.ReadUInt32(),
                VertexStride = checked((int)reader.ReadUInt32()),
                ClusterStride = checked((int)reader.ReadUInt32()),
                BatchStride = checked((int)reader.ReadUInt32()),
                IndirectArgsStride = checked((int)reader.ReadUInt32()),
                BatchCount = CheckedInt(reader.ReadUInt64()),
                ClusterCount = CheckedInt(reader.ReadUInt64()),
                VertexCount = CheckedInt(reader.ReadUInt64()),
                IndexCount = CheckedInt(reader.ReadUInt64()),
                TriangleCount = CheckedInt(reader.ReadUInt64()),
                VertexDataOffset = CheckedLong(reader.ReadUInt64()),
                IndexDataOffset = CheckedLong(reader.ReadUInt64()),
                ClusterDataOffset = CheckedLong(reader.ReadUInt64()),
                BatchDataOffset = CheckedLong(reader.ReadUInt64()),
                IndirectArgsOffset = CheckedLong(reader.ReadUInt64())
            };

            Vector3 min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 origin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 scale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            header.BoundsMin = min;
            header.BoundsMax = max;
            header.QuantOrigin = origin;
            header.QuantScale = scale;
            header.ClusterTriangleTarget = checked((int)reader.ReadUInt32());
            header.ClusterVertexTarget = checked((int)reader.ReadUInt32());

            if (stream.Length < HeaderBytes)
            {
                throw new InvalidDataException("BFP2 header is truncated.");
            }
            if (header.VertexStride != VertexStrideBytes)
            {
                throw new InvalidDataException("Unsupported BFP2 vertex stride " + header.VertexStride);
            }
            if (header.ClusterStride != ClusterStrideBytes)
            {
                throw new InvalidDataException("Unsupported BFP2 cluster stride " + header.ClusterStride);
            }

            return header;
        }
    }

    private static int CheckedInt(ulong value)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException("BFP2 count exceeds Unity buffer limits.");
        }
        return (int)value;
    }

    private static long CheckedLong(ulong value)
    {
        if (value > long.MaxValue)
        {
            throw new InvalidDataException("BFP2 offset exceeds Int64 range.");
        }
        return (long)value;
    }

    private static Bounds MakeBounds(Vector3 min, Vector3 max)
    {
        Bounds bounds = new Bounds();
        bounds.SetMinMax(min, max);
        return bounds;
    }

    private static bool ShouldForceNoTriangleWindingFlip(string directory)
    {
        string formatPath = System.IO.Path.Combine(directory, "format.json");
        if (!File.Exists(formatPath))
        {
            return false;
        }

        try
        {
            Bfp2FormatMetadata metadata = JsonUtility.FromJson<Bfp2FormatMetadata>(File.ReadAllText(formatPath));
            string defaultFacing = metadata == null ? null : metadata.defaultFacing;
            if (string.IsNullOrWhiteSpace(defaultFacing))
            {
                return false;
            }

            bool unityFacing = defaultFacing.IndexOf("unity-facing", StringComparison.OrdinalIgnoreCase) >= 0
                               || defaultFacing.IndexOf("unity facing", StringComparison.OrdinalIgnoreCase) >= 0;
            bool baked = defaultFacing.IndexOf("baked", StringComparison.OrdinalIgnoreCase) >= 0;
            return unityFacing && baked;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("BFP2 format metadata read failed for " + formatPath + ": " + ex.Message);
            return false;
        }
    }

    private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
    {
        Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
        Vector3 extents = bounds.extents;
        Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0.0f, 0.0f));
        Vector3 axisY = matrix.MultiplyVector(new Vector3(0.0f, extents.y, 0.0f));
        Vector3 axisZ = matrix.MultiplyVector(new Vector3(0.0f, 0.0f, extents.z));
        extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
        extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
        extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);
        return new Bounds(center, extents * 2.0f);
    }

    private static float DistanceToBounds(Bounds bounds, Vector3 point)
    {
        Vector3 closest = bounds.ClosestPoint(point);
        return Vector3.Distance(point, closest);
    }

    private static int ComparePackDistance(Bfp2PackInfo a, Bfp2PackInfo b)
    {
        int priorityCmp = a.StreamPriority.CompareTo(b.StreamPriority);
        if (priorityCmp != 0)
        {
            return priorityCmp;
        }

        int cmp = a.DistanceToCamera.CompareTo(b.DistanceToCamera);
        if (cmp != 0)
        {
            return cmp;
        }
        return string.CompareOrdinal(a.Name, b.Name);
    }

    private static int ComparePackDistanceDescending(Bfp2PackInfo a, Bfp2PackInfo b)
    {
        int cmp = b.DistanceToCamera.CompareTo(a.DistanceToCamera);
        if (cmp != 0)
        {
            return cmp;
        }
        return string.CompareOrdinal(a.Name, b.Name);
    }

    private static int CompareNearShadowCandidateDistance(NearShadowPackCandidate a, NearShadowPackCandidate b)
    {
        int cmp = a.distance.CompareTo(b.distance);
        if (cmp != 0)
        {
            return cmp;
        }

        string aName = a.pack != null ? a.pack.Name : string.Empty;
        string bName = b.pack != null ? b.pack.Name : string.Empty;
        return string.CompareOrdinal(aName, bName);
    }

    private static long EstimateGpuBytes(Bfp2Header header)
    {
        return (long)header.VertexCount * VertexStrideBytes
               + (long)header.IndexCount * sizeof(uint) * 2L
               + (long)((header.IndexCount + 1L) / 2L) * sizeof(uint)
               + (long)header.ClusterCount *
                 (ClusterStrideBytes +
                  CompactClusterWordsPerCluster * sizeof(uint) +
                  sizeof(uint))
               + 32L;
    }

    private static long EstimateMissionGpuBytes(Bfp2Header header)
    {
        // Reserve the optional secondary visible-index/args/stats output up front. It is created
        // only when the registered-camera union exceeds the 2.5x adaptive threshold, but adding
        // it after admission must never push a bounded mission beyond its declared GPU ceiling.
        return EstimateGpuBytes(header)
               + (long)header.IndexCount * sizeof(uint)
               + 8L * sizeof(uint);
    }

    private static string FormatBytes(long bytes)
    {
        const double mib = 1024.0 * 1024.0;
        const double gib = mib * 1024.0;
        if (bytes >= gib)
        {
            return (bytes / gib).ToString("0.00") + " GiB";
        }
        return (bytes / mib).ToString("0.0") + " MiB";
    }

    private void OnDrawGizmosSelected()
    {
        if (productionMode || !drawClusterBounds)
        {
            return;
        }

        Gizmos.color = clusterBoundsColor;
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        foreach (Bfp2PackInfo pack in packs)
        {
            if (pack.Gpu == null)
            {
                continue;
            }
            Gizmos.DrawWireCube(pack.Bounds.center, pack.Bounds.size);
        }
        Gizmos.matrix = oldMatrix;
    }

    private struct Bfp2Header
    {
        public uint Flags;
        public uint VertexFormat;
        public uint IndexFormat;
        public uint ClusterFormat;
        public uint BatchFormat;
        public uint IndirectArgsFormat;
        public int VertexStride;
        public int ClusterStride;
        public int BatchStride;
        public int IndirectArgsStride;
        public int BatchCount;
        public int ClusterCount;
        public int VertexCount;
        public int IndexCount;
        public int TriangleCount;
        public long VertexDataOffset;
        public long IndexDataOffset;
        public long ClusterDataOffset;
        public long BatchDataOffset;
        public long IndirectArgsOffset;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public Vector3 QuantOrigin;
        public Vector3 QuantScale;
        public int ClusterTriangleTarget;
        public int ClusterVertexTarget;
    }

    private sealed class Bfp2CpuPack
    {
        public uint[] VertexWords;
        public uint[] Indices;
        public uint[] PackedIndices16;
        public uint[] ClusterIndexBases16;
        public uint[] ClusterWords;
        public bool ClusterLocalIndex16Ready;
        public uint MaximumClusterIndexSpan16;
        public string ClusterLocalIndex16Error;
        public long EstimatedUploadBytes;
    }

    private sealed class Bfp2GpuPack
    {
        public GraphicsBuffer VertexWordsBuffer;
        public GraphicsBuffer IndexBuffer;
        public GraphicsBuffer PackedIndex16Buffer;
        public GraphicsBuffer ClusterIndexBases16Buffer;
        public GraphicsBuffer ClusterWordsBuffer;
        public GraphicsBuffer CompactClusterWordsBuffer;
        public GraphicsBuffer VisibleIndexBuffer;
        public GraphicsBuffer DrawArgsBuffer;
        public GraphicsBuffer StatsBuffer;
        public GraphicsBuffer SecondaryVisibleIndexBuffer;
        public GraphicsBuffer SecondaryDrawArgsBuffer;
        public GraphicsBuffer SecondaryStatsBuffer;
        public bool StatsReadbackPending;
        public AsyncGPUReadbackRequest StatsReadbackRequest;
        public int VertexWordsCount;
        public int IndexCount;
        public int PackedIndex16WordCount;
        public int ClusterIndexBaseCount;
        public int ClusterWordsCount;
        public int CompactClusterWordsCount;
        public bool CompactClusterDataReady;
        public bool ClusterLocalIndex16Ready;
        public uint MaximumClusterIndexSpan16;
        public int VisibleIndexCapacity;
        public int PrimaryVisibleTileTriangles;
        public int SecondaryVisibleTileTriangles;
        public bool PrimaryUsesClusterLocalIndex16;
        public bool SecondaryUsesClusterLocalIndex16;
        public long EstimatedBytes;
        public long SecondaryEstimatedBytes;

        public GraphicsBuffer GetVisibleIndexBuffer(int cullOutputIndex)
        {
            if (cullOutputIndex <= 0)
            {
                return VisibleIndexBuffer;
            }

            return SecondaryVisibleIndexBuffer ?? throw new InvalidOperationException("Secondary BFP2 visible-index buffer is not allocated.");
        }

        public GraphicsBuffer GetDrawArgsBuffer(int cullOutputIndex)
        {
            if (cullOutputIndex <= 0)
            {
                return DrawArgsBuffer;
            }

            return SecondaryDrawArgsBuffer ?? throw new InvalidOperationException("Secondary BFP2 draw-args buffer is not allocated.");
        }

        public GraphicsBuffer GetStatsBuffer(int cullOutputIndex)
        {
            if (cullOutputIndex <= 0)
            {
                return StatsBuffer;
            }

            return SecondaryStatsBuffer ?? throw new InvalidOperationException("Secondary BFP2 stats buffer is not allocated.");
        }

        public void SetCullOutputTileTriangleCount(int cullOutputIndex, int tileTriangleCount)
        {
            if (cullOutputIndex <= 0)
            {
                PrimaryVisibleTileTriangles = tileTriangleCount;
                return;
            }

            SecondaryVisibleTileTriangles = tileTriangleCount;
        }

        public int GetCullOutputTileTriangleCount(int cullOutputIndex)
        {
            if (cullOutputIndex <= 0)
            {
                return PrimaryVisibleTileTriangles;
            }

            return SecondaryVisibleTileTriangles;
        }

        public void SetCullOutputUsesClusterLocalIndex16(
            int cullOutputIndex,
            bool value)
        {
            if (cullOutputIndex <= 0)
            {
                PrimaryUsesClusterLocalIndex16 = value;
                return;
            }
            SecondaryUsesClusterLocalIndex16 = value;
        }

        public bool GetCullOutputUsesClusterLocalIndex16(int cullOutputIndex)
        {
            return cullOutputIndex <= 0
                ? PrimaryUsesClusterLocalIndex16
                : SecondaryUsesClusterLocalIndex16;
        }

        public void Release(Bfp2GpuIndirectRenderer owner)
        {
            owner.secondaryCullGpuBytes = Math.Max(0L, owner.secondaryCullGpuBytes - SecondaryEstimatedBytes);
            owner.RecycleBuffer(ref SecondaryVisibleIndexBuffer, GraphicsBuffer.Target.Structured, VisibleIndexCapacity, sizeof(uint));
            owner.RecycleBuffer(
                ref SecondaryDrawArgsBuffer,
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
                4,
                sizeof(uint));
            owner.RecycleBuffer(ref SecondaryStatsBuffer, GraphicsBuffer.Target.Structured, 4, sizeof(uint));
            owner.RecycleBuffer(ref VertexWordsBuffer, GraphicsBuffer.Target.Structured, VertexWordsCount, sizeof(uint));
            owner.RecycleBuffer(ref IndexBuffer, GraphicsBuffer.Target.Structured, IndexCount, sizeof(uint));
            owner.RecycleBuffer(ref ClusterWordsBuffer, GraphicsBuffer.Target.Structured, ClusterWordsCount, sizeof(uint));
            owner.RecycleBuffer(
                ref CompactClusterWordsBuffer,
                GraphicsBuffer.Target.Structured,
                CompactClusterWordsCount,
                sizeof(uint));
            owner.RecycleBuffer(ref VisibleIndexBuffer, GraphicsBuffer.Target.Structured, VisibleIndexCapacity, sizeof(uint));
            owner.RecycleBuffer(ref DrawArgsBuffer, GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, 4, sizeof(uint));
            StatsReadbackPending = false;
            owner.RecycleBuffer(ref StatsBuffer, GraphicsBuffer.Target.Structured, 4, sizeof(uint));
            VertexWordsCount = 0;
            IndexCount = 0;
            owner.RecycleBuffer(
                ref PackedIndex16Buffer,
                GraphicsBuffer.Target.Structured,
                PackedIndex16WordCount,
                sizeof(uint));
            owner.RecycleBuffer(
                ref ClusterIndexBases16Buffer,
                GraphicsBuffer.Target.Structured,
                ClusterIndexBaseCount,
                sizeof(uint));
            PackedIndex16WordCount = 0;
            ClusterIndexBaseCount = 0;
            ClusterLocalIndex16Ready = false;
            MaximumClusterIndexSpan16 = 0u;
            ClusterWordsCount = 0;
            CompactClusterWordsCount = 0;
            CompactClusterDataReady = false;
            VisibleIndexCapacity = 0;
            PrimaryVisibleTileTriangles = 0;
            SecondaryVisibleTileTriangles = 0;
            PrimaryUsesClusterLocalIndex16 = false;
            SecondaryUsesClusterLocalIndex16 = false;
            EstimatedBytes = 0;
            SecondaryEstimatedBytes = 0;
        }
    }

    private sealed class Bfp2FileHandleCache
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, CachedReadStream> streams = new Dictionary<string, CachedReadStream>(StringComparer.OrdinalIgnoreCase);

        public Bfp2CpuPack ReadPack(string path, Bfp2Header header)
        {
            CachedReadStream cached = GetOrOpen(path);
            lock (cached.SyncRoot)
            {
                return ReadPackPayloadFromStream(path, header, cached.Stream);
            }
        }

        public void CloseAll()
        {
            List<CachedReadStream> toClose;
            lock (gate)
            {
                if (streams.Count == 0)
                {
                    return;
                }

                toClose = new List<CachedReadStream>(streams.Values);
                streams.Clear();
            }

            for (int i = 0; i < toClose.Count; i++)
            {
                CachedReadStream cached = toClose[i];
                lock (cached.SyncRoot)
                {
                    cached.Stream.Dispose();
                }
            }
        }

        private CachedReadStream GetOrOpen(string path)
        {
            lock (gate)
            {
                if (streams.TryGetValue(path, out CachedReadStream cached))
                {
                    return cached;
                }

                cached = new CachedReadStream(OpenPackReadStream(path));
                streams[path] = cached;
                return cached;
            }
        }

        private sealed class CachedReadStream
        {
            public readonly FileStream Stream;
            public readonly object SyncRoot = new object();

            public CachedReadStream(FileStream stream)
            {
                Stream = stream;
            }
        }
    }

    private sealed class PooledBuffer
    {
        public GraphicsBuffer Buffer;
        public GraphicsBuffer.Target Target;
        public int Count;
        public int Stride;
        public long EstimatedBytes;
    }

    private sealed class Bfp2CameraFrameBuffer
    {
        private readonly NYCGISStreamingFrame[] frames = new NYCGISStreamingFrame[8];
        private int maxFrames = 1;

        public NYCGISStreamingFrame[] Frames => frames;
        public int Count { get; private set; }

        public void Clear(int frameLimit)
        {
            maxFrames = Mathf.Clamp(frameLimit, 1, frames.Length);
            Count = 0;
        }

        public bool Add(Camera camera)
        {
            if (camera == null || Count >= maxFrames)
            {
                return false;
            }

            int cameraId = camera.StableId();
            for (int i = 0; i < Count; i++)
            {
                Camera existing = frames[i].Camera;
                if (existing != null && existing.StableId() == cameraId)
                {
                    return false;
                }
            }

            frames[Count] = NYCGISStreamingFrameContext.GetFrame(camera);
            Count++;
            return true;
        }
    }

    private readonly struct NearShadowPackCandidate
    {
        public readonly Bfp2PackInfo pack;
        public readonly float distance;

        public NearShadowPackCandidate(Bfp2PackInfo pack, float distance)
        {
            this.pack = pack;
            this.distance = distance;
        }
    }

    private readonly struct Bfp2PackSnapshot
    {
        public readonly string Path;
        public readonly string Name;
        public readonly Bfp2Header Header;

        public Bfp2PackSnapshot(Bfp2PackInfo pack)
        {
            Path = pack.Path;
            Name = pack.Name;
            Header = pack.Header;
        }

        public Bfp2PackSnapshot(string path, string name, Bfp2Header header)
        {
            Path = path;
            Name = name;
            Header = header;
        }
    }

    private sealed class Bfp2ScheduledBuildResult
    {
        public string Name;
        public Bfp2Header Header;
        public Bounds Bounds;
        public Bfp2CpuPack Cpu;
    }

    private sealed class Bfp2ScheduledGroup
    {
        public string Signature;
        public string Name;
        public int LayerIndex;
        public bool ForceNoTriangleWindingFlip;
        public ShadowCastingMode ShadowCastingMode;
        public List<Bfp2PackInfo> Packs;
        public Task<Bfp2ScheduledBuildResult> BuildTask;
        public Bfp2CpuPack PendingUploadCpu;
        public Bfp2GpuPack PendingUploadGpu;
        public Bfp2UploadStage UploadStage;
        public int PendingVertexUploadWordOffset;
        public int PendingIndexUploadOffset;
        public int PendingPackedIndex16UploadOffset;
        public int PendingClusterIndexBase16UploadOffset;
        public int PendingClusterUploadWordOffset;
        public long EstimatedBytes;
        public Bfp2Header Header;
        public Bounds Bounds;
        public Bfp2GpuPack Gpu;

        public void Release(Bfp2GpuIndirectRenderer owner)
        {
            PendingUploadCpu = null;
            if (PendingUploadGpu != null)
            {
                PendingUploadGpu.Release(owner);
                PendingUploadGpu = null;
            }

            UploadStage = Bfp2UploadStage.None;
            PendingVertexUploadWordOffset = 0;
            PendingIndexUploadOffset = 0;
            PendingPackedIndex16UploadOffset = 0;
            PendingClusterIndexBase16UploadOffset = 0;
            PendingClusterUploadWordOffset = 0;

            if (Gpu == null)
            {
                return;
            }

            Gpu.Release(owner);
            Gpu = null;
        }
    }

    private enum Bfp2UploadStage
    {
        None,
        AllocateBuffers,
        UploadVertices,
        UploadIndices,
        UploadPackedIndices16,
        UploadClusterIndexBases16,
        UploadClusters,
        InitArgsAndStats
    }

    private sealed class Bfp2PackInfo
    {
        public int LayerIndex;
        public string Path;
        public string Name;
        public Bfp2Header Header;
        public Bounds Bounds;
        public long FileBytes;
        public bool Desired;
        public bool DrawDesired;
        public bool CandidateDrawDesired;
        public int StreamPriority = int.MaxValue;
        public float LastDesiredTime;
        public float LastDrawDesiredTime;
        public float DistanceToCamera;
        public float LoadStartTime;
        public Task<Bfp2CpuPack> LoadTask;
        public Bfp2CpuPack PendingUploadCpu;
        public Bfp2GpuPack PendingUploadGpu;
        public Bfp2UploadStage UploadStage;
        public int PendingVertexUploadWordOffset;
        public int PendingIndexUploadOffset;
        public int PendingPackedIndex16UploadOffset;
        public int PendingClusterIndexBase16UploadOffset;
        public int PendingClusterUploadWordOffset;
        public long PendingUploadBytes;
        public bool UploadQueued;
        public Bfp2GpuPack Gpu;
        public bool ForceNoTriangleWindingFlip;
        public ulong LastPrimaryCullPassEpoch;
        public uint LastVisibleClusters;
        public uint LastVisibleIndices;
        public uint LastCulledClusters;
        public uint LastOverflowClusters;

        public void ReleaseGpu(Bfp2GpuIndirectRenderer owner)
        {
            if (Gpu == null)
            {
                return;
            }

            Gpu.Release(owner);
            Gpu = null;
            if (owner != null)
            {
                owner.nearShadowDirty = true;
            }

            LastVisibleClusters = 0;
            LastVisibleIndices = 0;
            LastCulledClusters = 0;
            LastOverflowClusters = 0;
        }
    }
}
