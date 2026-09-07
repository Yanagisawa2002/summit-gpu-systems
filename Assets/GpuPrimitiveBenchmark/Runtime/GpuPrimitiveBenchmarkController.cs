using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Summit.GpuTimestamps;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// Single-Player, same-process GPU primitive benchmark. It is inert unless explicitly enabled
/// from the command line, and performs no GPU readback during measurement blocks.
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class GpuPrimitiveBenchmarkController : MonoBehaviour
{
    private const string EnableArgument = "-gpu-primitive-benchmark";
    private const string ControlId = "control/empty-command-buffer";
    private const string ControlMarker = "GPU.Primitives/Control/EmptyCommandBuffer";
    private const int MaxPreparedNativeTimestampScopes = 64;
    private static readonly ProfilerMarker EnqueueMarker =
        new ProfilerMarker("GPU.Primitives.Enqueue");
    private static readonly ProfilerMarker CompletionMarker =
        new ProfilerMarker("GPU.Primitives.CompletionFence");
    private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

    private readonly FrameTiming[] latestFrameTimings = new FrameTiming[1];
    private readonly List<GpuPrimitiveValidationResult> validationResults =
        new List<GpuPrimitiveValidationResult>();
    private readonly List<CommandBuffer> measurementCommands = new List<CommandBuffer>();
    private CommandBuffer[,] nativeTimestampMeasurementCommands;
    private readonly List<BenchmarkCaseView> allCases = new List<BenchmarkCaseView>();
    private readonly List<PendingNativeTimestamp> pendingNativeTimestamps =
        new List<PendingNativeTimestamp>(1024);

    private GpuPrimitiveBenchmarkCoreAdapter adapter;
    private GpuPrimitiveNativeTimestampBackend nativeTimestampBackend;
    private GpuTimestampSupport nativeTimestampSupport;
    private RawSample[] rawSamples;
    private BlockSummary[] blockSummaries;
    private int rawSampleCount;
    private int blockSummaryCount;
    private int nativeTimestampPreparedScopeCount;
    private ProfilerRecorder gpuFrameRecorder;
    private ProfilerRecorder mainThreadRecorder;
    private ProfilerRecorder renderThreadRecorder;
    private Camera timingCamera;
    private RenderTexture timingTarget;
    private bool finished;
    private bool nativeTimestampOperational;
    private string nativeTimestampInitializationError = string.Empty;
    private int nativeTimestampAcquireFailures;
    private int nativeTimestampResultFailures;
    private int nativeTimestampTimeouts;
    private bool nativeTimestampWarmupPassed;
    private string nativeTimestampWarmupStatus = "not-run";
    private double nativeTimestampWarmupElapsedMs;
    private ulong nativeTimestampWarmupFrequency;
    private ulong nativeTimestampWarmupFenceValue;
    private uint nativeTimestampWarmupDeviceGeneration;
    private int nativeTimestampWarmupResultUnityFrame;
    private int nativeTimestampWarmupInstrumentationReadbackBytes;

    private string reportDirectory;
    private int rounds = 3;
    private int localWarmupFrames = 60;
    private int sampleFrames = 240;
    private int cooldownFrames = 15;
    private int elementCount = 1 << 20;
    private int seed = 20260730;
    private int dispatchesPerFrame = 1;
    private float validationTimeoutSeconds = 60.0f;
    private int validationConsumeDelayFrames;
    private string operationFilter = "*";
    private string backendFilter = "portable,wave-ops";
    private bool requireCompleteGpuTimings = true;
    private string buildCommit = "unknown";
    private string portableShaderSha256 = "unknown";
    private string waveShaderSha256 = "unknown";
    private string runtimeApiSha256 = "unknown";
    private int processId;
    private double benchmarkStart;
    private string[] originalArguments;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!HasArgument(args, EnableArgument))
        {
            return;
        }

        GameObject host = new GameObject("GPU Primitive Benchmark Controller");
        DontDestroyOnLoad(host);
        GpuPrimitiveBenchmarkController controller =
            host.AddComponent<GpuPrimitiveBenchmarkController>();
        controller.Configure(args);
    }

    private void Start()
    {
        StartCoroutine(Run());
    }

    private void OnDisable()
    {
        DisposeResources();
    }

    private void Configure(string[] args)
    {
        originalArguments = args;
        reportDirectory = ReadString(args, "-gpu-primitive-report-dir", string.Empty);
        rounds = Mathf.Clamp(ReadInt(args, "-gpu-primitive-rounds", rounds), 1, 12);
        localWarmupFrames = Mathf.Clamp(
            ReadInt(args, "-gpu-primitive-warmup-frames", localWarmupFrames),
            5,
            1800);
        sampleFrames = Mathf.Clamp(
            ReadInt(args, "-gpu-primitive-sample-frames", sampleFrames),
            60,
            7200);
        cooldownFrames = Mathf.Clamp(
            ReadInt(args, "-gpu-primitive-cooldown-frames", cooldownFrames),
            0,
            600);
        elementCount = Mathf.Clamp(
            ReadInt(args, "-gpu-primitive-element-count", elementCount),
            1024,
            GpuPrimitiveBenchmarkCoreAdapter.MaxElementCount);
        seed = ReadInt(args, "-gpu-primitive-seed", seed);
        dispatchesPerFrame = Mathf.Clamp(
            ReadInt(args, "-gpu-primitive-dispatches-per-frame", dispatchesPerFrame),
            1,
            128);
        validationTimeoutSeconds = Mathf.Clamp(
            ReadFloat(
                args,
                "-gpu-primitive-validation-timeout-seconds",
                validationTimeoutSeconds),
            5.0f,
            600.0f);
        validationConsumeDelayFrames = Mathf.Clamp(ReadInt(args, "-gpu-primitive-validation-consume-delay-frames", 0), 0, 120);
        operationFilter = ReadString(
            args,
            "-gpu-primitive-operations",
            operationFilter);
        backendFilter = ReadString(
            args,
            "-gpu-primitive-backends",
            backendFilter);
        requireCompleteGpuTimings =
            ReadInt(
                args,
                "-gpu-primitive-require-complete-gpu-timings",
                requireCompleteGpuTimings ? 1 : 0) != 0;
        buildCommit = ReadString(
            args,
            "-gpu-primitive-build-commit",
            buildCommit);
        portableShaderSha256 = ReadString(
            args,
            "-gpu-primitive-portable-shader-sha256",
            portableShaderSha256);
        waveShaderSha256 = ReadString(
            args,
            "-gpu-primitive-wave-shader-sha256",
            waveShaderSha256);
        runtimeApiSha256 = ReadString(
            args,
            "-gpu-primitive-runtime-api-sha256",
            runtimeApiSha256);
    }

    private IEnumerator Run()
    {
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        processId = Process.GetCurrentProcess().Id;
        benchmarkStart = Time.realtimeSinceStartupAsDouble;
        SetupMinimalRuntime();

        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            Finish(false, "Missing mandatory -gpu-primitive-report-dir argument.");
            yield break;
        }

        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);

        try
        {
            adapter = new GpuPrimitiveBenchmarkCoreAdapter(
                elementCount,
                seed,
                dispatchesPerFrame,
                operationFilter,
                backendFilter,
                ReadString(originalArguments, "-gpu-primitive-distribution", "uniform"),
                ReadInt(originalArguments, "-gpu-primitive-key-bits", 32));
            InitializeNativeTimestampBackend();
            BuildCasesAndCommands();
            BuildNativeTimestampMeasurementCommands();
            rawSamples = new RawSample[
                checked(rounds * allCases.Count * sampleFrames)];
            blockSummaries = new BlockSummary[checked(rounds * allCases.Count)];
            WriteConfiguration();
            WriteDeviceMetadata();
        }
        catch (Exception exception)
        {
            Finish(false, "Benchmark initialization failed: " + exception);
            yield break;
        }

        // Pre-submit every command path before correctness validation so shader creation and
        // driver compilation are outside measured blocks.
        for (int i = 1; i < allCases.Count; i++)
        {
            RenderTimingAnchor();
            Graphics.ExecuteCommandBuffer(measurementCommands[i]);
            FrameTimingManager.CaptureFrameTimings();
            yield return EndOfFrame;
        }
        yield return WarmupNativeTimestampBackend();
        WriteConfiguration();

        bool warmupValidationPassed = true;
        for (int i = 0; i < adapter.Cases.Count; i++)
        {
            yield return ValidateCase(
                adapter.Cases[i],
                GpuPrimitiveValidationPhase.Warmup,
                passed => warmupValidationPassed &= passed);
        }
        if (!warmupValidationPassed)
        {
            WriteAllOutputs(false, "warmup-validation-failed");
            Finish(false, "Warmup correctness validation failed.");
            yield break;
        }

        StartRecorders();
        for (int round = 0; round < rounds; round++)
        {
            int[] order = BuildCounterbalancedOrder(allCases.Count, round);
            string orderText = BuildOrderText(order);
            for (int orderPosition = 0; orderPosition < order.Length; orderPosition++)
            {
                int caseIndex = order[orderPosition];
                BenchmarkCaseView benchmarkCase = allCases[caseIndex];
                CommandBuffer commands = measurementCommands[caseIndex];

                for (int frame = 0; frame < cooldownFrames; frame++)
                {
                    RenderTimingAnchor();
                    FrameTimingManager.CaptureFrameTimings();
                    yield return EndOfFrame;
                }

                for (int frame = 0; frame < localWarmupFrames; frame++)
                {
                    RenderTimingAnchor();
                    Graphics.ExecuteCommandBuffer(commands);
                    FrameTimingManager.CaptureFrameTimings();
                    yield return EndOfFrame;
                }

                int sampleStart = rawSampleCount;
                double blockStart = Time.realtimeSinceStartupAsDouble;
                for (int frame = 0; frame < sampleFrames; frame++)
                {
                    int sourceRowIndex = rawSampleCount;
                    int sourceUnityFrame = Time.frameCount;
                    ulong nativeUserTag = checked((ulong)sourceRowIndex + 1UL);
                    GpuTimestampSampleFlags nativeFlags =
                        benchmarkCase.Operation == "control"
                            ? GpuTimestampSampleFlags.EmptyScope
                            : GpuTimestampSampleFlags.None;
                    GpuTimestampToken nativeToken = default;
                    GpuTimestampStatus nativeStatus = GpuTimestampStatus.Unsupported;
                    CommandBuffer nativeMeasurementCommands = null;
                    if (nativeTimestampOperational && nativeTimestampBackend != null)
                    {
                        nativeStatus = nativeTimestampBackend.Acquire(
                            nativeUserTag,
                            nativeFlags,
                            sourceUnityFrame,
                            out nativeToken);
                        if (nativeStatus == GpuTimestampStatus.Ready)
                        {
                            if (!TryGetPreparedNativeTimestampCommands(
                                    nativeToken,
                                    caseIndex,
                                    out nativeMeasurementCommands))
                            {
                                nativeTimestampBackend.Cancel(nativeToken);
                                nativeStatus = GpuTimestampStatus.RingFull;
                                nativeTimestampAcquireFailures++;
                            }
                        }
                        else
                        {
                            nativeTimestampAcquireFailures++;
                        }
                    }

                    double enqueueStart;
                    double enqueueEnd;
                    long enqueueGcStart, enqueueGcBytes;
                    bool nativeSubmitted = false;
                    RenderTimingAnchor();

                    using (EnqueueMarker.Auto())
                    {
                        enqueueGcStart = GC.GetAllocatedBytesForCurrentThread();
                        enqueueStart = Stopwatch.GetTimestamp();
                        if (nativeStatus == GpuTimestampStatus.Ready)
                        {
                            nativeStatus =
                                nativeTimestampBackend.MarkSubmitted(nativeToken);
                            if (nativeStatus == GpuTimestampStatus.Ready)
                            {
                                Graphics.ExecuteCommandBuffer(
                                    nativeMeasurementCommands);
                                nativeSubmitted = true;
                            }
                            else
                            {
                                nativeTimestampResultFailures++;
                                nativeTimestampBackend.Cancel(nativeToken);
                                Graphics.ExecuteCommandBuffer(commands);
                            }
                        }
                        else
                        {
                            Graphics.ExecuteCommandBuffer(commands);
                        }
                        enqueueEnd = Stopwatch.GetTimestamp();
                        enqueueGcBytes = GC.GetAllocatedBytesForCurrentThread() - enqueueGcStart;
                    }

                    FrameTimingManager.CaptureFrameTimings();
                    yield return EndOfFrame;

                    float gpuMilliseconds;
                    string gpuTimingSource;
                    bool gpuTimingValid = TryReadGpuMilliseconds(
                        out gpuMilliseconds,
                        out gpuTimingSource);
                    rawSamples[sourceRowIndex] = new RawSample
                    {
                        ProcessId = processId,
                        Round = round + 1,
                        BlockIndex = blockSummaryCount + 1,
                        OrderPosition = orderPosition + 1,
                        Order = orderText,
                        CaseId = benchmarkCase.Id,
                        Operation = benchmarkCase.Operation,
                        Variant = benchmarkCase.Variant,
                        Marker = benchmarkCase.Marker,
                        SampleIndex = frame + 1,
                        SourceUnityFrame = sourceUnityFrame,
                        GpuRegionResultUnityFrame = 0,
                        GpuFrameDiagnosticReadUnityFrame = Time.frameCount,
                        ElapsedSeconds =
                            Time.realtimeSinceStartupAsDouble - benchmarkStart,
                        EnqueueCpuMs = TicksToMilliseconds(enqueueEnd - enqueueStart),
                        EnqueueGcBytes = enqueueGcBytes,
                        NativeTimestampToken = nativeToken.Value,
                        NativeTimestampUserTag = nativeUserTag,
                        NativeTimestampFlags = (uint)nativeFlags,
                        NativeTimestampStatus = nativeSubmitted
                            ? "pending"
                            : TimestampStatusName(nativeStatus),
                        NativeTimestampBeginTicks = 0,
                        NativeTimestampEndTicks = 0,
                        NativeTimestampElapsedTicks = 0,
                        NativeTimestampFrequency = 0,
                        NativeTimestampElapsedNanoseconds = 0,
                        NativeTimestampFenceValue = 0,
                        NativeTimestampDeviceGeneration = 0,
                        FrameMs = Time.unscaledDeltaTime * 1000.0f,
                        MainThreadMs = ReadRecorderMilliseconds(mainThreadRecorder),
                        RenderThreadMs = ReadRecorderMilliseconds(renderThreadRecorder),
                        GpuRegionTimingSource = nativeSubmitted
                            ? "native-d3d12-timestamp-query/pending"
                            : "unavailable",
                        GpuFrameMs = gpuMilliseconds,
                        GpuTimingSource = gpuTimingSource,
                        GpuTimingValid = gpuTimingValid,
                        MeasurementReadbackBytes = 0
                    };
                    rawSampleCount++;
                    if (nativeSubmitted)
                    {
                        pendingNativeTimestamps.Add(new PendingNativeTimestamp
                        {
                            Token = nativeToken,
                            SourceRowIndex = sourceRowIndex,
                            ExpectedFlags = nativeFlags
                        });
                    }
                    PollNativeTimestampResults(Time.frameCount);
                }

                double blockEnqueueElapsed =
                    Time.realtimeSinceStartupAsDouble - blockStart;
                yield return DrainNativeTimestampsForBlock(sampleStart, sampleFrames);
                FenceResult fenceResult = new FenceResult
                {
                    Supported = SystemInfo.supportsGraphicsFence,
                    Passed = false,
                    LatencyMs = -1.0
                };
                yield return CompleteWithFence(result => fenceResult = result);

                blockSummaries[blockSummaryCount] = SummarizeBlock(
                    benchmarkCase,
                    round + 1,
                    orderPosition + 1,
                    orderText,
                    sampleStart,
                    sampleFrames,
                    blockEnqueueElapsed,
                    fenceResult);
                blockSummaryCount++;
            }
        }
        yield return DrainAllNativeTimestamps();
        StopRecorders();

        bool finalValidationPassed = true;
        for (int i = 0; i < adapter.Cases.Count; i++)
        {
            yield return ValidateCase(
                adapter.Cases[i],
                GpuPrimitiveValidationPhase.Final,
                passed => finalValidationPassed &= passed);
        }

        bool gpuTimingComplete =
            nativeTimestampBackend != null &&
            nativeTimestampWarmupPassed &&
            nativeTimestampSupport.IsAvailable &&
            !nativeTimestampBackend.IsTerminal &&
            nativeTimestampBackend.ActiveSampleCount == 0 &&
            nativeTimestampBackend.ReservedSampleCount == 0 &&
            nativeTimestampBackend.SubmittedSampleCount == 0 &&
            pendingNativeTimestamps.Count == 0 &&
            nativeTimestampAcquireFailures == 0 &&
            nativeTimestampResultFailures == 0 &&
            nativeTimestampTimeouts == 0;
        for (int i = 0; i < blockSummaryCount; i++)
        {
            if (blockSummaries[i].GpuRegionValidSamples != sampleFrames)
            {
                gpuTimingComplete = false;
                break;
            }
        }

        bool passedBenchmark = warmupValidationPassed &&
            finalValidationPassed &&
            (!requireCompleteGpuTimings || gpuTimingComplete);
        WriteAllOutputs(
            passedBenchmark,
            passedBenchmark
                ? gpuTimingComplete
                    ? "completed"
                    : "completed-correctness-only"
                : !finalValidationPassed
                    ? "final-validation-failed"
                    : "incomplete-native-gpu-timing");
        Finish(
            passedBenchmark,
            passedBenchmark
                ? "GPU primitive benchmark completed."
                : "GPU primitive benchmark failed a quality gate.");
    }

    private void SetupMinimalRuntime()
    {
        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];
            if (behaviour != null && behaviour != this)
            {
                behaviour.enabled = false;
            }
        }

        timingCamera = FindFirstObjectByType<Camera>();
        if (timingCamera == null)
        {
            GameObject cameraHost = new GameObject("GPU Timing Anchor Camera");
            timingCamera = cameraHost.AddComponent<Camera>();
        }
        timingCamera.enabled = false;
        timingCamera.clearFlags = CameraClearFlags.SolidColor;
        timingCamera.backgroundColor = Color.black;
        timingCamera.cullingMask = 0;
        timingCamera.allowHDR = false;
        timingCamera.allowMSAA = false;
        timingTarget = new RenderTexture(
            64,
            64,
            24,
            RenderTextureFormat.ARGB32)
        {
            name = "GPU Primitive Benchmark Timing Anchor",
            useMipMap = false,
            autoGenerateMips = false
        };
        timingTarget.Create();
        timingCamera.targetTexture = timingTarget;
        AudioListener.pause = true;
    }

    private void RenderTimingAnchor()
    {
        timingCamera?.Render();
    }

    private void InitializeNativeTimestampBackend()
    {
        try
        {
            nativeTimestampOperational =
                GpuPrimitiveNativeTimestampBackend.TryCreate(
                    out nativeTimestampBackend,
                    out nativeTimestampSupport);
            if (nativeTimestampOperational)
            {
                nativeTimestampBackend.ExecuteFrequencyInitialization();
                Debug.Log(
                    "Native Direct3D 12 timestamp backend enabled: ringCapacity=" +
                    nativeTimestampSupport.RingCapacity +
                    ", abi=" + nativeTimestampSupport.AbiVersion,
                    this);
                return;
            }
            nativeTimestampInitializationError = nativeTimestampSupport.Message;
            Debug.LogWarning(
                "Native GPU timestamps unavailable; benchmark will remain correctness-only " +
                "unless strict timing is required. " + nativeTimestampInitializationError,
                this);
        }
        catch (Exception exception)
        {
            nativeTimestampOperational = false;
            nativeTimestampInitializationError =
                exception.GetType().Name + ": " + exception.Message;
            Debug.LogWarning(
                "Native GPU timestamp initialization failed; continuing correctness-only. " +
                nativeTimestampInitializationError,
                this);
        }
    }
    private IEnumerator WarmupNativeTimestampBackend()
    {
        nativeTimestampWarmupPassed = false;
        nativeTimestampWarmupElapsedMs = 0.0;
        nativeTimestampWarmupFrequency = 0;
        nativeTimestampWarmupFenceValue = 0;
        nativeTimestampWarmupDeviceGeneration = 0;
        nativeTimestampWarmupResultUnityFrame = 0;
        nativeTimestampWarmupInstrumentationReadbackBytes = 0;

        if (!nativeTimestampOperational || nativeTimestampBackend == null)
        {
            nativeTimestampWarmupStatus = "backend-unavailable";
            yield break;
        }

        const ulong warmupUserTag = ulong.MaxValue;
        const GpuTimestampSampleFlags warmupFlags =
            GpuTimestampSampleFlags.EmptyScope;
        int sourceFrame = Time.frameCount;
        GpuTimestampStatus status = nativeTimestampBackend.Acquire(
            warmupUserTag,
            warmupFlags,
            sourceFrame,
            out GpuTimestampToken token);
        if (status != GpuTimestampStatus.Ready)
        {
            nativeTimestampWarmupStatus =
                "acquire-" + TimestampStatusName(status);
            nativeTimestampOperational = false;
            yield break;
        }

        bool submitted = false;
        string submissionFailure = null;
        if (!TryGetPreparedNativeTimestampCommands(
                token,
                0,
                out CommandBuffer warmupCommands))
        {
            submissionFailure = "prepared-scope-unavailable";
        }
        try
        {
            if (submissionFailure == null)
            {
                status = nativeTimestampBackend.MarkSubmitted(token);
                if (status == GpuTimestampStatus.Ready)
                {
                    submitted = true;
                    RenderTimingAnchor();
                    Graphics.ExecuteCommandBuffer(warmupCommands);
                }
                else
                {
                    submissionFailure =
                        "mark-submitted-" + TimestampStatusName(status);
                }
            }
        }
        catch (Exception exception)
        {
            submissionFailure =
                "submit-exception-" + exception.GetType().Name;
        }

        if (submissionFailure != null)
        {
            if (!submitted)
            {
                try
                {
                    nativeTimestampBackend.Cancel(token);
                }
                catch
                {
                    // The backend is failed closed below; cleanup is best effort.
                }
            }
            nativeTimestampWarmupStatus = submissionFailure;
            nativeTimestampOperational = false;
            yield break;
        }

        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            FrameTimingManager.CaptureFrameTimings();
            yield return EndOfFrame;
            int resultFrame = Time.frameCount;
            status = nativeTimestampBackend.TryConsume(
                token,
                resultFrame,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                RenderTimingAnchor();
                continue;
            }
            if (status != GpuTimestampStatus.Ready)
            {
                nativeTimestampWarmupStatus =
                    "result-" + TimestampStatusName(status);
                nativeTimestampOperational = false;
                yield break;
            }

            nativeTimestampWarmupElapsedMs = result.ElapsedMilliseconds;
            nativeTimestampWarmupFrequency = result.TimestampFrequency;
            nativeTimestampWarmupFenceValue = result.FenceValue;
            nativeTimestampWarmupDeviceGeneration = result.DeviceGeneration;
            nativeTimestampWarmupResultUnityFrame = resultFrame;
            nativeTimestampWarmupInstrumentationReadbackBytes = 16;
            bool valid =
                result.Token.Value == token.Value &&
                result.Token.UserTag == warmupUserTag &&
                result.Token.Flags == warmupFlags &&
                result.NativeFlags == (uint)warmupFlags &&
                result.SourceFrame == sourceFrame &&
                result.ResultFrame == resultFrame &&
                result.TimestampFrequency > 0 &&
                result.FenceValue > 0 &&
                result.DeviceGeneration ==
                    nativeTimestampSupport.DeviceGeneration &&
                result.EndTicks >= result.BeginTicks &&
                result.ElapsedTicks == result.EndTicks - result.BeginTicks &&
                result.ElapsedMilliseconds >= 0.0 &&
                nativeTimestampBackend.ActiveSampleCount == 0 &&
                nativeTimestampBackend.ReservedSampleCount == 0 &&
                nativeTimestampBackend.SubmittedSampleCount == 0 &&
                !nativeTimestampBackend.IsTerminal;
            nativeTimestampWarmupPassed = valid;
            nativeTimestampWarmupStatus =
                valid ? "ready" : "validation-failed";
            if (!valid)
            {
                nativeTimestampOperational = false;
            }
            yield break;
        }

        nativeTimestampWarmupStatus = "timeout";
        nativeTimestampOperational = false;
    }


    private void BuildCasesAndCommands()
    {
        allCases.Add(new BenchmarkCaseView
        {
            Id = ControlId,
            Operation = "control",
            Variant = "empty-command-buffer",
            Marker = ControlMarker,
            LogicalBytesPerDispatch = 0L
        });
        CommandBuffer control = new CommandBuffer
        {
            name = ControlMarker
        };
        measurementCommands.Add(control);

        for (int i = 0; i < adapter.Cases.Count; i++)
        {
            GpuPrimitiveBenchmarkCase coreCase = adapter.Cases[i];
            allCases.Add(new BenchmarkCaseView
            {
                Id = coreCase.Id,
                Operation = coreCase.Operation,
                Variant = coreCase.Variant,
                Marker = coreCase.Marker,
                LogicalBytesPerDispatch = coreCase.LogicalBytesPerDispatch
            });
            measurementCommands.Add(
                adapter.CreateMeasurementCommandBuffer(coreCase));
        }
    }

    private void BuildNativeTimestampMeasurementCommands()
    {
        DisposeNativeTimestampMeasurementCommands();
        if (nativeTimestampBackend == null)
        {
            return;
        }

        nativeTimestampPreparedScopeCount = Math.Min(
            MaxPreparedNativeTimestampScopes,
            nativeTimestampBackend.Capacity);
        if (nativeTimestampPreparedScopeCount <= 0)
        {
            throw new InvalidOperationException(
                "Native timestamp backend exposed no usable scopes.");
        }

        nativeTimestampMeasurementCommands = new CommandBuffer[
            nativeTimestampPreparedScopeCount,
            allCases.Count];
        try
        {
            for (int scopeIndex = 0;
                scopeIndex < nativeTimestampPreparedScopeCount;
                scopeIndex++)
            {
                for (int caseIndex = 0; caseIndex < allCases.Count; caseIndex++)
                {
                    BenchmarkCaseView benchmarkCase = allCases[caseIndex];
                    CommandBuffer commands = new CommandBuffer
                    {
                        name =
                            benchmarkCase.Marker +
                            "/NativeTimestamp/" +
                            scopeIndex
                    };
                    nativeTimestampMeasurementCommands[
                        scopeIndex,
                        caseIndex] = commands;
                    nativeTimestampBackend.RecordBegin(scopeIndex, commands);
                    if (caseIndex != 0)
                    {
                        adapter.RecordMeasurement(
                            commands,
                            adapter.Cases[caseIndex - 1]);
                    }
                    nativeTimestampBackend.RecordEnd(scopeIndex, commands);
                }
            }
        }
        catch
        {
            DisposeNativeTimestampMeasurementCommands();
            throw;
        }
    }

    private bool TryGetPreparedNativeTimestampCommands(
        GpuTimestampToken token,
        int caseIndex,
        out CommandBuffer commands)
    {
        commands = null;
        if (!token.IsValid ||
            nativeTimestampMeasurementCommands == null ||
            token.ScopeIndex < 0 ||
            token.ScopeIndex >= nativeTimestampPreparedScopeCount ||
            caseIndex < 0 ||
            caseIndex >= allCases.Count)
        {
            return false;
        }

        commands = nativeTimestampMeasurementCommands[
            token.ScopeIndex,
            caseIndex];
        return commands != null;
    }

    private IEnumerator ValidateCase(
        GpuPrimitiveBenchmarkCase benchmarkCase,
        GpuPrimitiveValidationPhase phase,
        Action<bool> completion)
    {
        adapter.BeginValidation(benchmarkCase, phase);
        // Regression probe only: completed readbacks must remain owned even if
        // the validation consumer is delayed beyond Unity's one-frame lifetime.
        for (int frame = 0; frame < validationConsumeDelayFrames; frame++) yield return null;
        double deadline = Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            if (adapter.TryCompleteValidation(out GpuPrimitiveValidationResult result))
            {
                validationResults.Add(result);
                completion(result.Passed);
                yield break;
            }
            yield return null;
        }

        GpuPrimitiveValidationResult timeout = new GpuPrimitiveValidationResult
        {
            Phase = phase.ToString().ToLowerInvariant(),
            CaseId = benchmarkCase.Id,
            Operation = benchmarkCase.Operation,
            Variant = benchmarkCase.Variant,
            Passed = false,
            Message = "Validation timed out.",
            ReadbackBytes = 0L,
            ResultHash = "unavailable"
        };
        validationResults.Add(timeout);
        completion(false);
    }

    private IEnumerator CompleteWithFence(Action<FenceResult> completion)
    {
        if (!SystemInfo.supportsGraphicsFence)
        {
            completion(new FenceResult
            {
                Supported = false,
                Passed = false,
                LatencyMs = -1.0
            });
            yield break;
        }

        GraphicsFence fence;
        double start;
        using (CompletionMarker.Auto())
        {
            CommandBuffer fenceCommands = new CommandBuffer
            {
                name = "GPU.Primitives/CompletionFence"
            };
            fenceCommands.BeginSample("GPU.Primitives/CompletionFence");
            fence = fenceCommands.CreateGraphicsFence(
                GraphicsFenceType.AsyncQueueSynchronisation,
                SynchronisationStageFlags.ComputeProcessing);
            fenceCommands.EndSample("GPU.Primitives/CompletionFence");
            start = Time.realtimeSinceStartupAsDouble;
            Graphics.ExecuteCommandBuffer(fenceCommands);
            fenceCommands.Dispose();
        }

        double deadline = start + validationTimeoutSeconds;
        bool passed = false;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            try
            {
                if (fence.passed)
                {
                    passed = true;
                    break;
                }
            }
            catch (InvalidOperationException)
            {
                break;
            }
            yield return null;
        }
        completion(new FenceResult
        {
            Supported = true,
            Passed = passed,
            LatencyMs = passed
                ? (Time.realtimeSinceStartupAsDouble - start) * 1000.0
                : -1.0
        });
    }

    private bool TryReadGpuMilliseconds(out float milliseconds, out string source)
    {
        uint count = FrameTimingManager.GetLatestTimings(1, latestFrameTimings);
        if (count > 0 && latestFrameTimings[0].gpuFrameTime > 0.0)
        {
            milliseconds = (float)latestFrameTimings[0].gpuFrameTime;
            source = "FrameTimingManager/unpaired-block-diagnostic";
            return true;
        }

        milliseconds = ReadRecorderMilliseconds(gpuFrameRecorder);
        if (milliseconds > 0.0f)
        {
            source = "ProfilerRecorder/unpaired-block-diagnostic";
            return true;
        }
        source = "unavailable";
        return false;
    }

    private void PollNativeTimestampResults(int resultFrame)
    {
        if (nativeTimestampBackend == null)
        {
            return;
        }

        for (int i = pendingNativeTimestamps.Count - 1; i >= 0; i--)
        {
            PendingNativeTimestamp pending = pendingNativeTimestamps[i];
            GpuTimestampStatus status = nativeTimestampBackend.TryConsume(
                pending.Token,
                resultFrame,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                continue;
            }

            RawSample sample = rawSamples[pending.SourceRowIndex];
            sample.GpuRegionResultUnityFrame = resultFrame;
            sample.NativeTimestampStatus = TimestampStatusName(status);
            if (status == GpuTimestampStatus.Ready)
            {
                ulong expectedToken = pending.Token.Value;
                ulong expectedUserTag = pending.Token.UserTag;
                uint expectedFlags = (uint)pending.ExpectedFlags;
                bool identityValid =
                    result.Token.Value == expectedToken &&
                    result.Token.UserTag == expectedUserTag;
                sample.NativeTimestampToken = result.Token.Value;
                sample.NativeTimestampUserTag = result.Token.UserTag;
                sample.NativeTimestampFlags = result.NativeFlags;
                sample.NativeTimestampBeginTicks = result.BeginTicks;
                sample.NativeTimestampEndTicks = result.EndTicks;
                sample.NativeTimestampElapsedTicks = result.ElapsedTicks;
                sample.NativeTimestampFrequency = result.TimestampFrequency;
                sample.NativeTimestampElapsedNanoseconds =
                    result.ElapsedNanoseconds;
                sample.NativeTimestampFenceValue = result.FenceValue;
                sample.NativeTimestampDeviceGeneration =
                    result.DeviceGeneration;
                sample.TimestampInstrumentationReadbackBytes = 16;
                sample.GpuRegionElapsedMs = result.ElapsedMilliseconds;
                sample.GpuRegionSampleBlocks = 1;
                sample.GpuRegionTimingSource =
                    pending.TimedOut
                        ? "native-d3d12-timestamp-query/ready-after-timeout"
                        : "native-d3d12-timestamp-query";

                bool expectsEmpty =
                    (pending.ExpectedFlags & GpuTimestampSampleFlags.EmptyScope) != 0;
                bool nativeEmpty =
                    (result.NativeFlags &
                        (uint)GpuTimestampSampleFlags.EmptyScope) != 0;
                bool exactFlags = result.NativeFlags == expectedFlags;
                bool durationValid = expectsEmpty
                    ? result.ElapsedMilliseconds >= 0.0
                    : result.ElapsedTicks > 0 &&
                        result.ElapsedMilliseconds > 0.0;
                sample.GpuRegionTimingValid =
                    !pending.TimedOut &&
                    identityValid &&
                    result.SourceFrame == sample.SourceUnityFrame &&
                    result.ResultFrame == resultFrame &&
                    exactFlags &&
                    result.FenceValue > 0 &&
                    expectsEmpty == nativeEmpty &&
                    durationValid;
                if (!sample.GpuRegionTimingValid)
                {
                    nativeTimestampResultFailures++;
                    nativeTimestampOperational = false;
                }
            }
            else
            {
                sample.GpuRegionTimingSource =
                    "native-d3d12-timestamp-query/" +
                    TimestampStatusName(status);
                sample.GpuRegionSampleBlocks = 0;
                sample.GpuRegionTimingValid = false;
                nativeTimestampResultFailures++;
                nativeTimestampOperational = false;
            }
            rawSamples[pending.SourceRowIndex] = sample;
            pendingNativeTimestamps.RemoveAt(i);
        }
    }

    private IEnumerator DrainNativeTimestampsForBlock(int sampleStart, int count)
    {
        if (nativeTimestampBackend == null ||
            !HasPendingNativeTimestamps(sampleStart, count))
        {
            yield break;
        }

        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (HasPendingNativeTimestamps(sampleStart, count) &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            RenderTimingAnchor();
            FrameTimingManager.CaptureFrameTimings();
            yield return EndOfFrame;
            PollNativeTimestampResults(Time.frameCount);
        }

        if (HasPendingNativeTimestamps(sampleStart, count))
        {
            MarkNativeTimestampTimeouts(sampleStart, count);
            nativeTimestampOperational = false;
        }
    }

    private IEnumerator DrainAllNativeTimestamps()
    {
        if (nativeTimestampBackend == null || pendingNativeTimestamps.Count == 0)
        {
            yield break;
        }

        double deadline =
            Time.realtimeSinceStartupAsDouble + validationTimeoutSeconds;
        while (pendingNativeTimestamps.Count != 0 &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            RenderTimingAnchor();
            FrameTimingManager.CaptureFrameTimings();
            yield return EndOfFrame;
            PollNativeTimestampResults(Time.frameCount);
        }
    }

    private bool HasPendingNativeTimestamps(int sampleStart, int count)
    {
        int sampleEnd = sampleStart + count;
        for (int i = 0; i < pendingNativeTimestamps.Count; i++)
        {
            int row = pendingNativeTimestamps[i].SourceRowIndex;
            if (row >= sampleStart && row < sampleEnd)
            {
                return true;
            }
        }
        return false;
    }

    private void MarkNativeTimestampTimeouts(int sampleStart, int count)
    {
        int sampleEnd = sampleStart + count;
        for (int i = 0; i < pendingNativeTimestamps.Count; i++)
        {
            PendingNativeTimestamp pending = pendingNativeTimestamps[i];
            if (pending.SourceRowIndex < sampleStart ||
                pending.SourceRowIndex >= sampleEnd ||
                pending.TimedOut)
            {
                continue;
            }

            pending.TimedOut = true;
            pendingNativeTimestamps[i] = pending;
            RawSample sample = rawSamples[pending.SourceRowIndex];
            sample.GpuRegionResultUnityFrame = Time.frameCount;
            sample.NativeTimestampStatus = "timeout";
            sample.GpuRegionTimingSource =
                "native-d3d12-timestamp-query/timeout";
            sample.GpuRegionSampleBlocks = 0;
            sample.GpuRegionTimingValid = false;
            rawSamples[pending.SourceRowIndex] = sample;
            nativeTimestampTimeouts++;
        }
    }

    private static string TimestampStatusName(GpuTimestampStatus status)
    {
        switch (status)
        {
            case GpuTimestampStatus.Error: return "error";
            case GpuTimestampStatus.Ready: return "ready";
            case GpuTimestampStatus.Pending: return "pending";
            case GpuTimestampStatus.InvalidArgument: return "invalid-argument";
            case GpuTimestampStatus.InvalidToken: return "invalid-token";
            case GpuTimestampStatus.RingFull: return "ring-full";
            case GpuTimestampStatus.NotInitialized: return "not-initialized";
            case GpuTimestampStatus.Unsupported: return "unsupported";
            case GpuTimestampStatus.DeviceLost: return "device-lost";
            case GpuTimestampStatus.CallbackError: return "callback-error";
            case GpuTimestampStatus.FrequencyUnavailable:
                return "frequency-unavailable";
            case GpuTimestampStatus.StaleManagedToken:
                return "stale-managed-token";
            case GpuTimestampStatus.MalformedNativeResult:
                return "malformed-native-result";
            default: return "error";
        }
    }

    private BlockSummary SummarizeBlock(
        BenchmarkCaseView benchmarkCase,
        int round,
        int orderPosition,
        string order,
        int sampleStart,
        int count,
        double blockEnqueueElapsed,
        FenceResult fence)
    {
        double[] frame = new double[count];
        double[] gpuRegion = new double[count];
        double[] gpu = new double[count];
        double[] enqueue = new double[count];
        double[] main = new double[count];
        double[] render = new double[count];
        int gpuRegionCount = 0;
        int gpuCount = 0;
        long timestampInstrumentationReadbackBytes = 0L;
        for (int i = 0; i < count; i++)
        {
            RawSample sample = rawSamples[sampleStart + i];
            timestampInstrumentationReadbackBytes +=
                sample.TimestampInstrumentationReadbackBytes;
            frame[i] = sample.FrameMs;
            enqueue[i] = sample.EnqueueCpuMs;
            main[i] = sample.MainThreadMs;
            render[i] = sample.RenderThreadMs;
            if (sample.GpuRegionTimingValid)
            {
                gpuRegion[gpuRegionCount++] = sample.GpuRegionElapsedMs;
            }
            if (sample.GpuTimingValid)
            {
                gpu[gpuCount++] = sample.GpuFrameMs;
            }
        }

        MetricStats frameStats = CalculateStats(frame, count);
        MetricStats gpuRegionStats = CalculateStats(gpuRegion, gpuRegionCount);
        MetricStats gpuStats = CalculateStats(gpu, gpuCount);
        MetricStats enqueueStats = CalculateStats(enqueue, count);
        MetricStats mainStats = CalculateStats(main, count);
        MetricStats renderStats = CalculateStats(render, count);
        return new BlockSummary
        {
            ProcessId = processId,
            Round = round,
            BlockIndex = blockSummaryCount + 1,
            OrderPosition = orderPosition,
            Order = order,
            CaseId = benchmarkCase.Id,
            Operation = benchmarkCase.Operation,
            Variant = benchmarkCase.Variant,
            Marker = benchmarkCase.Marker,
            Samples = count,
            GpuRegionValidSamples = gpuRegionCount,
            GpuValidSamples = gpuCount,
            GpuRegion = gpuRegionStats,
            DispatchesPerFrame =
                benchmarkCase.Operation == "control" ? 0 : dispatchesPerFrame,
            LogicalBytesPerDispatch = benchmarkCase.LogicalBytesPerDispatch,
            BlockEnqueueElapsedMs = blockEnqueueElapsed * 1000.0,
            FenceSupported = fence.Supported,
            FencePassed = fence.Passed,
            FenceLatencyMs = fence.LatencyMs,
            Frame = frameStats,
            Gpu = gpuStats,
            Enqueue = enqueueStats,
            Main = mainStats,
            Render = renderStats,
            MeasurementReadbackBytes = 0L,
            TimestampInstrumentationReadbackBytes =
                timestampInstrumentationReadbackBytes
        };
    }

    private static MetricStats CalculateStats(double[] values, int count)
    {
        if (count <= 0)
        {
            return new MetricStats
            {
                Count = 0,
                Average = -1.0,
                P50 = -1.0,
                P95 = -1.0,
                P99 = -1.0,
                Minimum = -1.0,
                Maximum = -1.0
            };
        }

        double[] sorted = new double[count];
        double sum = 0.0;
        for (int i = 0; i < count; i++)
        {
            sorted[i] = values[i];
            sum += values[i];
        }
        Array.Sort(sorted);
        return new MetricStats
        {
            Count = count,
            Average = sum / count,
            P50 = Percentile(sorted, 0.50),
            P95 = Percentile(sorted, 0.95),
            P99 = Percentile(sorted, 0.99),
            Minimum = sorted[0],
            Maximum = sorted[count - 1]
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = Mathf.Clamp(
            Mathf.CeilToInt((float)(sorted.Length * percentile)) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private static int[] BuildCounterbalancedOrder(int count, int round)
    {
        int[] order = new int[count];
        int shift = (round / 2) % count;
        bool reverse = (round & 1) != 0;
        for (int position = 0; position < count; position++)
        {
            int offset = reverse ? count - 1 - position : position;
            order[position] = (offset + shift) % count;
        }
        return order;
    }

    private string BuildOrderText(int[] order)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < order.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('>');
            }
            builder.Append(allCases[order[i]].Id);
        }
        return builder.ToString();
    }

    private void WriteAllOutputs(bool passed, string status)
    {
        WriteRawSamples();
        WriteBlockSummaries();
        WriteValidationResults();
        WriteRunSummary(passed, status);
    }

    private void WriteConfiguration()
    {
        adapter.WriteCandidateMetadata(reportDirectory);
        string[] caseIds = new string[allCases.Count];
        string[] markers = new string[allCases.Count];
        for (int i = 0; i < allCases.Count; i++)
        {
            caseIds[i] = allCases[i].Id;
            markers[i] = allCases[i].Marker;
        }

        BenchmarkConfiguration config = new BenchmarkConfiguration
        {
            schemaVersion = 3,
            processId = processId,
            unityVersion = Application.unityVersion,
            startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            rounds = rounds,
            localWarmupFrames = localWarmupFrames,
            sampleFrames = sampleFrames,
            cooldownFrames = cooldownFrames,
            elementCount = elementCount,
            seed = seed,
            dispatchesPerFrame = dispatchesPerFrame,
            validationTimeoutSeconds = validationTimeoutSeconds,
            validationConsumeDelayFrames = validationConsumeDelayFrames,
            requestedOperations = operationFilter,
            requestedBackends = backendFilter,
            distribution = ReadString(originalArguments, "-gpu-primitive-distribution", "uniform"),
            keyBitCount = ReadInt(originalArguments, "-gpu-primitive-key-bits", 32),
            selectedCases = caseIds,
            gpuMarkers = markers,
            adapter = adapter.ImplementationName,
            externalBenchmarkBufferBytes = adapter.ExternalBufferBytes,
            primitiveScratchBytes = adapter.PrimitiveScratchBytes,
            totalResidentBytes = adapter.ResidentBytes,
            supportsWaveOperations = adapter.SupportsWaveOperations,
            supportsGpuRecorder = SystemInfo.supportsGpuRecorder,
            sameProcessCounterbalanced = true,
            requireCompleteGpuTimings = requireCompleteGpuTimings,
            nativeTimestampBackendRequested = true,
            nativeTimestampBackendSelected =
                nativeTimestampBackend != null
                    ? "native-d3d12-timestamp-query"
                    : "correctness-only",
            nativeTimestampAvailability =
                nativeTimestampSupport.Availability.ToString(),
            nativeTimestampMessage =
                nativeTimestampSupport.Message ?? nativeTimestampInitializationError,
            nativeTimestampAbiVersion = nativeTimestampSupport.AbiVersion,
            nativeTimestampCapabilityFlags =
                nativeTimestampSupport.CapabilityFlags,
            nativeTimestampCapabilityContract =
                "bit0=direct-queue;bit1=nonblocking-poll;bit2=raw-ticks;" +
                "bit3=stable-payloads;" +
                "bit4=private-completion-fence",
            nativeTimestampRingCapacity = nativeTimestampSupport.RingCapacity,
            nativeTimestampRendererType = nativeTimestampSupport.RendererType,
            nativeTimestampDeviceGeneration =
                nativeTimestampSupport.DeviceGeneration,
            nativeTimestampSessionCreateFrequencyReady =
                nativeTimestampSupport.FrequencyReady,
            nativeTimestampSessionCreateFrequency =
                nativeTimestampSupport.TimestampFrequency,
            nativeTimestampWarmupPassed = nativeTimestampWarmupPassed,
            nativeTimestampWarmupStatus = nativeTimestampWarmupStatus,
            nativeTimestampWarmupElapsedMs = nativeTimestampWarmupElapsedMs,
            nativeTimestampWarmupFrequency = nativeTimestampWarmupFrequency,
            nativeTimestampWarmupFenceValue = nativeTimestampWarmupFenceValue,
            nativeTimestampWarmupDeviceGeneration =
                nativeTimestampWarmupDeviceGeneration,
            nativeTimestampWarmupResultUnityFrame =
                nativeTimestampWarmupResultUnityFrame,
            nativeTimestampWarmupInstrumentationReadbackBytes =
                nativeTimestampWarmupInstrumentationReadbackBytes,
            gpuTimingScope =
                "primary: native Direct3D 12 begin/end timestamp queries around three " +
                "consecutively submitted pre-recorded command buffers; secondary diagnostic: " +
                "unpaired whole-frame FrameTimingManager/GPU Frame Time",
            measurementReadbackBytesPerFrame = 0,
            timestampInstrumentationReadbackBytesPerCompletedSample = 16,
            buildCommit = buildCommit,
            portableShaderSha256 = portableShaderSha256,
            waveShaderSha256 = waveShaderSha256,
            runtimeApiSha256 = runtimeApiSha256,
            commandLine = SanitizeCommandLine(originalArguments)
        };
        File.WriteAllText(
            Path.Combine(reportDirectory, "config.json"),
            JsonUtility.ToJson(config, true),
            new UTF8Encoding(false));
    }

    private void WriteDeviceMetadata()
    {
        DeviceMetadata device = new DeviceMetadata
        {
            processId = processId,
            operatingSystem = SystemInfo.operatingSystem,
            processorType = SystemInfo.processorType,
            processorCount = SystemInfo.processorCount,
            systemMemoryMiB = SystemInfo.systemMemorySize,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
            graphicsDeviceId = SystemInfo.graphicsDeviceID,
            graphicsDeviceVendorId = SystemInfo.graphicsDeviceVendorID,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
            graphicsMemoryMiB = SystemInfo.graphicsMemorySize,
            graphicsShaderLevel = SystemInfo.graphicsShaderLevel,
            supportsComputeShaders = SystemInfo.supportsComputeShaders,
            supportsAsyncCompute = SystemInfo.supportsAsyncCompute,
            supportsGraphicsFence = SystemInfo.supportsGraphicsFence,
            supportsAsyncGpuReadback = SystemInfo.supportsAsyncGPUReadback,
            supportsWaveOperations = adapter.SupportsWaveOperations,
            supportsGpuRecorder = SystemInfo.supportsGpuRecorder,
            frameTimingFeatureEnabled = FrameTimingManager.IsFeatureEnabled(),
            gpuTimerFrequency = FrameTimingManager.GetGpuTimerFrequency(),
            nativeTimestampAvailability =
                nativeTimestampSupport.Availability.ToString(),
            nativeTimestampAbiVersion = nativeTimestampSupport.AbiVersion,
            nativeTimestampCapabilityFlags =
                nativeTimestampSupport.CapabilityFlags,
            nativeTimestampRingCapacity = nativeTimestampSupport.RingCapacity,
            nativeTimestampRendererType = nativeTimestampSupport.RendererType,
            nativeTimestampDeviceGeneration =
                nativeTimestampSupport.DeviceGeneration,
            nativeTimestampSessionCreateFrequencyReady =
                nativeTimestampSupport.FrequencyReady,
            nativeTimestampSessionCreateFrequency =
                nativeTimestampSupport.TimestampFrequency,
            maxComputeBufferInputsCompute = SystemInfo.maxComputeBufferInputsCompute,
            maxGraphicsBufferSize = SystemInfo.maxGraphicsBufferSize
        };
        File.WriteAllText(
            Path.Combine(reportDirectory, "device.json"),
            JsonUtility.ToJson(device, true),
            new UTF8Encoding(false));
    }

    private void WriteRawSamples()
    {
        using (StreamWriter writer = CreateWriter("raw-frames.csv"))
        {
            writer.WriteLine(
                "processId,round,blockIndex,orderPosition,order,caseId,operation,variant," +
                "marker,sampleIndex,sourceUnityFrame,gpuRegionResultUnityFrame," +
                "gpuFrameDiagnosticReadUnityFrame,elapsedSeconds,enqueueCpuMs,enqueueGcBytes," +
                "nativeTimestampToken,nativeTimestampUserTag,nativeTimestampFlags," +
                "nativeTimestampStatus,nativeTimestampBeginTicks,nativeTimestampEndTicks," +
                "nativeTimestampElapsedTicks,nativeTimestampFrequency," +
                "nativeTimestampElapsedNanoseconds,nativeTimestampElapsedMs," +
                "nativeTimestampFenceValue,nativeTimestampDeviceGeneration,frameMs," +
                "mainThreadMs,renderThreadMs,gpuRegionElapsedMs,gpuRegionSampleBlocks," +
                "gpuRegionTimingSource,gpuRegionTimingValid,gpuFrameMs," +
                "gpuFrameTimingSource,gpuFrameTimingValid,measurementReadbackBytes," +
                "timestampInstrumentationReadbackBytes");
            for (int i = 0; i < rawSampleCount; i++)
            {
                RawSample row = rawSamples[i];
                writer.WriteLine(string.Join(",",
                    row.ProcessId.ToString(CultureInfo.InvariantCulture),
                    row.Round.ToString(CultureInfo.InvariantCulture),
                    row.BlockIndex.ToString(CultureInfo.InvariantCulture),
                    row.OrderPosition.ToString(CultureInfo.InvariantCulture),
                    Csv(row.Order),
                    Csv(row.CaseId),
                    Csv(row.Operation),
                    Csv(row.Variant),
                    Csv(row.Marker),
                    row.SampleIndex.ToString(CultureInfo.InvariantCulture),
                    row.SourceUnityFrame.ToString(CultureInfo.InvariantCulture),
                    row.GpuRegionResultUnityFrame.ToString(
                        CultureInfo.InvariantCulture),
                    row.GpuFrameDiagnosticReadUnityFrame.ToString(
                        CultureInfo.InvariantCulture),
                    Number(row.ElapsedSeconds),
                    Number(row.EnqueueCpuMs),
                    row.EnqueueGcBytes.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampToken.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampUserTag.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampFlags.ToString(CultureInfo.InvariantCulture),
                    Csv(row.NativeTimestampStatus),
                    row.NativeTimestampBeginTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampEndTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampElapsedTicks.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampFrequency.ToString(CultureInfo.InvariantCulture),
                    row.NativeTimestampElapsedNanoseconds.ToString(
                        CultureInfo.InvariantCulture),
                    Number(row.GpuRegionElapsedMs),
                    row.NativeTimestampFenceValue.ToString(
                        CultureInfo.InvariantCulture),
                    row.NativeTimestampDeviceGeneration.ToString(
                        CultureInfo.InvariantCulture),
                    Number(row.FrameMs),
                    Number(row.MainThreadMs),
                    Number(row.RenderThreadMs),
                    Number(row.GpuRegionElapsedMs),
                    row.GpuRegionSampleBlocks.ToString(CultureInfo.InvariantCulture),
                    Csv(row.GpuRegionTimingSource),
                    row.GpuRegionTimingValid ? "1" : "0",
                    Number(row.GpuFrameMs),
                    Csv(row.GpuTimingSource),
                    row.GpuTimingValid ? "1" : "0",
                    row.MeasurementReadbackBytes.ToString(CultureInfo.InvariantCulture),
                    row.TimestampInstrumentationReadbackBytes.ToString(
                        CultureInfo.InvariantCulture)));
            }
        }
    }

    private void WriteBlockSummaries()
    {
        using (StreamWriter writer = CreateWriter("block-summary.csv"))
        {
            writer.WriteLine(
                "processId,round,blockIndex,orderPosition,order,caseId,operation,variant," +
                "marker,samples,gpuRegionValidSamples,gpuFrameValidSamples," +
                "dispatchesPerFrame,logicalBytesPerDispatch," +
                "blockEnqueueElapsedMs,fenceSupported,fencePassed,fenceLatencyMs," +
                "frameAverageMs,frameP50Ms,frameP95Ms,frameP99Ms," +
                "gpuRegionAverageMs,gpuRegionP50Ms,gpuRegionP95Ms,gpuRegionP99Ms," +
                "gpuFrameAverageMs,gpuFrameP50Ms,gpuFrameP95Ms,gpuFrameP99Ms," +
                "enqueueAverageMs,enqueueP50Ms,enqueueP95Ms,enqueueP99Ms," +
                "mainAverageMs,mainP99Ms,renderAverageMs,renderP99Ms," +
                "measurementReadbackBytes,timestampInstrumentationReadbackBytes");
            for (int i = 0; i < blockSummaryCount; i++)
            {
                BlockSummary row = blockSummaries[i];
                writer.WriteLine(string.Join(",",
                    row.ProcessId.ToString(CultureInfo.InvariantCulture),
                    row.Round.ToString(CultureInfo.InvariantCulture),
                    row.BlockIndex.ToString(CultureInfo.InvariantCulture),
                    row.OrderPosition.ToString(CultureInfo.InvariantCulture),
                    Csv(row.Order),
                    Csv(row.CaseId),
                    Csv(row.Operation),
                    Csv(row.Variant),
                    Csv(row.Marker),
                    row.Samples.ToString(CultureInfo.InvariantCulture),
                    row.GpuRegionValidSamples.ToString(CultureInfo.InvariantCulture),
                    row.GpuValidSamples.ToString(CultureInfo.InvariantCulture),
                    row.DispatchesPerFrame.ToString(CultureInfo.InvariantCulture),
                    row.LogicalBytesPerDispatch.ToString(CultureInfo.InvariantCulture),
                    Number(row.BlockEnqueueElapsedMs),
                    row.FenceSupported ? "1" : "0",
                    row.FencePassed ? "1" : "0",
                    Number(row.FenceLatencyMs),
                    Number(row.Frame.Average),
                    Number(row.Frame.P50),
                    Number(row.Frame.P95),
                    Number(row.Frame.P99),
                    Number(row.GpuRegion.Average),
                    Number(row.GpuRegion.P50),
                    Number(row.GpuRegion.P95),
                    Number(row.GpuRegion.P99),
                    Number(row.Gpu.Average),
                    Number(row.Gpu.P50),
                    Number(row.Gpu.P95),
                    Number(row.Gpu.P99),
                    Number(row.Enqueue.Average),
                    Number(row.Enqueue.P50),
                    Number(row.Enqueue.P95),
                    Number(row.Enqueue.P99),
                    Number(row.Main.Average),
                    Number(row.Main.P99),
                    Number(row.Render.Average),
                    Number(row.Render.P99),
                    row.MeasurementReadbackBytes.ToString(CultureInfo.InvariantCulture),
                    row.TimestampInstrumentationReadbackBytes.ToString(
                        CultureInfo.InvariantCulture)));
            }
        }
    }

    private void WriteValidationResults()
    {
        using (StreamWriter writer = CreateWriter("validation.csv"))
        {
            writer.WriteLine(
                "phase,caseId,operation,variant,passed,message,readbackBytes,resultHash");
            for (int i = 0; i < validationResults.Count; i++)
            {
                GpuPrimitiveValidationResult row = validationResults[i];
                writer.WriteLine(string.Join(",",
                    Csv(row.Phase),
                    Csv(row.CaseId),
                    Csv(row.Operation),
                    Csv(row.Variant),
                    row.Passed ? "1" : "0",
                    Csv(row.Message),
                    row.ReadbackBytes.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ResultHash)));
            }
        }
    }

    private void WriteRunSummary(bool passed, string status)
    {
        long validationReadbackBytes = 0L;
        int validationFailures = 0;
        for (int i = 0; i < validationResults.Count; i++)
        {
            validationReadbackBytes += validationResults[i].ReadbackBytes;
            if (!validationResults[i].Passed)
            {
                validationFailures++;
            }
        }
        int invalidGpuRegionSamples = 0;
        int invalidGpuFrameSamples = 0;
        int nativeTimestampReadySamples = 0;
        int nativeTimestampObservedFrequencySamples = 0;
        int nativeTimestampObservedFrequencyMismatches = 0;
        int nativeTimestampObservedZeroFrequencies = 0;
        ulong nativeTimestampObservedFrequency = 0;
        long measurementReadbackBytes = 0L;
        long timestampInstrumentationReadbackBytes = 0L;
        for (int i = 0; i < rawSampleCount; i++)
        {
            RawSample sample = rawSamples[i];
            if (!sample.GpuRegionTimingValid)
            {
                invalidGpuRegionSamples++;
            }
            if (!sample.GpuTimingValid)
            {
                invalidGpuFrameSamples++;
            }
            if (sample.NativeTimestampStatus == "ready")
            {
                nativeTimestampReadySamples++;
                nativeTimestampObservedFrequencySamples++;
                if (sample.NativeTimestampFrequency == 0)
                {
                    nativeTimestampObservedZeroFrequencies++;
                }
                else if (nativeTimestampObservedFrequency == 0)
                {
                    nativeTimestampObservedFrequency =
                        sample.NativeTimestampFrequency;
                }
                else if (sample.NativeTimestampFrequency !=
                    nativeTimestampObservedFrequency)
                {
                    nativeTimestampObservedFrequencyMismatches++;
                }
            }
            measurementReadbackBytes += sample.MeasurementReadbackBytes;
            timestampInstrumentationReadbackBytes +=
                sample.TimestampInstrumentationReadbackBytes;
        }

        int nativeActiveSamples =
            nativeTimestampBackend != null
                ? nativeTimestampBackend.ActiveSampleCount
                : 0;
        int nativeReservedSamples =
            nativeTimestampBackend != null
                ? nativeTimestampBackend.ReservedSampleCount
                : 0;
        int nativeSubmittedSamples =
            nativeTimestampBackend != null
                ? nativeTimestampBackend.SubmittedSampleCount
                : 0;
        bool nativeTerminal =
            nativeTimestampBackend != null && nativeTimestampBackend.IsTerminal;
        string nativeTerminalStatus =
            nativeTerminal
                ? TimestampStatusName(nativeTimestampBackend.TerminalStatus)
                : "none";
        bool nativeTimestampObservedFrequencyConsistent =
            nativeTimestampObservedFrequencySamples > 0 &&
            nativeTimestampObservedFrequencySamples ==
                nativeTimestampReadySamples &&
            nativeTimestampObservedZeroFrequencies == 0 &&
            nativeTimestampObservedFrequencyMismatches == 0;
        bool gpuRegionTimingComplete =
            nativeTimestampWarmupPassed &&
            rawSampleCount > 0 &&
            nativeTimestampObservedFrequencyConsistent &&
            invalidGpuRegionSamples == 0 &&
            nativeTimestampReadySamples == rawSampleCount &&
            pendingNativeTimestamps.Count == 0 &&
            nativeActiveSamples == 0 &&
            nativeReservedSamples == 0 &&
            nativeSubmittedSamples == 0 &&
            !nativeTerminal &&
            nativeTimestampAcquireFailures == 0 &&
            nativeTimestampResultFailures == 0 &&
            nativeTimestampTimeouts == 0;
        bool performanceMetricsUsable =
            gpuRegionTimingComplete &&
            validationFailures == 0 &&
            measurementReadbackBytes == 0;

        string[] lines =
        {
            "GPU primitive same-process benchmark",
            "status=" + status,
            "passed=" + (passed ? "1" : "0"),
            "processId=" + processId.ToString(CultureInfo.InvariantCulture),
            "rounds=" + rounds.ToString(CultureInfo.InvariantCulture),
            "caseCount=" + allCases.Count.ToString(CultureInfo.InvariantCulture),
            "blockCount=" + blockSummaryCount.ToString(CultureInfo.InvariantCulture),
            "rawSampleCount=" + rawSampleCount.ToString(CultureInfo.InvariantCulture),
            "invalidGpuRegionTimingSamples=" +
                invalidGpuRegionSamples.ToString(CultureInfo.InvariantCulture),
            "invalidGpuFrameTimingSamples=" +
                invalidGpuFrameSamples.ToString(CultureInfo.InvariantCulture),
            "gpuRegionTimingComplete=" +
                (gpuRegionTimingComplete ? "1" : "0"),
            "gpuFrameTimingComplete=" + (invalidGpuFrameSamples == 0 ? "1" : "0"),
            "performanceMetricsUsable=" +
                (performanceMetricsUsable ? "1" : "0"),
            "requireCompleteGpuTimings=" +
                (requireCompleteGpuTimings ? "1" : "0"),
            "nativeTimestampBackendRequested=1",
            "nativeTimestampBackendAvailable=" +
                (nativeTimestampSupport.IsAvailable ? "1" : "0"),
            "nativeTimestampAvailability=" +
                nativeTimestampSupport.Availability,
            "nativeTimestampMessage=" +
                (nativeTimestampSupport.Message ?? nativeTimestampInitializationError),
            "nativeTimestampAbiVersion=" +
                nativeTimestampSupport.AbiVersion.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampCapabilityFlags=" +
                nativeTimestampSupport.CapabilityFlags.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampRingCapacity=" +
                nativeTimestampSupport.RingCapacity.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampDeviceGeneration=" +
                nativeTimestampSupport.DeviceGeneration.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampSessionCreateFrequencyReady=" +
                (nativeTimestampSupport.FrequencyReady ? "1" : "0"),
            "nativeTimestampSessionCreateFrequency=" +
                nativeTimestampSupport.TimestampFrequency.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampWarmupPassed=" +
                (nativeTimestampWarmupPassed ? "1" : "0"),
            "nativeTimestampWarmupStatus=" + nativeTimestampWarmupStatus,
            "nativeTimestampWarmupElapsedMs=" +
                Number(nativeTimestampWarmupElapsedMs),
            "nativeTimestampWarmupFrequency=" +
                nativeTimestampWarmupFrequency.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampWarmupFenceValue=" +
                nativeTimestampWarmupFenceValue.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampWarmupDeviceGeneration=" +
                nativeTimestampWarmupDeviceGeneration.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampWarmupResultUnityFrame=" +
                nativeTimestampWarmupResultUnityFrame.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampWarmupInstrumentationReadbackBytes=" +
                nativeTimestampWarmupInstrumentationReadbackBytes.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampObservedFrequency=" +
                nativeTimestampObservedFrequency.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampObservedFrequencySamples=" +
                nativeTimestampObservedFrequencySamples.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampObservedFrequencyMismatches=" +
                nativeTimestampObservedFrequencyMismatches.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampObservedZeroFrequencies=" +
                nativeTimestampObservedZeroFrequencies.ToString(
                    CultureInfo.InvariantCulture),
            "nativeTimestampObservedFrequencyConsistent=" +
                (nativeTimestampObservedFrequencyConsistent ? "1" : "0"),
            "nativeTimestampReadySamples=" +
                nativeTimestampReadySamples.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampAcquireFailures=" +
                nativeTimestampAcquireFailures.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampResultFailures=" +
                nativeTimestampResultFailures.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampTimeouts=" +
                nativeTimestampTimeouts.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampActiveSamples=" +
                nativeActiveSamples.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampReservedSamples=" +
                nativeReservedSamples.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampSubmittedSamples=" +
                nativeSubmittedSamples.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampPendingRows=" +
                pendingNativeTimestamps.Count.ToString(CultureInfo.InvariantCulture),
            "nativeTimestampTerminal=" + (nativeTerminal ? "1" : "0"),
            "nativeTimestampTerminalStatus=" + nativeTerminalStatus,
            "frameTimingFeatureEnabled=" +
                (FrameTimingManager.IsFeatureEnabled() ? "1" : "0"),
            "gpuTimerFrequency=" +
                FrameTimingManager.GetGpuTimerFrequency().ToString(CultureInfo.InvariantCulture),
            "validationRows=" + validationResults.Count.ToString(CultureInfo.InvariantCulture),
            "validationFailures=" + validationFailures.ToString(CultureInfo.InvariantCulture),
            "validationReadbackBytes=" +
                validationReadbackBytes.ToString(CultureInfo.InvariantCulture),
            "measurementReadbackBytes=" +
                measurementReadbackBytes.ToString(CultureInfo.InvariantCulture),
            "timestampInstrumentationReadbackBytes=" +
                timestampInstrumentationReadbackBytes.ToString(
                    CultureInfo.InvariantCulture),
            "timestampInstrumentationBytesPerReadySample=16",
            "sameProcessCounterbalanced=1",
            "supportsWaveOperations=" + (adapter.SupportsWaveOperations ? "1" : "0"),
            "adapter=" + adapter.ImplementationName,
            "elapsedSeconds=" +
                Number(Time.realtimeSinceStartupAsDouble - benchmarkStart)
        };
        File.WriteAllLines(
            Path.Combine(reportDirectory, "run-summary.txt"),
            lines,
            new UTF8Encoding(false));
    }

    private StreamWriter CreateWriter(string name)
    {
        return new StreamWriter(
            Path.Combine(reportDirectory, name),
            false,
            new UTF8Encoding(false));
    }

    private void StartRecorders()
    {
        gpuFrameRecorder = StartRecorder(ProfilerCategory.Render, "GPU Frame Time");
        mainThreadRecorder = StartRecorder(ProfilerCategory.Internal, "Main Thread");
        renderThreadRecorder = StartRecorder(ProfilerCategory.Internal, "Render Thread");
    }

    private void StopRecorders()
    {
        DisposeRecorder(ref gpuFrameRecorder);
        DisposeRecorder(ref mainThreadRecorder);
        DisposeRecorder(ref renderThreadRecorder);
    }

    private void Finish(bool passed, string message)
    {
        if (finished)
        {
            return;
        }
        finished = true;
        if (passed)
        {
            Debug.Log(message, this);
        }
        else
        {
            Debug.LogError(message, this);
        }
        StartCoroutine(QuitAfterFrame(passed ? 0 : 2));
    }

    private IEnumerator QuitAfterFrame(int exitCode)
    {
        yield return null;
        DisposeResources();
        Application.Quit(exitCode);
    }

    private void DisposeResources()
    {
        StopRecorders();
        DisposeNativeTimestampMeasurementCommands();
        for (int i = 0; i < measurementCommands.Count; i++)
        {
            measurementCommands[i]?.Dispose();
        }
        measurementCommands.Clear();
        if (timingCamera != null)
        {
            timingCamera.targetTexture = null;
        }
        if (timingTarget != null)
        {
            timingTarget.Release();
            Destroy(timingTarget);
            timingTarget = null;
        }
        adapter?.Dispose();
        adapter = null;
        nativeTimestampBackend?.Dispose();
        nativeTimestampBackend = null;
        nativeTimestampOperational = false;
        pendingNativeTimestamps.Clear();
    }

    private void DisposeNativeTimestampMeasurementCommands()
    {
        if (nativeTimestampMeasurementCommands != null)
        {
            int scopeCount =
                nativeTimestampMeasurementCommands.GetLength(0);
            int caseCount =
                nativeTimestampMeasurementCommands.GetLength(1);
            for (int scopeIndex = 0; scopeIndex < scopeCount; scopeIndex++)
            {
                for (int caseIndex = 0; caseIndex < caseCount; caseIndex++)
                {
                    nativeTimestampMeasurementCommands[
                        scopeIndex,
                        caseIndex]?.Dispose();
                }
            }
        }

        nativeTimestampMeasurementCommands = null;
        nativeTimestampPreparedScopeCount = 0;
    }

    private static ProfilerRecorder StartRecorder(
        ProfilerCategory category,
        string statName)
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

    private static float ReadRecorderMilliseconds(ProfilerRecorder recorder)
    {
        return recorder.Valid ? (float)(recorder.LastValue * 1.0e-6) : 0.0f;
    }

    private static void DisposeRecorder(ref ProfilerRecorder recorder)
    {
        if (recorder.Valid)
        {
            recorder.Dispose();
        }
        recorder = default;
    }

    private static double TicksToMilliseconds(double ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static string Number(double value)
    {
        return value.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static string Csv(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }
        return "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private static string SanitizeCommandLine(string[] arguments)
    {
        if (arguments == null)
        {
            return string.Empty;
        }
        string[] sanitized = new string[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            sanitized[i] = arguments[i]
                .Replace("\\", "/")
                .Replace("\"", "'");
        }
        return string.Join(" ", sanitized);
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
        string value = ReadString(args, name, null);
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed)
            ? parsed
            : fallback;
    }

    private static float ReadFloat(string[] args, string name, float fallback)
    {
        string value = ReadString(args, name, null);
        return float.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float parsed)
            ? parsed
            : fallback;
    }

    private sealed class BenchmarkCaseView
    {
        public string Id;
        public string Operation;
        public string Variant;
        public string Marker;
        public long LogicalBytesPerDispatch;
    }

    private struct FenceResult
    {
        public bool Supported;
        public bool Passed;
        public double LatencyMs;
    }

    private struct PendingNativeTimestamp
    {
        public GpuTimestampToken Token;
        public int SourceRowIndex;
        public GpuTimestampSampleFlags ExpectedFlags;
        public bool TimedOut;
    }

    private struct RawSample
    {
        public int ProcessId;
        public int Round;
        public int BlockIndex;
        public int OrderPosition;
        public string Order;
        public string CaseId;
        public string Operation;
        public string Variant;
        public string Marker;
        public int SampleIndex;
        public int SourceUnityFrame;
        public int GpuRegionResultUnityFrame;
        public int GpuFrameDiagnosticReadUnityFrame;
        public double ElapsedSeconds;
        public double EnqueueCpuMs;
        public long EnqueueGcBytes;
        public ulong NativeTimestampToken;
        public ulong NativeTimestampUserTag;
        public uint NativeTimestampFlags;
        public string NativeTimestampStatus;
        public ulong NativeTimestampBeginTicks;
        public ulong NativeTimestampEndTicks;
        public ulong NativeTimestampElapsedTicks;
        public ulong NativeTimestampFrequency;
        public long NativeTimestampElapsedNanoseconds;
        public ulong NativeTimestampFenceValue;
        public uint NativeTimestampDeviceGeneration;
        public double FrameMs;
        public double MainThreadMs;
        public double RenderThreadMs;
        public double GpuRegionElapsedMs;
        public int GpuRegionSampleBlocks;
        public string GpuRegionTimingSource;
        public bool GpuRegionTimingValid;
        public double GpuFrameMs;
        public string GpuTimingSource;
        public bool GpuTimingValid;
        public long MeasurementReadbackBytes;
        public long TimestampInstrumentationReadbackBytes;
    }

    private struct MetricStats
    {
        public int Count;
        public double Average;
        public double P50;
        public double P95;
        public double P99;
        public double Minimum;
        public double Maximum;
    }

    private struct BlockSummary
    {
        public int ProcessId;
        public int Round;
        public int BlockIndex;
        public int OrderPosition;
        public string Order;
        public string CaseId;
        public string Operation;
        public string Variant;
        public string Marker;
        public int Samples;
        public int GpuRegionValidSamples;
        public int GpuValidSamples;
        public int DispatchesPerFrame;
        public long LogicalBytesPerDispatch;
        public double BlockEnqueueElapsedMs;
        public bool FenceSupported;
        public bool FencePassed;
        public double FenceLatencyMs;
        public MetricStats GpuRegion;
        public MetricStats Frame;
        public MetricStats Gpu;
        public MetricStats Enqueue;
        public MetricStats Main;
        public MetricStats Render;
        public long MeasurementReadbackBytes;
        public long TimestampInstrumentationReadbackBytes;
    }

    [Serializable]
    private sealed class BenchmarkConfiguration
    {
        public int schemaVersion;
        public int processId;
        public string unityVersion;
        public string startedUtc;
        public int rounds;
        public int localWarmupFrames;
        public int sampleFrames;
        public int cooldownFrames;
        public int elementCount;
        public int seed;
        public int dispatchesPerFrame;
        public float validationTimeoutSeconds;
        public int validationConsumeDelayFrames;
        public string requestedOperations;
        public string requestedBackends;
        public string distribution;
        public int keyBitCount;
        public string[] selectedCases;
        public string[] gpuMarkers;
        public string adapter;
        public long externalBenchmarkBufferBytes;
        public long primitiveScratchBytes;
        public long totalResidentBytes;
        public bool supportsWaveOperations;
        public bool supportsGpuRecorder;
        public bool sameProcessCounterbalanced;
        public bool requireCompleteGpuTimings;
        public bool nativeTimestampBackendRequested;
        public string nativeTimestampBackendSelected;
        public string nativeTimestampAvailability;
        public string nativeTimestampMessage;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
        public string nativeTimestampCapabilityContract;
        public int nativeTimestampRingCapacity;
        public int nativeTimestampRendererType;
        public bool nativeTimestampWarmupPassed;
        public string nativeTimestampWarmupStatus;
        public double nativeTimestampWarmupElapsedMs;
        public ulong nativeTimestampWarmupFrequency;
        public ulong nativeTimestampWarmupFenceValue;
        public int nativeTimestampWarmupResultUnityFrame;
        public uint nativeTimestampWarmupDeviceGeneration;
        public int nativeTimestampWarmupInstrumentationReadbackBytes;
        public uint nativeTimestampDeviceGeneration;
        public bool nativeTimestampSessionCreateFrequencyReady;
        public ulong nativeTimestampSessionCreateFrequency;
        public string gpuTimingScope;
        public int measurementReadbackBytesPerFrame;
        public int timestampInstrumentationReadbackBytesPerCompletedSample;
        public string buildCommit;
        public string portableShaderSha256;
        public string waveShaderSha256;
        public string runtimeApiSha256;
        public string commandLine;
    }

    [Serializable]
    private sealed class DeviceMetadata
    {
        public int processId;
        public string operatingSystem;
        public string processorType;
        public int processorCount;
        public int systemMemoryMiB;
        public string graphicsDeviceName;
        public string graphicsDeviceVendor;
        public int graphicsDeviceId;
        public int graphicsDeviceVendorId;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public int graphicsMemoryMiB;
        public int graphicsShaderLevel;
        public bool supportsComputeShaders;
        public bool supportsAsyncCompute;
        public bool supportsGraphicsFence;
        public bool supportsAsyncGpuReadback;
        public bool supportsWaveOperations;
        public bool supportsGpuRecorder;
        public bool frameTimingFeatureEnabled;
        public ulong gpuTimerFrequency;
        public string nativeTimestampAvailability;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
        public int nativeTimestampRingCapacity;
        public int nativeTimestampRendererType;
        public uint nativeTimestampDeviceGeneration;
        public bool nativeTimestampSessionCreateFrequencyReady;
        public ulong nativeTimestampSessionCreateFrequency;
        public int maxComputeBufferInputsCompute;
        public long maxGraphicsBufferSize;
    }
}
