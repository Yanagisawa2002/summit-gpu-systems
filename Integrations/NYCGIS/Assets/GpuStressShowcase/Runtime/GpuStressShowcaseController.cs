using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DefaultExecutionOrder(30000)]
public sealed class GpuStressShowcaseController : MonoBehaviour
{
    private const string EnableArgument = "-gpu-stress-showcase";
    private const int FirstFeedId = 9201;
    private const uint ValidationState = 63u;
    private const int CityValidationRequiredStableSamples = 3;
    private const int CityValidationMaximumSamples = 30;
    private const int NativeTimestampWarmupPairs = 8;
    private const int NativeTimestampControlIntervalFrames = 8;
    private const double NativeTimestampDrainTimeoutSeconds = 30.0;
    private const string NativeTimestampMode =
        "NativeDx12DirectQueueTimestamp";
    private const string NativeTimestampScopeVersion =
        "SplitDirectQueueBegin_CityCull_ExplicitCameraGroup_End_v1";
    private const string NativeTimestampQueue = "D3D12Direct";
    private const string CityCullHeroPayloadMode =
        "ExplicitHeroPlusPayloadUnion";
    private const string CityCullHeroOnlyMode =
        "ExplicitHeroOnly";
    private const string PayloadRenderMode =
        "QueuedExplicitCamerasAfterBfp2LateUpdateSrpVerified";
    private const string CityValidationMode =
        "ForcedHeroFreshPrimaryPassConverged3";
    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private struct RendererCameraValidationState
    {
        public Bfp2GpuIndirectRenderer Renderer;
        public Camera CameraOverride;
        public bool ForceCameraOverride;
        public bool UseWeatherCameraRegistry;
        public bool EnableBatchedCameraFrameBuffer;
    }

    private readonly List<Camera> extraCameras = new List<Camera>(3);
    private readonly List<float> extraCameraYaw = new List<float>(3);
    private readonly List<bool> extraCameraFixed = new List<bool>(3);
    private readonly List<RenderTexture> extraOutputs =
        new List<RenderTexture>(3);
    private readonly List<float> frameSamples = new List<float>(3600);
    private readonly List<float> gpuSamples = new List<float>(3600);

    private GpuStressShowcaseConfiguration configuration;
    private GpuStressTuningDecision tuning;
    private GpuStressShowcaseStack stack;
    private GpuCinematicRoute cinematicRoute;
    private GpuStressShowcaseReport completedReport;
    private GpuCinematicNativeTimestampBackend nativeTimestampBackend;
    private string nativeTimestampFailure = string.Empty;
    private Camera baseCamera;
    private NYCGISPayloadVisibilityCamera heroFeed;
    private OrthophotoVirtualTextureController orthophoto;
    private NYCGISWeatherSystem weatherSystem;
    private GameObject cinematicVolumeHost;
    private VolumeProfile cinematicVolumeProfile;
    private RenderTexture cinematicHeroOutput;
    private bool baseCameraWasEnabled;
    private Bfp2GpuIndirectRenderer[] renderers;
    private Vector3 cameraOriginPosition;
    private Quaternion cameraOriginRotation;
    private Vector3 overviewCameraPosition;
    private Quaternion overviewCameraRotation;
    private float overviewOrthographicSize;
    private Vector3 desiredCameraPosition;
    private Quaternion desiredCameraRotation;
    private float desiredCameraFieldOfView = 50.0f;
    private float cinematicProgress;
    private string cinematicShotTitle = "CITY AT SCALE";
    private string cinematicShotSubtitle = string.Empty;
    private bool cameraPoseValid;
    private bool cinematicGalleryCapture;
    private string phase = "BOOT";
    private string failure = string.Empty;
    private string hudText = string.Empty;
    private float nextHudRefresh;
    private float latestFrameMs;
    private float latestGpuMs;
    private double hudFrameAverageMs;
    private double hudFrameP99Ms;
    private double hudGpuAverageMs;
    private double hudGpuP99Ms;
    private int currentSampleFrame;
    private int longFrames16;
    private int longFrames33;
    private double sampleElapsedSeconds;
    private bool completed;
    private bool disposed;
    private Texture2D whiteTexture;
    private GUIStyle titleStyle;
    private GUIStyle bodyStyle;
    private GUIStyle phaseStyle;
    private GUIStyle cinematicBrandStyle;
    private GUIStyle cinematicShotStyle;
    private GUIStyle cinematicCaptionStyle;
    private GUIStyle cinematicMetricStyle;
    private GUIStyle cinematicSmallStyle;
    private bool nativeTimestampWarmupPassed;
    private bool nativeTimestampMeasurementPassed;
    private bool measurePayloadRenders;
    private int measuredPayloadRenderCount;
    private bool payloadRenderRequested;
    private int payloadRenderRequestFrame = -1;
    private ulong lastPayloadCitySubmissionEpoch;
    private int measuredPrePayloadCitySubmissionCount;
    private bool measurementPrePayloadCitySubmissionsValid =
        true;
    private int measuredPayloadSrpRenderCount;
    private int measuredHeroSrpRenderCount;
    private bool measurementCameraRenderOrderValid = true;
    private bool validatingPayloadRenderGroup;
    private int payloadRenderCallbackMask;
    private int payloadRenderCallbacksInGroup;
    private ulong expectedRenderedCityEpoch;
    private int expectedRenderedCityFrame = -1;
    private int lastMeasuredHeroRenderFrame = -1;
    private int measurementMinResidentPacks = int.MaxValue;
    private int measurementMaxResidentPacks;
    private long measurementMinResidentBytes = long.MaxValue;
    private long measurementMaxResidentBytes;
    private int measurementMaxLoadingPacks;
    private int measurementMinCullCameraCount = int.MaxValue;
    private int measurementMaxCullCameraCount;
    private int measurementMinCullPackCount = int.MaxValue;
    private int measurementMaxCullPackCount;
    private int measurementCullTopologySamples;
    private bool measurementCullPassesFresh = true;
    private bool measurementCullPackSetsComplete = true;
    private ulong measurementLastCullPassEpoch;
    private ulong measurementCullTopologySequenceHash =
        1469598103934665603UL;
    private int warmupExpectedWorkloadSubmissions;
    private int warmupScheduledWorkloadIssues;
    private int warmupSensorSubmitted;
    private int warmupSensorDropped;
    private int warmupResidencySubmitted;
    private int warmupResidencyDropped;
    private int warmupDeadlineSubmitted;
    private int warmupDeadlineDropped;
    private int warmupUniqueLogicalStates;
    private string warmupIssueFrameSequenceHash = string.Empty;
    private string warmupLogicalStateSequenceHash = string.Empty;
    private bool warmupSubmissionCadenceValid;
    private bool measurementWorkloadsDrained;
    private bool cityValidationStable;
    private bool cityValidationFreshDispatches;
    private int cityValidationSamples;
    private ulong cityValidationFirstPassEpoch;
    private ulong cityValidationLastPassEpoch;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!GpuStressShowcaseConfiguration.HasArgument(
            args,
            EnableArgument))
        {
            return;
        }
        if (FindAnyObjectByType<GpuStressShowcaseController>() != null)
        {
            return;
        }
        var host = new GameObject("GPU Stress Showcase");
        host.AddComponent<GpuStressShowcaseController>();
    }

    private void Awake()
    {
        configuration = GpuStressShowcaseConfiguration.Parse(
            Environment.GetCommandLineArgs());
        RenderPipelineManager.beginCameraRendering +=
            OnBeginCameraRendering;
        tuning = GpuStressTuningDecision.Resolve(configuration.Variant);
        whiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            name = "GPU Stress Showcase HUD Pixel",
            hideFlags = HideFlags.HideAndDontSave
        };
        whiteTexture.SetPixel(0, 0, Color.white);
        whiteTexture.Apply(false, true);
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        if (configuration.Cinematic)
        {
            Screen.SetResolution(
                configuration.OutputWidth,
                configuration.OutputHeight,
                configuration.CinematicFullscreen
                    ? FullScreenMode.FullScreenWindow
                    : FullScreenMode.Windowed);
        }
    }

    private IEnumerator Start()
    {
        yield return RunShowcase();
    }

    private IEnumerator RunShowcase()
    {
        phase = "WAITING FOR PRODUCTION SCENE";
        double discoveryDeadline =
            Time.realtimeSinceStartupAsDouble + 60.0;
        while (Time.realtimeSinceStartupAsDouble < discoveryDeadline)
        {
            baseCamera = ResolveBaseCamera();
            renderers = FindObjectsByType<Bfp2GpuIndirectRenderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            if (baseCamera != null && renderers.Length > 0)
            {
                break;
            }
            yield return null;
        }
        if (baseCamera == null || renderers == null ||
            renderers.Length == 0)
        {
            Fail("The production camera or BFP2 renderer was not found.");
            yield break;
        }
        if (renderers.Length != 1)
        {
            Fail("The cinematic benchmark requires exactly one BFP2 " +
                "renderer; found " + renderers.Length + ".");
            yield break;
        }
        if (configuration.Cinematic &&
            configuration.CameraCount != 1 &&
            configuration.CameraCount != 4)
        {
            Fail("The cinematic benchmark requires one or four cameras; " +
                "requested " + configuration.CameraCount + ".");
            yield break;
        }

        try
        {
            PrepareFullCityResidency();
            ConfigureProductionScene();
            CreateAdditionalCameras();
            ConfigureDeterministicCityCullCameras();
            stack = new GpuStressShowcaseStack(configuration, tuning);
            DisableStandaloneNetworkSyncNoise();
        }
        catch (Exception exception)
        {
            Fail("GPU workload initialization failed: " + exception);
            yield break;
        }

        phase = "SCENE STREAMING / SHADER WARMUP";
        if (configuration.Cinematic)
        {
            int fixedWarmupFrames = Math.Max(
                1,
                Mathf.RoundToInt(configuration.SceneWarmupSeconds * 60.0f));
            for (int frame = 0; frame < fixedWarmupFrames; frame++)
            {
                SetCinematicProgress(frame /
                    (float)Math.Max(1, fixedWarmupFrames - 1));
                RenderExtraCameras();
                yield return null;
            }
        }
        else
        {
            double sceneWarmupEnd = Time.realtimeSinceStartupAsDouble +
                configuration.SceneWarmupSeconds;
            while (Time.realtimeSinceStartupAsDouble < sceneWarmupEnd)
            {
                SetCameraPathFrame(0);
                RenderExtraCameras();
                yield return null;
            }
        }

        phase = "WAITING FOR STABLE CITY RESIDENCY";
        double residencyDeadline =
            Time.realtimeSinceStartupAsDouble + 30.0;
        int stableResidencyFrames = 0;
        int previousResidentPacks = -1;
        long previousResidentBytes = -1L;
        while (stableResidencyFrames < 30 &&
               Time.realtimeSinceStartupAsDouble < residencyDeadline)
        {
            if (configuration.Cinematic)
            {
                SetCinematicProgress(GpuCinematicRoute.SkylineGalleryProgress);
            }
            else
            {
                SetCameraPathFrame(0);
            }
            RenderExtraCameras();
            GetCityResidencyTotals(
                out int residentPacks,
                out long residentBytes,
                out int loadingPacks);
            bool residencyReady = IsCityResidencyReady() &&
                loadingPacks == 0;
            if (residencyReady &&
                residentPacks == previousResidentPacks &&
                residentBytes == previousResidentBytes)
            {
                stableResidencyFrames++;
            }
            else
            {
                stableResidencyFrames = residencyReady ? 1 : 0;
            }
            previousResidentPacks = residentPacks;
            previousResidentBytes = residentBytes;
            yield return null;
        }
        if (stableResidencyFrames < 30)
        {
            Fail("Full-city BFP2 residency did not stabilize: " +
                DescribeCityResidency());
            yield break;
        }

        phase = "DETERMINISTIC WORKLOAD WARMUP";
        stack.BeginMeasurement();
        for (int frame = 0;
            frame < configuration.WorkloadWarmupFrames;
            frame++)
        {
            if (configuration.Cinematic)
            {
                SetCinematicProgress(frame /
                    (float)Math.Max(
                        1,
                        configuration.WorkloadWarmupFrames - 1));
            }
            else
            {
                SetCameraPathFrame(frame);
            }
            if (!TryTickStack(frame, "Workload warmup"))
            {
                yield break;
            }
            RenderExtraCameras();
            yield return null;
        }
        phase = "DRAINING WARMUP";
        yield return DrainStack(30.0);
        bool warmupDrained = stack.IsDrained;
        stack.EndMeasurement();
        warmupExpectedWorkloadSubmissions =
            CountScheduledWorkloadSubmissions(
                0,
                configuration.WorkloadWarmupFrames,
                configuration.WorkloadIssueIntervalFrames);
        warmupScheduledWorkloadIssues = stack.ScheduledIssueCount;
        warmupSensorSubmitted = stack.Sensor.Submitted;
        warmupSensorDropped = stack.Sensor.Dropped;
        warmupResidencySubmitted = stack.Residency.Submitted;
        warmupResidencyDropped = stack.Residency.Dropped;
        warmupDeadlineSubmitted = stack.Deadline.Submitted;
        warmupDeadlineDropped = stack.Deadline.Dropped;
        warmupUniqueLogicalStates = stack.UniqueLogicalStateCount;
        warmupIssueFrameSequenceHash = stack.IssueFrameSequenceHash;
        warmupLogicalStateSequenceHash = stack.LogicalStateSequenceHash;
        int expectedWarmupUniqueStates = Math.Min(
            warmupExpectedWorkloadSubmissions,
            stack.LogicalStateCount);
        warmupSubmissionCadenceValid = warmupDrained &&
            warmupScheduledWorkloadIssues ==
                warmupExpectedWorkloadSubmissions &&
            warmupSensorSubmitted == warmupExpectedWorkloadSubmissions &&
            warmupResidencySubmitted ==
                warmupExpectedWorkloadSubmissions &&
            warmupDeadlineSubmitted ==
                warmupExpectedWorkloadSubmissions &&
            warmupSensorDropped == 0 &&
            warmupResidencyDropped == 0 &&
            warmupDeadlineDropped == 0 &&
            warmupUniqueLogicalStates == expectedWarmupUniqueStates;
        if (!warmupSubmissionCadenceValid)
        {
            Fail("Warmup workload cadence was not exact and zero-drop: " +
                "expected=" + warmupExpectedWorkloadSubmissions +
                "; scheduled=" + warmupScheduledWorkloadIssues +
                "; sensor=" + warmupSensorSubmitted + "/" +
                warmupSensorDropped + "; residency=" +
                warmupResidencySubmitted + "/" +
                warmupResidencyDropped + "; deadline=" +
                warmupDeadlineSubmitted + "/" +
                warmupDeadlineDropped + "; drained=" + warmupDrained +
                "; uniqueStates=" + warmupUniqueLogicalStates + "/" +
                expectedWarmupUniqueStates + ".");
            yield break;
        }

        phase = "VALIDATING NATIVE DX12 GPU TIMESTAMPS";
        if (!configuration.Cinematic)
        {
            Fail("Native cinematic GPU timing requires cinematic mode.");
            yield break;
        }
        if (!GpuCinematicNativeTimestampBackend.TryCreate(
                out nativeTimestampBackend,
                out _,
                out string nativeCreateError))
        {
            Fail("Native DX12 GPU timestamp initialization failed: " +
                nativeCreateError);
            yield break;
        }
        for (int pair = 0; pair < NativeTimestampWarmupPairs; pair++)
        {
            SetCinematicProgress(0.0f);
            if (!nativeTimestampBackend.SubmitEmptyControl(
                    pair,
                    GpuCinematicTimestampKind.WarmupControl,
                    out string controlError))
            {
                Fail("Native timestamp warmup control failed: " +
                    controlError);
                yield break;
            }
            RenderExtraCameras();
            if (!nativeTimestampBackend.BeginHero(
                    pair,
                    GpuCinematicTimestampKind.WarmupHero,
                    out string heroError))
            {
                Fail("Native timestamp warmup hero scope failed: " +
                    heroError);
                yield break;
            }
            yield return null;
            nativeTimestampBackend.Poll(Time.frameCount, null);
            if (!ValidateNativeTimestampRuntime("warmup"))
            {
                yield break;
            }
        }
        yield return DrainNativeTimestamps(
            NativeTimestampDrainTimeoutSeconds,
            null);
        if (!nativeTimestampBackend.ValidateWarmup(
                NativeTimestampWarmupPairs,
                out string warmupTimestampError))
        {
            Fail("Native timestamp split-command validation failed: " +
                warmupTimestampError);
            yield break;
        }
        if (!nativeTimestampBackend.ResetMeasurement(
                out string resetTimestampError))
        {
            Fail("Native timestamp measurement boundary failed: " +
                resetTimestampError);
            yield break;
        }
        nativeTimestampWarmupPassed = true;

        frameSamples.Clear();
        gpuSamples.Clear();
        for (int sample = 0; sample < configuration.SampleFrames; sample++)
        {
            gpuSamples.Add(0.0f);
        }
        measuredPayloadRenderCount = 0;
        measuredPrePayloadCitySubmissionCount = 0;
        measurementPrePayloadCitySubmissionsValid =
            true;
        measuredPayloadSrpRenderCount = 0;
        measuredHeroSrpRenderCount = 0;
        measurementCameraRenderOrderValid = true;
        validatingPayloadRenderGroup = false;
        payloadRenderCallbackMask = 0;
        payloadRenderCallbacksInGroup = 0;
        expectedRenderedCityEpoch = 0UL;
        expectedRenderedCityFrame = -1;
        lastMeasuredHeroRenderFrame = -1;
        lastPayloadCitySubmissionEpoch = renderers[0] != null
            ? renderers[0].PrimaryCullPassEpoch : 0UL;
        measurementMinResidentPacks = int.MaxValue;
        measurementMaxResidentPacks = 0;
        measurementMinResidentBytes = long.MaxValue;
        measurementMaxResidentBytes = 0L;
        measurementMaxLoadingPacks = 0;
        measurementMinCullCameraCount = int.MaxValue;
        measurementMaxCullCameraCount = 0;
        measurementMinCullPackCount = int.MaxValue;
        measurementMaxCullPackCount = 0;
        measurementCullTopologySamples = 0;
        measurementCullPassesFresh = true;
        measurementCullPackSetsComplete = true;
        // The first measured sample must observe a pass completed after the
        // warmup boundary, rather than reusing the final warmup pass.
        measurementLastCullPassEpoch = renderers[0] != null
            ? renderers[0].PrimaryCullPassEpoch : 0UL;
        measurementCullTopologySequenceHash =
            1469598103934665603UL;
        longFrames16 = 0;
        longFrames33 = 0;
        measurementWorkloadsDrained = false;
        stack.BeginMeasurement();
        measurePayloadRenders = true;
        phase = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline
                ? "MEASURING A / BASELINE"
                : "MEASURING B / OPTIMIZED";
        long sampleStart = Stopwatch.GetTimestamp();
        for (int frame = 0; frame < configuration.SampleFrames; frame++)
        {
            currentSampleFrame = frame + 1;
            int workloadFrame = checked(
                configuration.WorkloadWarmupFrames + frame);
            if (configuration.Cinematic)
            {
                SetCinematicProgress(frame /
                    (float)Math.Max(
                        1,
                        configuration.SampleFrames - 1));
            }
            else
            {
                SetCameraPathFrame(workloadFrame);
            }
            if (!TryTickStack(workloadFrame, "Measured workload"))
            {
                yield break;
            }
            RenderExtraCameras();
            if (frame % NativeTimestampControlIntervalFrames == 0 &&
                !nativeTimestampBackend.SubmitEmptyControl(
                    frame,
                    GpuCinematicTimestampKind.MeasurementControl,
                    out string measurementControlError))
            {
                Fail("Native timestamp measurement control failed at " +
                    frame + ": " + measurementControlError);
                yield break;
            }
            if (!nativeTimestampBackend.BeginHero(
                    frame,
                    GpuCinematicTimestampKind.MeasurementHero,
                    out string measurementHeroError))
            {
                Fail("Native timestamp hero scope failed at " + frame +
                    ": " + measurementHeroError);
                yield break;
            }
            yield return null;
            nativeTimestampBackend.Poll(Time.frameCount, gpuSamples);
            if (!ValidateNativeTimestampRuntime("measurement"))
            {
                yield break;
            }

            latestFrameMs = Time.unscaledDeltaTime * 1000.0f;
            latestGpuMs = (float)
                nativeTimestampBackend.LastMeasurementHeroMilliseconds;
            frameSamples.Add(latestFrameMs);
            CaptureMeasurementResidencyState();
            if (latestFrameMs > 16.667f)
            {
                longFrames16++;
            }
            if (latestFrameMs > 33.333f)
            {
                longFrames33++;
            }
        }
        stack.Poll();
        measurementWorkloadsDrained = stack.IsDrained;
        measurePayloadRenders = false;
        sampleElapsedSeconds =
            (Stopwatch.GetTimestamp() - sampleStart) /
            (double)Stopwatch.Frequency;
        stack.EndMeasurement();

        phase = "DRAINING NATIVE GPU TIMESTAMPS";
        yield return DrainNativeTimestamps(
            NativeTimestampDrainTimeoutSeconds,
            gpuSamples);
        int expectedTimestampControls =
            (configuration.SampleFrames +
             NativeTimestampControlIntervalFrames - 1) /
            NativeTimestampControlIntervalFrames;
        nativeTimestampMeasurementPassed =
            nativeTimestampBackend.ValidateMeasurement(
                configuration.SampleFrames,
                expectedTimestampControls,
                gpuSamples,
                out string measurementTimestampError);
        if (!nativeTimestampMeasurementPassed)
        {
            Fail("Native timestamp evidence was incomplete: " +
                measurementTimestampError);
            yield break;
        }

        if (!measurementWorkloadsDrained)
        {
            phase = "REJECTED / WORK EXTENDED BEYOND SAMPLE WINDOW";
            yield return DrainStack(45.0);
            Fail("The final scheduled GPU workload did not complete " +
                "inside the measured frame window.");
            yield break;
        }

        phase = "OUTSIDE-TIMING OUTPUT VALIDATION";
        int validationPathFrame = checked(
            configuration.WorkloadWarmupFrames +
            configuration.SampleFrames + 1024);
        bool validationIssued;
        try
        {
            validationIssued = stack.TryIssueValidation(
                ValidationState,
                validationPathFrame);
        }
        catch (Exception exception)
        {
            Fail("Validation submission failed: " + exception);
            yield break;
        }
        if (!validationIssued)
        {
            Fail("Could not issue the deterministic validation state.");
            yield break;
        }
        yield return DrainStack(45.0);
        if (!stack.IsDrained)
        {
            Fail("GPU validation workload did not drain.");
            yield break;
        }

        GpuStressValidationSnapshot workloadValidation;
        try
        {
            workloadValidation = stack.CaptureValidation(ValidationState);
        }
        catch (Exception exception)
        {
            Fail("GPU validation readback failed: " + exception);
            yield break;
        }

        RendererCameraValidationState[] validationCameraStates = null;
        ulong validationPassEpoch = 0UL;
        if (configuration.Cinematic)
        {
            SetCinematicProgress(GpuCinematicRoute.FacadeGalleryProgress);
            for (int i = 0; i < extraCameras.Count; i++)
            {
                if (extraCameras[i] != null)
                {
                    extraCameras[i].gameObject.SetActive(false);
                }
            }
            validationCameraStates =
                BeginDeterministicCityValidation();
            validationPassEpoch =
                renderers[0].PrimaryCullPassEpoch;
            for (int frame = 0; frame < 8; frame++)
            {
                yield return null;
            }
        }
        else
        {
            SetCameraPathFrame(validationPathFrame);
            RenderExtraCameras();
            yield return null;
        }
        Bfp2CullValidationSnapshot citySnapshot = default;
        bool cityValid = false;
        cityValidationStable = false;
        cityValidationFreshDispatches = false;
        cityValidationSamples = 0;
        cityValidationFirstPassEpoch = 0UL;
        cityValidationLastPassEpoch = 0UL;
        if (configuration.Cinematic)
        {
            Bfp2CullValidationSnapshot previousSnapshot = default;
            bool previousValid = false;
            int consecutiveStableSamples = 0;
            for (int sample = 0;
                sample < CityValidationMaximumSamples;
                sample++)
            {
                yield return new WaitForEndOfFrame();
                bool currentValid =
                    renderers[0].TryCaptureFreshCullValidationSnapshot(
                        validationPassEpoch,
                        baseCamera,
                        out Bfp2CullValidationSnapshot currentSnapshot);
                cityValidationSamples++;
                if (currentValid)
                {
                    if (cityValidationFirstPassEpoch == 0UL)
                    {
                        cityValidationFirstPassEpoch =
                            currentSnapshot.PrimaryCullPassEpoch;
                    }
                    cityValidationLastPassEpoch =
                        currentSnapshot.PrimaryCullPassEpoch;
                    validationPassEpoch =
                        currentSnapshot.PrimaryCullPassEpoch;
                    consecutiveStableSamples = previousValid &&
                        CullValidationSnapshotsEqual(
                            previousSnapshot,
                            currentSnapshot)
                            ? consecutiveStableSamples + 1
                            : 1;
                    previousSnapshot = currentSnapshot;
                    previousValid = true;
                    citySnapshot = currentSnapshot;
                    if (consecutiveStableSamples >=
                        CityValidationRequiredStableSamples)
                    {
                        cityValidationStable = true;
                        break;
                    }
                }
                else
                {
                    previousValid = false;
                    consecutiveStableSamples = 0;
                }
                yield return null;
            }
            cityValidationFreshDispatches = cityValidationStable &&
                cityValidationFirstPassEpoch > 0UL &&
                cityValidationLastPassEpoch >
                    cityValidationFirstPassEpoch;
            cityValid = cityValidationStable &&
                cityValidationFreshDispatches &&
                citySnapshot.PackSetComplete &&
                citySnapshot.DispatchedPackCount ==
                    renderers[0].ResidentPackCount;
            RestoreRendererCameraValidationStates(
                validationCameraStates);

            for (int i = 0; i < extraCameras.Count; i++)
            {
                if (extraCameras[i] != null)
                {
                    extraCameras[i].gameObject.SetActive(true);
                }
            }
        }
        else
        {
            yield return new WaitForEndOfFrame();
            cityValid = renderers[0].TryCaptureCullValidationSnapshot(
                out citySnapshot);
            cityValidationStable = cityValid;
            cityValidationFreshDispatches = cityValid;
            cityValidationSamples = 1;
            cityValidationFirstPassEpoch =
                citySnapshot.PrimaryCullPassEpoch;
            cityValidationLastPassEpoch =
                citySnapshot.PrimaryCullPassEpoch;
        }
        string activeAlgorithm =
            renderers[0].ActiveClusterCullAlgorithmName;
        bool rendererPathValid = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline
                ? string.Equals(
                    activeAlgorithm,
                    nameof(Bfp2ClusterCullAlgorithm.ScalarAoS),
                    StringComparison.Ordinal)
                : string.Equals(
                    activeAlgorithm,
                    nameof(Bfp2ClusterCullAlgorithm.WaveTile32),
                    StringComparison.Ordinal);

        GpuStressShowcaseReport report = BuildReport(
            workloadValidation,
            cityValid,
            rendererPathValid,
            citySnapshot,
            activeAlgorithm);
        phase = report.qualityPassed
            ? "COMPLETED / OUTPUT VALIDATED"
            : "COMPLETED / VALIDATION FAILED";
        completed = true;
        completedReport = report;
        try
        {
            WriteReport(report);
        }
        catch (Exception exception)
        {
            Fail("Benchmark evidence write failed: " + exception);
            yield break;
        }
        UpdateHudText(force: true);

        if (!string.IsNullOrWhiteSpace(configuration.ScreenshotPath))
        {
            if (configuration.Cinematic)
            {
                yield return CaptureCinematicGallery(
                    configuration.ScreenshotPath);
            }
            else
            {
                string absoluteScreenshot = Path.GetFullPath(
                    configuration.ScreenshotPath);
                string screenshotDirectory = Path.GetDirectoryName(
                    absoluteScreenshot);
                if (!string.IsNullOrEmpty(screenshotDirectory))
                {
                    Directory.CreateDirectory(screenshotDirectory);
                }
                ScreenCapture.CaptureScreenshot(absoluteScreenshot);
                for (int frame = 0; frame < 4; frame++)
                {
                    yield return null;
                }
            }
        }

        if (configuration.AutoExit)
        {
            Application.Quit(report.qualityPassed ? 0 : 2);
        }
    }

    private IEnumerator DrainStack(double timeoutSeconds)
    {
        double deadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (!stack.IsDrained &&
               Time.realtimeSinceStartupAsDouble < deadline)
        {
            stack.Poll();
            yield return null;
        }
    }

    private IEnumerator DrainNativeTimestamps(
        double timeoutSeconds,
        IList<float> measurementSamples)
    {
        if (nativeTimestampBackend == null)
        {
            yield break;
        }
        double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (nativeTimestampBackend.PendingCount > 0 &&
               nativeTimestampBackend.Operational &&
               Time.realtimeSinceStartupAsDouble < deadline)
        {
            nativeTimestampBackend.Poll(
                Time.frameCount,
                measurementSamples);
            if (nativeTimestampBackend.PendingCount > 0)
            {
                yield return null;
            }
        }
        nativeTimestampBackend.Poll(Time.frameCount, measurementSamples);
        if (nativeTimestampBackend.PendingCount > 0)
        {
            nativeTimestampBackend.MarkPendingTimeouts();
        }
    }

    private bool ValidateNativeTimestampRuntime(string stage)
    {
        if (nativeTimestampBackend != null &&
            nativeTimestampBackend.Operational &&
            string.IsNullOrEmpty(nativeTimestampFailure))
        {
            return true;
        }
        Fail("Native GPU timestamp runtime failed during " + stage +
            ": " + (string.IsNullOrEmpty(nativeTimestampFailure)
                ? "backend entered a terminal state"
                : nativeTimestampFailure));
        return false;
    }

    private void PrepareFullCityResidency()
    {
        NYCGISMapWorldService[] mapServices =
            FindObjectsByType<NYCGISMapWorldService>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
        for (int i = 0; i < mapServices.Length; i++)
        {
            mapServices[i].bootstrapMissionAreaAroundActiveCamera = false;
            mapServices[i].ClearMissionArea();
        }
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].ApplyDataRootConfig();
            renderers[i].RescanPacks();
        }
    }

    private void ConfigureProductionScene()
    {
        DemoHUD[] huds = FindObjectsByType<DemoHUD>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        for (int i = 0; i < huds.Length; i++)
        {
            huds[i].showHud = false;
        }
        TimeOfDayController[] timeControllers =
            FindObjectsByType<TimeOfDayController>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
        for (int i = 0; i < timeControllers.Length; i++)
        {
            timeControllers[i].showControls = false;
        }
        NYCGISDynamicBudgetController[] dynamicBudgets =
            FindObjectsByType<NYCGISDynamicBudgetController>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
        for (int i = 0; i < dynamicBudgets.Length; i++)
        {
            dynamicBudgets[i].enableDynamicBudgets = false;
        }
        FreeFlyCamera[] freeFlyCameras =
            FindObjectsByType<FreeFlyCamera>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
        for (int i = 0; i < freeFlyCameras.Length; i++)
        {
            freeFlyCameras[i].SetControlEnabled(false);
        }

        for (int i = 0; i < renderers.Length; i++)
        {
            Bfp2GpuIndirectRenderer renderer = renderers[i];
            renderer.productionMode = true;
            renderer.showDebugPanel = false;
            renderer.enableDebugReadback = false;
            renderer.enableDetailedRuntimeStats = false;
            renderer.enableGpuProfilerMarkers = false;
            renderer.enableScreenSizeCulling = false;
            if (configuration.Cinematic)
            {
                renderer.loadAllEnabledPacksResident = true;
                renderer.pinLoadedPacks = true;
                renderer.maxUploadPacksPerFrame = Mathf.Max(
                    renderer.maxUploadPacksPerFrame,
                    4);
            }
            renderer.clusterCullAlgorithm = configuration.Variant ==
                GpuStressShowcaseVariant.Baseline
                    ? Bfp2ClusterCullAlgorithm.ScalarAoS
                    : Bfp2ClusterCullAlgorithm.WaveTile32;
        }

        cameraOriginPosition = baseCamera.transform.position;
        cameraOriginRotation = baseCamera.transform.rotation;
        overviewCameraPosition = cameraOriginPosition;
        overviewCameraRotation = cameraOriginRotation;
        overviewOrthographicSize = baseCamera.orthographicSize;
        baseCamera.targetTexture = null;
        weatherSystem = NYCGISWeatherSystem.Instance;

        if (configuration.Cinematic)
        {
            ConfigureCinematicPresentation(timeControllers);
        }
        else if (weatherSystem != null && weatherSystem.IsReady)
        {
            weatherSystem.ApplyProfile(
                NYCGISWeatherProfile.HeavyStorm,
                0.0f);
            weatherSystem.GetComponent<NYCGISWeatherVisualController>()
                ?.RefreshNow();
        }
    }

    private void ConfigureCinematicPresentation(
        TimeOfDayController[] timeControllers)
    {
        baseCamera.orthographic = false;
        baseCamera.fieldOfView = 50.0f;
        baseCamera.nearClipPlane = 1.0f;
        baseCamera.farClipPlane = Mathf.Max(
            baseCamera.farClipPlane,
            60000.0f);
        baseCamera.allowHDR = true;
        baseCamera.allowDynamicResolution = false;
        baseCameraWasEnabled = baseCamera.enabled;
        CreateCinematicHeroOutput();
        baseCamera.enabled = false;

        UniversalAdditionalCameraData cameraData =
            baseCamera.GetUniversalAdditionalCameraData();
        cameraData.renderPostProcessing = true;
        cameraData.renderShadows = true;
        cameraData.requiresColorOption = CameraOverrideOption.Off;
        cameraData.requiresDepthOption = CameraOverrideOption.On;
        cameraData.antialiasing =
            AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        cameraData.antialiasingQuality = AntialiasingQuality.High;
        cameraData.stopNaN = true;
        cameraData.dithering = true;

        for (int i = 0; i < timeControllers.Length; i++)
        {
            TimeOfDayController time = timeControllers[i];
            time.animateTime = false;
            time.SetTimeOfDay(configuration.CinematicTimeOfDayHours);
            time.ApplyLightingNow();
        }

        if (weatherSystem != null && weatherSystem.IsReady)
        {
            weatherSystem.showRuntimeControls = false;
            weatherSystem.advanceLocalSimulationTime = false;
            weatherSystem.ApplyProfile(NYCGISWeatherProfile.Clear, 0.0f);
            weatherSystem.TrySetSimulationTime(0.0, out _);
            weatherSystem.GetComponent<NYCGISWeatherVisualController>()
                ?.RefreshNow();
        }

        NYCGISWeatherTimelineController[] weatherTimelines =
            FindObjectsByType<NYCGISWeatherTimelineController>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
        for (int i = 0; i < weatherTimelines.Length; i++)
        {
            weatherTimelines[i].enabled = false;
        }

        orthophoto = FindAnyObjectByType<
            OrthophotoVirtualTextureController>();
        if (orthophoto != null)
        {
            orthophoto.flightCameraOverride = baseCamera;
            orthophoto.highPrecisionCameraOverride = baseCamera;
            orthophoto.textureAnisoLevel = 16;
            orthophoto.textureMipMapBias = -0.35f;
            orthophoto.forceEnableAnisotropicFiltering = true;
            orthophoto.refreshIntervalSeconds = 0.0f;
            orthophoto.useCameraRefreshHysteresis = false;
        }

        BuildCinematicRoute();
        ConfigureHeroFeed();
        CreateCinematicVolume();
        SetCinematicProgress(0.0f);
    }

    private void BuildCinematicRoute()
    {
        Camera skyline = FindAuthoredCamera(
            "Camera_Profile_1200m_Manhattan_Oblique");
        Camera lateral = FindAuthoredCamera("Camera_Aerial_Oblique");
        Camera medium = FindAuthoredCamera("Camera_FreeFly");
        Camera roofline = FindAuthoredCamera("Camera_Close_Check");
        Camera close = FindAuthoredCamera(
            "Camera_Profile_200m_Zoom_Check");

        Vector3 skylinePosition = skyline != null
            ? skyline.transform.position
            : new Vector3(-624.7369f, 1200.0f, -1073.6841f);
        Vector3 lateralPosition = lateral != null
            ? lateral.transform.position
            : new Vector3(520.0f, 1100.0f, -900.0f);
        Vector3 mediumPosition = medium != null
            ? medium.transform.position
            : new Vector3(180.0f, 700.0f, -260.0f);
        Vector3 rooflinePosition = roofline != null
            ? roofline.transform.position
            : new Vector3(130.0f, 520.0f, -200.0f);
        Vector3 closePosition = close != null
            ? close.transform.position
            : new Vector3(-94.73686f, 200.0f, -213.68413f);
        Quaternion closeRotation = close != null
            ? close.transform.rotation
            : Quaternion.Euler(28.0f, 25.0f, 0.0f);

        cinematicRoute = new GpuCinematicRoute(
            skylinePosition,
            lateralPosition,
            mediumPosition,
            rooflinePosition,
            closePosition,
            closeRotation);
        if (!cinematicRoute.TryValidate(out string routeError))
        {
            throw new InvalidOperationException(routeError);
        }
    }

    private static Camera FindAuthoredCamera(string cameraName)
    {
        Camera[] cameras = FindObjectsByType<Camera>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] != null &&
                string.Equals(
                    cameras[i].name,
                    cameraName,
                    StringComparison.Ordinal))
            {
                return cameras[i];
            }
        }
        return null;
    }

    private void ConfigureHeroFeed()
    {
        NYCGISWeatherCameraRegistry.ConfigureCapacity(
            NYCGISWeatherCameraRegistry.HardCapacity);
        heroFeed = baseCamera.GetComponent<
            NYCGISPayloadVisibilityCamera>();
        if (heroFeed == null)
        {
            heroFeed = baseCamera.gameObject.AddComponent<
                NYCGISPayloadVisibilityCamera>();
        }
        NYCGISWeatherCameraRegistry.Unregister(heroFeed);
        heroFeed.feedId = FirstFeedId - 1;
        heroFeed.role = NYCGISWeatherCameraRole.MLCSelected;
        heroFeed.quality = NYCGISWeatherCameraQuality.Primary;
        heroFeed.thumbnailQuality =
            NYCGISWeatherCameraQuality.Thumbnail;
        heroFeed.selectedByDefault = true;
        heroFeed.streamingPriority = 1200;
        heroFeed.weatherSystem = weatherSystem;
        heroFeed.payloadBand = NYCGISPayloadBand.EO;
        heroFeed.evaluationRangeMeters = 16000.0f;
        heroFeed.targetTexture = null;
        heroFeed.refreshRateHz = 60.0f;
        heroFeed.hidden = false;
        heroFeed.EnsureRegisteredFromBootstrap();
        if (!heroFeed.Select())
        {
            throw new InvalidOperationException(
                "The cinematic hero camera could not become the selected " +
                "virtual-texture feed.");
        }
    }

    private void CreateCinematicVolume()
    {
        cinematicVolumeHost = new GameObject(
            "GPU Cinematic Benchmark Volume");
        cinematicVolumeProfile =
            ScriptableObject.CreateInstance<VolumeProfile>();
        cinematicVolumeProfile.name =
            "GPU Cinematic Benchmark Runtime Profile";
        Volume volume = cinematicVolumeHost.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 1000.0f;
        volume.profile = cinematicVolumeProfile;

        Tonemapping tonemapping =
            cinematicVolumeProfile.Add<Tonemapping>(true);
        tonemapping.mode.Override(TonemappingMode.ACES);
        Bloom bloom = cinematicVolumeProfile.Add<Bloom>(true);
        bloom.intensity.Override(0.22f);
        bloom.threshold.Override(1.05f);
        bloom.scatter.Override(0.55f);
        Vignette vignette =
            cinematicVolumeProfile.Add<Vignette>(true);
        vignette.intensity.Override(0.16f);
        vignette.smoothness.Override(0.42f);
        ColorAdjustments color =
            cinematicVolumeProfile.Add<ColorAdjustments>(true);
        color.postExposure.Override(0.08f);
        color.contrast.Override(9.0f);
        color.saturation.Override(6.0f);
        WhiteBalance whiteBalance =
            cinematicVolumeProfile.Add<WhiteBalance>(true);
        whiteBalance.temperature.Override(14.0f);
        whiteBalance.tint.Override(-2.0f);
    }

    private void CreateCinematicHeroOutput()
    {
        cinematicHeroOutput = new RenderTexture(
            configuration.OutputWidth,
            configuration.OutputHeight,
            24,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Default)
        {
            name = "GPU Cinematic Hero 1080p",
            useMipMap = false,
            autoGenerateMips = false,
            antiAliasing = 1
        };
        if (!cinematicHeroOutput.Create())
        {
            throw new InvalidOperationException(
                "The cinematic hero render target could not be created.");
        }
        baseCamera.targetTexture = cinematicHeroOutput;
    }

    private static void DisableStandaloneNetworkSyncNoise()
    {
        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        int disabledCount = 0;
        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];
            if (behaviour == null)
            {
                continue;
            }
            string fullName = behaviour.GetType().FullName ?? string.Empty;
            if (fullName == "MissionAreaSync" ||
                fullName == "MissionEntityMirror" ||
                fullName == "MissionStatusHUD" ||
                fullName == "TimeOfDaySync" ||
                fullName == "TimeService+Runner")
            {
                behaviour.enabled = false;
                disabledCount++;
            }
        }
        UnityEngine.Debug.Log(
            "GPU stress showcase disabled " + disabledCount +
            " standalone network-sync component(s).");
    }

    private void CreateAdditionalCameras()
    {
        float[] yawOffsets = { -28.0f, 24.0f, 48.0f };
        for (int i = 0; i < configuration.CameraCount - 1; i++)
        {
            var host = new GameObject(
                "GPU Stress Payload Camera " + (i + 2));
            host.SetActive(false);
            Camera camera = host.AddComponent<Camera>();
            camera.CopyFrom(baseCamera);
            camera.enabled = false;
            bool fixedOverview = configuration.Cinematic && i == 0;
            camera.orthographic = fixedOverview;
            camera.depth = baseCamera.depth - 10.0f - i;
            camera.tag = "Untagged";
            if (fixedOverview)
            {
                camera.transform.SetPositionAndRotation(
                    overviewCameraPosition,
                    overviewCameraRotation);
                camera.orthographicSize = overviewOrthographicSize;
            }
            var output = new RenderTexture(
                960,
                540,
                24,
                RenderTextureFormat.ARGB32)
            {
                name = "GPU_Stress_Payload_" + (i + 2),
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1
            };
            output.Create();
            camera.targetTexture = output;

            UniversalAdditionalCameraData additional =
                camera.GetUniversalAdditionalCameraData();
            additional.renderPostProcessing = false;
            additional.renderShadows = true;
            additional.requiresColorOption = CameraOverrideOption.Off;
            additional.requiresDepthOption = CameraOverrideOption.Off;

            NYCGISPayloadVisibilityCamera feed =
                host.AddComponent<NYCGISPayloadVisibilityCamera>();
            feed.feedId = FirstFeedId + i;
            feed.role = fixedOverview
                ? NYCGISWeatherCameraRole.MLCThumbnail
                : NYCGISWeatherCameraRole.UOC;
            feed.quality = fixedOverview
                ? NYCGISWeatherCameraQuality.Thumbnail
                : NYCGISWeatherCameraQuality.Standard;
            feed.thumbnailQuality = NYCGISWeatherCameraQuality.Thumbnail;
            feed.selectedByDefault = false;
            feed.streamingPriority = fixedOverview ? 320 : 850 - i;
            feed.weatherSystem = weatherSystem;
            feed.payloadBand = (NYCGISPayloadBand)(i % 5);
            feed.evaluationRangeMeters = 12000.0f;
            feed.targetTexture = output;
            feed.refreshRateHz = 0.0f;
            feed.hidden = false;

            host.SetActive(true);
            extraCameras.Add(camera);
            extraCameraYaw.Add(yawOffsets[i]);
            extraOutputs.Add(output);
            extraCameraFixed.Add(fixedOverview);
        }
    }

    private void ConfigureDeterministicCityCullCameras()
    {
        Camera[] payloadCameras = extraCameras.ToArray();
        for (int i = 0; i < renderers.Length; i++)
        {
            Bfp2GpuIndirectRenderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }
            renderer.cameraOverride = baseCamera;
            renderer.forceCameraOverride = true;
            renderer.forceSubmitAllResidentPacks = true;
            renderer.enableRenderScheduler = false;
            renderer.enableRenderSchedulerPilot = false;
            renderer.submitDrawsToAllCamerasForShadows = true;
            renderer.useWeatherCameraRegistry = false;
            renderer.enableBatchedCameraFrameBuffer = true;
            renderer.maxBfp2CameraFrames = Mathf.Clamp(
                1 + payloadCameras.Length,
                1,
                8);
            renderer.batchedCullCameras = payloadCameras;
            renderer.enableAdaptiveCameraCullGrouping = false;
        }
    }

    private string ResolveCityCullCameraMode()
    {
        return configuration != null &&
            configuration.CameraCount == 1
                ? CityCullHeroOnlyMode
                : CityCullHeroPayloadMode;
    }

    private void SetCameraPathFrame(int logicalFrame)
    {
        GpuStressShowcaseMath.EvaluateCameraPose(
            cameraOriginPosition,
            cameraOriginRotation,
            logicalFrame,
            out desiredCameraPosition,
            out desiredCameraRotation);
        cameraPoseValid = true;
        ApplyDesiredCameraPose();
    }

    private void SetCinematicProgress(float progress)
    {
        if (cinematicRoute == null)
        {
            return;
        }
        GpuCinematicShot shot = cinematicRoute.Evaluate(progress);
        desiredCameraPosition = shot.Position;
        desiredCameraRotation = shot.Rotation;
        desiredCameraFieldOfView = shot.FieldOfView;
        cinematicProgress = shot.Progress;
        cinematicShotTitle = shot.Title;
        cinematicShotSubtitle = shot.Subtitle;
        cameraPoseValid = true;
        if (weatherSystem != null)
        {
            weatherSystem.TrySetSimulationTime(
                cinematicProgress *
                configuration.CinematicDurationSeconds,
                out _);
        }
        ApplyDesiredCameraPose();
    }

    private void LateUpdate()
    {
        if (cameraPoseValid)
        {
            ApplyDesiredCameraPose();
        }
        try
        {
            FlushQueuedPayloadRenders();
        }
        finally
        {
            if (nativeTimestampBackend != null &&
                nativeTimestampBackend.HasActiveHero &&
                !nativeTimestampBackend.EndHero(out string endError))
            {
                nativeTimestampFailure =
                    "Could not submit the native timestamp END: " +
                    endError;
            }
        }
        if (Time.unscaledTime >= nextHudRefresh)
        {
            nextHudRefresh = Time.unscaledTime + 0.2f;
            UpdateHudText(force: false);
        }
    }

    private void ApplyDesiredCameraPose()
    {
        if (baseCamera == null)
        {
            return;
        }
        baseCamera.transform.SetPositionAndRotation(
            desiredCameraPosition,
            desiredCameraRotation);
        if (configuration.Cinematic)
        {
            baseCamera.fieldOfView = desiredCameraFieldOfView;
        }
        for (int i = 0; i < extraCameras.Count; i++)
        {
            Camera camera = extraCameras[i];
            if (camera != null)
            {
                bool isFixed = i < extraCameraFixed.Count &&
                    extraCameraFixed[i];
                if (!isFixed)
                {
                    camera.transform.SetPositionAndRotation(
                        desiredCameraPosition,
                        desiredCameraRotation * Quaternion.Euler(
                            0.0f,
                            extraCameraYaw[i],
                            0.0f));
                    if (!camera.orthographic)
                    {
                        camera.fieldOfView =
                            desiredCameraFieldOfView;
                    }
                }
            }
        }
    }

    private void RenderExtraCameras()
    {
        ApplyDesiredCameraPose();
        if (payloadRenderRequested)
        {
            if (measurePayloadRenders)
            {
                measurementPrePayloadCitySubmissionsValid =
                    false;
            }
            return;
        }
        payloadRenderRequested = true;
        payloadRenderRequestFrame = Time.frameCount;
    }

    private void FlushQueuedPayloadRenders()
    {
        if (!payloadRenderRequested)
        {
            return;
        }

        payloadRenderRequested = false;
        bool citySubmittedBeforePayload =
            payloadRenderRequestFrame == Time.frameCount &&
            ValidateCurrentCitySubmission();
        if (measurePayloadRenders)
        {
            measuredPrePayloadCitySubmissionCount++;
            measurementPrePayloadCitySubmissionsValid &=
                citySubmittedBeforePayload;
            expectedRenderedCityEpoch = renderers[0] != null
                ? renderers[0].PrimaryCullPassEpoch : 0UL;
            expectedRenderedCityFrame = Time.frameCount;
            payloadRenderCallbackMask = 0;
            payloadRenderCallbacksInGroup = 0;
            validatingPayloadRenderGroup = true;
        }

        for (int i = 0; i < extraCameras.Count; i++)
        {
            Camera camera = extraCameras[i];
            if (camera != null &&
                camera.gameObject.activeInHierarchy &&
                camera.targetTexture != null)
            {
                camera.Render();
                if (measurePayloadRenders)
                {
                    measuredPayloadRenderCount++;
                }
                // The weather registry owns visibility/quality scheduling, but
                // this benchmark owns the render cadence. Its early Update may
                // enable a feed for normal SRP rendering; disable it after the
                // explicit render so every logical frame has exactly one pass.
                camera.enabled = false;
            }
        }

        if (baseCamera != null &&
            baseCamera.gameObject.activeInHierarchy &&
            baseCamera.targetTexture != null)
        {
            // Explicit rendering keeps the hero workload inside the same
            // GPU-timing boundary as payload cameras.
            baseCamera.enabled = false;
            try
            {
                baseCamera.Render();
            }
            finally
            {
                baseCamera.enabled = false;
            }
        }

        if (measurePayloadRenders)
        {
            int expectedMask = (1 << extraCameras.Count) - 1;
            measurementCameraRenderOrderValid &=
                payloadRenderCallbacksInGroup == extraCameras.Count &&
                payloadRenderCallbackMask == expectedMask;
            validatingPayloadRenderGroup = false;
        }
    }

    private bool ValidateCurrentCitySubmission()
    {
        if (configuration == null || !configuration.Cinematic)
        {
            return true;
        }
        if (renderers == null || renderers.Length != 1 ||
            renderers[0] == null)
        {
            return false;
        }

        Bfp2GpuIndirectRenderer renderer = renderers[0];
        ulong epoch = renderer.PrimaryCullPassEpoch;
        bool valid = epoch ==
                lastPayloadCitySubmissionEpoch + 1UL &&
            renderer.PrimaryCullPassDispatchFrame ==
                Time.frameCount &&
            renderer.PrimaryCullPassCameraCount ==
                configuration.CameraCount &&
            renderer.PrimaryCullPassPackSetComplete &&
            renderer.PrimaryCullPassPackCount ==
                renderer.ResidentPackCount &&
            renderer.submitDrawsToAllCamerasForShadows;
        lastPayloadCitySubmissionEpoch = epoch;
        return valid;
    }

    private void OnBeginCameraRendering(
        ScriptableRenderContext context,
        Camera camera)
    {
        if (camera != null && camera == baseCamera &&
            nativeTimestampBackend != null)
        {
            nativeTimestampBackend.NoteHeroSrpCallback();
        }
        if (!measurePayloadRenders || camera == null)
        {
            return;
        }

        if (camera == baseCamera)
        {
            measuredHeroSrpRenderCount++;
            bool uniqueFrame = lastMeasuredHeroRenderFrame !=
                Time.frameCount;
            measurementCameraRenderOrderValid &=
                uniqueFrame && CurrentRenderUsesExpectedCityPass();
            lastMeasuredHeroRenderFrame = Time.frameCount;
            return;
        }

        int payloadIndex = extraCameras.IndexOf(camera);
        if (payloadIndex < 0)
        {
            return;
        }

        measuredPayloadSrpRenderCount++;
        payloadRenderCallbacksInGroup++;
        int cameraBit = 1 << payloadIndex;
        bool firstRenderInGroup =
            (payloadRenderCallbackMask & cameraBit) == 0;
        measurementCameraRenderOrderValid &=
            validatingPayloadRenderGroup &&
            firstRenderInGroup &&
            CurrentRenderUsesExpectedCityPass();
        payloadRenderCallbackMask |= cameraBit;
    }

    private bool CurrentRenderUsesExpectedCityPass()
    {
        if (renderers == null || renderers.Length != 1 ||
            renderers[0] == null)
        {
            return false;
        }
        Bfp2GpuIndirectRenderer renderer = renderers[0];
        return expectedRenderedCityEpoch > 0UL &&
            renderer.PrimaryCullPassEpoch ==
                expectedRenderedCityEpoch &&
            renderer.PrimaryCullPassDispatchFrame ==
                expectedRenderedCityFrame &&
            expectedRenderedCityFrame == Time.frameCount;
    }

    private void CaptureMeasurementResidencyState()
    {
        GetCityResidencyTotals(
            out int residentPacks,
            out long residentBytes,
            out int loadingPacks);
        measurementMinResidentPacks = Math.Min(
            measurementMinResidentPacks,
            residentPacks);
        measurementMaxResidentPacks = Math.Max(
            measurementMaxResidentPacks,
            residentPacks);
        measurementMinResidentBytes = Math.Min(
            measurementMinResidentBytes,
            residentBytes);
        measurementMaxResidentBytes = Math.Max(
            measurementMaxResidentBytes,
            residentBytes);
        measurementMaxLoadingPacks = Math.Max(
            measurementMaxLoadingPacks,
            loadingPacks);
        CaptureMeasurementCullTopology();
    }

    private void CaptureMeasurementCullTopology()
    {
        if (renderers == null || renderers.Length != 1 ||
            renderers[0] == null)
        {
            measurementCullPassesFresh = false;
            measurementCullPackSetsComplete = false;
            return;
        }
        Bfp2GpuIndirectRenderer renderer = renderers[0];
        ulong epoch = renderer.PrimaryCullPassEpoch;
        int cameraCount = renderer.PrimaryCullPassCameraCount;
        int packCount = renderer.PrimaryCullPassPackCount;
        if (epoch == 0UL || epoch <= measurementLastCullPassEpoch)
        {
            measurementCullPassesFresh = false;
        }
        measurementLastCullPassEpoch = epoch;
        measurementCullPackSetsComplete &=
            renderer.PrimaryCullPassPackSetComplete;
        measurementMinCullCameraCount = Math.Min(
            measurementMinCullCameraCount,
            cameraCount);
        measurementMaxCullCameraCount = Math.Max(
            measurementMaxCullCameraCount,
            cameraCount);
        measurementMinCullPackCount = Math.Min(
            measurementMinCullPackCount,
            packCount);
        measurementMaxCullPackCount = Math.Max(
            measurementMaxCullPackCount,
            packCount);
        measurementCullTopologySequenceHash =
            HashTopologySequenceValue(
                measurementCullTopologySequenceHash,
                renderer.PrimaryCullPassTopologyHash);
        measurementCullTopologySamples++;
    }

    private static ulong HashTopologySequenceValue(
        ulong hash,
        ulong value)
    {
        unchecked
        {
            hash ^= (uint)(value & uint.MaxValue);
            hash *= 1099511628211UL;
            hash ^= (uint)(value >> 32);
            return hash * 1099511628211UL;
        }
    }

    private void GetCityResidencyTotals(
        out int residentPacks,
        out long residentBytes,
        out int loadingPacks)
    {
        residentPacks = 0;
        residentBytes = 0L;
        loadingPacks = 0;
        for (int i = 0; i < renderers.Length; i++)
        {
            Bfp2GpuIndirectRenderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }
            residentPacks += renderer.ResidentPackCount;
            residentBytes += renderer.ResidentGpuBytes;
            loadingPacks += renderer.LoadingPackCount;
        }
    }

    private static int CountScheduledWorkloadSubmissions(
        int firstLogicalFrame,
        int frameCount,
        int intervalFrames)
    {
        if (frameCount <= 0)
        {
            return 0;
        }

        int interval = Math.Max(1, intervalFrames);
        int remainder = firstLogicalFrame % interval;
        int firstOffset = remainder == 0 ? 0 : interval - remainder;
        if (firstOffset >= frameCount)
        {
            return 0;
        }
        return 1 + ((frameCount - 1 - firstOffset) / interval);
    }

    private RendererCameraValidationState[]
        BeginDeterministicCityValidation()
    {
        var states = new RendererCameraValidationState[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            Bfp2GpuIndirectRenderer renderer = renderers[i];
            states[i] = new RendererCameraValidationState
            {
                Renderer = renderer,
                CameraOverride = renderer != null
                    ? renderer.cameraOverride
                    : null,
                ForceCameraOverride = renderer != null &&
                    renderer.forceCameraOverride,
                UseWeatherCameraRegistry = renderer != null &&
                    renderer.useWeatherCameraRegistry,
                EnableBatchedCameraFrameBuffer = renderer != null &&
                    renderer.enableBatchedCameraFrameBuffer
            };
            if (renderer == null)
            {
                continue;
            }
            renderer.cameraOverride = baseCamera;
            renderer.forceCameraOverride = true;
            renderer.useWeatherCameraRegistry = false;
            renderer.enableBatchedCameraFrameBuffer = false;
        }
        return states;
    }

    private static void RestoreRendererCameraValidationStates(
        RendererCameraValidationState[] states)
    {
        if (states == null)
        {
            return;
        }
        for (int i = 0; i < states.Length; i++)
        {
            RendererCameraValidationState state = states[i];
            if (state.Renderer == null)
            {
                continue;
            }
            state.Renderer.cameraOverride = state.CameraOverride;
            state.Renderer.forceCameraOverride =
                state.ForceCameraOverride;
            state.Renderer.useWeatherCameraRegistry =
                state.UseWeatherCameraRegistry;
            state.Renderer.enableBatchedCameraFrameBuffer =
                state.EnableBatchedCameraFrameBuffer;
        }
    }

    private static bool CullValidationSnapshotsEqual(
        Bfp2CullValidationSnapshot left,
        Bfp2CullValidationSnapshot right)
    {
        return left.PreparedCameraCount == right.PreparedCameraCount &&
            left.CameraStateHash == right.CameraStateHash &&
            left.DispatchedPackCount == right.DispatchedPackCount &&
            left.DispatchedPackHash == right.DispatchedPackHash &&
            left.PackSetComplete == right.PackSetComplete &&
            left.ResidentPacks == right.ResidentPacks &&
            left.VisibleClusters == right.VisibleClusters &&
            left.VisibleIndices == right.VisibleIndices &&
            left.CulledClusters == right.CulledClusters &&
            left.OverflowClusters == right.OverflowClusters &&
            left.DrawIndices == right.DrawIndices &&
            left.VisibleTiles == right.VisibleTiles &&
            left.PaddedDrawIndices == right.PaddedDrawIndices &&
            left.PerPackHash == right.PerPackHash;
    }

    private GpuStressShowcaseReport BuildReport(
        GpuStressValidationSnapshot workloadValidation,
        bool cityValid,
        bool rendererPathValid,
        Bfp2CullValidationSnapshot city,
        string activeAlgorithm)
    {
        string variantName = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline
                ? "baseline"
                : "optimized";
        ulong logicalOutputBytes = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline
                ? city.VisibleIndices * sizeof(uint)
                : city.VisibleTiles * 2UL * sizeof(uint);
        ulong avoidedTraffic = configuration.Variant ==
            GpuStressShowcaseVariant.Optimized
                ? city.VisibleIndices * 2UL * sizeof(uint)
                : 0UL;
        string cityHash = city.PerPackHash.ToString(
            "X16",
            Invariant);
        int expectedPayloadRenderCount =
            frameSamples.Count * extraCameras.Count;
        bool payloadRenderCountValid =
            measuredPayloadRenderCount == expectedPayloadRenderCount;
        bool prePayloadCitySubmissionValid =
            !configuration.Cinematic ||
            (measurementPrePayloadCitySubmissionsValid &&
             measuredPrePayloadCitySubmissionCount ==
                frameSamples.Count);
        int expectedPayloadSrpRenderCount =
            expectedPayloadRenderCount;
        int expectedHeroSrpRenderCount = frameSamples.Count;
        bool cameraRenderOrderValid =
            !configuration.Cinematic ||
            (measurementCameraRenderOrderValid &&
             measuredPayloadSrpRenderCount ==
                expectedPayloadSrpRenderCount &&
             measuredHeroSrpRenderCount == expectedHeroSrpRenderCount);
        int expectedWorkloadSubmissions =
            CountScheduledWorkloadSubmissions(
                configuration.WorkloadWarmupFrames,
                frameSamples.Count,
                configuration.WorkloadIssueIntervalFrames);
        int expectedUniqueLogicalStates = Math.Min(
            expectedWorkloadSubmissions,
            stack.LogicalStateCount);
        bool workloadSubmissionCadenceValid =
            expectedWorkloadSubmissions > 0 &&
            stack.ScheduledIssueCount == expectedWorkloadSubmissions &&
            stack.Sensor.Submitted == expectedWorkloadSubmissions &&
            stack.Residency.Submitted == expectedWorkloadSubmissions &&
            stack.Deadline.Submitted == expectedWorkloadSubmissions &&
            stack.Sensor.Dropped == 0 &&
            stack.Residency.Dropped == 0 &&
            stack.Deadline.Dropped == 0 &&
            stack.UniqueLogicalStateCount == expectedUniqueLogicalStates &&
            warmupSubmissionCadenceValid &&
            measurementWorkloadsDrained;
        bool sampleCompletenessValid =
            frameSamples.Count == configuration.SampleFrames &&
            gpuSamples.Count == configuration.SampleFrames;
        int expectedTimestampControls =
            (configuration.SampleFrames +
             NativeTimestampControlIntervalFrames - 1) /
            NativeTimestampControlIntervalFrames;
        string nativeTimestampDllSha256 =
            ComputeNativeTimestampDllSha256();
        bool gpuTimingCoverageValid = frameSamples.Count > 0 &&
            nativeTimestampBackend != null &&
            nativeTimestampWarmupPassed &&
            nativeTimestampMeasurementPassed &&
            nativeTimestampBackend.OrderingDiscriminatorPassed &&
            nativeTimestampBackend.MeasurementHeroSubmitted ==
                frameSamples.Count &&
            nativeTimestampBackend.MeasurementHeroReady ==
                frameSamples.Count &&
            nativeTimestampBackend.MeasurementHeroValid ==
                frameSamples.Count &&
            nativeTimestampBackend.MeasurementControlSubmitted ==
                expectedTimestampControls &&
            nativeTimestampBackend.MeasurementControlReady ==
                expectedTimestampControls &&
            nativeTimestampBackend.MeasurementControlValid ==
                expectedTimestampControls &&
            nativeTimestampBackend.PendingCount == 0 &&
            nativeTimestampBackend.ActiveSampleCount == 0 &&
            nativeTimestampBackend.ReservedSampleCount == 0 &&
            nativeTimestampBackend.SubmittedSampleCount == 0 &&
            nativeTimestampBackend.AcquireFailures == 0 &&
            nativeTimestampBackend.PreparedScopeFailures == 0 &&
            nativeTimestampBackend.ResultFailures == 0 &&
            nativeTimestampBackend.Timeouts == 0 &&
            !nativeTimestampBackend.IsTerminal &&
            nativeTimestampBackend.ObservedTimestampFrequency > 0UL &&
            nativeTimestampDllSha256.Length == 64;
        bool measurementResidencyStable =
            !configuration.Cinematic ||
            (measurementMinResidentPacks == measurementMaxResidentPacks &&
             measurementMinResidentBytes == measurementMaxResidentBytes &&
             measurementMaxLoadingPacks == 0);
        bool measurementCullTopologyValid =
            !configuration.Cinematic ||
            (measurementCullTopologySamples == frameSamples.Count &&
             measurementCullPassesFresh &&
             measurementCullPackSetsComplete &&
             measurementMinCullCameraCount == configuration.CameraCount &&
             measurementMaxCullCameraCount == configuration.CameraCount &&
             measurementMinCullPackCount ==
                renderers[0].ResidentPackCount &&
             measurementMaxCullPackCount ==
                renderers[0].ResidentPackCount);
        bool qualityPassed = workloadValidation.Passed && cityValid &&
            rendererPathValid && payloadRenderCountValid &&
            prePayloadCitySubmissionValid &&
            cameraRenderOrderValid &&
            workloadSubmissionCadenceValid && sampleCompletenessValid &&
            gpuTimingCoverageValid && measurementResidencyStable &&
            measurementCullTopologyValid;
        string qualityMessage =
            "workloads=" + (workloadValidation.Passed ? "pass" : "fail") +
            "; city=" + (cityValid ? "pass" : "fail") +
            "; rendererPath=" +
            (rendererPathValid ? "pass" : "fail") +
            "; payloadRenders=" +
            (payloadRenderCountValid ? "pass" : "fail") +
            "(" + measuredPayloadRenderCount + "/" +
            expectedPayloadRenderCount + ")" +
            "; cityBeforePayload=" +
            (prePayloadCitySubmissionValid ? "pass" : "fail") +
            "(" + measuredPrePayloadCitySubmissionCount + "/" +
            frameSamples.Count + ")" +
            "; cameraRenderOrder=" +
            (cameraRenderOrderValid ? "pass" : "fail") +
            "(hero=" + measuredHeroSrpRenderCount + "/" +
            expectedHeroSrpRenderCount + "; payload=" +
            measuredPayloadSrpRenderCount + "/" +
            expectedPayloadSrpRenderCount + ")" +
            "; workloadCadence=" +
            (workloadSubmissionCadenceValid ? "pass" : "fail") +
            "(expected=" + expectedWorkloadSubmissions +
            "; sensor=" + stack.Sensor.Submitted + "/" +
            stack.Sensor.Dropped + "; residency=" +
            stack.Residency.Submitted + "/" + stack.Residency.Dropped +
            "; deadline=" + stack.Deadline.Submitted + "/" +
            stack.Deadline.Dropped + "; scheduled=" +
            stack.ScheduledIssueCount + "; uniqueStates=" +
            stack.UniqueLogicalStateCount + "/" +
            expectedUniqueLogicalStates + "; warmup=" +
            (warmupSubmissionCadenceValid ? "pass" : "fail") +
            "; endDrained=" + measurementWorkloadsDrained + ")" +
            "; samples=" +
            (sampleCompletenessValid ? "complete" : "incomplete") +
            "; nativeGpuTiming=" +
            (gpuTimingCoverageValid ? "complete" : "incomplete") +
            "(" + NativeTimestampScopeVersion + ")" +
            "; measurementResidency=" +
            (measurementResidencyStable ? "stable" : "changed") +
            "; cullTopology=" +
            (measurementCullTopologyValid ? "exact" : "invalid") +
            "(" + measurementMinCullCameraCount + "/" +
            measurementMaxCullCameraCount + " cameras)" +
            "; residency=" + workloadValidation.ResidencyMessage +
            "; deadline=" + workloadValidation.DeadlineMessage;
        string rawPath = ResolveRawFramesPath();
        string rawTimestampPath = ResolveRawTimestampsPath();
        return new GpuStressShowcaseReport
        {
            status = qualityPassed ? "completed" : "validation-failed",
            variant = variantName,
            generatedUtc = DateTime.UtcNow.ToString("O", Invariant),
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            graphicsVersion = SystemInfo.graphicsDeviceVersion,
            graphicsMemoryMiB = SystemInfo.graphicsMemorySize,
            unityVersion = Application.unityVersion,
            sourceCommit = configuration.SourceCommit,
            sourceDirty = configuration.SourceDirty,
            playerSha256 = configuration.PlayerSha256,
            deviceKey = tuning.DeviceKey,
            autotuneProfileMatched = tuning.ProfileMatched,
            autotuneSource = tuning.Source,
            sensorBackend = stack.Sensor.Backend.ToString(),
            rendererAlgorithm = activeAlgorithm,
            rendererCount = renderers.Length,
            cameraCount = configuration.CameraCount,
            cityCullCameraMode = ResolveCityCullCameraMode(),
            cityValidationMode = configuration.Cinematic
                ? CityValidationMode
                : "ActiveCameraSingleRead",
            cityValidationStable = cityValidationStable,
            cityValidationFreshDispatches =
                cityValidationFreshDispatches,
            cityValidationSamples = cityValidationSamples,
            cityValidationFirstPassEpoch = cityValidationFirstPassEpoch,
            cityValidationLastPassEpoch = cityValidationLastPassEpoch,
            cityValidationPreparedCameraCount =
                city.PreparedCameraCount,
            cityValidationCameraStateHash =
                city.CameraStateHash.ToString("X16", Invariant),
            cityValidationDispatchedPackCount =
                city.DispatchedPackCount,
            cityValidationDispatchedPackHash =
                city.DispatchedPackHash.ToString("X16", Invariant),
            cityValidationPackSetComplete = city.PackSetComplete,
            payloadRenderMode = PayloadRenderMode,
            measuredPayloadRenderCount = measuredPayloadRenderCount,
            expectedPayloadRenderCount = expectedPayloadRenderCount,
            payloadRenderCountValid = payloadRenderCountValid,
            measuredPrePayloadCitySubmissionCount =
                measuredPrePayloadCitySubmissionCount,
            expectedPrePayloadCitySubmissionCount =
                frameSamples.Count,
            prePayloadCitySubmissionValid =
                prePayloadCitySubmissionValid,
            measuredPayloadSrpRenderCount =
                measuredPayloadSrpRenderCount,
            expectedPayloadSrpRenderCount =
                expectedPayloadSrpRenderCount,
            measuredHeroSrpRenderCount =
                measuredHeroSrpRenderCount,
            expectedHeroSrpRenderCount =
                expectedHeroSrpRenderCount,
            cameraRenderOrderValid = cameraRenderOrderValid,
            workloadIssueIntervalFrames =
                configuration.WorkloadIssueIntervalFrames,
            expectedWorkloadSubmissions = expectedWorkloadSubmissions,
            workloadSubmissionCadenceValid =
                workloadSubmissionCadenceValid,
            scheduledWorkloadIssueCount = stack.ScheduledIssueCount,
            workloadIssueFrameSequenceHash =
                stack.IssueFrameSequenceHash,
            workloadLogicalStateSequenceHash =
                stack.LogicalStateSequenceHash,
            workloadLogicalStateCount = stack.LogicalStateCount,
            workloadUniqueLogicalStates =
                stack.UniqueLogicalStateCount,
            warmupExpectedWorkloadSubmissions =
                warmupExpectedWorkloadSubmissions,
            warmupScheduledWorkloadIssueCount =
                warmupScheduledWorkloadIssues,
            warmupSensorSubmitted = warmupSensorSubmitted,
            warmupSensorDropped = warmupSensorDropped,
            warmupResidencySubmitted = warmupResidencySubmitted,
            warmupResidencyDropped = warmupResidencyDropped,
            warmupDeadlineSubmitted = warmupDeadlineSubmitted,
            warmupDeadlineDropped = warmupDeadlineDropped,
            warmupUniqueLogicalStates = warmupUniqueLogicalStates,
            warmupIssueFrameSequenceHash =
                warmupIssueFrameSequenceHash,
            warmupLogicalStateSequenceHash =
                warmupLogicalStateSequenceHash,
            warmupSubmissionCadenceValid =
                warmupSubmissionCadenceValid,
            measurementWorkloadsDrained = measurementWorkloadsDrained,
            measurementResidencyStable = measurementResidencyStable,
            measurementMinResidentPacks =
                measurementMinResidentPacks,
            measurementMaxResidentPacks =
                measurementMaxResidentPacks,
            measurementMinResidentBytes =
                measurementMinResidentBytes,
            measurementMaxResidentBytes =
                measurementMaxResidentBytes,
            measurementMaxLoadingPacks = measurementMaxLoadingPacks,
            measurementCullTopologyValid =
                measurementCullTopologyValid,
            measurementCullTopologySamples =
                measurementCullTopologySamples,
            measurementMinCullCameraCount =
                measurementMinCullCameraCount,
            measurementMaxCullCameraCount =
                measurementMaxCullCameraCount,
            measurementMinCullPackCount =
                measurementMinCullPackCount,
            measurementMaxCullPackCount =
                measurementMaxCullPackCount,
            measurementCullTopologySequenceHash =
                measurementCullTopologySequenceHash.ToString(
                    "X16",
                    Invariant),
            cinematic = configuration.Cinematic,
            cinematicRouteId = configuration.Cinematic
                ? GpuCinematicRoute.RouteId
                : string.Empty,
            cinematicDurationSeconds =
                configuration.CinematicDurationSeconds,
            cinematicTimeOfDayHours =
                configuration.CinematicTimeOfDayHours,
            cinematicWeatherProfile = configuration.Cinematic
                ? NYCGISWeatherProfile.Clear.ToString()
                : NYCGISWeatherProfile.HeavyStorm.ToString(),
            outputWidth = cinematicHeroOutput != null
                ? cinematicHeroOutput.width
                : Screen.width,
            outputHeight = cinematicHeroOutput != null
                ? cinematicHeroOutput.height
                : Screen.height,
            configuredMaxSampleFrames = configuration.SampleFrames,
            orthophotoLoadedLod0Pages = orthophoto != null
                ? orthophoto.LoadedLod0PageCount
                : 0,
            orthophotoManifestTiles = orthophoto != null
                ? orthophoto.Lod0ManifestTileCount
                : 0,
            orthophotoTileResolution = orthophoto != null ? 2048 : 0,
            cinematicGalleryPrefix =
                ResolveCinematicGalleryPrefix(),
            sampleFrames = frameSamples.Count,
            sampleElapsedSeconds = sampleElapsedSeconds,
            fpsAverage = frameSamples.Count /
                Math.Max(0.001, sampleElapsedSeconds),
            frameAverageMs = GpuStressShowcaseMath.Average(frameSamples),
            frameP95Ms = GpuStressShowcaseMath.Percentile(
                frameSamples,
                0.95),
            frameP99Ms = GpuStressShowcaseMath.Percentile(
                frameSamples,
                0.99),
            gpuTimingMode = NativeTimestampMode,
            gpuTimingScopeVersion = NativeTimestampScopeVersion,
            gpuTimingQueue = NativeTimestampQueue,
            gpuTimingIncludedWork = configuration.CameraCount == 1
                ? "BFP2 main-queue city cull plus one explicit hero render"
                : "BFP2 main-queue city cull plus explicit camera group",
            gpuTimingExcludedWork =
                "Sensor/residency/deadline submissions before BEGIN; " +
                "independent async compute and copy queues",
            gpuAverageMs = nativeTimestampBackend.Average(
                GpuCinematicTimestampKind.MeasurementHero),
            gpuTimingPrimeFrames = NativeTimestampWarmupPairs,
            gpuTimingValidSamples =
                nativeTimestampBackend.MeasurementHeroValid,
            sampleCompletenessValid = sampleCompletenessValid,
            gpuTimingCoverageValid = gpuTimingCoverageValid,
            frameTimingGpuSamples = 0,
            profilerGpuSamples = 0,
            gpuP95Ms = nativeTimestampBackend.Percentile(
                GpuCinematicTimestampKind.MeasurementHero,
                0.95),
            gpuP99Ms = nativeTimestampBackend.Percentile(
                GpuCinematicTimestampKind.MeasurementHero,
                0.99),
            nativeTimestampBackendAvailable =
                nativeTimestampBackend.Support.IsAvailable,
            nativeTimestampSupportMessage =
                nativeTimestampBackend.Support.Message,
            nativeTimestampAbiVersion =
                (int)nativeTimestampBackend.Support.AbiVersion,
            nativeTimestampCapabilityFlags =
                nativeTimestampBackend.Support.CapabilityFlags,
            nativeTimestampRingCapacity =
                nativeTimestampBackend.Support.RingCapacity,
            nativeTimestampPreparedScopes =
                nativeTimestampBackend.PreparedScopeCount,
            nativeTimestampRendererType =
                nativeTimestampBackend.Support.RendererType,
            nativeTimestampDeviceGeneration =
                nativeTimestampBackend.ObservedDeviceGeneration,
            nativeTimestampObservedFrequency =
                nativeTimestampBackend.ObservedTimestampFrequency,
            nativeTimestampDllSha256 = nativeTimestampDllSha256,
            nativeTimestampWarmupPairs = NativeTimestampWarmupPairs,
            nativeTimestampWarmupPassed = nativeTimestampWarmupPassed,
            nativeTimestampOrderingDiscriminatorPassed =
                nativeTimestampBackend.OrderingDiscriminatorPassed,
            nativeTimestampWarmupControlSubmitted =
                nativeTimestampBackend.WarmupControlSubmitted,
            nativeTimestampWarmupControlReady =
                nativeTimestampBackend.WarmupControlReady,
            nativeTimestampWarmupControlValid =
                nativeTimestampBackend.WarmupControlValid,
            nativeTimestampWarmupHeroSubmitted =
                nativeTimestampBackend.WarmupHeroSubmitted,
            nativeTimestampWarmupHeroReady =
                nativeTimestampBackend.WarmupHeroReady,
            nativeTimestampWarmupHeroValid =
                nativeTimestampBackend.WarmupHeroValid,
            nativeTimestampWarmupControlP50Ms =
                nativeTimestampBackend.Percentile(
                    GpuCinematicTimestampKind.WarmupControl,
                    0.50),
            nativeTimestampWarmupControlP99Ms =
                nativeTimestampBackend.Percentile(
                    GpuCinematicTimestampKind.WarmupControl,
                    0.99),
            nativeTimestampWarmupHeroP50Ms =
                nativeTimestampBackend.Percentile(
                    GpuCinematicTimestampKind.WarmupHero,
                    0.50),
            nativeTimestampExpectedHeroSamples = frameSamples.Count,
            nativeTimestampHeroSubmitted =
                nativeTimestampBackend.MeasurementHeroSubmitted,
            nativeTimestampHeroReady =
                nativeTimestampBackend.MeasurementHeroReady,
            nativeTimestampHeroValid =
                nativeTimestampBackend.MeasurementHeroValid,
            nativeTimestampExpectedControlSamples =
                expectedTimestampControls,
            nativeTimestampControlSubmitted =
                nativeTimestampBackend.MeasurementControlSubmitted,
            nativeTimestampControlReady =
                nativeTimestampBackend.MeasurementControlReady,
            nativeTimestampControlValid =
                nativeTimestampBackend.MeasurementControlValid,
            nativeTimestampControlP50Ms =
                nativeTimestampBackend.Percentile(
                    GpuCinematicTimestampKind.MeasurementControl,
                    0.50),
            nativeTimestampControlP99Ms =
                nativeTimestampBackend.Percentile(
                    GpuCinematicTimestampKind.MeasurementControl,
                    0.99),
            nativeTimestampAcquireFailures =
                nativeTimestampBackend.AcquireFailures,
            nativeTimestampPreparedScopeFailures =
                nativeTimestampBackend.PreparedScopeFailures,
            nativeTimestampResultFailures =
                nativeTimestampBackend.ResultFailures,
            nativeTimestampTimeouts = nativeTimestampBackend.Timeouts,
            nativeTimestampPeakActiveSamples =
                nativeTimestampBackend.PeakActiveSamples,
            nativeTimestampFinalPendingSamples =
                nativeTimestampBackend.PendingCount,
            nativeTimestampFinalActiveSamples =
                nativeTimestampBackend.ActiveSampleCount,
            nativeTimestampFinalReservedSamples =
                nativeTimestampBackend.ReservedSampleCount,
            nativeTimestampFinalSubmittedSamples =
                nativeTimestampBackend.SubmittedSampleCount,
            nativeTimestampTerminal = nativeTimestampBackend.IsTerminal,
            nativeTimestampTerminalStatus =
                nativeTimestampBackend.TerminalStatus,
            nativeTimestampInstrumentationReadbackBytes =
                nativeTimestampBackend.MeasurementInstrumentationReadbackBytes,
            nativeTimestampMaximumResultPendingFrames = Math.Max(
                nativeTimestampBackend.MaximumPendingFrames(
                    GpuCinematicTimestampKind.MeasurementHero),
                nativeTimestampBackend.MaximumPendingFrames(
                    GpuCinematicTimestampKind.MeasurementControl)),
            heroTimestampEvidenceComplete = gpuTimingCoverageValid,
            longFrames16Ms = longFrames16,
            longFrames33Ms = longFrames33,
            longFrame33Rate = longFrames33 /
                Math.Max(1.0, frameSamples.Count),
            sensorElementCount = configuration.SensorElementCount,
            sensorCount = configuration.SensorCount,
            queriesPerSensor = configuration.QueriesPerSensor,
            sensorSubmitted = stack.Sensor.Submitted,
            sensorDropped = stack.Sensor.Dropped,
            sensorCpuProducerAverageMs =
                stack.Sensor.CpuProducerAverageMs,
            sensorLogicalUploadBytes = stack.Sensor.LogicalUploadBytes,
            sensorIndexBuildsPerUpdate =
                stack.Sensor.IndexBuildsPerUpdate,
            residencyPointsPerPage =
                configuration.ResidencyPointsPerPage,
            residencySubmitted = stack.Residency.Submitted,
            residencyDropped = stack.Residency.Dropped,
            residencyLogicalUploadBytes =
                stack.Residency.LogicalUploadBytes,
            residencyHitRate = stack.Residency.HitRate,
            deadlineSubmitted = stack.Deadline.Submitted,
            deadlineDropped = stack.Deadline.Dropped,
            criticalLatencyAverageMs =
                stack.Deadline.CriticalLatencyAverageMs,
            criticalLatencyP99Ms =
                stack.Deadline.CriticalLatencyP99Ms,
            criticalLateObservations =
                stack.Deadline.CriticalLateObservations,
            deadlinePolicy = stack.Deadline.PolicyName,
            cityTotalPacks = renderers[0].TotalPackCount,
            cityResidentPacks = renderers[0].ResidentPackCount,
            cityLoadingPacks = renderers[0].LoadingPackCount,
            cityResidentGpuBytes = renderers[0].ResidentGpuBytes,
            cityStatus = renderers[0].LastStatus,
            cityVisibleClusters = city.VisibleClusters,
            cityVisibleIndices = city.VisibleIndices,
            cityVisibleTiles = city.VisibleTiles,
            cityDrawIndices = city.DrawIndices,
            cityLogicalOutputBytes = logicalOutputBytes,
            cityAvoidedIndexTrafficBytes = avoidedTraffic,
            cityOutputHash = cityHash,
            sensorOutputHash = workloadValidation.SensorHash,
            residencyOutputHash = workloadValidation.ResidencyHash,
            deadlineOutputHash = workloadValidation.DeadlineHash,
            compositeOutputHash = GpuStressShowcaseMath.HashStrings(
                cityHash,
                workloadValidation.SensorHash,
                workloadValidation.ResidencyHash,
                workloadValidation.DeadlineHash),
            qualityPassed = qualityPassed,
            qualityMessage = qualityMessage,
            rawFramesPath = rawPath,
            rawTimestampsPath = rawTimestampPath,
            screenshotPath = configuration.ScreenshotPath
        };
    }

    private void WriteReport(GpuStressShowcaseReport report)
    {
        string reportPath = ResolveReportPath();
        string directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(
            reportPath,
            JsonUtility.ToJson(report, true) + Environment.NewLine);

        var csv = new StringBuilder(frameSamples.Count * 32);
        csv.AppendLine("frameIndex,frameMs,gpuMs,long16,long33");
        for (int index = 0; index < frameSamples.Count; index++)
        {
            float gpu = index < gpuSamples.Count ? gpuSamples[index] : 0.0f;
            csv.Append(index + 1).Append(',')
                .Append(frameSamples[index].ToString("F5", Invariant))
                .Append(',')
                .Append(gpu.ToString("F5", Invariant)).Append(',')
                .Append(frameSamples[index] > 16.667f ? 1 : 0)
                .Append(',')
                .Append(frameSamples[index] > 33.333f ? 1 : 0)
                .AppendLine();
        }
        File.WriteAllText(ResolveRawFramesPath(), csv.ToString());
        if (nativeTimestampBackend == null)
        {
            throw new InvalidOperationException(
                "Native timestamp evidence was unavailable at report write.");
        }
        File.WriteAllText(ResolveRawTimestampsPath(),
            nativeTimestampBackend.BuildEvidenceCsv());
        UnityEngine.Debug.Log(
            "GPU stress showcase report: " + reportPath +
            "\n" + JsonUtility.ToJson(report, true),
            this);
    }

    private string ResolveReportPath()
    {
        if (!string.IsNullOrWhiteSpace(configuration.ReportPath))
        {
            return Path.GetFullPath(configuration.ReportPath);
        }
        string name = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline
                ? "baseline.json"
                : "optimized.json";
        return Path.Combine(
            Application.persistentDataPath,
            "GpuStressShowcase",
            name);
    }

    private string ResolveRawFramesPath()
    {
        string reportPath = ResolveReportPath();
        string directory = Path.GetDirectoryName(reportPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(reportPath);
        return Path.Combine(directory, stem + ".frames.csv");
    }

    private string ResolveRawTimestampsPath()
    {
        string reportPath = ResolveReportPath();
        string directory = Path.GetDirectoryName(reportPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(reportPath);
        return Path.Combine(directory, stem + ".timestamps.csv");
    }

    private static string ComputeNativeTimestampDllSha256()
    {
        string[] candidates =
        {
            Path.Combine(
                Application.dataPath,
                "Plugins",
                "x86_64",
                "SummitGpuTimestamps.dll"),
            Path.Combine(
                Application.dataPath,
                "Plugins",
                "SummitGpuTimestamps.dll")
        };
        for (int i = 0; i < candidates.Length; i++)
        {
            string path = candidates[i];
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash)
                        .Replace("-", string.Empty);
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
        return string.Empty;
    }

    private string ResolveCinematicGalleryPrefix()
    {
        if (!configuration.Cinematic ||
            string.IsNullOrWhiteSpace(configuration.ScreenshotPath))
        {
            return string.Empty;
        }
        string screenshot = Path.GetFullPath(
            configuration.ScreenshotPath);
        string directory = Path.GetDirectoryName(screenshot) ??
            string.Empty;
        string stem = Path.GetFileNameWithoutExtension(screenshot);
        return Path.Combine(directory, stem);
    }

    private IEnumerator CaptureCinematicGallery(string requestedPath)
    {
        string absoluteScreenshot = Path.GetFullPath(requestedPath);
        string directory = Path.GetDirectoryName(absoluteScreenshot);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string prefix = Path.Combine(
            directory ?? string.Empty,
            Path.GetFileNameWithoutExtension(absoluteScreenshot));

        yield return CaptureCinematicStill(
            GpuCinematicRoute.SkylineGalleryProgress,
            prefix + "-skyline.png",
            showResultCard: false);
        yield return CaptureCinematicStill(
            GpuCinematicRoute.FacadeGalleryProgress,
            prefix + "-facade.png",
            showResultCard: false);
        yield return CaptureCinematicStill(
            GpuCinematicRoute.TextureGalleryProgress,
            prefix + "-texture.png",
            showResultCard: false);
        yield return CaptureCinematicStill(
            GpuCinematicRoute.FacadeGalleryProgress,
            absoluteScreenshot,
            showResultCard: true);
    }

    private IEnumerator CaptureCinematicStill(
        float progress,
        string path,
        bool showResultCard)
    {
        cinematicGalleryCapture = !showResultCard;
        SetCinematicProgress(progress);
        double minimumWaitEnd =
            Time.realtimeSinceStartupAsDouble + 8.0;
        double galleryDeadline = minimumWaitEnd + 4.0;
        int idlePageFrames = 0;
        while (Time.realtimeSinceStartupAsDouble < minimumWaitEnd ||
               (Time.realtimeSinceStartupAsDouble < galleryDeadline &&
                idlePageFrames < 30))
        {
            RenderExtraCameras();
            yield return null;
            bool pageUploadsIdle = orthophoto == null ||
                (orthophoto.LastTextureUploadMegabytes <= 0.0001f &&
                 orthophoto.LastPageTableDirtyRegionCount == 0);
            if (pageUploadsIdle)
            {
                idlePageFrames++;
            }
            else
            {
                idlePageFrames = 0;
            }
        }
        yield return new WaitForEndOfFrame();
        if (!showResultCard && cinematicHeroOutput != null)
        {
            CaptureCinematicRenderTarget(path);
        }
        else
        {
            ScreenCapture.CaptureScreenshot(path);
        }
        for (int frame = 0; frame < 6; frame++)
        {
            yield return null;
        }
    }

    private void CaptureCinematicRenderTarget(string path)
    {
        RenderTexture previous = RenderTexture.active;
        Texture2D capture = null;
        try
        {
            RenderTexture.active = cinematicHeroOutput;
            capture = new Texture2D(
                cinematicHeroOutput.width,
                cinematicHeroOutput.height,
                TextureFormat.RGB24,
                false,
                false);
            capture.ReadPixels(
                new Rect(
                    0.0f,
                    0.0f,
                    cinematicHeroOutput.width,
                    cinematicHeroOutput.height),
                0,
                0,
                false);
            capture.Apply(false, false);
            File.WriteAllBytes(path, capture.EncodeToPNG());
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogError(
                "Could not capture the 1080p cinematic hero target: " +
                exception,
                this);
            ScreenCapture.CaptureScreenshot(path);
        }
        finally
        {
            RenderTexture.active = previous;
            if (capture != null)
            {
                Destroy(capture);
            }
        }
    }

    private void UpdateHudText(bool force)
    {
        if (configuration == null || configuration.HideHud)
        {
            return;
        }
        hudFrameAverageMs =
            GpuStressShowcaseMath.Average(frameSamples);
        hudFrameP99Ms =
            GpuStressShowcaseMath.Percentile(frameSamples, 0.99);
        hudGpuAverageMs =
            GpuStressShowcaseMath.Average(gpuSamples);
        hudGpuP99Ms =
            GpuStressShowcaseMath.Percentile(gpuSamples, 0.99);
        var text = new StringBuilder(1024);
        string label = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline
                ? "A  STANDARD PIPELINE"
                : "B  GPU PERFORMANCE STACK";
        text.AppendLine(label);
        text.Append("Phase: ").Append(phase);
        if (!string.IsNullOrEmpty(failure))
        {
            text.Append(" — ").Append(failure);
        }
        text.AppendLine();
        text.Append("Sample: ").Append(currentSampleFrame)
            .Append('/').Append(configuration.SampleFrames)
            .Append("    Cameras: ").Append(configuration.CameraCount)
            .AppendLine();
        text.Append("Frame: ").Append(latestFrameMs.ToString("F2", Invariant))
            .Append(" ms    FPS: ")
            .Append(latestFrameMs > 0.0f
                ? (1000.0f / latestFrameMs).ToString("F1", Invariant)
                : "--")
            .Append("    GPU: ")
            .Append(latestGpuMs > 0.0f
                ? latestGpuMs.ToString("F2", Invariant) + " ms"
                : "sampling")
            .AppendLine();
        text.Append("Running avg/P99: ")
            .Append(GpuStressShowcaseMath.Average(frameSamples)
                .ToString("F2", Invariant))
            .Append(" / ")
            .Append(GpuStressShowcaseMath.Percentile(frameSamples, 0.99)
                .ToString("F2", Invariant))
            .Append(" ms    >33 ms: ").Append(longFrames33)
            .AppendLine();

        if (configuration.Variant == GpuStressShowcaseVariant.Baseline)
        {
            text.AppendLine("City: Scalar AoS -> full visible-index stream");
            text.AppendLine("Sensors: CPU producer/upload -> 4 CSR rebuilds");
            text.AppendLine("Pages: rebuild + upload complete visible window");
            text.AppendLine("Tasks: FIFO on main graphics queue");
        }
        else
        {
            text.AppendLine("City: Wave64 compaction -> no-copy Tile32 descriptors");
            text.AppendLine("Sensors: GPU producer -> one shared GPU-resident CSR");
            text.AppendLine("Pages: persistent LRU -> delta uploads only");
            text.AppendLine("Tasks: least slack first; AMD async path rejected");
        }
        text.Append("Tuning: ").Append(tuning.Summary).AppendLine();

        if (stack != null)
        {
            text.Append("Aux workload cadence: every ")
                .Append(stack.IssueIntervalFrames)
                .Append(" logical frames")
                .AppendLine();
            text.Append("Sensor updates/drop/age: ")
                .Append(stack.Sensor.Submitted).Append(" / ")
                .Append(stack.Sensor.Dropped).Append(" / ")
                .Append(stack.Sensor.DataAgeFrames).Append(" frames")
                .AppendLine();
            text.Append("Sensor CPU/upload: ")
                .Append(stack.Sensor.CpuProducerAverageMs
                    .ToString("F2", Invariant)).Append(" ms / ")
                .Append(FormatBytes(stack.Sensor.LogicalUploadBytes))
                .AppendLine();
            text.Append("Page hit/upload pages: ")
                .Append((stack.Residency.HitRate * 100.0)
                    .ToString("F1", Invariant)).Append("% / ")
                .Append(stack.Residency.LastUploadPages).AppendLine();
            text.Append("Critical age/late observations: ")
                .Append(stack.Deadline.LatestCriticalLatencyMs
                    .ToString("F2", Invariant)).Append(" ms / ")
                .Append(stack.Deadline.CriticalLateObservations)
                .AppendLine();
        }
        text.Append("City resident/loading/total: ")
            .Append(renderers != null && renderers.Length > 0
                ? renderers[0].ResidentPackCount.ToString(Invariant)
                : "--")
            .Append(" / ")
            .Append(renderers != null && renderers.Length > 0
                ? renderers[0].LoadingPackCount.ToString(Invariant)
                : "--")
            .Append(" / ")
            .Append(renderers != null && renderers.Length > 0
                ? renderers[0].TotalPackCount.ToString(Invariant)
                : "--")
            .Append("    ")
            .Append(renderers != null && renderers.Length > 0
                ? FormatBytes(renderers[0].ResidentGpuBytes)
                : "--")
            .AppendLine();
        if (completed)
        {
            text.AppendLine(
                "Run complete. Validation and raw frames are saved.");
        }
        hudText = text.ToString();
    }

    private void OnGUI()
    {
        if (configuration == null || whiteTexture == null)
        {
            return;
        }
        EnsureGuiStyles();
        if (configuration.Cinematic)
        {
            DrawCinematicGui();
            return;
        }
        if (configuration.HideHud)
        {
            return;
        }
        bool baseline = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline;
        Color accent = baseline
            ? new Color(1.0f, 0.22f, 0.16f, 1.0f)
            : new Color(0.15f, 1.0f, 0.48f, 1.0f);
        Rect panel = new Rect(18.0f, 18.0f, 570.0f, 472.0f);
        DrawSolid(panel, new Color(0.015f, 0.02f, 0.03f, 0.90f));
        DrawSolid(new Rect(panel.x, panel.y, 8.0f, panel.height), accent);
        GUI.Label(
            new Rect(panel.x + 22.0f, panel.y + 15.0f, 525.0f, 35.0f),
            baseline ? "A — STANDARD PIPELINE" : "B — GPU PERFORMANCE STACK",
            titleStyle);
        GUI.Label(
            new Rect(panel.x + 22.0f, panel.y + 55.0f, 525.0f, 330.0f),
            hudText,
            bodyStyle);
        DrawFrameChart(new Rect(
            panel.x + 22.0f,
            panel.y + 386.0f,
            525.0f,
            68.0f));

        if (latestFrameMs > 33.333f && !completed)
        {
            Color previous = GUI.color;
            GUI.color = new Color(1.0f, 0.08f, 0.04f, 0.92f);
            GUI.Label(
                new Rect(Screen.width * 0.5f - 170.0f, 28.0f, 340.0f, 56.0f),
                "HITCH  " + latestFrameMs.ToString("F1", Invariant) + " ms",
                phaseStyle);
            GUI.color = previous;
        }
    }

    private void DrawCinematicGui()
    {
        EnsureCinematicStyles();
        Matrix4x4 previousMatrix = GUI.matrix;
        float scale = Mathf.Max(
            0.65f,
            Mathf.Min(Screen.width / 1920.0f, Screen.height / 1080.0f));
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));
        float width = Screen.width / scale;
        float height = Screen.height / scale;
        bool baseline = configuration.Variant ==
            GpuStressShowcaseVariant.Baseline;
        Color accent = baseline
            ? new Color(1.0f, 0.25f, 0.12f, 1.0f)
            : new Color(0.08f, 0.92f, 0.70f, 1.0f);
        DrawSolid(
            new Rect(0.0f, 0.0f, width, height),
            Color.black);
        if (cinematicHeroOutput != null)
        {
            GUI.DrawTexture(
                new Rect(0.0f, 0.0f, width, height),
                cinematicHeroOutput,
                ScaleMode.ScaleToFit,
                false);
        }
        if (configuration.HideHud)
        {
            GUI.matrix = previousMatrix;
            return;
        }

        DrawSolid(
            new Rect(0.0f, 0.0f, width, 72.0f),
            new Color(0.005f, 0.009f, 0.014f, 0.88f));
        DrawSolid(
            new Rect(0.0f, height - 188.0f, width, 188.0f),
            new Color(0.005f, 0.009f, 0.014f, 0.91f));
        DrawSolid(
            new Rect(48.0f, 70.0f, width - 96.0f, 2.0f),
            new Color(accent.r, accent.g, accent.b, 0.82f));

        GUI.Label(
            new Rect(48.0f, 20.0f, 620.0f, 32.0f),
            "SUMMIT // NYC GPU STRESS",
            cinematicBrandStyle);
        string variantLabel = baseline
            ? "A  STANDARD GPU PIPELINE"
            : "B  GPU-RESIDENT PERFORMANCE STACK";
        Rect badge = new Rect(width - 420.0f, 15.0f, 372.0f, 42.0f);
        DrawSolid(
            badge,
            new Color(accent.r, accent.g, accent.b, 0.90f));
        GUI.Label(
            new Rect(badge.x + 18.0f, badge.y + 6.0f,
                badge.width - 36.0f, 30.0f),
            variantLabel,
            cinematicBrandStyle);

        float lowerTop = height - 178.0f;
        GUI.Label(
            new Rect(48.0f, lowerTop + 15.0f, 700.0f, 46.0f),
            cinematicShotTitle,
            cinematicShotStyle);
        GUI.Label(
            new Rect(50.0f, lowerTop + 61.0f, 740.0f, 30.0f),
            cinematicShotSubtitle,
            cinematicCaptionStyle);
        GUI.Label(
            new Rect(50.0f, lowerTop + 94.0f, 740.0f, 24.0f),
            phase + "  /  ROUTE " +
            GpuCinematicRoute.RouteId.ToUpperInvariant(),
            cinematicSmallStyle);

        float metricX = width - 730.0f;
        float fps = latestFrameMs > 0.0f
            ? 1000.0f / latestFrameMs
            : 0.0f;
        string gpu = latestGpuMs > 0.0f
            ? latestGpuMs.ToString("F2", Invariant)
            : "--";
        GUI.Label(
            new Rect(metricX, lowerTop + 18.0f, 682.0f, 36.0f),
            "FPS " + fps.ToString("F1", Invariant) +
            "    FRAME " + latestFrameMs.ToString("F2", Invariant) +
            " ms    GPU " + gpu + " ms",
            cinematicMetricStyle);
        GUI.Label(
            new Rect(metricX, lowerTop + 57.0f, 682.0f, 28.0f),
            "AVG " + hudFrameAverageMs.ToString("F2", Invariant) +
            " ms    P99 " + hudFrameP99Ms.ToString("F2", Invariant) +
            " ms    GPU P99 " + hudGpuP99Ms.ToString("F2", Invariant) +
            " ms    >33 ms " + longFrames33,
            cinematicCaptionStyle);
        DrawFrameChart(
            new Rect(metricX, lowerTop + 94.0f, 682.0f, 40.0f));

        float progressWidth = width - 96.0f;
        DrawSolid(
            new Rect(48.0f, height - 43.0f, progressWidth, 3.0f),
            new Color(1.0f, 1.0f, 1.0f, 0.18f));
        DrawSolid(
            new Rect(
                48.0f,
                height - 43.0f,
                progressWidth * cinematicProgress,
                3.0f),
            accent);
        string city = renderers != null && renderers.Length > 0
            ? renderers[0].ResidentPackCount + "/" +
              renderers[0].TotalPackCount + " CITY PACKS / " +
              FormatBytes(renderers[0].ResidentGpuBytes)
            : "CITY STREAMING";
        string ortho = orthophoto != null
            ? orthophoto.LoadedLod0PageCount + "/" +
              orthophoto.Lod0ManifestTileCount + " 2K ORTHO PAGES"
            : "ORTHO STREAMING";
        GUI.Label(
            new Rect(48.0f, height - 34.0f, width - 96.0f, 24.0f),
            city + "    /    " + ortho + "    /    1 HERO + " +
            extraCameras.Count + " PAYLOAD CAMERAS",
            cinematicSmallStyle);

        if (latestFrameMs > 33.333f && !completed)
        {
            Rect hitch = new Rect(
                width * 0.5f - 150.0f,
                92.0f,
                300.0f,
                48.0f);
            DrawSolid(hitch, new Color(0.94f, 0.08f, 0.04f, 0.94f));
            GUI.Label(
                hitch,
                "HITCH  " + latestFrameMs.ToString("F1", Invariant) +
                " ms",
                phaseStyle);
        }

        if (completed && !cinematicGalleryCapture &&
            completedReport != null)
        {
            DrawCinematicResultCard(width, height, accent);
        }
        GUI.matrix = previousMatrix;
    }

    private void DrawCinematicResultCard(
        float width,
        float height,
        Color accent)
    {
        Rect panel = new Rect(
            width * 0.5f - 330.0f,
            height * 0.5f - 185.0f,
            660.0f,
            370.0f);
        DrawSolid(panel, new Color(0.006f, 0.012f, 0.020f, 0.94f));
        DrawSolid(
            new Rect(panel.x, panel.y, 7.0f, panel.height),
            accent);
        GUI.Label(
            new Rect(panel.x + 35.0f, panel.y + 24.0f,
                panel.width - 70.0f, 48.0f),
            completedReport.variant.ToUpperInvariant() +
            " RUN COMPLETE",
            cinematicShotStyle);
        GUI.Label(
            new Rect(panel.x + 36.0f, panel.y + 76.0f,
                panel.width - 72.0f, 25.0f),
            completedReport.qualityPassed
                ? "INTERNAL OUTPUT VALIDATED"
                : "OUTPUT VALIDATION FAILED",
            cinematicCaptionStyle);

        GUI.Label(
            new Rect(panel.x + 36.0f, panel.y + 126.0f,
                270.0f, 70.0f),
            "AVERAGE FPS\n" +
            completedReport.fpsAverage.ToString("F2", Invariant),
            cinematicMetricStyle);
        GUI.Label(
            new Rect(panel.x + 346.0f, panel.y + 126.0f,
                270.0f, 70.0f),
            "FRAME P99\n" +
            completedReport.frameP99Ms.ToString("F2", Invariant) +
            " ms",
            cinematicMetricStyle);
        GUI.Label(
            new Rect(panel.x + 36.0f, panel.y + 218.0f,
                270.0f, 70.0f),
            "GPU AVERAGE\n" +
            completedReport.gpuAverageMs.ToString("F2", Invariant) +
            " ms",
            cinematicMetricStyle);
        GUI.Label(
            new Rect(panel.x + 346.0f, panel.y + 218.0f,
                270.0f, 70.0f),
            "GPU P99\n" +
            completedReport.gpuP99Ms.ToString("F2", Invariant) +
            " ms",
            cinematicMetricStyle);
        GUI.Label(
            new Rect(panel.x + 36.0f, panel.y + 323.0f,
                panel.width - 72.0f, 26.0f),
            completedReport.sampleFrames + " MEASURED FRAMES / " +
            completedReport.cameraCount + " CAMERAS / " +
            completedReport.graphicsDeviceVendor.ToUpperInvariant() +
            " / " +
            completedReport.graphicsApi.ToUpperInvariant(),
            cinematicSmallStyle);
    }

    private void EnsureCinematicStyles()
    {
        if (cinematicBrandStyle != null)
        {
            return;
        }
        cinematicBrandStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        cinematicShotStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 34,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        cinematicCaptionStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 17,
            wordWrap = false,
            normal =
            {
                textColor = new Color(0.84f, 0.90f, 0.95f, 1.0f)
            }
        };
        cinematicMetricStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 23,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        cinematicSmallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            normal =
            {
                textColor = new Color(0.65f, 0.74f, 0.81f, 1.0f)
            }
        };
    }

    private void DrawFrameChart(Rect rect)
    {
        DrawSolid(rect, new Color(0.0f, 0.0f, 0.0f, 0.55f));
        int visible = Mathf.Min(frameSamples.Count, 240);
        if (visible == 0)
        {
            return;
        }
        float barWidth = rect.width / 240.0f;
        int start = frameSamples.Count - visible;
        for (int i = 0; i < visible; i++)
        {
            float value = frameSamples[start + i];
            float height = Mathf.Clamp01(value / 66.667f) * rect.height;
            Color color = value > 33.333f
                ? new Color(1.0f, 0.12f, 0.08f, 0.95f)
                : value > 16.667f
                    ? new Color(1.0f, 0.72f, 0.12f, 0.9f)
                    : new Color(0.18f, 0.9f, 0.45f, 0.85f);
            DrawSolid(
                new Rect(
                    rect.x + i * barWidth,
                    rect.yMax - height,
                    Mathf.Max(1.0f, barWidth - 0.35f),
                    height),
                color);
        }
        float line16 = rect.yMax - rect.height * (16.667f / 66.667f);
        float line33 = rect.yMax - rect.height * (33.333f / 66.667f);
        DrawSolid(
            new Rect(rect.x, line16, rect.width, 1.0f),
            new Color(1.0f, 0.75f, 0.2f, 0.55f));
        DrawSolid(
            new Rect(rect.x, line33, rect.width, 1.0f),
            new Color(1.0f, 0.15f, 0.1f, 0.7f));
    }

    private void EnsureGuiStyles()
    {
        if (titleStyle != null)
        {
            return;
        }
        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 24,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        bodyStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            richText = false,
            wordWrap = false,
            normal = { textColor = new Color(0.9f, 0.94f, 0.98f) }
        };
        phaseStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 28,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
    }

    private void DrawSolid(Rect rect, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, whiteTexture, ScaleMode.StretchToFill);
        GUI.color = previous;
    }

    private bool IsCityResidencyReady()
    {
        if (renderers == null || renderers.Length != 1)
        {
            return false;
        }
        for (int i = 0; i < renderers.Length; i++)
        {
            Bfp2GpuIndirectRenderer renderer = renderers[i];
            if (renderer == null ||
                renderer.TotalPackCount <= 0 ||
                renderer.ResidentPackCount != renderer.TotalPackCount ||
                renderer.LoadingPackCount != 0 ||
                renderer.PendingCpuBytes != 0L ||
                renderer.StagedUploadBytes != 0L ||
                renderer.StagedUploadPackCount != 0 ||
                renderer.FullResidencyResidentPackCount !=
                    renderer.FullResidencyBaselinePackCount)
            {
                return false;
            }
        }
        return true;
    }

    private string DescribeCityResidency()
    {
        return renderers == null || renderers.Length == 0 ||
               renderers[0] == null
            ? "renderer unavailable"
            : "resident=" + renderers[0].ResidentPackCount +
              ", loading=" + renderers[0].LoadingPackCount +
              ", total=" + renderers[0].TotalPackCount +
              ", status=" + renderers[0].LastStatus;
    }

    private bool TryTickStack(int logicalFrame, string stage)
    {
        try
        {
            stack.Tick(logicalFrame);
            return true;
        }
        catch (Exception exception)
        {
            Fail(stage + " failed at logical frame " + logicalFrame +
                ": " + exception);
            return false;
        }
    }

    private void Fail(string message)
    {
        failure = message ?? "Unknown failure.";
        phase = "FAILED";
        UnityEngine.Debug.LogError(
            "GPU stress showcase failed: " + failure,
            this);
        WriteFailureReport();
        UpdateHudText(force: true);
        if (configuration != null && configuration.AutoExit)
        {
            Application.Quit(2);
        }
    }

    private void WriteFailureReport()
    {
        if (configuration == null)
        {
            return;
        }
        try
        {
            var report = new GpuStressShowcaseReport
            {
                status = "failed",
                variant = configuration.Variant ==
                    GpuStressShowcaseVariant.Baseline
                        ? "baseline"
                        : "optimized",
                generatedUtc = DateTime.UtcNow.ToString("O", Invariant),
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                graphicsVersion = SystemInfo.graphicsDeviceVersion,
                qualityPassed = false,
                qualityMessage = failure
            };
            string path = ResolveReportPath();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(
                path,
                JsonUtility.ToJson(report, true) + Environment.NewLine);
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogError(
                "Could not write failure report: " + exception.Message,
                this);
        }
    }

    private static Camera ResolveBaseCamera()
    {
        Camera main = Camera.main;
        if (main != null && main.enabled &&
            main.gameObject.activeInHierarchy)
        {
            return main;
        }
        Camera[] cameras = FindObjectsByType<Camera>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
        Camera best = null;
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera candidate = cameras[i];
            if (candidate != null && candidate.enabled &&
                candidate.targetTexture == null &&
                (best == null || candidate.depth > best.depth))
            {
                best = candidate;
            }
        }
        return best;
    }

    private static string FormatBytes(long bytes)
    {
        double value = Math.Max(0L, bytes);
        string[] suffix = { "B", "KiB", "MiB", "GiB" };
        int unit = 0;
        while (value >= 1024.0 && unit < suffix.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        return value.ToString(unit == 0 ? "F0" : "F2", Invariant) +
            " " + suffix[unit];
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -=
            OnBeginCameraRendering;
        if (disposed)
        {
            return;
        }
        disposed = true;
        nativeTimestampBackend?.Dispose();
        nativeTimestampBackend = null;
        stack?.Dispose();
        stack = null;
        if (heroFeed != null)
        {
            NYCGISWeatherCameraRegistry.Unregister(heroFeed);
            Destroy(heroFeed);
            heroFeed = null;
        }
        if (cinematicVolumeHost != null)
        {
            Destroy(cinematicVolumeHost);
            cinematicVolumeHost = null;
        }
        if (cinematicVolumeProfile != null)
        {
            Destroy(cinematicVolumeProfile);
            cinematicVolumeProfile = null;
        }
        if (cinematicHeroOutput != null)
        {
            if (baseCamera != null)
            {
                baseCamera.enabled = baseCameraWasEnabled;
            }
            if (baseCamera != null &&
                baseCamera.targetTexture == cinematicHeroOutput)
            {
                baseCamera.targetTexture = null;
            }
            cinematicHeroOutput.Release();
            Destroy(cinematicHeroOutput);
            cinematicHeroOutput = null;
        }
        for (int i = 0; i < extraCameras.Count; i++)
        {
            if (extraCameras[i] != null)
            {
                Destroy(extraCameras[i].gameObject);
            }
        }
        for (int i = 0; i < extraOutputs.Count; i++)
        {
            RenderTexture output = extraOutputs[i];
            if (output != null)
            {
                output.Release();
                Destroy(output);
            }
        }
        extraCameras.Clear();
        extraOutputs.Clear();
        extraCameraYaw.Clear();
        extraCameraFixed.Clear();
        if (whiteTexture != null)
        {
            Destroy(whiteTexture);
            whiteTexture = null;
        }
    }
}
