using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Command-line-only full-city performance sampler. It is inert in normal builds unless the
/// -nycgis-weather-perf switch is supplied.
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class NYCGISFullCityWeatherPerformanceProbe : MonoBehaviour
{
    private const string EnableArgument = "-nycgis-weather-perf";
    private const int FirstFeedId = 8101;

    private int cameraCount = 1;
    private float warmupSeconds = 30.0f;
    private int sampleFrames = 300;
    private string reportPath;
    private float bfp2ScreenCullPixelsOverride = -1.0f;
    private NYCGISSensorDataMode sensorMode = NYCGISSensorDataMode.Off;
    private int sensorWidth = 1280;
    private int sensorHeight = 720;
    private float sensorRateHz = 30.0f;
    private int spatialQueryCount = 256;
    private string bfp2CullAlgorithmOverride = string.Empty;
    private bool bfp2GpuProfilerMarkers;
    private string screenshotPath = string.Empty;
    private readonly List<RenderTexture> outputs = new List<RenderTexture>();
    private readonly List<Camera> qaCameras = new List<Camera>();
    private INYCGISSensorBenchmark sensorBenchmark;

    private ProfilerRecorder mainThreadRecorder;
    private ProfilerRecorder renderThreadRecorder;
    private ProfilerRecorder gpuFrameRecorder;
    private ProfilerRecorder totalUsedMemoryRecorder;
    private ProfilerRecorder gcAllocatedRecorder;
    private ProfilerRecorder drawCallsRecorder;

    private float[] mainSamples;
    private float[] renderSamples;
    private float[] gpuSamples;
    private float[] frameSamples;
    private readonly FrameTiming[] latestFrameTimings = new FrameTiming[1];
    private int frameTimingGpuSampleCount;
    private int profilerGpuSampleCount;
    private long totalGcBytes;
    private long maxGcBytes;
    private long totalDrawCalls;
    private long maxUsedMemoryBytes;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!HasArgument(args, EnableArgument))
        {
            return;
        }

        GameObject host = new GameObject("NYCGIS Full City Weather Performance Probe");
        DontDestroyOnLoad(host);
        NYCGISFullCityWeatherPerformanceProbe probe =
            host.AddComponent<NYCGISFullCityWeatherPerformanceProbe>();
        probe.Configure(args);
    }

    private void Start()
    {
        StartCoroutine(Run());
    }

    private void OnDisable()
    {
        DisposeRecorders();
        sensorBenchmark?.Dispose();
        sensorBenchmark = null;
        for (int i = 0; i < outputs.Count; i++)
        {
            if (outputs[i] != null)
            {
                outputs[i].Release();
                Destroy(outputs[i]);
            }
        }
        outputs.Clear();
    }

    private void Configure(string[] args)
    {
        cameraCount = Mathf.Clamp(ReadInt(args, "-nycgis-weather-cameras", 1), 1, 6);
        warmupSeconds = Mathf.Clamp(ReadFloat(args, "-nycgis-weather-warmup-seconds", 30.0f), 5.0f, 300.0f);
        sampleFrames = Mathf.Clamp(ReadInt(args, "-nycgis-weather-sample-frames", 300), 120, 3600);
        reportPath = ReadString(args, "-nycgis-weather-report", string.Empty);
        bfp2ScreenCullPixelsOverride =
            Mathf.Max(-1.0f, ReadFloat(args, "-nycgis-bfp2-screen-cull-pixels", -1.0f));
        sensorMode = NYCGISSensorDataBenchmark.ParseMode(
            ReadString(args, "-nycgis-sensor-mode", "off"));
        sensorWidth = Mathf.Clamp(ReadInt(args, "-nycgis-sensor-width", 1280), 160, 3840);
        sensorHeight = Mathf.Clamp(ReadInt(args, "-nycgis-sensor-height", 720), 90, 2160);
        sensorRateHz = Mathf.Clamp(
            ReadFloat(args, "-nycgis-sensor-rate-hz", 30.0f),
            1.0f,
            240.0f);
        spatialQueryCount = Mathf.Clamp(
            ReadInt(args, "-nycgis-spatial-query-count", 256),
            16,
            4096);
        bfp2CullAlgorithmOverride =
            ReadString(args, "-nycgis-bfp2-cull-algorithm", string.Empty);
        bfp2GpuProfilerMarkers = HasArgument(args, "-nycgis-bfp2-gpu-markers");
        screenshotPath = ReadString(args, "-nycgis-weather-screenshot", string.Empty);
    }

    private IEnumerator Run()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        Camera baseCamera = null;
        double cameraDeadline = Time.realtimeSinceStartupAsDouble + 30.0;
        while (baseCamera == null && Time.realtimeSinceStartupAsDouble < cameraDeadline)
        {
            baseCamera = ResolveBaseCamera();
            yield return null;
        }
        if (baseCamera == null)
        {
            Finish(false, "Full-city weather QA FAILED: no enabled base camera was found.");
            yield break;
        }

        DisableMissionAreaForFullCityQa();
        if (NYCGISWeatherCameraRegistry.RegisteredCount != 0)
        {
            Finish(false,
                "Full-city weather QA FAILED: production scene already has registered payload feeds; " +
                "the automated camera count would be ambiguous.");
            yield break;
        }
        NYCGISWeatherCameraRegistry.ConfigureCapacity(NYCGISWeatherCameraRegistry.HardCapacity);
        AttachOffscreenOutput(baseCamera, 1280, 720, "QA_Main");
        qaCameras.Add(baseCamera);
        CreateAdditionalCameras(baseCamera);
        ConfigureProductionProfilingMode(
            bfp2ScreenCullPixelsOverride,
            bfp2CullAlgorithmOverride,
            bfp2GpuProfilerMarkers);
        try
        {
            if (sensorMode == NYCGISSensorDataMode.CpuSpatialIndex ||
                sensorMode == NYCGISSensorDataMode.GpuSpatialBrute ||
                sensorMode == NYCGISSensorDataMode.GpuSpatialIndex)
            {
                sensorBenchmark = new NYCGISSensorSpatialIndexBenchmark(
                    sensorMode,
                    sensorWidth,
                    sensorHeight,
                    sensorRateHz,
                    spatialQueryCount,
                    sampleFrames);
            }
            else if (sensorMode == NYCGISSensorDataMode.CpuBevCost ||
                sensorMode == NYCGISSensorDataMode.GpuBevResident)
            {
                sensorBenchmark = new NYCGISSensorBevResidentBenchmark(
                    sensorMode,
                    sensorWidth,
                    sensorHeight,
                    sensorRateHz,
                    sampleFrames);
            }
            else if (sensorMode == NYCGISSensorDataMode.CpuBev ||
                sensorMode == NYCGISSensorDataMode.GpuBev)
            {
                sensorBenchmark = new NYCGISSensorBevBenchmark(
                    sensorMode,
                    sensorWidth,
                    sensorHeight,
                    sensorRateHz,
                    sampleFrames);
            }
            else
            {
                sensorBenchmark = new NYCGISSensorDataBenchmark(
                    sensorMode,
                    sensorWidth,
                    sensorHeight,
                    sensorRateHz,
                    sampleFrames);
            }
            sensorBenchmark.BeginWarmup(Time.realtimeSinceStartupAsDouble);
        }
        catch (Exception exception)
        {
            Finish(false, "Full-city sensor benchmark FAILED to initialize: " + exception.Message);
            yield break;
        }

        NYCGISWeatherSystem weather = NYCGISWeatherSystem.Instance;
        if (weather == null || !weather.IsReady)
        {
            Finish(false, "Full-city weather QA FAILED: NYCGISWeatherSystem is not ready.");
            yield break;
        }
        weather.ApplyProfile(NYCGISWeatherProfile.HeavyStorm, 0.0f);
        weather.GetComponent<NYCGISWeatherVisualController>()?.RefreshNow();

        double warmupEnd = Time.realtimeSinceStartupAsDouble + warmupSeconds;
        while (Time.realtimeSinceStartupAsDouble < warmupEnd)
        {
            RenderQaCameras();
            sensorBenchmark.Tick(Time.realtimeSinceStartupAsDouble);
            FrameTimingManager.CaptureFrameTimings();
            yield return null;
        }

        mainSamples = new float[sampleFrames];
        renderSamples = new float[sampleFrames];
        gpuSamples = new float[sampleFrames];
        frameSamples = new float[sampleFrames];
        StartRecorders();
        sensorBenchmark.BeginMeasurement(Time.realtimeSinceStartupAsDouble);

        double sampleStart = Time.realtimeSinceStartupAsDouble;
        for (int i = 0; i < sampleFrames; i++)
        {
            RenderQaCameras();
            sensorBenchmark.Tick(Time.realtimeSinceStartupAsDouble);
            yield return null;
            mainSamples[i] = ReadMilliseconds(mainThreadRecorder);
            renderSamples[i] = ReadMilliseconds(renderThreadRecorder);
            gpuSamples[i] = ReadGpuFrameMilliseconds();
            frameSamples[i] = Time.unscaledDeltaTime * 1000.0f;
            FrameTimingManager.CaptureFrameTimings();
            long gc = ReadValue(gcAllocatedRecorder);
            long memory = ReadValue(totalUsedMemoryRecorder);
            long draws = ReadValue(drawCallsRecorder);
            totalGcBytes += Math.Max(0L, gc);
            maxGcBytes = Math.Max(maxGcBytes, gc);
            maxUsedMemoryBytes = Math.Max(maxUsedMemoryBytes, memory);
            totalDrawCalls += Math.Max(0L, draws);
        }
        double elapsed = Math.Max(0.001, Time.realtimeSinceStartupAsDouble - sampleStart);
        sensorBenchmark.StopIssuing();
        double drainDeadline = Time.realtimeSinceStartupAsDouble + 10.0;
        while (sensorBenchmark.HasPendingReadbacks &&
               Time.realtimeSinceStartupAsDouble < drainDeadline)
        {
            yield return null;
            sensorBenchmark.PollOnly(Time.realtimeSinceStartupAsDouble);
        }
        bool sensorDrainCompleted = !sensorBenchmark.HasPendingReadbacks;
        bool sensorPassed = sensorDrainCompleted && sensorBenchmark.Passed;

        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            RenderQaCameras();
            yield return new WaitForEndOfFrame();
        }

        Bfp2GpuIndirectRenderer bfp2 = FindAnyObjectByType<Bfp2GpuIndirectRenderer>();
        OrthophotoVirtualTextureController orthophoto =
            FindAnyObjectByType<OrthophotoVirtualTextureController>();
        ProjectedBuildingShadowController shadows =
            FindAnyObjectByType<ProjectedBuildingShadowController>();

        Bfp2CullValidationSnapshot cullValidation = default;
        bool cullValidationValid = bfp2 != null &&
            bfp2.TryCaptureCullValidationSnapshot(out cullValidation);

        bool screenshotSaved = false;
        string screenshotError = string.Empty;
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            screenshotSaved = CapturePrimaryOutput(screenshotPath, out screenshotError);
        }

        StringBuilder report = new StringBuilder();
        report.AppendLine("NYC GIS Full-City Weather Performance QA");
        report.AppendLine($"status={(sensorPassed ? "completed" : "failed")}");
        report.AppendLine($"cameraCount={cameraCount}");
        report.AppendLine("weather=HeavyStorm");
        report.AppendLine($"warmupSeconds={warmupSeconds:F1}");
        report.AppendLine($"sampleFrames={sampleFrames}");
        report.AppendLine($"sampleElapsedSeconds={elapsed:F3}");
        report.AppendLine($"fpsAverage={sampleFrames / elapsed:F2}");
        report.AppendLine("graphicsDeviceName=" + SanitizeLine(SystemInfo.graphicsDeviceName));
        report.AppendLine("graphicsDeviceVendor=" + SanitizeLine(SystemInfo.graphicsDeviceVendor));
        report.AppendLine("graphicsDeviceType=" + SystemInfo.graphicsDeviceType);
        report.AppendLine("graphicsDeviceVersion=" + SanitizeLine(SystemInfo.graphicsDeviceVersion));
        report.AppendLine($"graphicsMemorySizeMiB={SystemInfo.graphicsMemorySize}");
        report.AppendLine("operatingSystem=" + SanitizeLine(SystemInfo.operatingSystem));
        report.AppendLine($"sensorDrainCompleted={(sensorDrainCompleted ? 1 : 0)}");
        sensorBenchmark.AppendReport(report, elapsed);
        AppendMetric(report, "frameMs", frameSamples);
        AppendMetric(report, "mainThreadMs", mainSamples);
        AppendMetric(report, "renderThreadMs", renderSamples);
        AppendMetric(report, "gpuFrameMs", gpuSamples);
        report.AppendLine(
            "gpuFrameTimingSource=" +
            (frameTimingGpuSampleCount > 0
                ? "FrameTimingManager"
                : profilerGpuSampleCount > 0
                    ? "ProfilerRecorder"
                    : "unavailable"));
        report.AppendLine($"gpuFrameTimingValidSamples={frameTimingGpuSampleCount + profilerGpuSampleCount}");
        report.AppendLine($"gcBytesPerFrameAverage={(double)totalGcBytes / sampleFrames:F1}");
        report.AppendLine($"gcBytesPerFrameMaximum={maxGcBytes}");
        report.AppendLine($"usedMemoryMaximumBytes={maxUsedMemoryBytes}");
        report.AppendLine($"drawCallsAverage={(double)totalDrawCalls / sampleFrames:F1}");
        report.AppendLine($"registeredPayloadFeeds={NYCGISWeatherCameraRegistry.RegisteredCount}");
        report.AppendLine($"bfp2TotalPacks={(bfp2 != null ? bfp2.TotalPackCount : -1)}");
        report.AppendLine($"bfp2ResidentPacks={(bfp2 != null ? bfp2.ResidentPackCount : -1)}");
        report.AppendLine($"bfp2LoadingPacks={(bfp2 != null ? bfp2.LoadingPackCount : -1)}");
        report.AppendLine($"bfp2ResidentGpuBytes={(bfp2 != null ? bfp2.ResidentGpuBytes : -1L)}");
        report.AppendLine(
            $"bfp2ScreenSizeCullingEnabled={(bfp2 != null && bfp2.enableScreenSizeCulling ? 1 : 0)}");
        report.AppendLine(
            $"bfp2ProjectedPixelCullingEnabled={(bfp2 != null && bfp2.useProjectedPixelScreenSizeCulling ? 1 : 0)}");
        report.AppendLine(
            $"bfp2MinScreenRadiusPixels={(bfp2 != null ? bfp2.minScreenRadiusPixels : -1.0f):F3}");
        report.AppendLine(
            "bfp2RequestedCullAlgorithm=" +
            SanitizeLine(string.IsNullOrWhiteSpace(bfp2CullAlgorithmOverride)
                ? "scene-default"
                : bfp2CullAlgorithmOverride));
        report.AppendLine(
            "bfp2ActiveCullAlgorithm=" +
            SanitizeLine(bfp2 != null ? bfp2.ActiveClusterCullAlgorithmName : "missing"));
        report.AppendLine($"bfp2WaveCullSupported={(bfp2 != null && bfp2.WaveCullSupported ? 1 : 0)}");
        report.AppendLine(
            $"bfp2WaveTileCullSupported={(bfp2 != null && bfp2.WaveTileCullSupported ? 1 : 0)}");
        report.AppendLine(
            $"bfp2WaveTileIndex16CullSupported={(bfp2 != null && bfp2.WaveTileIndex16CullSupported ? 1 : 0)}");
        report.AppendLine(
            $"bfp2ClusterLocalIndex16ReadyPacks={(bfp2 != null ? bfp2.ClusterLocalIndex16ReadyPackCount : 0)}");
        long sourceIndex32Bytes =
            bfp2 != null ? bfp2.ClusterLocalIndex32SourceBytes : 0L;
        long packedIndex16Bytes =
            bfp2 != null ? bfp2.ClusterLocalIndex16PackedBytes : 0L;
        long clusterBaseVertexBytes =
            bfp2 != null ? bfp2.ClusterLocalIndex16BaseVertexBytes : 0L;
        long shippingIndex16Bytes = packedIndex16Bytes + clusterBaseVertexBytes;
        report.AppendLine($"bfp2ClusterLocalIndex32SourceBytes={sourceIndex32Bytes}");
        report.AppendLine($"bfp2ClusterLocalIndex16PackedBytes={packedIndex16Bytes}");
        report.AppendLine($"bfp2ClusterLocalIndex16BaseVertexBytes={clusterBaseVertexBytes}");
        report.AppendLine($"bfp2ClusterLocalIndex16ShippingBytes={shippingIndex16Bytes}");
        report.AppendLine(
            $"bfp2ClusterLocalIndex16PotentialSavedBytes={Math.Max(0L, sourceIndex32Bytes - shippingIndex16Bytes)}");
        report.AppendLine(
            $"bfp2ClusterLocalIndex16MaximumSpan={(bfp2 != null ? bfp2.ClusterLocalIndex16MaximumSpan : 0u)}");
        report.AppendLine(
            $"bfp2GpuProfilerMarkers={(bfp2 != null && bfp2.enableGpuProfilerMarkers ? 1 : 0)}");
        report.AppendLine(
            $"bfp2CompactClusterLayoutBytes={(bfp2 != null ? bfp2.TotalResidentClusters * 40UL : 0UL)}");
        report.AppendLine($"bfp2CullValidationValid={(cullValidationValid ? 1 : 0)}");
        report.AppendLine(
            $"bfp2ValidationResidentPacks={(cullValidationValid ? cullValidation.ResidentPacks : 0)}");
        report.AppendLine(
            $"bfp2ValidationVisibleClusters={(cullValidationValid ? cullValidation.VisibleClusters : 0UL)}");
        report.AppendLine(
            $"bfp2ValidationVisibleIndices={(cullValidationValid ? cullValidation.VisibleIndices : 0UL)}");
        report.AppendLine(
            $"bfp2ValidationCulledClusters={(cullValidationValid ? cullValidation.CulledClusters : 0UL)}");
        report.AppendLine(
            $"bfp2ValidationOverflowClusters={(cullValidationValid ? cullValidation.OverflowClusters : 0UL)}");
        report.AppendLine(
            $"bfp2ValidationDrawIndices={(cullValidationValid ? cullValidation.DrawIndices : 0UL)}");
        report.AppendLine(
            $"bfp2ValidationVisibleTiles={(cullValidationValid ? cullValidation.VisibleTiles : 0UL)}");
        report.AppendLine(
            $"bfp2ValidationPaddedDrawIndices={(cullValidationValid ? cullValidation.PaddedDrawIndices : 0UL)}");
        report.AppendLine(
            $"bfp2ValidationAvoidedLogicalIndexBytes={(cullValidationValid && cullValidation.VisibleTiles > 0UL ? cullValidation.VisibleIndices * 8UL : 0UL)}");
        report.AppendLine(
            $"bfp2ValidationPerPackHash={(cullValidationValid ? cullValidation.PerPackHash : 0UL):X16}");
        report.AppendLine("bfp2Status=" + SanitizeLine(bfp2 != null ? bfp2.LastStatus : "missing"));
        report.AppendLine($"orthophotoBasePages={(orthophoto != null ? orthophoto.LoadedBasePageCount : -1)}");
        report.AppendLine($"orthophotoLod0Pages={(orthophoto != null ? orthophoto.LoadedLod0PageCount : -1)}");
        report.AppendLine("shadowStatus=" + SanitizeLine(shadows != null ? shadows.DebugStatus : "missing"));
        report.AppendLine($"screenshotSaved={(screenshotSaved ? 1 : 0)}");
        report.AppendLine("screenshotPath=" + SanitizeLine(screenshotPath));
        report.AppendLine("screenshotError=" + SanitizeLine(screenshotError));
        report.AppendLine("manualReviewRequired=true");
        Finish(sensorPassed, report.ToString().TrimEnd());
    }

    private void CreateAdditionalCameras(Camera baseCamera)
    {
        float[] yawOffsets = { -32.0f, -16.0f, 16.0f, 32.0f, 48.0f };
        for (int i = 0; i < cameraCount - 1; i++)
        {
            GameObject host = new GameObject($"QA Payload Camera {i + 2}");
            host.SetActive(false);
            host.transform.position = baseCamera.transform.position;
            host.transform.rotation = baseCamera.transform.rotation * Quaternion.Euler(0.0f, yawOffsets[i], 0.0f);

            Camera camera = host.AddComponent<Camera>();
            camera.CopyFrom(baseCamera);
            camera.enabled = true;
            camera.depth = baseCamera.depth - 10.0f - i;
            camera.tag = "Untagged";
            RenderTexture output = AttachOffscreenOutput(camera, 960, 540, $"QA_Payload_{i + 2}");

            UniversalAdditionalCameraData additional = camera.GetUniversalAdditionalCameraData();
            additional.renderPostProcessing = false;
            additional.renderShadows = true;
            additional.requiresColorOption = CameraOverrideOption.Off;
            additional.requiresDepthOption = CameraOverrideOption.Off;

            NYCGISPayloadVisibilityCamera feed = host.AddComponent<NYCGISPayloadVisibilityCamera>();
            feed.feedId = FirstFeedId + i;
            feed.role = NYCGISWeatherCameraRole.UOC;
            feed.quality = NYCGISWeatherCameraQuality.Standard;
            feed.streamingPriority = 900 - i;
            feed.weatherSystem = NYCGISWeatherSystem.Instance;
            feed.payloadBand = (NYCGISPayloadBand)(i % 5);
            feed.evaluationRangeMeters = 12000.0f;
            feed.targetTexture = output;
            feed.refreshRateHz = 60.0f;
            feed.hidden = false;
            host.SetActive(true);
            qaCameras.Add(camera);
        }
    }

    private void RenderQaCameras()
    {
        for (int i = 0; i < qaCameras.Count; i++)
        {
            Camera camera = qaCameras[i];
            if (camera != null && camera.gameObject.activeInHierarchy && camera.targetTexture != null)
            {
                camera.Render();
            }
        }
    }

    private RenderTexture AttachOffscreenOutput(Camera camera, int width, int height, string outputName)
    {
        RenderTexture output = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = outputName,
            useMipMap = false,
            autoGenerateMips = false,
            antiAliasing = 1
        };
        output.Create();
        outputs.Add(output);
        camera.targetTexture = output;
        return output;
    }

    private static void ConfigureProductionProfilingMode(
        float screenCullPixelsOverride,
        string cullAlgorithmOverride,
        bool gpuProfilerMarkers)
    {
        DemoHUD[] huds = FindObjectsByType<DemoHUD>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < huds.Length; i++)
        {
            huds[i].showHud = false;
        }
        Bfp2GpuIndirectRenderer[] renderers =
            FindObjectsByType<Bfp2GpuIndirectRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].productionMode = true;
            renderers[i].showDebugPanel = false;
            renderers[i].enableDebugReadback = false;
            renderers[i].enableDetailedRuntimeStats = false;
            renderers[i].enableGpuProfilerMarkers = gpuProfilerMarkers;
            ApplyCullAlgorithmOverride(renderers[i], cullAlgorithmOverride);
            if (screenCullPixelsOverride >= 0.0f)
            {
                renderers[i].enableScreenSizeCulling = screenCullPixelsOverride > 0.0f;
                renderers[i].useProjectedPixelScreenSizeCulling = true;
                renderers[i].minScreenRadiusPixels = screenCullPixelsOverride;
            }
        }
    }

    private static void ApplyCullAlgorithmOverride(
        Bfp2GpuIndirectRenderer renderer,
        string algorithm)
    {
        if (renderer == null || string.IsNullOrWhiteSpace(algorithm))
        {
            return;
        }

        switch (algorithm.Trim().ToLowerInvariant())
        {
            case "scalar-aos":
            case "scalar_aos":
            case "scalaraos":
                renderer.clusterCullAlgorithm = Bfp2ClusterCullAlgorithm.ScalarAoS;
                break;
            case "scalar-compact":
            case "scalar_compact":
            case "scalarcompact":
                renderer.clusterCullAlgorithm = Bfp2ClusterCullAlgorithm.ScalarCompact;
                break;
            case "wave-compact":
            case "wave_compact":
            case "wavecompact":
                renderer.clusterCullAlgorithm = Bfp2ClusterCullAlgorithm.WaveCompact;
                break;
            case "wave-tile-32":
            case "wave_tile_32":
            case "wavetile32":
                renderer.clusterCullAlgorithm = Bfp2ClusterCullAlgorithm.WaveTile32;
                break;
            case "wave-tile-64":
            case "wave_tile_64":
            case "wavetile64":
                renderer.clusterCullAlgorithm = Bfp2ClusterCullAlgorithm.WaveTile64;
                break;
            case "wave-tile-32-index16":
            case "wave_tile_32_index16":
            case "wavetile32index16":
                renderer.clusterCullAlgorithm =
                    Bfp2ClusterCullAlgorithm.WaveTile32Index16;
                break;
            default:
                Debug.LogWarning("Unknown BFP2 cull algorithm override: " + algorithm, renderer);
                break;
        }
    }

    private bool CapturePrimaryOutput(string path, out string error)
    {
        error = string.Empty;
        if (outputs.Count == 0 || outputs[0] == null)
        {
            error = "primary render texture is unavailable";
            return false;
        }

        RenderTexture previous = RenderTexture.active;
        Texture2D texture = null;
        try
        {
            RenderTexture output = outputs[0];
            texture = new Texture2D(output.width, output.height, TextureFormat.RGB24, false);
            RenderTexture.active = output;
            texture.ReadPixels(new Rect(0, 0, output.width, output.height), 0, 0);
            texture.Apply(false, false);

            string absolute = Path.GetFullPath(path);
            string folder = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
            File.WriteAllBytes(absolute, ImageConversion.EncodeToPNG(texture));
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            if (texture != null)
            {
                Destroy(texture);
            }
        }
    }

    private static void DisableMissionAreaForFullCityQa()
    {
        NYCGISMapWorldService[] mapServices =
            FindObjectsByType<NYCGISMapWorldService>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < mapServices.Length; i++)
        {
            mapServices[i].bootstrapMissionAreaAroundActiveCamera = false;
            mapServices[i].ClearMissionArea();
        }

        Bfp2GpuIndirectRenderer[] renderers =
            FindObjectsByType<Bfp2GpuIndirectRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].ApplyDataRootConfig();
            renderers[i].RescanPacks();
        }
    }

    private void StartRecorders()
    {
        mainThreadRecorder = StartRecorder(ProfilerCategory.Internal, "Main Thread");
        renderThreadRecorder = StartRecorder(ProfilerCategory.Internal, "Render Thread");
        gpuFrameRecorder = StartRecorder(ProfilerCategory.Render, "GPU Frame Time");
        totalUsedMemoryRecorder = StartRecorder(ProfilerCategory.Memory, "Total Used Memory");
        gcAllocatedRecorder = StartRecorder(ProfilerCategory.Memory, "GC Allocated In Frame");
        drawCallsRecorder = StartRecorder(ProfilerCategory.Render, "Draw Calls Count");
    }

    private float ReadGpuFrameMilliseconds()
    {
        uint count = FrameTimingManager.GetLatestTimings(1, latestFrameTimings);
        if (count > 0 && latestFrameTimings[0].gpuFrameTime > 0.0)
        {
            frameTimingGpuSampleCount++;
            return (float)latestFrameTimings[0].gpuFrameTime;
        }

        float profilerMilliseconds = ReadMilliseconds(gpuFrameRecorder);
        if (profilerMilliseconds > 0.0f)
        {
            profilerGpuSampleCount++;
        }
        return profilerMilliseconds;
    }

    private void Finish(bool passed, string report)
    {
        if (passed)
        {
            Debug.Log(report, this);
        }
        else
        {
            Debug.LogError(report, this);
        }
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            try
            {
                string absolute = Path.GetFullPath(reportPath);
                string folder = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                File.WriteAllText(absolute, report + Environment.NewLine);
            }
            catch (Exception exception)
            {
                Debug.LogError("Could not write full-city weather QA report: " + exception.Message, this);
            }
        }
        Application.Quit(passed ? 0 : 2);
    }

    private static void AppendMetric(StringBuilder report, string name, float[] values)
    {
        float[] valid = GetValidSamples(values);
        if (valid.Length == 0)
        {
            report.AppendLine(name + "Average=unavailable");
            report.AppendLine(name + "P95=unavailable");
            report.AppendLine(name + "P99=unavailable");
            return;
        }
        Array.Sort(valid);
        double total = 0.0;
        for (int i = 0; i < valid.Length; i++)
        {
            total += valid[i];
        }
        report.AppendLine($"{name}Average={total / valid.Length:F3}");
        report.AppendLine($"{name}P95={Percentile(valid, 0.95f):F3}");
        report.AppendLine($"{name}P99={Percentile(valid, 0.99f):F3}");
    }

    private static float[] GetValidSamples(float[] values)
    {
        List<float> valid = new List<float>(values != null ? values.Length : 0);
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.IsNaN(values[i]) && !float.IsInfinity(values[i]) && values[i] > 0.0f)
                {
                    valid.Add(values[i]);
                }
            }
        }
        return valid.ToArray();
    }

    private static float Percentile(float[] sorted, float percentile)
    {
        int index = Mathf.Clamp(Mathf.CeilToInt(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static ProfilerRecorder StartRecorder(ProfilerCategory category, string statName)
    {
        try
        {
            return ProfilerRecorder.StartNew(category, statName, 15);
        }
        catch
        {
            return default;
        }
    }

    private static float ReadMilliseconds(ProfilerRecorder recorder)
    {
        return recorder.Valid ? (float)(recorder.LastValue * 1.0e-6) : 0.0f;
    }

    private static long ReadValue(ProfilerRecorder recorder)
    {
        return recorder.Valid ? recorder.LastValue : 0L;
    }

    private void DisposeRecorders()
    {
        DisposeRecorder(ref mainThreadRecorder);
        DisposeRecorder(ref renderThreadRecorder);
        DisposeRecorder(ref gpuFrameRecorder);
        DisposeRecorder(ref totalUsedMemoryRecorder);
        DisposeRecorder(ref gcAllocatedRecorder);
        DisposeRecorder(ref drawCallsRecorder);
    }

    private static void DisposeRecorder(ref ProfilerRecorder recorder)
    {
        if (recorder.Valid)
        {
            recorder.Dispose();
        }
        recorder = default;
    }

    private static Camera ResolveBaseCamera()
    {
        Camera main = Camera.main;
        if (main != null && main.enabled && main.gameObject.activeInHierarchy)
        {
            return main;
        }
        Camera[] cameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Camera best = null;
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera candidate = cameras[i];
            if (candidate != null && candidate.enabled && candidate.targetTexture == null &&
                (best == null || candidate.depth > best.depth))
            {
                best = candidate;
            }
        }
        return best;
    }

    private static bool HasArgument(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string ReadString(string[] args, string name, string fallback)
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

    private static int ReadInt(string[] args, string name, int fallback)
    {
        return int.TryParse(ReadString(args, name, string.Empty), out int value) ? value : fallback;
    }

    private static float ReadFloat(string[] args, string name, float fallback)
    {
        return float.TryParse(
            ReadString(args, name, string.Empty),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float value) ? value : fallback;
    }

    private static string SanitizeLine(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace('\r', ' ').Replace('\n', ' ');
    }
}
