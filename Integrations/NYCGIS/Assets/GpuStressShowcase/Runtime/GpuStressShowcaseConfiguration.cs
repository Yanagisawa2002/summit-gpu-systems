using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Summit.GpuAutotuning;
using Summit.GpuPrimitives;
using UnityEngine;

public enum GpuStressShowcaseVariant
{
    Baseline = 0,
    Optimized = 1
}

internal sealed class GpuStressShowcaseConfiguration
{
    public GpuStressShowcaseVariant Variant =
        GpuStressShowcaseVariant.Baseline;
    public int CameraCount = 1;
    public bool Cinematic;
    public float CinematicDurationSeconds = 45.0f;
    public float CinematicTimeOfDayHours = 17.10f;
    public int OutputWidth = 1920;
    public int OutputHeight = 1080;
    public bool CinematicFullscreen;
    public float SceneWarmupSeconds = 60.0f;
    public int WorkloadWarmupFrames = 120;
    public int WorkloadIssueIntervalFrames = 1;
    public int SampleFrames = 900;
    public int SensorElementCount = 1048576;
    public int SensorCount = 4;
    public int QueriesPerSensor = 64;
    public int ResidencyPointsPerPage = 2048;
    public int Seed = 1731;
    public bool AutoExit;
    public bool HideHud;
    public string ReportPath = string.Empty;
    public string ScreenshotPath = string.Empty;
    public string SourceCommit = string.Empty;
    public bool SourceDirty;
    public string PlayerSha256 = string.Empty;

    public static GpuStressShowcaseConfiguration Parse(string[] args)
    {
        var result = new GpuStressShowcaseConfiguration();
        string variant = ReadString(
            args,
            "-gpu-stress-variant",
            "baseline");
        result.Variant = string.Equals(
            variant,
            "optimized",
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(variant, "b", StringComparison.OrdinalIgnoreCase)
                ? GpuStressShowcaseVariant.Optimized
                : GpuStressShowcaseVariant.Baseline;
        result.Cinematic = HasArgument(args, "-gpu-stress-cinematic");
        result.CinematicDurationSeconds = Mathf.Clamp(
            ReadFloat(
                args,
                "-gpu-stress-cinematic-duration-seconds",
                result.CinematicDurationSeconds),
            15.0f,
            120.0f);
        result.CinematicTimeOfDayHours = Mathf.Clamp(
            ReadFloat(
                args,
                "-gpu-stress-cinematic-time-of-day",
                result.CinematicTimeOfDayHours),
            0.0f,
            24.0f);
        result.OutputWidth = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-output-width",
                result.OutputWidth),
            1024,
            7680);
        result.OutputHeight = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-output-height",
                result.OutputHeight),
            720,
            4320);
        result.CinematicFullscreen = HasArgument(
            args,
            "-gpu-stress-cinematic-fullscreen");
        result.CameraCount = Mathf.Clamp(
            ReadInt(args, "-gpu-stress-cameras", result.CameraCount),
            1,
            4);
        result.SceneWarmupSeconds = Mathf.Clamp(
            ReadFloat(
                args,
                "-gpu-stress-scene-warmup-seconds",
                result.SceneWarmupSeconds),
            0.0f,
            300.0f);
        result.WorkloadWarmupFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-workload-warmup-frames",
                result.WorkloadWarmupFrames),
            0,
            600);
        result.WorkloadIssueIntervalFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-workload-issue-interval-frames",
                result.WorkloadIssueIntervalFrames),
            1,
            60);
        result.SampleFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-sample-frames",
                result.SampleFrames),
            60,
            3600);
        result.SensorElementCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-sensor-elements",
                result.SensorElementCount),
            65536,
            1048576);
        result.SensorCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-sensor-count",
                result.SensorCount),
            2,
            8);
        result.QueriesPerSensor = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-queries-per-sensor",
                result.QueriesPerSensor),
            8,
            512);
        result.ResidencyPointsPerPage = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-stress-points-per-page",
                result.ResidencyPointsPerPage),
            256,
            2048);
        result.Seed = ReadInt(args, "-gpu-stress-seed", result.Seed);
        result.ReportPath = ReadString(
            args,
            "-gpu-stress-report",
            string.Empty);
        result.ScreenshotPath = ReadString(
            args,
            "-gpu-stress-screenshot",
            string.Empty);
        result.AutoExit = HasArgument(args, "-gpu-stress-auto-exit");
        result.HideHud = HasArgument(args, "-gpu-stress-no-hud");
        result.SourceCommit = ReadString(
            args,
            "-gpu-stress-source-commit",
            string.Empty);
        result.SourceDirty = HasArgument(
            args,
            "-gpu-stress-source-dirty");
        result.PlayerSha256 = ReadString(
            args,
            "-gpu-stress-player-sha256",
            string.Empty);
        return result;
    }

    public static bool HasArgument(string[] args, string name)
    {
        if (args == null)
        {
            return false;
        }
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string ReadString(
        string[] args,
        string name,
        string fallback)
    {
        if (args == null)
        {
            return fallback;
        }
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name,
                StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return fallback;
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        return int.TryParse(
            ReadString(args, name, string.Empty),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value)
                ? value
                : fallback;
    }

    private static float ReadFloat(
        string[] args,
        string name,
        float fallback)
    {
        return float.TryParse(
            ReadString(args, name, string.Empty),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float value)
                ? value
                : fallback;
    }
}

internal sealed class GpuStressTuningDecision
{
    private const string EmbeddedProfilePath =
        "GpuStressShowcase/AmdR9700AutotuneProfile";

    public GpuPrimitiveBackend SensorBackend = GpuPrimitiveBackend.Portable;
    public bool ProfileMatched;
    public string DeviceKey = string.Empty;
    public string Source = string.Empty;
    public string Summary = string.Empty;

    public static GpuStressTuningDecision Resolve(
        GpuStressShowcaseVariant variant)
    {
        GpuDeviceFingerprint device = GpuDeviceFingerprint.Capture();
        var result = new GpuStressTuningDecision
        {
            DeviceKey = device.StableKey
        };
        if (variant == GpuStressShowcaseVariant.Baseline)
        {
            result.Source = "portable fixed baseline";
            result.Summary = "Portable primitives; ScalarAoS; FIFO";
            return result;
        }

        GpuAutotuneProfile profile = null;
        string persistentPath = GpuAutotuneProfileStore.GetDefaultPath(device);
        if (GpuAutotuneProfileStore.TryLoad(
            persistentPath,
            device,
            out GpuAutotuneProfile persistent))
        {
            profile = persistent;
            result.Source = "persistent device profile";
        }
        else
        {
            TextAsset embedded = Resources.Load<TextAsset>(
                EmbeddedProfilePath);
            if (embedded != null)
            {
                try
                {
                    GpuAutotuneProfile candidate =
                        JsonUtility.FromJson<GpuAutotuneProfile>(
                            embedded.text);
                    if (candidate != null && candidate.device != null &&
                        candidate.device.Equals(device))
                    {
                        profile = candidate;
                        result.Source =
                            "embedded AMD R9700 formal profile bd60f3e";
                    }
                }
                catch (Exception)
                {
                    profile = null;
                }
            }
        }

        bool scanWave = TryResolveWave(
            profile,
            device,
            "exclusive-scan");
        bool sortWave = TryResolveWave(
            profile,
            device,
            "radix-sort-32");
        bool compactWave = TryResolveWave(
            profile,
            device,
            "stable-compaction");
        result.ProfileMatched =
            scanWave && sortWave && compactWave;
        bool waveSupported = GpuPrimitives.SupportsWaveOperations;
        result.SensorBackend =
            waveSupported && result.ProfileMatched
                ? GpuPrimitiveBackend.WaveOps
                : GpuPrimitiveBackend.Portable;
        if (!result.ProfileMatched)
        {
            result.Source = waveSupported
                ? "portable fallback: no matching accepted profile"
                : "portable fallback: WaveOps unsupported";
        }
        result.Summary = result.SensorBackend == GpuPrimitiveBackend.WaveOps
            ? "R9700 profile: WaveOps + Tile32; async rejected"
            : "Portable fallback + renderer capability fallback";
        return result;
    }

    private static bool TryResolveWave(
        GpuAutotuneProfile profile,
        GpuDeviceFingerprint device,
        string workload)
    {
        return profile != null &&
            profile.TryResolve(
                workload,
                device,
                out GpuPrimitiveBackend backend) &&
            backend == GpuPrimitiveBackend.WaveOps;
    }
}

internal static class GpuStressShowcaseMath
{
    public static void EvaluateCameraPose(
        Vector3 originPosition,
        Quaternion originRotation,
        int logicalFrame,
        out Vector3 position,
        out Quaternion rotation)
    {
        float phase = (logicalFrame % 720) / 720.0f * Mathf.PI * 2.0f;
        Vector3 localOffset = new Vector3(
            Mathf.Sin(phase) * 240.0f,
            Mathf.Sin(phase * 2.0f) * 45.0f,
            (Mathf.Cos(phase) - 1.0f) * 240.0f);
        position = originPosition + originRotation * localOffset;
        rotation = originRotation * Quaternion.Euler(
            Mathf.Sin(phase * 2.0f) * 4.0f,
            Mathf.Sin(phase) * 24.0f,
            0.0f);
    }

    public static double Average(IReadOnlyList<float> values)
    {
        if (values == null || values.Count == 0)
        {
            return 0.0;
        }
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < values.Count; i++)
        {
            float value = values[i];
            if (value > 0.0f && !float.IsNaN(value) &&
                !float.IsInfinity(value))
            {
                sum += value;
                count++;
            }
        }
        return count > 0 ? sum / count : 0.0;
    }

    public static double Percentile(
        IReadOnlyList<float> values,
        double percentile)
    {
        if (values == null || values.Count == 0)
        {
            return 0.0;
        }
        var valid = new List<float>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            float value = values[i];
            if (value > 0.0f && !float.IsNaN(value) &&
                !float.IsInfinity(value))
            {
                valid.Add(value);
            }
        }
        if (valid.Count == 0)
        {
            return 0.0;
        }
        valid.Sort();
        int index = Mathf.Clamp(
            Mathf.CeilToInt((float)(valid.Count * percentile)) - 1,
            0,
            valid.Count - 1);
        return valid[index];
    }

    public static string HashStrings(params string[] values)
    {
        ulong hash = 1469598103934665603UL;
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i] ?? string.Empty;
                for (int character = 0; character < value.Length; character++)
                {
                    hash ^= value[character];
                    hash *= 1099511628211UL;
                }
            }
        }
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }
}

[Serializable]
internal sealed class GpuStressShowcaseReport
{
    public int schemaVersion = 5;
    public string status = string.Empty;
    public string variant = string.Empty;
    public string generatedUtc = string.Empty;
    public string graphicsDeviceName = string.Empty;
    public string graphicsDeviceVendor = string.Empty;
    public string graphicsApi = string.Empty;
    public string graphicsVersion = string.Empty;
    public int graphicsMemoryMiB;
    public string unityVersion = string.Empty;
    public string sourceCommit = string.Empty;
    public bool sourceDirty;
    public string playerSha256 = string.Empty;
    public string deviceKey = string.Empty;
    public bool autotuneProfileMatched;
    public string autotuneSource = string.Empty;
    public string sensorBackend = string.Empty;
    public string rendererAlgorithm = string.Empty;
    public int rendererCount;
    public int cameraCount;
    public bool cinematic;
    public string cityCullCameraMode = string.Empty;
    public string cityValidationMode = string.Empty;
    public bool cityValidationStable;
    public bool cityValidationFreshDispatches;
    public int cityValidationSamples;
    public ulong cityValidationFirstPassEpoch;
    public ulong cityValidationLastPassEpoch;
    public int cityValidationPreparedCameraCount;
    public string cityValidationCameraStateHash = string.Empty;
    public int cityValidationDispatchedPackCount;
    public string cityValidationDispatchedPackHash = string.Empty;
    public bool cityValidationPackSetComplete;
    public string payloadRenderMode = string.Empty;
    public int measuredPayloadRenderCount;
    public int expectedPayloadRenderCount;
    public bool payloadRenderCountValid;
    public int measuredPrePayloadCitySubmissionCount;
    public int expectedPrePayloadCitySubmissionCount;
    public bool prePayloadCitySubmissionValid;
    public int measuredPayloadSrpRenderCount;
    public int expectedPayloadSrpRenderCount;
    public int measuredHeroSrpRenderCount;
    public int expectedHeroSrpRenderCount;
    public bool cameraRenderOrderValid;
    public int workloadIssueIntervalFrames;
    public int expectedWorkloadSubmissions;
    public bool workloadSubmissionCadenceValid;
    public int scheduledWorkloadIssueCount;
    public string workloadIssueFrameSequenceHash = string.Empty;
    public string workloadLogicalStateSequenceHash = string.Empty;
    public int workloadLogicalStateCount;
    public int workloadUniqueLogicalStates;
    public int warmupExpectedWorkloadSubmissions;
    public int warmupScheduledWorkloadIssueCount;
    public int warmupSensorSubmitted;
    public int warmupSensorDropped;
    public int warmupResidencySubmitted;
    public int warmupResidencyDropped;
    public int warmupDeadlineSubmitted;
    public int warmupDeadlineDropped;
    public int warmupUniqueLogicalStates;
    public string warmupIssueFrameSequenceHash = string.Empty;
    public string warmupLogicalStateSequenceHash = string.Empty;
    public bool warmupSubmissionCadenceValid;
    public bool measurementWorkloadsDrained;
    public bool measurementResidencyStable;
    public int measurementMinResidentPacks;
    public int measurementMaxResidentPacks;
    public long measurementMinResidentBytes;
    public long measurementMaxResidentBytes;
    public int measurementMaxLoadingPacks;
    public bool measurementCullTopologyValid;
    public int measurementCullTopologySamples;
    public int measurementMinCullCameraCount;
    public int measurementMaxCullCameraCount;
    public int measurementMinCullPackCount;
    public int measurementMaxCullPackCount;
    public string measurementCullTopologySequenceHash = string.Empty;
    public string cinematicRouteId = string.Empty;
    public double cinematicDurationSeconds;
    public float cinematicTimeOfDayHours;
    public string cinematicWeatherProfile = string.Empty;
    public int outputWidth;
    public int outputHeight;
    public int configuredMaxSampleFrames;
    public int orthophotoLoadedLod0Pages;
    public int orthophotoManifestTiles;
    public int orthophotoTileResolution;
    public string cinematicGalleryPrefix = string.Empty;
    public int sampleFrames;
    public double sampleElapsedSeconds;
    public double fpsAverage;
    public double frameAverageMs;
    public double frameP95Ms;
    public double frameP99Ms;
    public string gpuTimingMode = string.Empty;
    public string gpuTimingScopeVersion = string.Empty;
    public string gpuTimingQueue = string.Empty;
    public string gpuTimingIncludedWork = string.Empty;
    public string gpuTimingExcludedWork = string.Empty;
    public double gpuAverageMs;
    public int gpuTimingPrimeFrames;
    public int gpuTimingValidSamples;
    public bool sampleCompletenessValid;
    public bool gpuTimingCoverageValid;
    public int frameTimingGpuSamples;
    public int profilerGpuSamples;
    public double gpuP95Ms;
    public double gpuP99Ms;
    public bool nativeTimestampBackendAvailable;
    public string nativeTimestampSupportMessage = string.Empty;
    public int nativeTimestampAbiVersion;
    public uint nativeTimestampCapabilityFlags;
    public int nativeTimestampRingCapacity;
    public int nativeTimestampPreparedScopes;
    public int nativeTimestampRendererType;
    public uint nativeTimestampDeviceGeneration;
    public ulong nativeTimestampObservedFrequency;
    public string nativeTimestampDllSha256 = string.Empty;
    public int nativeTimestampWarmupPairs;
    public bool nativeTimestampWarmupPassed;
    public bool nativeTimestampOrderingDiscriminatorPassed;
    public int nativeTimestampWarmupControlSubmitted;
    public int nativeTimestampWarmupControlReady;
    public int nativeTimestampWarmupControlValid;
    public int nativeTimestampWarmupHeroSubmitted;
    public int nativeTimestampWarmupHeroReady;
    public int nativeTimestampWarmupHeroValid;
    public double nativeTimestampWarmupControlP50Ms;
    public double nativeTimestampWarmupControlP99Ms;
    public double nativeTimestampWarmupHeroP50Ms;
    public int nativeTimestampExpectedHeroSamples;
    public int nativeTimestampHeroSubmitted;
    public int nativeTimestampHeroReady;
    public int nativeTimestampHeroValid;
    public int nativeTimestampExpectedControlSamples;
    public int nativeTimestampControlSubmitted;
    public int nativeTimestampControlReady;
    public int nativeTimestampControlValid;
    public double nativeTimestampControlP50Ms;
    public double nativeTimestampControlP99Ms;
    public int nativeTimestampAcquireFailures;
    public int nativeTimestampPreparedScopeFailures;
    public int nativeTimestampResultFailures;
    public int nativeTimestampTimeouts;
    public int nativeTimestampPeakActiveSamples;
    public int nativeTimestampFinalPendingSamples;
    public int nativeTimestampFinalActiveSamples;
    public int nativeTimestampFinalReservedSamples;
    public int nativeTimestampFinalSubmittedSamples;
    public bool nativeTimestampTerminal;
    public string nativeTimestampTerminalStatus = string.Empty;
    public int nativeTimestampInstrumentationReadbackBytes;
    public int nativeTimestampMaximumResultPendingFrames;
    public bool heroTimestampEvidenceComplete;
    public int longFrames16Ms;
    public int longFrames33Ms;
    public double longFrame33Rate;
    public int sensorElementCount;
    public int sensorCount;
    public int queriesPerSensor;
    public int sensorSubmitted;
    public int sensorDropped;
    public double sensorCpuProducerAverageMs;
    public long sensorLogicalUploadBytes;
    public int sensorIndexBuildsPerUpdate;
    public int residencyPointsPerPage;
    public int residencySubmitted;
    public int residencyDropped;
    public long residencyLogicalUploadBytes;
    public double residencyHitRate;
    public int deadlineSubmitted;
    public int deadlineDropped;
    public double criticalLatencyAverageMs;
    public double criticalLatencyP99Ms;
    public int criticalLateObservations;
    public string deadlinePolicy = string.Empty;
    public int cityTotalPacks;
    public int cityResidentPacks;
    public int cityLoadingPacks;
    public long cityResidentGpuBytes;
    public string cityStatus = string.Empty;
    public ulong cityVisibleClusters;
    public ulong cityVisibleIndices;
    public ulong cityVisibleTiles;
    public ulong cityDrawIndices;
    public ulong cityLogicalOutputBytes;
    public ulong cityAvoidedIndexTrafficBytes;
    public string cityOutputHash = string.Empty;
    public string sensorOutputHash = string.Empty;
    public string residencyOutputHash = string.Empty;
    public string deadlineOutputHash = string.Empty;
    public string compositeOutputHash = string.Empty;
    public bool qualityPassed;
    public string qualityMessage = string.Empty;
    public string rawFramesPath = string.Empty;
    public string rawTimestampsPath = string.Empty;
    public string screenshotPath = string.Empty;
}
