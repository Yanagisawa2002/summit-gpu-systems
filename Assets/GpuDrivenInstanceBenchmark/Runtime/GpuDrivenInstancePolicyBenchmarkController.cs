using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Summit.GpuAutotuning;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using Summit.GpuTimestamps;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

[DefaultExecutionOrder(10000)]
public sealed class GpuDrivenInstancePolicyBenchmarkController : MonoBehaviour
{
    private const string EnableArgument =
        "-gpu-driven-instance-policy-benchmark";
    private const string SuiteId =
        "summit.gpu-driven-instance-policy";
    private const string ScheduleContract = "ABBA;BAAB";
    private const int FrameTimingResultLatencyFrames = 4;
    private const int GpuFrameBlockMinimumCoveragePercent = 95;
    private const int GpuFramePairedMinimumCoveragePercent = 90;
    private const int MeasurementBlockCount = 8;
    private const int ValidationCount = 4;
    private const int MaximumTimestampScopes = 64;
    private const int SelectorOverheadIterations = 100000;
    private const float DefaultTimeoutSeconds = 120f;
    private const uint UnmeasuredOrdinalNamespace = 0x80000000u;
    private const uint UnmeasuredOrdinalSequenceMask = 0x7FFFFFFFu;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static readonly WaitForEndOfFrame EndOfFrame =
        new WaitForEndOfFrame();
    private static readonly CultureInfo Invariant =
        CultureInfo.InvariantCulture;

    private GpuDrivenInstancePolicyBenchmarkAdapter adapter;
    private GpuDrivenInstancePolicySelector selector;
    private GpuDrivenInstancePolicyProfile profile;
    private GpuDrivenInstancePolicyEnvironment environment;
    private GpuDrivenInstancePolicyValidationError profileValidationError;
    private bool profileAccepted;
    private string profileSha256 = "unavailable";

    private GpuDrivenInstanceFrameTimingCollector frameTimingCollector;
    private GpuDrivenInstanceNativeTimestampBackend timestampBackend;
    private GpuTimestampSupport timestampSupport;
    private PendingTimestamp[] pendingTimestamps;
    private int pendingTimestampCount;
    private int preparedTimestampScopes;
    private bool timestampOperational;
    private bool timestampWarmupPassed;
    private string timestampWarmupStatus = "not-run";
    private double timestampWarmupElapsedMs;
    private ulong timestampWarmupFrequency;
    private ulong timestampWarmupFence;
    private uint timestampWarmupGeneration;
    private int timestampAcquireFailures;
    private int timestampResultFailures;
    private int timestampTimeouts;

    private BlockPlan[] blockPlans;
    private RawSample[] rawSamples;
    private BlockSummary[] blockSummaries;
    private GpuDrivenInstancePolicyValidationResult[] validationResults;
    private EnginePresentationValidationReceipt[]
        enginePresentationValidationResults;
    private double[] metricScratch;
    private long[] selectorOverheadTicks;
    private int rawSampleCount;
    private int blockSummaryCount;
    private int validationResultCount;
    private int enginePresentationValidationResultCount;
    private SelectorOverheadSummary selectorOverhead;
    private bool allCompletionFencesPassed = true;
    private int decisionIssueCount;
    private int frameAlignmentIssueCount;
    private int pairedInputIssueCount;
    private int firstMeasuredResidentIssueCount;
    private int validationLifecycleDrainIssueCount;
    private int validationTimeoutCount;
    private int validationDrainTimeoutCount;
    private int presentationValidationTimeoutCount;
    private bool validationLifecycleDrainIncomplete;
    private string validationLifecycleDrainStatus = "not-required";
    private int processId;
    private double benchmarkStart;
    private string benchmarkStartedUtc;
    private uint nextUnmeasuredOrdinalSequence;
    private ulong lastUnmeasuredExpectedStateHash;
    private string deterministicRenderTargetHash = string.Empty;
    private GraphicsBuffer validationPipelineIndirectArguments;
    private GraphicsBuffer validationEngineIndirectArguments;
    private bool finished;

    private string reportDirectory = string.Empty;
    private string scenarioId = string.Empty;
    private int instanceCount = 1 << 20;
    private int viewCount = 4;
    private string visibility = "visible25";
    private int visibilityBasisPoints = 2500;
    private int dirtyBasisPoints = 1000;
    private int seed = 20260830;
    private int warmupFrames = 60;
    private int sampleFrames = 240;
    private float timeoutSeconds = DefaultTimeoutSeconds;
    private string leftCaseText = "forced-selected";
    private string rightCaseText = "actual-auto";
    private string requiredOutputText = "visible-only";
    private GpuDrivenInstanceOutputMode requiredOutputMode =
        GpuDrivenInstanceOutputMode.VisibleOnly;
    private string profilePath = string.Empty;
    private string pipelineContractFingerprint = string.Empty;
    private string shaderContractFingerprint = string.Empty;
    private string calibrationProtocol =
        "gpu-driven-policy-calibration-v1-holdout-v1";
    private string measurementContractFingerprint = string.Empty;
    private string buildCommit = "unknown";
    private string configurationParseError = string.Empty;
    private string[] originalArguments;
    private GpuDrivenInstancePolicyBenchmarkCase leftCase;
    private GpuDrivenInstancePolicyBenchmarkCase rightCase;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ConfigureBenchmarkProcessLogging()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!HasArgument(args, EnableArgument))
        {
            return;
        }
        Debug.unityLogger.filterLogType = LogType.Warning;
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!HasArgument(args, EnableArgument))
        {
            return;
        }
        GameObject host = new GameObject(
            "GPU Driven Instance Policy Benchmark Controller");
        DontDestroyOnLoad(host);
        GpuDrivenInstancePolicyBenchmarkController controller =
            host.AddComponent<GpuDrivenInstancePolicyBenchmarkController>();
        controller.Configure(args);
    }

    private void Start()
    {
        StartCoroutine(RunGuarded());
    }

    private void OnDisable()
    {
        DisposeResources();
    }

    private void Configure(string[] args)
    {
        originalArguments = args;
        reportDirectory = ReadString(
            args,
            "-gpu-driven-instance-policy-output",
            ReadString(
                args,
                "-gpu-driven-instance-policy-report-dir",
                reportDirectory));
        scenarioId = ReadString(
            args,
            "-gpu-driven-instance-policy-scenario-id",
            scenarioId);
        instanceCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-policy-instance-count",
                instanceCount),
            1,
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount);
        viewCount = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-policy-view-count",
                viewCount),
            1,
            GpuDrivenInstancePipeline.MaximumViewCount);
        string requestedVisibility = ReadString(
            args,
            "-gpu-driven-instance-policy-visibility",
            visibility);
        int defaultVisibilityBasisPoints;
        try
        {
            defaultVisibilityBasisPoints =
                VisibilityBasisPoints(requestedVisibility);
        }
        catch (ArgumentException)
        {
            defaultVisibilityBasisPoints = visibilityBasisPoints;
            configurationParseError = "unsupported-visibility";
        }
        int requestedVisibilityBasisPoints = ReadInt(
            args,
            "-gpu-driven-instance-policy-visibility-bps",
            defaultVisibilityBasisPoints);
        try
        {
            visibility = VisibilityId(requestedVisibilityBasisPoints);
            visibilityBasisPoints = requestedVisibilityBasisPoints;
        }
        catch (ArgumentException)
        {
            configurationParseError =
                "unsupported-visibility-basis-points";
        }
        dirtyBasisPoints = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-policy-dirty-bps",
                dirtyBasisPoints),
            0,
            GpuDrivenInstancePolicyContract.BasisPointScale);
        seed = ReadInt(args, "-gpu-driven-instance-policy-seed", seed);
        warmupFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-policy-warmup",
                ReadInt(
                    args,
                    "-gpu-driven-instance-policy-warmup-frames",
                    warmupFrames)),
            0,
            1800);
        sampleFrames = Mathf.Clamp(
            ReadInt(
                args,
                "-gpu-driven-instance-policy-sample",
                ReadInt(
                    args,
                    "-gpu-driven-instance-policy-sample-frames",
                    sampleFrames)),
            FrameTimingResultLatencyFrames,
            7200);
        timeoutSeconds = Mathf.Clamp(
            ReadFloat(
                args,
                "-gpu-driven-instance-policy-timeout-seconds",
                timeoutSeconds),
            5f,
            600f);
        leftCaseText = ReadString(
            args,
            "-gpu-driven-instance-policy-left-case",
            leftCaseText);
        rightCaseText = ReadString(
            args,
            "-gpu-driven-instance-policy-right-case",
            rightCaseText);
        requiredOutputText = ReadString(
            args,
            "-gpu-driven-instance-policy-required-output",
            requiredOutputText);
        try
        {
            requiredOutputMode = ParseRequiredOutput(requiredOutputText);
        }
        catch (ArgumentException)
        {
            configurationParseError = "unsupported-required-output";
        }
        profilePath = ReadString(
            args,
            "-gpu-driven-instance-policy-profile",
            ReadString(
                args,
                "-gpu-driven-instance-policy-profile-path",
                profilePath));
        pipelineContractFingerprint = ReadString(
            args,
            "-gpu-driven-instance-policy-pipeline-fingerprint",
            ReadString(
                args,
                "-gpu-driven-instance-policy-pipeline-contract-fingerprint",
                pipelineContractFingerprint));
        shaderContractFingerprint = ReadString(
            args,
            "-gpu-driven-instance-policy-shader-fingerprint",
            ReadString(
                args,
                "-gpu-driven-instance-policy-shader-contract-fingerprint",
                shaderContractFingerprint));
        calibrationProtocol = ReadString(
            args,
            "-gpu-driven-instance-policy-calibration-protocol",
            calibrationProtocol);
        measurementContractFingerprint = ReadString(
            args,
            "-gpu-driven-instance-policy-measurement-contract-fingerprint",
            measurementContractFingerprint);
        buildCommit = ReadString(
            args,
            "-gpu-driven-instance-policy-build-commit",
            buildCommit);
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            scenarioId = "policy-n" + I(instanceCount) + "-v" +
                I(viewCount) + "-visible" + I(visibilityBasisPoints) +
                "-dirty" + I(dirtyBasisPoints) + "-seed" + I(seed);
        }
    }

    private IEnumerator RunGuarded()
    {
        Stack<IEnumerator> routines = new Stack<IEnumerator>();
        routines.Push(Run());
        while (routines.Count != 0)
        {
            IEnumerator current = routines.Peek();
            bool moved = false;
            object yielded = null;
            Exception failure = null;
            try
            {
                moved = current.MoveNext();
                if (moved)
                {
                    yielded = current.Current;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            if (failure != null)
            {
                while (routines.Count != 0)
                {
                    (routines.Pop() as IDisposable)?.Dispose();
                }
                HandleUnhandledException(failure);
                yield break;
            }
            if (!moved)
            {
                (routines.Pop() as IDisposable)?.Dispose();
                continue;
            }
            if (yielded is IEnumerator nested)
            {
                routines.Push(nested);
                continue;
            }
            yield return yielded;
        }
    }

    private void HandleUnhandledException(Exception exception)
    {
        Debug.LogException(exception);
        try
        {
            if (!string.IsNullOrWhiteSpace(reportDirectory))
            {
                reportDirectory = Path.GetFullPath(reportDirectory);
                Directory.CreateDirectory(reportDirectory);
                WriteAvailableEvidence();
                WriteRunSummary(false, "unhandled-exception");
            }
        }
        catch (Exception evidenceException)
        {
            Debug.LogException(evidenceException);
        }
        Finish(false, "unhandled-exception");
    }

    private IEnumerator Run()
    {
        Application.runInBackground = true;
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        processId = Process.GetCurrentProcess().Id;
        benchmarkStart = Time.realtimeSinceStartupAsDouble;
        benchmarkStartedUtc = DateTime.UtcNow.ToString("O", Invariant);

        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            Finish(false, "missing-output-directory");
            yield break;
        }
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);
        if (!ValidateStaticConfiguration(out string configurationError))
        {
            WriteRunSummary(false, configurationError);
            Finish(false, configurationError);
            yield break;
        }

        try
        {
            LoadSelectorOnce();
            adapter = new GpuDrivenInstancePolicyBenchmarkAdapter(
                instanceCount,
                viewCount,
                visibility,
                seed,
                dirtyBasisPoints,
                requiredOutputMode,
                selector);
            ResolvePresentationValidationResources();
            GpuDrivenInstancePolicyDecision selectedDecision =
                SelectConvergedDecision();
            leftCase = ParseCase(leftCaseText, in selectedDecision);
            rightCase = ParseCase(rightCaseText, in selectedDecision);
            if (CaseEquals(leftCase, rightCase))
            {
                throw new InvalidOperationException(
                    "Left and right policy benchmark cases must differ.");
            }
            ValidateCaseCompatibility(leftCase);
            ValidateCaseCompatibility(rightCase);
            frameTimingCollector =
                new GpuDrivenInstanceFrameTimingCollector();
            blockPlans = BuildBlockPlans(leftCase, rightCase);
            rawSamples = new RawSample[checked(
                MeasurementBlockCount * sampleFrames)];
            blockSummaries = new BlockSummary[MeasurementBlockCount];
            validationResults =
                new GpuDrivenInstancePolicyValidationResult[ValidationCount];
            enginePresentationValidationResults =
                new EnginePresentationValidationReceipt[ValidationCount];
            metricScratch = new double[sampleFrames];
            selectorOverheadTicks =
                new long[SelectorOverheadIterations];
            InitializeTimestampBackend();
            MeasureSelectorOverhead();
            WriteConfiguration();
            WriteDeviceMetadata();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            WriteAvailableEvidence();
            WriteRunSummary(false, "initialization-failed");
            Finish(false, "initialization-failed");
            yield break;
        }

        yield return WarmupTimestampBackend();
        if (!timestampWarmupPassed)
        {
            WriteAvailableEvidence();
            WriteRunSummary(false, "native-timestamp-warmup-failed");
            Finish(false, "native-timestamp-warmup-failed");
            yield break;
        }

        bool warmupValid = true;
        yield return ValidateCase(
            leftCase,
            "warmup-left",
            passed => warmupValid &= passed);
        if (!warmupValid)
        {
            WriteAvailableEvidence();
            WriteRunSummary(false, "warmup-left-validation-failed");
            Finish(false, "warmup-left-validation-failed");
            yield break;
        }
        yield return ValidateCase(
            rightCase,
            "warmup-right",
            passed => warmupValid &= passed);
        if (!warmupValid)
        {
            WriteAvailableEvidence();
            WriteRunSummary(false, "warmup-validation-failed");
            Finish(false, "warmup-validation-failed");
            yield break;
        }

        for (int index = 0; index < blockPlans.Length; index++)
        {
            yield return RunBlock(blockPlans[index]);
        }
        yield return DrainAllTimestamps();
        pairedInputIssueCount = CountPairedInputIssues();

        bool finalValid = true;
        yield return ValidateCase(
            leftCase,
            "final-left",
            passed => finalValid &= passed);
        if (!finalValid)
        {
            WriteAvailableEvidence();
            WriteRunSummary(false, "final-left-validation-failed");
            Finish(false, "final-left-validation-failed");
            yield break;
        }
        yield return ValidateCase(
            rightCase,
            "final-right",
            passed => finalValid &= passed);

        bool passedBenchmark = finalValid && EvidenceGatePassed();
        string status = passedBenchmark
            ? "passed"
            : "evidence-gate-failed";
        WriteAvailableEvidence();
        WriteRunSummary(passedBenchmark, status);
        Finish(passedBenchmark, status);
    }

    private bool ValidateStaticConfiguration(out string error)
    {
        if (!string.IsNullOrWhiteSpace(configurationParseError))
        {
            error = configurationParseError;
            return false;
        }
        if ((long)instanceCount * viewCount >
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            error = "instance-view-capacity-exceeded";
            return false;
        }
        if (!SystemInfo.supportsComputeShaders)
        {
            error = "compute-shaders-unavailable";
            return false;
        }
        if (!SystemInfo.supportsGraphicsFence)
        {
            error = "graphics-fence-unavailable";
            return false;
        }
        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            error = "async-readback-unavailable";
            return false;
        }
        if (!FrameTimingManager.IsFeatureEnabled())
        {
            error = "frame-timing-feature-unavailable";
            return false;
        }
        if (string.IsNullOrWhiteSpace(pipelineContractFingerprint) ||
            string.IsNullOrWhiteSpace(shaderContractFingerprint) ||
            string.IsNullOrWhiteSpace(calibrationProtocol) ||
            !IsSha256(measurementContractFingerprint))
        {
            error = "missing-contract-fingerprint";
            return false;
        }
        if (!IsHex(buildCommit, 40))
        {
            error = "invalid-build-commit";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private void LoadSelectorOnce()
    {
        environment = GpuDrivenInstancePolicyEnvironment.Capture(
            pipelineContractFingerprint,
            shaderContractFingerprint,
            calibrationProtocol,
            measurementContractFingerprint);
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            profilePath = Path.GetFullPath(profilePath);
            if (File.Exists(profilePath))
            {
                profileSha256 = ComputeSha256(profilePath);
            }
        }
        profileAccepted = GpuDrivenInstancePolicyProfileStore.TryLoad(
            profilePath,
            in environment,
            out profile,
            out selector,
            out profileValidationError);
        if (selector == null)
        {
            throw new InvalidOperationException(
                "Policy profile loading did not return a fail-safe selector.");
        }
        if ((CaseRequiresAcceptedProfile(leftCaseText) ||
             CaseRequiresAcceptedProfile(rightCaseText)) &&
            !profileAccepted)
        {
            throw new InvalidOperationException(
                "Actual-auto and forced-selected cases require an exact, " +
                "holdout-accepted profile. Validation error: " +
                profileValidationError + ".");
        }
    }

    private void ResolvePresentationValidationResources()
    {
        if (!AdapterPresentationResourceContractIsAvailable())
        {
            throw new MissingFieldException(
                "The policy adapter presentation-validation resource " +
                "contract is unavailable.");
        }
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.NonPublic;
        Type adapterType =
            typeof(GpuDrivenInstancePolicyBenchmarkAdapter);
        validationPipelineIndirectArguments =
            adapterType.GetField("indirectArgumentWords", flags)
                ?.GetValue(adapter) as GraphicsBuffer;
        validationEngineIndirectArguments =
            adapterType.GetField("renderIndirectArguments", flags)
                ?.GetValue(adapter) as GraphicsBuffer;
        if (validationPipelineIndirectArguments == null ||
            validationEngineIndirectArguments == null ||
            adapter.RenderTarget == null ||
            !adapter.RenderTarget.IsCreated())
        {
            throw new InvalidOperationException(
                "Policy presentation-validation GPU resources are not " +
                "available after adapter construction.");
        }
        long pipelineBytes = checked(
            (long)validationPipelineIndirectArguments.count *
            validationPipelineIndirectArguments.stride);
        long engineBytes = checked(
            (long)validationEngineIndirectArguments.count *
            validationEngineIndirectArguments.stride);
        if (pipelineBytes <= 0L || pipelineBytes != engineBytes)
        {
            throw new InvalidOperationException(
                "Pipeline and engine indirect-argument resources do not " +
                "have an identical non-empty byte extent.");
        }
    }

    internal static bool AdapterPresentationResourceContractIsAvailable()
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.NonPublic;
        Type adapterType =
            typeof(GpuDrivenInstancePolicyBenchmarkAdapter);
        FieldInfo pipelineField = adapterType.GetField(
            "indirectArgumentWords",
            flags);
        FieldInfo engineField = adapterType.GetField(
            "renderIndirectArguments",
            flags);
        return pipelineField != null &&
            pipelineField.FieldType == typeof(GraphicsBuffer) &&
            engineField != null &&
            engineField.FieldType == typeof(GraphicsBuffer);
    }

    private GpuDrivenInstancePolicyDecision SelectConvergedDecision()
    {
        GpuDrivenInstancePolicyObservation observation =
            BuildSyntheticSelectorObservation();
        GpuDrivenInstancePolicyState state = default;
        GpuDrivenInstancePolicyDecision decision = default;
        for (int frame = 0;
             frame < GpuDrivenInstancePolicyContract.MaximumConsecutiveFrames;
             frame++)
        {
            decision = selector.Select(in observation, ref state);
        }
        return decision;
    }

    private GpuDrivenInstancePolicyObservation
        BuildSyntheticSelectorObservation()
    {
        return adapter.CreateSelectorOverheadObservation();
    }

    private void MeasureSelectorOverhead()
    {
        GpuDrivenInstancePolicyObservation observation =
            BuildSyntheticSelectorObservation();
        GpuDrivenInstancePolicyState state = default;
        GpuDrivenInstancePolicyDecision expected = default;
        for (int index = 0;
             index < GpuDrivenInstancePolicyContract.MaximumConsecutiveFrames;
             index++)
        {
            expected = selector.Select(in observation, ref state);
        }

        int unstable = 0;
        ulong decisionChecksum = 14695981039346656037UL;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long totalStart = Stopwatch.GetTimestamp();
        for (int index = 0; index < selectorOverheadTicks.Length; index++)
        {
            long start = Stopwatch.GetTimestamp();
            GpuDrivenInstancePolicyDecision decision =
                selector.Select(in observation, ref state);
            long elapsed = checked(Stopwatch.GetTimestamp() - start);
            selectorOverheadTicks[index] = elapsed;
            if (!PolicyDecisionEquals(in decision, in expected))
            {
                unstable++;
            }
            decisionChecksum ^= unchecked((uint)decision.ProfileRuleIndex);
            decisionChecksum *= 1099511628211UL;
            decisionChecksum ^= unchecked((uint)decision.Flags);
            decisionChecksum *= 1099511628211UL;
        }
        long totalTicks = checked(Stopwatch.GetTimestamp() - totalStart);
        long allocatedBytes = Math.Max(
            0L,
            GC.GetAllocatedBytesForCurrentThread() - allocationStart);

        Array.Sort(selectorOverheadTicks);
        long sum = 0L;
        for (int index = 0; index < selectorOverheadTicks.Length; index++)
        {
            sum = checked(sum + selectorOverheadTicks[index]);
        }
        selectorOverhead = new SelectorOverheadSummary
        {
            Iterations = selectorOverheadTicks.Length,
            WarmupCalls =
                GpuDrivenInstancePolicyContract.MaximumConsecutiveFrames,
            TotalTicks = totalTicks,
            StopwatchFrequency = Stopwatch.Frequency,
            MeanNanoseconds =
                TicksToNanoseconds((double)sum /
                    selectorOverheadTicks.Length),
            P50Nanoseconds = TicksToNanoseconds(
                Percentile(selectorOverheadTicks, 0.50)),
            P95Nanoseconds = TicksToNanoseconds(
                Percentile(selectorOverheadTicks, 0.95)),
            P99Nanoseconds = TicksToNanoseconds(
                Percentile(selectorOverheadTicks, 0.99)),
            AllocatedBytes = allocatedBytes,
            UnstableDecisionCount = unstable,
            DecisionChecksum = decisionChecksum,
            Decision = expected,
        };
    }

    private IEnumerator RunBlock(BlockPlan plan)
    {
        GpuDrivenInstancePolicyRecordReceipt? warmedReceipt = null;
        yield return ResetAndWarmCase(
            plan.BenchmarkCase,
            value => warmedReceipt = value);
        if (!warmedReceipt.HasValue)
        {
            throw new InvalidOperationException(
                "Case-local warmup did not produce a decision receipt.");
        }
        GpuDrivenInstancePolicyResolvedDecision expectedDecision =
            warmedReceipt.Value.Decision;
        ulong residentStateBeforeMeasured =
            lastUnmeasuredExpectedStateHash;

        frameTimingCollector.Reset();
        frameTimingCollector.CollectLatest();
        int sampleStart = rawSampleCount;
        for (int sampleIndex = 1;
             sampleIndex <= sampleFrames;
             sampleIndex++)
        {
            yield return null;
            int slotIndex = -1;
            int slotWaitFrames = 0;
            double acquireDeadline =
                Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            while (!adapter.TryAcquireWorkSlot(
                out slotIndex,
                out slotWaitFrames))
            {
                if (DeadlineHasExpired(
                    Time.realtimeSinceStartupAsDouble,
                    acquireDeadline))
                {
                    throw new TimeoutException(
                        "Timed out acquiring a measured policy work slot.");
                }
                yield return null;
            }
            int sourceFrame = Time.frameCount;
            uint logicalOrdinal = MeasuredLogicalOrdinal(
                plan.PairIndex,
                sampleIndex,
                sampleFrames);
            int rowIndex = rawSampleCount;
            RawSample row = SubmitMeasured(
                plan,
                sampleIndex,
                logicalOrdinal,
                sourceFrame,
                slotIndex,
                slotWaitFrames,
                rowIndex,
                in expectedDecision);
            rawSamples[rowIndex] = row;
            rawSampleCount++;
            if (sampleIndex == 1 &&
                row.ChangedInstanceCount > 0 &&
                row.ExpectedStateHash == residentStateBeforeMeasured)
            {
                firstMeasuredResidentIssueCount++;
            }

            frameTimingCollector.CaptureFrameTimings();
            yield return EndOfFrame;
            GpuDrivenInstanceFrameTimingSample timing =
                frameTimingCollector.CollectLatest();
            int readyOffset =
                sampleIndex - 1 - FrameTimingResultLatencyFrames;
            if (readyOffset >= 0)
            {
                ApplyFrameTiming(
                    sampleStart + readyOffset,
                    timing,
                    Time.frameCount);
            }
            PollTimestampResults();
        }

        for (int drain = 0;
             drain < FrameTimingResultLatencyFrames;
             drain++)
        {
            yield return null;
            GpuDrivenInstancePolicyRecordReceipt? drainReceipt = null;
            int drainWaitFrames = 0;
            yield return SubmitUnmeasured(
                plan.BenchmarkCase,
                value => drainReceipt = value,
                value => drainWaitFrames = value);
            GpuDrivenInstancePolicyResolvedDecision drainDecision =
                drainReceipt.HasValue
                    ? drainReceipt.Value.Decision
                    : default;
            if (!drainReceipt.HasValue ||
                !ResolvedDecisionEquals(
                    in drainDecision,
                    in expectedDecision))
            {
                decisionIssueCount++;
            }
            if (drainWaitFrames != 0)
            {
                frameAlignmentIssueCount++;
            }
            frameTimingCollector.CaptureFrameTimings();
            yield return EndOfFrame;
            GpuDrivenInstanceFrameTimingSample timing =
                frameTimingCollector.CollectLatest();
            ApplyFrameTiming(
                sampleStart + sampleFrames -
                    FrameTimingResultLatencyFrames + drain,
                timing,
                Time.frameCount);
            PollTimestampResults();
        }

        yield return DrainTimestamps(sampleStart, sampleFrames);
        bool fencesPassed = false;
        yield return WaitForAllCompletionFences(
            value => fencesPassed = value);
        allCompletionFencesPassed &= fencesPassed;
        blockSummaries[blockSummaryCount++] = SummarizeBlock(
            plan,
            sampleStart,
            sampleFrames,
            residentStateBeforeMeasured,
            fencesPassed,
            in expectedDecision);
    }

    private IEnumerator ResetAndWarmCase(
        GpuDrivenInstancePolicyBenchmarkCase benchmarkCase,
        Action<GpuDrivenInstancePolicyRecordReceipt> completion)
    {
        bool drained = false;
        yield return WaitForAllCompletionFences(value => drained = value);
        if (!drained)
        {
            throw new TimeoutException(
                "Timed out before the block-local state reset.");
        }
        adapter.ResetSelectorState();

        GpuDrivenInstancePolicyRecordReceipt? resetReceipt = null;
        yield return SubmitUnmeasured(
            GpuDrivenInstancePolicyBenchmarkCase.SafeBaseline(),
            value => resetReceipt = value,
            null);
        yield return WaitForAllCompletionFences(value => drained = value);
        if (!drained || !resetReceipt.HasValue)
        {
            throw new TimeoutException(
                "The block-local safe state reset did not complete.");
        }

        int convergenceFrames = Math.Max(
            warmupFrames,
            GpuDrivenInstancePolicyContract.MaximumConsecutiveFrames);
        GpuDrivenInstancePolicyRecordReceipt? last = null;
        for (int frame = 0; frame < convergenceFrames; frame++)
        {
            yield return null;
            yield return SubmitUnmeasured(
                benchmarkCase,
                value => last = value,
                null);
        }
        drained = false;
        yield return WaitForAllCompletionFences(value => drained = value);
        if (!drained || !last.HasValue)
        {
            throw new TimeoutException(
                "The block-local selector convergence did not complete.");
        }
        completion(last.Value);
    }

    private RawSample SubmitMeasured(
        BlockPlan plan,
        int sampleIndex,
        uint logicalOrdinal,
        int sourceFrame,
        int slotIndex,
        int slotWaitFrames,
        int rowIndex,
        in GpuDrivenInstancePolicyResolvedDecision expectedDecision)
    {
        bool adapterSubmitted = false;
        bool timestampSubmitted = false;
        GpuTimestampStatus timestampStatus =
            GpuTimestampStatus.Unsupported;
        GpuTimestampToken timestampToken = default;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        long totalStart = Stopwatch.GetTimestamp();
        try
        {
            long stateStart = Stopwatch.GetTimestamp();
            GpuDrivenInstancePolicyPreparationReceipt preparation =
                adapter.PrepareSlot(
                    slotIndex,
                    plan.BenchmarkCase,
                    logicalOrdinal);
            long stateEnd = Stopwatch.GetTimestamp();

            CommandBuffer commands = adapter.Commands(slotIndex);
            ulong userTag = checked((ulong)rowIndex + 1UL);
            if (timestampOperational && timestampBackend != null)
            {
                timestampStatus = timestampBackend.Acquire(
                    userTag,
                    GpuTimestampSampleFlags.None,
                    sourceFrame,
                    out timestampToken);
                if (timestampStatus == GpuTimestampStatus.Ready &&
                    timestampToken.ScopeIndex >= 0 &&
                    timestampToken.ScopeIndex < preparedTimestampScopes &&
                    !pendingTimestamps[timestampToken.ScopeIndex].InUse)
                {
                    timestampStatus =
                        timestampBackend.MarkSubmitted(timestampToken);
                    if (timestampStatus == GpuTimestampStatus.Ready)
                    {
                        timestampBackend.RecordBegin(
                            timestampToken.ScopeIndex,
                            commands);
                        timestampSubmitted = true;
                    }
                    else
                    {
                        timestampResultFailures++;
                        timestampBackend.Cancel(timestampToken);
                    }
                }
                else
                {
                    if (timestampStatus == GpuTimestampStatus.Ready)
                    {
                        timestampBackend.Cancel(timestampToken);
                        timestampStatus = GpuTimestampStatus.RingFull;
                    }
                    timestampAcquireFailures++;
                }
            }

            GpuDrivenInstancePolicyRecordReceipt recorded =
                adapter.RecordWorkload(slotIndex);
            if (timestampSubmitted)
            {
                timestampBackend.RecordEnd(
                    timestampToken.ScopeIndex,
                    commands);
            }
            long fenceStart = Stopwatch.GetTimestamp();
            adapter.AppendLifetimeFence(slotIndex);
            long fenceEnd = Stopwatch.GetTimestamp();
            long enqueueStart = Stopwatch.GetTimestamp();
            Graphics.ExecuteCommandBuffer(commands);
            adapter.MarkWorkSlotSubmitted(slotIndex);
            adapterSubmitted = true;
            long enqueueEnd = Stopwatch.GetTimestamp();
            long allocationEnd = GC.GetAllocatedBytesForCurrentThread();

            GpuDrivenInstancePolicyResolvedDecision recordedDecision =
                recorded.Decision;
            bool expected = ResolvedDecisionEquals(
                in recordedDecision,
                in expectedDecision) &&
                recorded.SelectorInvoked ==
                    plan.BenchmarkCase.InvokesSelector;
            if (!expected)
            {
                decisionIssueCount++;
            }

            RawSample row = new RawSample
            {
                SourceRowIndex = rowIndex,
                ProcessId = processId,
                ScenarioId = scenarioId,
                BlockIndex = plan.BlockIndex,
                SuperRound = plan.SuperRound,
                SequencePosition = plan.SequencePosition,
                PairIndex = plan.PairIndex,
                PairOrder = plan.PairOrder,
                WithinPairPosition = plan.WithinPairPosition,
                Side = plan.Side,
                BenchmarkCase = plan.BenchmarkCase,
                SampleIndex = sampleIndex,
                LogicalOrdinal = logicalOrdinal,
                SourceUnityFrame = sourceFrame,
                ElapsedSeconds =
                    Time.realtimeSinceStartupAsDouble - benchmarkStart,
                SlotIndex = slotIndex,
                SlotWaitFrames = slotWaitFrames,
                StateCpuMs = Milliseconds(stateEnd - stateStart),
                PlanCpuMs = Milliseconds(recorded.PlanCpuTicks),
                SelectorCpuMs = Milliseconds(recorded.SelectorCpuTicks),
                RecordCpuMs = Milliseconds(recorded.RecordCpuTicks),
                FenceCpuMs = Milliseconds(fenceEnd - fenceStart),
                EnqueueCpuMs = Milliseconds(enqueueEnd - enqueueStart),
                TotalCpuMs = Milliseconds(enqueueEnd - totalStart),
                SelectorCpuTicks = recorded.SelectorCpuTicks,
                SelectorInvoked = recorded.SelectorInvoked,
                MainThreadAllocatedBytes =
                    Math.Max(0L, allocationEnd - allocationStart),
                StateRecordsWritten = preparation.StateRecordsWritten,
                ChangedInstanceCount =
                    preparation.UpdatePlan.ChangedInstanceCount,
                DirtyRangeCount = preparation.UpdatePlan.RangeCount,
                RangePlanHash = preparation.UpdatePlan.RangePlanHash,
                UpdateHash = preparation.UpdateHash,
                StateRevision = preparation.StateRevision,
                ExpectedStateHash = preparation.ExpectedStateHash,
                Decision = recorded.Decision,
                DecisionStableExpected = expected,
                PlannedUpload = recorded.PlannedUpload,
                RecordedUpload = recorded.RecordedUpload,
                RenderApiCallCount = recorded.RenderApiCallCount,
                LogicalDrawCommandCount =
                    recorded.LogicalDrawCommandCount,
                CompletionFenceAppended = true,
                NativeTimestampToken = timestampToken.Value,
                NativeTimestampUserTag = userTag,
                NativeTimestampFlags =
                    (uint)GpuTimestampSampleFlags.None,
                NativeTimestampStatus = timestampSubmitted
                    ? "pending"
                    : TimestampStatusName(timestampStatus),
                FrameTimingStatus =
                    GpuDrivenInstanceFrameTimingStatus.NoTimingAvailable,
                SubmissionWindowStatus =
                    GpuDrivenInstanceSubmissionWindowStatus
                        .FrameTimingUnavailable,
                CpuFrameMs = double.NaN,
                CpuMainThreadFrameMs = double.NaN,
                CpuRenderThreadFrameMs = double.NaN,
                GpuFrameMs = double.NaN,
                CpuSubmissionWindowMs = double.NaN,
                GpuRegionElapsedMs = double.NaN,
                MeasurementReadbackBytes = 0L,
                TimestampInstrumentationReadbackBytes = 0L,
            };
            if (timestampSubmitted)
            {
                int scope = timestampToken.ScopeIndex;
                pendingTimestamps[scope] = new PendingTimestamp
                {
                    InUse = true,
                    Token = timestampToken,
                    RowIndex = rowIndex,
                    ExpectedFlags = GpuTimestampSampleFlags.None,
                };
                pendingTimestampCount++;
            }
            return row;
        }
        catch
        {
            if (!adapterSubmitted)
            {
                adapter.AbandonUnsubmittedWorkSlot(slotIndex);
            }
            throw;
        }
    }

    private IEnumerator SubmitUnmeasured(
        GpuDrivenInstancePolicyBenchmarkCase benchmarkCase,
        Action<GpuDrivenInstancePolicyRecordReceipt> receiptCompletion,
        Action<int> waitFramesCompletion)
    {
        int slotIndex = -1;
        int ignoredWaitFrames = 0;
        double acquireDeadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (!adapter.TryAcquireWorkSlot(
            out slotIndex,
            out ignoredWaitFrames))
        {
            if (DeadlineHasExpired(
                Time.realtimeSinceStartupAsDouble,
                acquireDeadline))
            {
                throw new TimeoutException(
                    "Timed out acquiring an unmeasured policy work slot.");
            }
            yield return null;
        }
        bool submitted = false;
        try
        {
            GpuDrivenInstancePolicyPreparationReceipt preparation =
                adapter.PrepareSlot(
                slotIndex,
                benchmarkCase,
                NextUnmeasuredLogicalOrdinal());
            GpuDrivenInstancePolicyRecordReceipt receipt =
                adapter.RecordWorkload(slotIndex);
            adapter.AppendLifetimeFence(slotIndex);
            Graphics.ExecuteCommandBuffer(adapter.Commands(slotIndex));
            adapter.MarkWorkSlotSubmitted(slotIndex);
            submitted = true;
            lastUnmeasuredExpectedStateHash =
                preparation.ExpectedStateHash;
            receiptCompletion?.Invoke(receipt);
            waitFramesCompletion?.Invoke(ignoredWaitFrames);
        }
        finally
        {
            if (!submitted)
            {
                adapter.AbandonUnsubmittedWorkSlot(slotIndex);
            }
        }
    }

    private IEnumerator ValidateCase(
        GpuDrivenInstancePolicyBenchmarkCase benchmarkCase,
        string phase,
        Action<bool> completion)
    {
        GpuDrivenInstancePolicyRecordReceipt? receipt = null;
        yield return ResetAndWarmCase(
            benchmarkCase,
            value => receipt = value);
        if (!receipt.HasValue)
        {
            throw new InvalidOperationException(
                "Validation warmup did not produce a receipt.");
        }

        adapter.BeginValidation(receipt.Value.SlotIndex, phase);
        double deadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            if (adapter.TryCompleteValidation(
                out GpuDrivenInstancePolicyValidationResult result))
            {
                if (validationResultCount >= validationResults.Length)
                {
                    throw new InvalidOperationException(
                        "The validation-result pool is exhausted.");
                }
                EnginePresentationValidationReceipt presentation = default;
                yield return ValidateEnginePresentation(
                    value => presentation = value);
                result.Passed &= presentation.Passed;
                result.ReadbackBytes = checked(
                    result.ReadbackBytes + presentation.ReadbackBytes);
                result.Message += " " + presentation.Message;
                validationResults[validationResultCount++] = result;
                enginePresentationValidationResults[
                    enginePresentationValidationResultCount++] =
                        presentation;
                completion(result.Passed);
                yield break;
            }
            yield return null;
        }

        validationLifecycleDrainIssueCount++;
        validationTimeoutCount++;
        validationLifecycleDrainStatus = "draining-after-timeout";
        GpuDrivenInstancePolicyValidationResult drainedResult = null;
        double drainDeadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (!DeadlineHasExpired(
            Time.realtimeSinceStartupAsDouble,
            drainDeadline))
        {
            if (adapter.TryCompleteValidation(out drainedResult))
            {
                break;
            }
            yield return null;
        }
        if (drainedResult == null)
        {
            validationLifecycleDrainIncomplete = true;
            validationDrainTimeoutCount++;
            validationLifecycleDrainStatus = "incomplete";
            drainedResult = new GpuDrivenInstancePolicyValidationResult
            {
                Phase = phase,
                CaseId = benchmarkCase.CaseId,
                ExpectedOutputHash = adapter.ExpectedOutputHash,
                ActualOutputHash = "unavailable",
                ReadbackBytes = 0L,
            };
            drainedResult.Message =
                "Validation timed out and its sequential readbacks did " +
                "not drain within the bounded cleanup window. No later " +
                "workload or adapter disposal is permitted.";
        }
        else
        {
            validationLifecycleDrainStatus = "drained-after-timeout";
            drainedResult.Message =
                "Validation exceeded its evidence deadline; all " +
                "sequential readbacks were drained before fail-closed " +
                "shutdown. " + drainedResult.Message;
        }
        drainedResult.Passed = false;
        validationResults[validationResultCount++] = drainedResult;
        enginePresentationValidationResults[
            enginePresentationValidationResultCount++] =
                EnginePresentationValidationReceipt.Failed(
                    "Presentation validation was not started after the " +
                    "pipeline validation deadline.");
        completion(false);
    }

    private IEnumerator ValidateEnginePresentation(
        Action<EnginePresentationValidationReceipt> completion)
    {
        var receipt = new EnginePresentationValidationReceipt
        {
            RenderTargetFormat = TextureFormat.RGBA32.ToString(),
            RenderTargetWidth = adapter.RenderTarget.width,
            RenderTargetHeight = adapter.RenderTarget.height,
            EngineIndirectMismatchCount = -1,
            EngineIndirectExpectedHash = "unavailable",
            EngineIndirectActualHash = "unavailable",
            RenderTargetHash = "unavailable",
            RenderTargetBlackReferenceHash = "unavailable",
        };
        AsyncGPUReadbackRequest pipelineRequest =
            AsyncGPUReadback.Request(
                validationPipelineIndirectArguments);
        AsyncGPUReadbackRequest engineRequest =
            AsyncGPUReadback.Request(
                validationEngineIndirectArguments);
        AsyncGPUReadbackRequest renderTargetRequest =
            AsyncGPUReadback.Request(
                adapter.RenderTarget,
                0,
                TextureFormat.RGBA32);
        double deadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while ((!pipelineRequest.done ||
                !engineRequest.done ||
                !renderTargetRequest.done) &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return null;
        }
        if (!pipelineRequest.done ||
            !engineRequest.done ||
            !renderTargetRequest.done)
        {
            presentationValidationTimeoutCount++;
            validationLifecycleDrainIssueCount++;
            validationLifecycleDrainIncomplete = true;
            validationLifecycleDrainStatus =
                "presentation-readback-incomplete";
            receipt.Message =
                "Engine presentation validation timed out.";
            completion(receipt);
            yield break;
        }
        if (pipelineRequest.hasError ||
            engineRequest.hasError ||
            renderTargetRequest.hasError)
        {
            receipt.Message =
                "Engine presentation validation readback failed: " +
                "pipelineArgs=" + B(!pipelineRequest.hasError) +
                ",engineArgs=" + B(!engineRequest.hasError) +
                ",renderTarget=" + B(!renderTargetRequest.hasError) + ".";
            completion(receipt);
            yield break;
        }

        uint[] pipelineWords = CopyReadback(
            pipelineRequest.GetData<uint>());
        uint[] engineWords = CopyReadback(
            engineRequest.GetData<uint>());
        byte[] renderTargetBytes = CopyReadback(
            renderTargetRequest.GetData<byte>());
        receipt.EngineIndirectWordCount = engineWords.Length;
        receipt.EngineIndirectReadbackBytes = checked(
            (long)(pipelineWords.Length + engineWords.Length) *
            sizeof(uint));
        receipt.EngineIndirectExact = ValidateExactEngineIndirectWords(
            pipelineWords,
            engineWords,
            out ulong expectedArgumentsHash,
            out ulong actualArgumentsHash,
            out int argumentMismatchCount);
        receipt.EngineIndirectExpectedHash = H(expectedArgumentsHash);
        receipt.EngineIndirectActualHash = H(actualArgumentsHash);
        receipt.EngineIndirectMismatchCount = argumentMismatchCount;

        receipt.RenderTargetReadbackBytes = renderTargetBytes.LongLength;
        bool renderTargetNonBlack = ValidateRenderTargetRgba32(
            renderTargetBytes,
            receipt.RenderTargetWidth,
            receipt.RenderTargetHeight,
            out ulong renderTargetHash,
            out ulong blackReferenceHash,
            out int nonBlackPixelCount);
        receipt.RenderTargetHash = H(renderTargetHash);
        receipt.RenderTargetBlackReferenceHash = H(blackReferenceHash);
        receipt.RenderTargetNonBlackPixelCount = nonBlackPixelCount;
        if (renderTargetNonBlack &&
            string.IsNullOrEmpty(deterministicRenderTargetHash))
        {
            deterministicRenderTargetHash = receipt.RenderTargetHash;
        }
        receipt.RenderTargetHashConsistent =
            renderTargetNonBlack &&
            string.Equals(
                deterministicRenderTargetHash,
                receipt.RenderTargetHash,
                StringComparison.Ordinal);
        receipt.ReadbackBytes = checked(
            receipt.EngineIndirectReadbackBytes +
            receipt.RenderTargetReadbackBytes);
        receipt.Passed = receipt.EngineIndirectExact &&
            renderTargetNonBlack &&
            receipt.RenderTargetHashConsistent;
        receipt.Message = receipt.Passed
            ? "Engine indirect arguments exactly match the CPU-oracle-" +
              "validated pipeline words, and the deterministic render " +
              "target is non-black."
            : "Engine presentation validation failed: indirectExact=" +
              B(receipt.EngineIndirectExact) +
              ",renderTargetNonBlack=" + B(renderTargetNonBlack) +
              ",renderTargetHashConsistent=" +
              B(receipt.RenderTargetHashConsistent) + ".";
        completion(receipt);
    }

    private IEnumerator WaitForAllCompletionFences(Action<bool> completion)
    {
        double deadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (!adapter.AllCompletionFencesPassed &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return null;
        }
        completion(adapter.AllCompletionFencesPassed);
    }

    private void InitializeTimestampBackend()
    {
        timestampOperational =
            GpuDrivenInstanceNativeTimestampBackend.TryCreate(
                out timestampBackend,
                out timestampSupport);
        if (timestampOperational)
        {
            timestampBackend.ExecuteFrequencyInitialization();
            preparedTimestampScopes = Math.Min(
                MaximumTimestampScopes,
                timestampBackend.Capacity);
            pendingTimestamps =
                new PendingTimestamp[preparedTimestampScopes];
        }
        else
        {
            pendingTimestamps = Array.Empty<PendingTimestamp>();
        }
    }

    private IEnumerator WarmupTimestampBackend()
    {
        timestampWarmupPassed = false;
        if (!timestampOperational || timestampBackend == null ||
            preparedTimestampScopes == 0)
        {
            timestampWarmupStatus = "backend-unavailable";
            yield break;
        }

        GpuTimestampStatus status = timestampBackend.Acquire(
            ulong.MaxValue,
            GpuTimestampSampleFlags.EmptyScope,
            Time.frameCount,
            out GpuTimestampToken token);
        if (status != GpuTimestampStatus.Ready ||
            token.ScopeIndex < 0 ||
            token.ScopeIndex >= preparedTimestampScopes)
        {
            if (status == GpuTimestampStatus.Ready)
            {
                timestampBackend.Cancel(token);
            }
            timestampWarmupStatus =
                "acquire-" + TimestampStatusName(status);
            timestampOperational = false;
            yield break;
        }

        status = timestampBackend.MarkSubmitted(token);
        if (status != GpuTimestampStatus.Ready)
        {
            timestampBackend.Cancel(token);
            timestampWarmupStatus =
                "mark-submitted-" + TimestampStatusName(status);
            timestampOperational = false;
            yield break;
        }
        using (var commands = new CommandBuffer
        {
            name = "GPU.DrivenPolicy/NativeTimestamp/Warmup"
        })
        {
            timestampBackend.RecordBegin(token.ScopeIndex, commands);
            timestampBackend.RecordEnd(token.ScopeIndex, commands);
            Graphics.ExecuteCommandBuffer(commands);
        }

        double deadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return EndOfFrame;
            status = timestampBackend.TryConsume(
                token,
                Time.frameCount,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                continue;
            }
            if (status == GpuTimestampStatus.Ready)
            {
                timestampWarmupElapsedMs = result.ElapsedMilliseconds;
                timestampWarmupFrequency = result.TimestampFrequency;
                timestampWarmupFence = result.FenceValue;
                timestampWarmupGeneration = result.DeviceGeneration;
                timestampWarmupPassed =
                    result.Token.UserTag == ulong.MaxValue &&
                    result.TimestampFrequency > 0UL &&
                    result.EndTicks >= result.BeginTicks &&
                    result.DeviceGeneration ==
                        timestampSupport.DeviceGeneration &&
                    result.FenceValue > 0UL;
                timestampWarmupStatus = timestampWarmupPassed
                    ? "ready"
                    : "validation-failed";
            }
            else
            {
                timestampWarmupStatus =
                    "result-" + TimestampStatusName(status);
            }
            timestampOperational = timestampWarmupPassed;
            yield break;
        }
        timestampWarmupStatus = "timeout";
        timestampOperational = false;
        timestampTimeouts++;
    }

    private void PollTimestampResults()
    {
        if (timestampBackend == null || pendingTimestamps == null)
        {
            return;
        }
        for (int scope = 0; scope < pendingTimestamps.Length; scope++)
        {
            PendingTimestamp pending = pendingTimestamps[scope];
            if (!pending.InUse)
            {
                continue;
            }
            GpuTimestampStatus status = timestampBackend.TryConsume(
                pending.Token,
                Time.frameCount,
                out GpuTimestampResult result);
            if (status == GpuTimestampStatus.Pending)
            {
                continue;
            }

            RawSample row = rawSamples[pending.RowIndex];
            row.NativeTimestampStatus = TimestampStatusName(status);
            if (status == GpuTimestampStatus.Ready)
            {
                bool valid =
                    result.Token.Value == pending.Token.Value &&
                    result.Token.UserTag == pending.Token.UserTag &&
                    result.SourceFrame == pending.Token.SourceFrame &&
                    result.NativeFlags == (uint)pending.ExpectedFlags &&
                    result.TimestampFrequency > 0UL &&
                    result.EndTicks >= result.BeginTicks &&
                    result.DeviceGeneration ==
                        timestampSupport.DeviceGeneration &&
                    result.FenceValue > 0UL;
                if (!valid)
                {
                    row.NativeTimestampStatus = "malformed-result";
                    timestampResultFailures++;
                    timestampOperational = false;
                }
                else
                {
                    row.TimestampResultUnityFrame = result.ResultFrame;
                    row.NativeTimestampToken = result.Token.Value;
                    row.NativeTimestampUserTag = result.Token.UserTag;
                    row.NativeTimestampFlags = result.NativeFlags;
                    row.NativeTimestampBeginTicks = result.BeginTicks;
                    row.NativeTimestampEndTicks = result.EndTicks;
                    row.NativeTimestampElapsedTicks = result.ElapsedTicks;
                    row.NativeTimestampFrequency =
                        result.TimestampFrequency;
                    row.NativeTimestampElapsedNanoseconds =
                        result.ElapsedNanoseconds;
                    row.NativeTimestampFenceValue = result.FenceValue;
                    row.NativeTimestampDeviceGeneration =
                        result.DeviceGeneration;
                    row.GpuRegionElapsedMs = result.ElapsedMilliseconds;
                    row.TimestampInstrumentationReadbackBytes = 16L;
                }
            }
            else
            {
                timestampResultFailures++;
                timestampOperational = false;
            }
            rawSamples[pending.RowIndex] = row;
            pendingTimestamps[scope] = default;
            pendingTimestampCount--;
        }
    }

    private IEnumerator DrainTimestamps(int sampleStart, int count)
    {
        double deadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (HasPendingRows(sampleStart, count) &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return EndOfFrame;
            PollTimestampResults();
        }
        if (HasPendingRows(sampleStart, count))
        {
            MarkTimestampTimeouts(sampleStart, count);
            timestampOperational = false;
        }
    }

    private IEnumerator DrainAllTimestamps()
    {
        double deadline =
            Time.realtimeSinceStartupAsDouble + timeoutSeconds;
        while (pendingTimestampCount != 0 &&
            Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return EndOfFrame;
            PollTimestampResults();
        }
        if (pendingTimestampCount != 0)
        {
            MarkTimestampTimeouts(0, rawSampleCount);
            timestampOperational = false;
        }
    }

    private bool HasPendingRows(int start, int count)
    {
        int end = checked(start + count);
        for (int scope = 0; scope < pendingTimestamps.Length; scope++)
        {
            PendingTimestamp pending = pendingTimestamps[scope];
            if (pending.InUse &&
                pending.RowIndex >= start &&
                pending.RowIndex < end)
            {
                return true;
            }
        }
        return false;
    }

    private void MarkTimestampTimeouts(int start, int count)
    {
        int end = checked(start + count);
        for (int scope = 0; scope < pendingTimestamps.Length; scope++)
        {
            PendingTimestamp pending = pendingTimestamps[scope];
            if (!pending.InUse ||
                pending.RowIndex < start ||
                pending.RowIndex >= end)
            {
                continue;
            }
            RawSample row = rawSamples[pending.RowIndex];
            row.NativeTimestampStatus = "timeout";
            rawSamples[pending.RowIndex] = row;
            pendingTimestamps[scope] = default;
            pendingTimestampCount--;
            timestampTimeouts++;
        }
    }

    private void ApplyFrameTiming(
        int rowIndex,
        GpuDrivenInstanceFrameTimingSample timing,
        int resultUnityFrame)
    {
        RawSample row = rawSamples[rowIndex];
        row.FrameTimingValid = timing.Valid;
        row.FrameTimingStatus = timing.Status;
        row.CpuRenderThreadFrameValid =
            timing.CpuRenderThreadValid;
        row.GpuFrameValid = timing.GpuFrameValid;
        row.SubmissionWindowValid = timing.SubmissionWindowValid;
        row.SubmissionWindowStatus = timing.SubmissionWindowStatus;
        row.FrameTimingResultUnityFrame = resultUnityFrame;
        row.FrameTimingCaptureLatencyFrames =
            FrameTimingResultLatencyFrames;
        row.FrameStartTimestamp = timing.FrameStartTimestamp;
        row.FirstSubmitTimestamp = timing.FirstSubmitTimestamp;
        row.CpuTimePresentCalled = timing.CpuTimePresentCalled;
        row.CpuTimeFrameComplete = timing.CpuTimeFrameComplete;
        row.CpuFrameMs = timing.CpuFrameMs;
        row.CpuMainThreadFrameMs = timing.CpuMainThreadMs;
        row.CpuRenderThreadFrameMs = timing.CpuRenderThreadMs;
        row.GpuFrameMs = timing.GpuFrameMs;
        row.CpuSubmissionWindowMs = timing.CpuSubmissionMs;
        rawSamples[rowIndex] = row;
    }

    private BlockSummary SummarizeBlock(
        BlockPlan plan,
        int sampleStart,
        int count,
        ulong residentStateBeforeMeasured,
        bool fencePassed,
        in GpuDrivenInstancePolicyResolvedDecision expectedDecision)
    {
        int allocationRows = 0;
        long allocationBytes = 0L;
        int timestampReadyRows = 0;
        int frameTimingReadyRows = 0;
        int submissionWindowReadyRows = 0;
        int stableDecisionRows = 0;
        long slotWaitFrames = 0L;
        long logicalUploadBytes = 0L;
        long uploadCalls = 0L;
        RawSample firstMeasured = rawSamples[sampleStart];
        bool residentDifferenceRequired =
            firstMeasured.ChangedInstanceCount > 0;
        bool firstMeasuredStateDiffersFromResident =
            !residentDifferenceRequired ||
            firstMeasured.ExpectedStateHash !=
                residentStateBeforeMeasured;
        for (int offset = 0; offset < count; offset++)
        {
            RawSample row = rawSamples[sampleStart + offset];
            allocationBytes += row.MainThreadAllocatedBytes;
            if (row.MainThreadAllocatedBytes != 0L)
            {
                allocationRows++;
            }
            if (row.NativeTimestampStatus == "ready")
            {
                timestampReadyRows++;
            }
            if (row.FrameTimingValid)
            {
                frameTimingReadyRows++;
            }
            if (row.SubmissionWindowValid)
            {
                submissionWindowReadyRows++;
            }
            if (row.DecisionStableExpected)
            {
                stableDecisionRows++;
            }
            slotWaitFrames += row.SlotWaitFrames;
            logicalUploadBytes += row.RecordedUpload.LogicalUploadBytes;
            uploadCalls += row.RecordedUpload.UploadCallCount;
        }

        return new BlockSummary
        {
            BlockIndex = plan.BlockIndex,
            SuperRound = plan.SuperRound,
            SequencePosition = plan.SequencePosition,
            PairIndex = plan.PairIndex,
            PairOrder = plan.PairOrder,
            WithinPairPosition = plan.WithinPairPosition,
            Side = plan.Side,
            BenchmarkCase = plan.BenchmarkCase,
            SampleCount = count,
            ExpectedDecision = expectedDecision,
            State = ComputeMetric(sampleStart, count, MetricKind.State),
            Plan = ComputeMetric(sampleStart, count, MetricKind.Plan),
            Selector = ComputeMetric(
                sampleStart,
                count,
                MetricKind.Selector),
            Record = ComputeMetric(sampleStart, count, MetricKind.Record),
            Fence = ComputeMetric(sampleStart, count, MetricKind.Fence),
            Enqueue = ComputeMetric(sampleStart, count, MetricKind.Enqueue),
            Total = ComputeMetric(sampleStart, count, MetricKind.Total),
            NativeGpu = ComputeMetric(
                sampleStart,
                count,
                MetricKind.NativeGpu),
            CpuFrame = ComputeMetric(
                sampleStart,
                count,
                MetricKind.CpuFrame),
            CpuMainThreadFrame = ComputeMetric(
                sampleStart,
                count,
                MetricKind.CpuMainThreadFrame),
            CpuRenderThreadFrame = ComputeMetric(
                sampleStart,
                count,
                MetricKind.CpuRenderThreadFrame),
            GpuFrame = ComputeMetric(
                sampleStart,
                count,
                MetricKind.GpuFrame),
            SubmissionWindow = ComputeMetric(
                sampleStart,
                count,
                MetricKind.SubmissionWindow),
            TimestampReadyRows = timestampReadyRows,
            FrameTimingReadyRows = frameTimingReadyRows,
            SubmissionWindowReadyRows = submissionWindowReadyRows,
            StableDecisionRows = stableDecisionRows,
            MainThreadAllocationRows = allocationRows,
            MainThreadAllocatedBytes = allocationBytes,
            SlotWaitFrames = slotWaitFrames,
            LogicalUploadBytes = logicalUploadBytes,
            UploadCallCount = uploadCalls,
            ResidentStateHashBeforeMeasured =
                residentStateBeforeMeasured,
            FirstMeasuredExpectedStateHash =
                firstMeasured.ExpectedStateHash,
            FirstMeasuredResidentDifferenceRequired =
                residentDifferenceRequired,
            FirstMeasuredStateDiffersFromResident =
                firstMeasuredStateDiffersFromResident,
            CompletionFencesPassed = fencePassed,
        };
    }

    private MetricStats ComputeMetric(
        int sampleStart,
        int count,
        MetricKind kind)
    {
        int validCount = 0;
        double sum = 0.0;
        for (int offset = 0; offset < count; offset++)
        {
            RawSample row = rawSamples[sampleStart + offset];
            double value = MetricValue(in row, kind);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                continue;
            }
            metricScratch[validCount++] = value;
            sum += value;
        }
        if (validCount == 0)
        {
            return MetricStats.Unavailable;
        }
        Array.Sort(metricScratch, 0, validCount);
        return new MetricStats
        {
            ValidCount = validCount,
            Mean = sum / validCount,
            P50 = Percentile(metricScratch, validCount, 0.50),
            P95 = Percentile(metricScratch, validCount, 0.95),
            P99 = Percentile(metricScratch, validCount, 0.99),
        };
    }

    private static double MetricValue(in RawSample row, MetricKind kind)
    {
        switch (kind)
        {
            case MetricKind.State:
                return row.StateCpuMs;
            case MetricKind.Plan:
                return row.PlanCpuMs;
            case MetricKind.Selector:
                return row.SelectorCpuMs;
            case MetricKind.Record:
                return row.RecordCpuMs;
            case MetricKind.Fence:
                return row.FenceCpuMs;
            case MetricKind.Enqueue:
                return row.EnqueueCpuMs;
            case MetricKind.Total:
                return row.TotalCpuMs;
            case MetricKind.NativeGpu:
                return row.GpuRegionElapsedMs;
            case MetricKind.CpuFrame:
                return row.CpuFrameMs;
            case MetricKind.CpuMainThreadFrame:
                return row.CpuMainThreadFrameMs;
            case MetricKind.CpuRenderThreadFrame:
                return row.CpuRenderThreadFrameMs;
            case MetricKind.GpuFrame:
                return row.GpuFrameValid
                    ? row.GpuFrameMs
                    : double.NaN;
            case MetricKind.SubmissionWindow:
                return row.CpuSubmissionWindowMs;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private bool EvidenceGatePassed()
    {
        bool requireAcceptedSelectorEvidence =
            CasesRequireAcceptedSelectorEvidence(
                leftCaseText,
                rightCaseText);
        if (!IsHex(buildCommit, 40) ||
            rawSampleCount != checked(
                MeasurementBlockCount * sampleFrames) ||
            blockSummaryCount != MeasurementBlockCount ||
            validationResultCount != ValidationCount ||
            enginePresentationValidationResultCount != ValidationCount ||
            !allCompletionFencesPassed ||
            decisionIssueCount != 0 ||
            frameAlignmentIssueCount != 0 ||
            pairedInputIssueCount != 0 ||
            firstMeasuredResidentIssueCount != 0 ||
            validationLifecycleDrainIssueCount != 0 ||
            validationTimeoutCount != 0 ||
            validationDrainTimeoutCount != 0 ||
            presentationValidationTimeoutCount != 0 ||
            validationLifecycleDrainIncomplete ||
            selectorOverhead.AllocatedBytes != 0L ||
            selectorOverhead.UnstableDecisionCount != 0 ||
            (requireAcceptedSelectorEvidence &&
             !SelectorOverheadDecisionAccepted(
                 in selectorOverhead.Decision)) ||
            !TimestampEvidenceComplete())
        {
            return false;
        }
        if (requireAcceptedSelectorEvidence &&
            !profileAccepted)
        {
            return false;
        }
        for (int index = 0; index < validationResultCount; index++)
        {
            if (validationResults[index] == null ||
                !validationResults[index].Passed ||
                !enginePresentationValidationResults[index].Passed)
            {
                return false;
            }
        }
        for (int index = 0; index < blockSummaryCount; index++)
        {
            BlockSummary block = blockSummaries[index];
            if (block.SampleCount != sampleFrames ||
                block.State.ValidCount != sampleFrames ||
                block.Plan.ValidCount != sampleFrames ||
                block.Selector.ValidCount != sampleFrames ||
                block.Record.ValidCount != sampleFrames ||
                block.Fence.ValidCount != sampleFrames ||
                block.Enqueue.ValidCount != sampleFrames ||
                block.Total.ValidCount != sampleFrames ||
                block.NativeGpu.ValidCount != sampleFrames ||
                block.CpuFrame.ValidCount != sampleFrames ||
                block.CpuMainThreadFrame.ValidCount != sampleFrames ||
                block.CpuRenderThreadFrame.ValidCount != sampleFrames ||
                !GpuFrameBlockCoveragePasses(
                    block.GpuFrame.ValidCount,
                    sampleFrames) ||
                block.SubmissionWindow.ValidCount != sampleFrames ||
                block.TimestampReadyRows != sampleFrames ||
                block.FrameTimingReadyRows != sampleFrames ||
                block.SubmissionWindowReadyRows != sampleFrames ||
                block.StableDecisionRows != sampleFrames)
            {
                return false;
            }
        }
        for (int index = 0; index < rawSampleCount; index++)
        {
            RawSample row = rawSamples[index];
            if (row.MainThreadAllocatedBytes != 0L ||
                row.SlotWaitFrames != 0 ||
                !row.DecisionStableExpected ||
                !row.CompletionFenceAppended ||
                !row.FrameTimingValid ||
                !row.SubmissionWindowValid ||
                row.GpuFrameValid !=
                    IsPositiveFinite(row.GpuFrameMs) ||
                row.FrameTimingCaptureLatencyFrames !=
                    FrameTimingResultLatencyFrames ||
                (row.BenchmarkCase.InvokesSelector &&
                 !ResolvedDecisionAccepted(in row.Decision)) ||
                row.MeasurementReadbackBytes != 0L)
            {
                return false;
            }
        }
        return true;
    }

    internal static bool GpuFrameBlockCoveragePasses(
        int validCount,
        int sampleCount)
    {
        return sampleCount > 0 &&
            validCount >= 0 &&
            validCount <= sampleCount &&
            (long)validCount * 100L >=
                (long)sampleCount * GpuFrameBlockMinimumCoveragePercent;
    }

    private static bool IsPositiveFinite(double value)
    {
        return value > 0.0 &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value);
    }

    private bool TimestampEvidenceComplete()
    {
        if (timestampBackend == null ||
            !timestampWarmupPassed ||
            !timestampSupport.IsAvailable ||
            timestampSupport.AbiVersion != 2U ||
            (timestampSupport.CapabilityFlags & 0x1FU) != 0x1FU ||
            timestampBackend.IsTerminal ||
            timestampBackend.ActiveSampleCount != 0 ||
            timestampBackend.ReservedSampleCount != 0 ||
            timestampBackend.SubmittedSampleCount != 0 ||
            pendingTimestampCount != 0 ||
            timestampAcquireFailures != 0 ||
            timestampResultFailures != 0 ||
            timestampTimeouts != 0)
        {
            return false;
        }
        for (int index = 0; index < rawSampleCount; index++)
        {
            if (rawSamples[index].NativeTimestampStatus != "ready")
            {
                return false;
            }
        }
        return true;
    }

    private void WriteAvailableEvidence()
    {
        if (adapter != null)
        {
            WriteConfiguration();
            WriteDeviceMetadata();
        }
        if (rawSamples != null)
        {
            WriteRawFrames();
        }
        if (blockSummaries != null)
        {
            WriteBlockSummaries();
        }
        if (validationResults != null)
        {
            WriteValidations();
        }
        if (selectorOverheadTicks != null)
        {
            WriteSelectorOverhead();
        }
    }

    private void WriteConfiguration()
    {
        ConfigurationBlock[] blocks = blockPlans == null
            ? Array.Empty<ConfigurationBlock>()
            : new ConfigurationBlock[blockPlans.Length];
        if (blockPlans != null)
        {
            for (int index = 0; index < blockPlans.Length; index++)
            {
                BlockPlan plan = blockPlans[index];
                blocks[index] = new ConfigurationBlock
                {
                    blockIndex = plan.BlockIndex,
                    superRound = plan.SuperRound,
                    sequencePosition = plan.SequencePosition,
                    pairIndex = plan.PairIndex,
                    pairOrder = plan.PairOrder,
                    withinPairPosition = plan.WithinPairPosition,
                    side = plan.Side,
                    caseId = plan.BenchmarkCase.CaseId,
                    marker = plan.BenchmarkCase.Marker,
                    measuredLogicalOrdinalFirst =
                        MeasuredLogicalOrdinal(
                            plan.PairIndex,
                            1,
                            sampleFrames),
                    measuredLogicalOrdinalLast =
                        MeasuredLogicalOrdinal(
                            plan.PairIndex,
                            sampleFrames,
                            sampleFrames),
                };
            }
        }
        var configuration = new Configuration
        {
            schemaVersion = 1,
            suite = SuiteId,
            processId = processId,
            benchmarkStartedUtc = benchmarkStartedUtc,
            unityVersion = Application.unityVersion,
            scenarioId = scenarioId,
            instanceCount = instanceCount,
            viewCount = viewCount,
            visibility = visibility,
            visibilityBasisPoints = visibilityBasisPoints,
            visibleInstanceCount = adapter.VisibleInstanceCount,
            visiblePairCount = adapter.VisiblePairCount,
            dirtyBasisPoints = dirtyBasisPoints,
            seed = seed,
            requiredOutputMode = requiredOutputMode.ToString(),
            inputLayout = GpuDrivenInstancePolicyInputGenerator.LayoutId,
            clusterCount = adapter.ClusterCount,
            expectedCoarseVisibleClusterViewCount =
                adapter.ExpectedCoarseVisibleClusterViewCount,
            expectedCandidateInstanceViewCount =
                adapter.ExpectedCandidateInstanceViewCount,
            hierarchyCandidateBasisPoints =
                adapter.HierarchyCandidateBasisPoints,
            warmupFramesRequested = warmupFrames,
            convergenceFramesPerBlock = Math.Max(
                warmupFrames,
                GpuDrivenInstancePolicyContract.MaximumConsecutiveFrames),
            sampleFramesPerBlock = sampleFrames,
            measurementBlocks = MeasurementBlockCount,
            scheduleContract = ScheduleContract,
            frameTimingResultLatencyFrames =
                FrameTimingResultLatencyFrames,
            gpuFrameUnavailableLiteral = "unavailable",
            gpuFrameBlockMinimumCoveragePercent =
                GpuFrameBlockMinimumCoveragePercent,
            gpuFramePairedMinimumCoveragePercent =
                GpuFramePairedMinimumCoveragePercent,
            otherTimedMetricCoveragePercent = 100,
            stateResetOutsideMeasuredWindow = true,
            selectorResetOutsideMeasuredWindow = true,
            caseLocalConvergenceOutsideMeasuredWindow = true,
            perFrameExecutionContract =
                "PrepareSlot;PlanDirtyUpload;SelectActualAutoOnly;" +
                "RecordUploadAndCullingAndDraws;AppendFence;Execute",
            measurementReadbackBytesPerFrame = 0L,
            timestampInstrumentationBytesPerCompletedSample = 16,
            rawSamplePoolCapacity = rawSamples?.Length ?? 0,
            timestampResultPoolCapacity =
                pendingTimestamps?.Length ?? 0,
            blockSummaryPoolCapacity = blockSummaries?.Length ?? 0,
            validationResultPoolCapacity =
                validationResults?.Length ?? 0,
            enginePresentationValidationResultPoolCapacity =
                enginePresentationValidationResults?.Length ?? 0,
            measuredLogicalOrdinalContract =
                "pair-local-v1;(pairIndex-1)*samplesPerBlock+sampleIndex",
            unmeasuredLogicalOrdinalContract =
                "independent-high-bit-v1;0x80000000|sequence",
            pairedInputContract =
                "same-pairIndex+sampleIndex=>logicalOrdinal+" +
                "updateHash+expectedStateHash-exact",
            presentationValidationContract =
                "pipeline-indirect-exact=>engine-indirect-exact;" +
                "rgba32-non-black;four-validation-hashes-identical",
            validationTimeoutContract =
                "fail-fast;bounded-sequential-drain;no-later-workload;" +
                "no-dispose-while-readback-in-flight",
            validationLifecycleComplete =
                validationLifecycleDrainIssueCount == 0 &&
                !validationLifecycleDrainIncomplete,
            validationLifecycleDrainStatus =
                this.validationLifecycleDrainStatus,
            validationTimeoutCount = this.validationTimeoutCount,
            validationDrainTimeoutCount =
                this.validationDrainTimeoutCount,
            presentationValidationTimeoutCount =
                this.presentationValidationTimeoutCount,
            selectorOverheadIterations = SelectorOverheadIterations,
            selectorOverheadObservation =
                "adapter-exact-synthetic-facts;live-unconsumed-plan;" +
                "invalidated-by-first-workload;no-gpu-work",
            leftCase = leftCase.CaseId,
            rightCase = rightCase.CaseId,
            profilePath = profilePath,
            profileSha256 = profileSha256,
            profileAccepted = profileAccepted,
            profileValidationError = profileValidationError.ToString(),
            profileRevision = profile?.profileRevision ?? 0,
            profileHoldoutEvidenceSetId =
                profile?.holdoutEvidenceSetId ?? string.Empty,
            deviceFingerprint = environment.Device?.StableKey ?? string.Empty,
            pipelineContractFingerprint = pipelineContractFingerprint,
            shaderContractFingerprint = shaderContractFingerprint,
            calibrationProtocol = calibrationProtocol,
            measurementContractFingerprint =
                measurementContractFingerprint,
            buildCommit = buildCommit,
            nativeTimestampAvailability =
                timestampSupport.Availability.ToString(),
            nativeTimestampAbiVersion = timestampSupport.AbiVersion,
            nativeTimestampCapabilityFlags =
                timestampSupport.CapabilityFlags,
            nativeTimestampRingCapacity = timestampSupport.RingCapacity,
            nativeTimestampWarmupPassed = timestampWarmupPassed,
            nativeTimestampWarmupStatus = timestampWarmupStatus,
            commandLineArguments = originalArguments,
            blocks = blocks,
        };
        WriteText(
            "config.json",
            JsonUtility.ToJson(configuration, true) + Environment.NewLine);
    }

    private void WriteDeviceMetadata()
    {
        var device = new DeviceMetadata
        {
            schemaVersion = 1,
            suite = SuiteId,
            processId = processId,
            operatingSystem = SystemInfo.operatingSystem,
            processorType = SystemInfo.processorType,
            processorCount = SystemInfo.processorCount,
            systemMemorySizeMb = SystemInfo.systemMemorySize,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
            graphicsDeviceVendorId = SystemInfo.graphicsDeviceVendorID,
            graphicsDeviceId = SystemInfo.graphicsDeviceID,
            graphicsDeviceType =
                SystemInfo.graphicsDeviceType.ToString(),
            graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
            graphicsMemorySizeMb = SystemInfo.graphicsMemorySize,
            graphicsShaderLevel = SystemInfo.graphicsShaderLevel,
            graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded,
            supportsComputeShaders = SystemInfo.supportsComputeShaders,
            supportsGraphicsFence = SystemInfo.supportsGraphicsFence,
            supportsAsyncGpuReadback =
                SystemInfo.supportsAsyncGPUReadback,
            supportsWaveOperations =
                global::Summit.GpuPrimitives.GpuPrimitives
                    .SupportsWaveOperations,
            frameTimingFeatureEnabled =
                FrameTimingManager.IsFeatureEnabled(),
            deviceFingerprint = environment.Device?.StableKey ?? string.Empty,
            unityVersion = Application.unityVersion,
        };
        WriteText(
            "device.json",
            JsonUtility.ToJson(device, true) + Environment.NewLine);
    }

    private void WriteRawFrames()
    {
        using (StreamWriter writer = CreateWriter("raw-frames.csv"))
        {
            writer.WriteLine(
                "sourceRowIndex,processId,scenarioId,blockIndex," +
                "superRound,sequencePosition,pairIndex,pairOrder," +
                "withinPairPosition,side,caseId,marker,sampleIndex," +
                "logicalOrdinal,sourceUnityFrame,elapsedSeconds," +
                "instanceCount,viewCount,visibilityBasisPoints," +
                "dirtyBasisPoints,clusterCount,hierarchyCandidateBp," +
                "slotIndex,slotWaitFrames,stateCpuMs,planCpuMs," +
                "selectorCpuMs,selectorCpuTicks,selectorInvoked," +
                "recordCpuMs,fenceCpuMs,enqueueCpuMs,totalCpuMs," +
                "mainThreadAllocatedBytes,stateRecordsWritten," +
                "changedInstanceCount,dirtyRangeCount,rangePlanHash," +
                "updateHash,stateRevision,expectedStateHash," +
                "decisionUploadMode,decisionOutputMode," +
                "decisionCullingMode,decisionPrimitiveBackend," +
                "decisionProfileRuleIndex,decisionRuleId," +
                "decisionFlags,decisionSource,decisionAccepted," +
                "decisionStableExpected," +
                "plannedUploadMode,plannedFullUploadReason," +
                "plannedInputRangeCount,plannedDirtyRecordCount," +
                "plannedDirtyRecordCountExact," +
                "plannedUploadedRecordCount,plannedUploadCallCount," +
                "plannedLogicalUploadBytes,recordedUploadMode," +
                "recordedFullUploadReason,recordedInputRangeCount," +
                "recordedDirtyRecordCount," +
                "recordedDirtyRecordCountExact," +
                "recordedUploadedRecordCount," +
                "recordedBridgedCleanRecordCount," +
                "recordedUploadCallCount,recordedLogicalUploadBytes," +
                "uploadAmplificationBp,renderApiCallCount," +
                "logicalDrawCommandCount,completionFenceAppended," +
                "nativeTimestampToken,nativeTimestampUserTag," +
                "nativeTimestampFlags,nativeTimestampStatus," +
                "timestampResultUnityFrame,nativeTimestampBeginTicks," +
                "nativeTimestampEndTicks,nativeTimestampElapsedTicks," +
                "nativeTimestampFrequency," +
                "nativeTimestampElapsedNanoseconds," +
                "nativeTimestampFenceValue," +
                "nativeTimestampDeviceGeneration,gpuRegionElapsedMs," +
                "frameTimingValid,frameTimingStatus," +
                "frameTimingResultUnityFrame," +
                "frameTimingCaptureLatencyFrames," +
                "cpuRenderThreadFrameValid,gpuFrameValid," +
                "submissionWindowValid,submissionWindowStatus," +
                "frameStartTimestamp,firstSubmitTimestamp," +
                "cpuTimePresentCalled,cpuTimeFrameComplete," +
                "cpuFrameMs,cpuMainThreadFrameMs," +
                "cpuRenderThreadFrameMs,gpuFrameMs," +
                "cpuSubmissionWindowMs,measurementReadbackBytes," +
                "timestampInstrumentationReadbackBytes");
            for (int index = 0; index < rawSampleCount; index++)
            {
                RawSample row = rawSamples[index];
                GpuInstanceUploadReceipt planned = row.PlannedUpload;
                GpuInstanceUploadReceipt recorded = row.RecordedUpload;
                writer.WriteLine(string.Join(",", new[]
                {
                    I(row.SourceRowIndex), I(row.ProcessId),
                    Csv(row.ScenarioId), I(row.BlockIndex),
                    I(row.SuperRound), I(row.SequencePosition),
                    I(row.PairIndex), Csv(row.PairOrder),
                    I(row.WithinPairPosition), row.Side,
                    Csv(row.BenchmarkCase.CaseId),
                    Csv(row.BenchmarkCase.Marker), I(row.SampleIndex),
                    U(row.LogicalOrdinal), I(row.SourceUnityFrame),
                    D(row.ElapsedSeconds), I(instanceCount), I(viewCount),
                    I(visibilityBasisPoints), I(dirtyBasisPoints),
                    I(adapter.ClusterCount),
                    I(adapter.HierarchyCandidateBasisPoints),
                    I(row.SlotIndex), I(row.SlotWaitFrames),
                    D(row.StateCpuMs), D(row.PlanCpuMs),
                    D(row.SelectorCpuMs), L(row.SelectorCpuTicks),
                    B(row.SelectorInvoked), D(row.RecordCpuMs),
                    D(row.FenceCpuMs), D(row.EnqueueCpuMs),
                    D(row.TotalCpuMs), L(row.MainThreadAllocatedBytes),
                    I(row.StateRecordsWritten),
                    I(row.ChangedInstanceCount), I(row.DirtyRangeCount),
                    H(row.RangePlanHash), H(row.UpdateHash),
                    U64(row.StateRevision), H(row.ExpectedStateHash),
                    row.Decision.UploadMode.ToString(),
                    row.Decision.OutputMode.ToString(),
                    row.Decision.CullingMode.ToString(),
                    row.Decision.PrimitiveBackend.ToString(),
                    I(row.Decision.ProfileRuleIndex),
                    Csv(RuleId(row.Decision.ProfileRuleIndex)),
                    U(unchecked((uint)row.Decision.Flags)),
                    row.Decision.Source.ToString(),
                    B(ResolvedDecisionAccepted(in row.Decision)),
                    B(row.DecisionStableExpected),
                    planned.Mode.ToString(),
                    planned.FullUploadReason.ToString(),
                    I(planned.InputRangeCount),
                    I(planned.DirtyRecordCount),
                    B(planned.DirtyRecordCountExact),
                    I(planned.UploadedRecordCount),
                    I(planned.UploadCallCount),
                    L(planned.LogicalUploadBytes),
                    recorded.Mode.ToString(),
                    recorded.FullUploadReason.ToString(),
                    I(recorded.InputRangeCount),
                    I(recorded.DirtyRecordCount),
                    B(recorded.DirtyRecordCountExact),
                    I(recorded.UploadedRecordCount),
                    I(recorded.BridgedCleanRecordCount),
                    I(recorded.UploadCallCount),
                    L(recorded.LogicalUploadBytes),
                    UploadAmplificationBasisPoints(row),
                    I(row.RenderApiCallCount),
                    I(row.LogicalDrawCommandCount),
                    B(row.CompletionFenceAppended),
                    U64(row.NativeTimestampToken),
                    U64(row.NativeTimestampUserTag),
                    U(row.NativeTimestampFlags),
                    row.NativeTimestampStatus,
                    I(row.TimestampResultUnityFrame),
                    U64(row.NativeTimestampBeginTicks),
                    U64(row.NativeTimestampEndTicks),
                    U64(row.NativeTimestampElapsedTicks),
                    U64(row.NativeTimestampFrequency),
                    L(row.NativeTimestampElapsedNanoseconds),
                    U64(row.NativeTimestampFenceValue),
                    U(row.NativeTimestampDeviceGeneration),
                    M(row.GpuRegionElapsedMs), B(row.FrameTimingValid),
                    row.FrameTimingStatus.ToString(),
                    I(row.FrameTimingResultUnityFrame),
                    I(row.FrameTimingCaptureLatencyFrames),
                    B(row.CpuRenderThreadFrameValid),
                    B(row.GpuFrameValid),
                    B(row.SubmissionWindowValid),
                    row.SubmissionWindowStatus.ToString(),
                    U64(row.FrameStartTimestamp),
                    U64(row.FirstSubmitTimestamp),
                    U64(row.CpuTimePresentCalled),
                    U64(row.CpuTimeFrameComplete),
                    M(row.CpuFrameMs), M(row.CpuMainThreadFrameMs),
                    M(row.CpuRenderThreadFrameMs),
                    M(row.GpuFrameValid
                        ? row.GpuFrameMs
                        : double.NaN),
                    M(row.CpuSubmissionWindowMs),
                    L(row.MeasurementReadbackBytes),
                    L(row.TimestampInstrumentationReadbackBytes),
                }));
            }
        }
    }

    private void WriteBlockSummaries()
    {
        using (StreamWriter writer = CreateWriter("block-summary.csv"))
        {
            writer.WriteLine(
                "blockIndex,superRound,sequencePosition,pairIndex," +
                "pairOrder,withinPairPosition,side,caseId,marker," +
                "sampleCount,expectedUploadMode,expectedOutputMode," +
                "expectedCullingMode,expectedPrimitiveBackend," +
                "expectedProfileRuleIndex,expectedRuleId," +
                "expectedFlags,expectedDecisionSource," +
                MetricHeader("stateCpu") + "," +
                MetricHeader("planCpu") + "," +
                MetricHeader("selectorCpu") + "," +
                MetricHeader("recordCpu") + "," +
                MetricHeader("fenceCpu") + "," +
                MetricHeader("enqueueCpu") + "," +
                MetricHeader("totalCpu") + "," +
                MetricHeader("nativeGpu") + "," +
                MetricHeader("cpuFrame") + "," +
                MetricHeader("cpuMainThreadFrame") + "," +
                MetricHeader("cpuRenderThreadFrame") + "," +
                MetricHeader("gpuFrame") + "," +
                MetricHeader("cpuSubmissionWindow") + "," +
                "timestampReadyRows,frameTimingReadyRows," +
                "submissionWindowReadyRows,stableDecisionRows," +
                "mainThreadAllocationRows,mainThreadAllocatedBytes," +
                "slotWaitFrames,logicalUploadBytes,uploadCallCount," +
                "clusterCount,hierarchyCandidateBp," +
                "residentStateHashBeforeMeasured," +
                "firstMeasuredExpectedStateHash," +
                "firstMeasuredResidentDifferenceRequired," +
                "firstMeasuredStateDiffersFromResident," +
                "completionFencesPassed");
            for (int index = 0; index < blockSummaryCount; index++)
            {
                BlockSummary block = blockSummaries[index];
                var cells = new List<string>(96)
                {
                    I(block.BlockIndex), I(block.SuperRound),
                    I(block.SequencePosition), I(block.PairIndex),
                    Csv(block.PairOrder), I(block.WithinPairPosition),
                    block.Side, Csv(block.BenchmarkCase.CaseId),
                    Csv(block.BenchmarkCase.Marker), I(block.SampleCount),
                    block.ExpectedDecision.UploadMode.ToString(),
                    block.ExpectedDecision.OutputMode.ToString(),
                    block.ExpectedDecision.CullingMode.ToString(),
                    block.ExpectedDecision.PrimitiveBackend.ToString(),
                    I(block.ExpectedDecision.ProfileRuleIndex),
                    Csv(RuleId(block.ExpectedDecision.ProfileRuleIndex)),
                    U(unchecked((uint)block.ExpectedDecision.Flags)),
                    block.ExpectedDecision.Source.ToString(),
                };
                AddMetric(cells, block.State);
                AddMetric(cells, block.Plan);
                AddMetric(cells, block.Selector);
                AddMetric(cells, block.Record);
                AddMetric(cells, block.Fence);
                AddMetric(cells, block.Enqueue);
                AddMetric(cells, block.Total);
                AddMetric(cells, block.NativeGpu);
                AddMetric(cells, block.CpuFrame);
                AddMetric(cells, block.CpuMainThreadFrame);
                AddMetric(cells, block.CpuRenderThreadFrame);
                AddMetric(cells, block.GpuFrame);
                AddMetric(cells, block.SubmissionWindow);
                cells.Add(I(block.TimestampReadyRows));
                cells.Add(I(block.FrameTimingReadyRows));
                cells.Add(I(block.SubmissionWindowReadyRows));
                cells.Add(I(block.StableDecisionRows));
                cells.Add(I(block.MainThreadAllocationRows));
                cells.Add(L(block.MainThreadAllocatedBytes));
                cells.Add(L(block.SlotWaitFrames));
                cells.Add(L(block.LogicalUploadBytes));
                cells.Add(L(block.UploadCallCount));
                cells.Add(I(adapter.ClusterCount));
                cells.Add(I(adapter.HierarchyCandidateBasisPoints));
                cells.Add(H(block.ResidentStateHashBeforeMeasured));
                cells.Add(H(block.FirstMeasuredExpectedStateHash));
                cells.Add(B(
                    block.FirstMeasuredResidentDifferenceRequired));
                cells.Add(B(
                    block.FirstMeasuredStateDiffersFromResident));
                cells.Add(B(block.CompletionFencesPassed));
                writer.WriteLine(string.Join(",", cells));
            }
        }
    }

    private void WriteValidations()
    {
        using (StreamWriter writer = CreateWriter("validation.csv"))
        {
            writer.WriteLine(
                "phase,caseId,passed,message,readbackBytes," +
                "expectedOutputHash,actualOutputHash,expectedStateHash," +
                "actualStateHash,invalidKeyCount,diagnosticFlags," +
                "hierarchyStatisticsAvailable," +
                "expectedCoarseVisibleClusterViewCount," +
                "expectedCandidateInstanceViewCount," +
                "expectedHierarchicalVisiblePairCount," +
                "coarseVisibleClusterViewCount," +
                "candidateInstanceViewCount," +
                "hierarchicalVisiblePairCount,clusterCount," +
                "hierarchyCandidateBp,engineIndirectWordCount," +
                "engineIndirectReadbackBytes," +
                "engineIndirectExpectedHash," +
                "engineIndirectActualHash," +
                "engineIndirectMismatchCount,engineIndirectExact," +
                "renderTargetFormat,renderTargetWidth," +
                "renderTargetHeight,renderTargetReadbackBytes," +
                "renderTargetHash,renderTargetBlackReferenceHash," +
                "renderTargetNonBlackPixelCount," +
                "renderTargetHashConsistent," +
                "presentationValidationPassed," +
                "presentationValidationMessage");
            for (int index = 0; index < validationResultCount; index++)
            {
                GpuDrivenInstancePolicyValidationResult row =
                    validationResults[index];
                EnginePresentationValidationReceipt presentation =
                    index < enginePresentationValidationResultCount
                        ? enginePresentationValidationResults[index]
                        : EnginePresentationValidationReceipt.Failed(
                            "Presentation receipt unavailable.");
                writer.WriteLine(string.Join(",", new[]
                {
                    Csv(row.Phase), Csv(row.CaseId), B(row.Passed),
                    Csv(row.Message), L(row.ReadbackBytes),
                    Csv(row.ExpectedOutputHash), Csv(row.ActualOutputHash),
                    H(row.ExpectedStateHash), H(row.ActualStateHash),
                    U(row.InvalidKeyCount), U(row.DiagnosticFlags),
                    B(row.HierarchyStatisticsAvailable),
                    U(row.ExpectedCoarseVisibleClusterViewCount),
                    U(row.ExpectedCandidateInstanceViewCount),
                    U(row.ExpectedHierarchicalVisiblePairCount),
                    U(row.CoarseVisibleClusterViewCount),
                    U(row.CandidateInstanceViewCount),
                    U(row.HierarchicalVisiblePairCount),
                    I(adapter.ClusterCount),
                    I(adapter.HierarchyCandidateBasisPoints),
                    I(presentation.EngineIndirectWordCount),
                    L(presentation.EngineIndirectReadbackBytes),
                    presentation.EngineIndirectExpectedHash,
                    presentation.EngineIndirectActualHash,
                    I(presentation.EngineIndirectMismatchCount),
                    B(presentation.EngineIndirectExact),
                    presentation.RenderTargetFormat,
                    I(presentation.RenderTargetWidth),
                    I(presentation.RenderTargetHeight),
                    L(presentation.RenderTargetReadbackBytes),
                    presentation.RenderTargetHash,
                    presentation.RenderTargetBlackReferenceHash,
                    I(presentation.RenderTargetNonBlackPixelCount),
                    B(presentation.RenderTargetHashConsistent),
                    B(presentation.Passed),
                    Csv(presentation.Message),
                }));
            }
        }
    }

    private void WriteSelectorOverhead()
    {
        using (StreamWriter writer = CreateWriter(
            "selector-overhead.csv"))
        {
            writer.WriteLine(
                "iterations,warmupCalls,totalTicks,stopwatchFrequency," +
                "meanNs,p50Ns,p95Ns,p99Ns,allocatedBytes," +
                "unstableDecisionCount,decisionChecksum," +
                "uploadMode,outputMode,cullingMode,primitiveBackend," +
                "profileRuleIndex,ruleId,flags,profileAccepted," +
                "profileValidationError,observationContract");
            writer.WriteLine(string.Join(",", new[]
            {
                I(selectorOverhead.Iterations),
                I(selectorOverhead.WarmupCalls),
                L(selectorOverhead.TotalTicks),
                L(selectorOverhead.StopwatchFrequency),
                D(selectorOverhead.MeanNanoseconds),
                D(selectorOverhead.P50Nanoseconds),
                D(selectorOverhead.P95Nanoseconds),
                D(selectorOverhead.P99Nanoseconds),
                L(selectorOverhead.AllocatedBytes),
                I(selectorOverhead.UnstableDecisionCount),
                H(selectorOverhead.DecisionChecksum),
                selectorOverhead.Decision.UploadMode.ToString(),
                selectorOverhead.Decision.OutputMode.ToString(),
                selectorOverhead.Decision.CullingMode.ToString(),
                selectorOverhead.Decision.PrimitiveBackend.ToString(),
                I(selectorOverhead.Decision.ProfileRuleIndex),
                Csv(RuleId(selectorOverhead.Decision.ProfileRuleIndex)),
                U(unchecked((uint)selectorOverhead.Decision.Flags)),
                B(profileAccepted), profileValidationError.ToString(),
                Csv("adapter-exact-synthetic-facts;no-gpu-work;" +
                    "exact-plan-counts-revisions-capabilities;" +
                    "live-unconsumed-plan;invalidated-by-first-workload"),
            }));
        }
    }

    private void WriteRunSummary(bool passed, string status)
    {
        int allocationRows = 0;
        long allocationBytes = 0L;
        int frameTimingRows = 0;
        int gpuFrameRows = 0;
        int submissionWindowRows = 0;
        int timestampRows = 0;
        int stableDecisionRows = 0;
        long slotWaitFrames = 0L;
        long measurementReadbackBytes = 0L;
        long timestampInstrumentationBytes = 0L;
        long logicalUploadBytes = 0L;
        long uploadCallCount = 0L;
        if (rawSamples != null)
        {
            for (int index = 0; index < rawSampleCount; index++)
            {
                RawSample row = rawSamples[index];
                allocationBytes += row.MainThreadAllocatedBytes;
                if (row.MainThreadAllocatedBytes != 0L)
                {
                    allocationRows++;
                }
                if (row.FrameTimingValid)
                {
                    frameTimingRows++;
                }
                if (row.GpuFrameValid)
                {
                    gpuFrameRows++;
                }
                if (row.SubmissionWindowValid)
                {
                    submissionWindowRows++;
                }
                if (row.NativeTimestampStatus == "ready")
                {
                    timestampRows++;
                }
                if (row.DecisionStableExpected)
                {
                    stableDecisionRows++;
                }
                slotWaitFrames += row.SlotWaitFrames;
                measurementReadbackBytes += row.MeasurementReadbackBytes;
                timestampInstrumentationBytes +=
                    row.TimestampInstrumentationReadbackBytes;
                logicalUploadBytes +=
                    row.RecordedUpload.LogicalUploadBytes;
                uploadCallCount += row.RecordedUpload.UploadCallCount;
            }
        }
        int validationFailures = 0;
        long validationReadbackBytes = 0L;
        if (validationResults != null)
        {
            for (int index = 0; index < validationResultCount; index++)
            {
                GpuDrivenInstancePolicyValidationResult validation =
                    validationResults[index];
                if (validation == null || !validation.Passed)
                {
                    validationFailures++;
                }
                if (validation != null)
                {
                    validationReadbackBytes += validation.ReadbackBytes;
                }
            }
        }
        int presentationValidationFailures = 0;
        long presentationValidationReadbackBytes = 0L;
        int minimumRenderTargetNonBlackPixels = int.MaxValue;
        if (enginePresentationValidationResults != null)
        {
            for (int index = 0;
                 index < enginePresentationValidationResultCount;
                 index++)
            {
                EnginePresentationValidationReceipt presentation =
                    enginePresentationValidationResults[index];
                if (!presentation.Passed)
                {
                    presentationValidationFailures++;
                }
                presentationValidationReadbackBytes = checked(
                    presentationValidationReadbackBytes +
                    presentation.ReadbackBytes);
                minimumRenderTargetNonBlackPixels = Math.Min(
                    minimumRenderTargetNonBlackPixels,
                    presentation.RenderTargetNonBlackPixelCount);
            }
        }
        string[] lines =
        {
            "suite=" + SuiteId,
            "passed=" + B(passed),
            "status=" + status,
            "processId=" + I(processId),
            "scenarioId=" + scenarioId,
            "benchmarkStartedUtc=" +
                (benchmarkStartedUtc ?? "unavailable"),
            "elapsedSeconds=" + D(
                Time.realtimeSinceStartupAsDouble - benchmarkStart),
            "instanceCount=" + I(instanceCount),
            "viewCount=" + I(viewCount),
            "visibility=" + visibility,
            "visibilityBasisPoints=" + I(visibilityBasisPoints),
            "dirtyBasisPoints=" + I(dirtyBasisPoints),
            "clusterCount=" +
                (adapter == null ? "unavailable" : I(adapter.ClusterCount)),
            "hierarchyCandidateBasisPoints=" +
                (adapter == null
                    ? "unavailable"
                    : I(adapter.HierarchyCandidateBasisPoints)),
            "requiredOutputMode=" + requiredOutputMode,
            "leftCase=" +
                (adapter == null ? leftCaseText : leftCase.CaseId),
            "rightCase=" +
                (adapter == null ? rightCaseText : rightCase.CaseId),
            "scheduleContract=" + ScheduleContract,
            "measurementBlockCount=" + I(blockSummaryCount),
            "expectedMeasurementBlockCount=" + I(MeasurementBlockCount),
            "rawFrameCount=" + I(rawSampleCount),
            "expectedRawFrameCount=" + I(checked(
                MeasurementBlockCount * sampleFrames)),
            "warmupFramesRequested=" + I(warmupFrames),
            "convergenceFramesPerBlock=" + I(Math.Max(
                warmupFrames,
                GpuDrivenInstancePolicyContract.MaximumConsecutiveFrames)),
            "sampleFramesPerBlock=" + I(sampleFrames),
            "frameTimingResultLatencyFrames=" +
                I(FrameTimingResultLatencyFrames),
            "frameTimingReadyRows=" + I(frameTimingRows),
            "gpuFrameReadyRows=" + I(gpuFrameRows),
            "gpuFrameUnavailableRows=" +
                I(rawSampleCount - gpuFrameRows),
            "submissionWindowReadyRows=" + I(submissionWindowRows),
            "nativeTimestampReadyRows=" + I(timestampRows),
            "stableDecisionRows=" + I(stableDecisionRows),
            "decisionIssueCount=" + I(decisionIssueCount),
            "frameAlignmentIssueCount=" +
                I(frameAlignmentIssueCount),
            "pairedInputIssueCount=" + I(pairedInputIssueCount),
            "firstMeasuredResidentIssueCount=" +
                I(firstMeasuredResidentIssueCount),
            "validationLifecycleDrainIssueCount=" +
                I(validationLifecycleDrainIssueCount),
            "validationTimeoutCount=" + I(validationTimeoutCount),
            "validationDrainTimeoutCount=" +
                I(validationDrainTimeoutCount),
            "presentationValidationTimeoutCount=" +
                I(presentationValidationTimeoutCount),
            "validationLifecycleComplete=" + B(
                validationLifecycleDrainIssueCount == 0 &&
                !validationLifecycleDrainIncomplete),
            "validationLifecycleDrainStatus=" +
                validationLifecycleDrainStatus,
            "validationLifecycleDrainIncomplete=" +
                B(validationLifecycleDrainIncomplete),
            "measuredLogicalOrdinalContract=" +
                "pair-local-v1",
            "unmeasuredLogicalOrdinalContract=" +
                "independent-high-bit-v1",
            "mainThreadAllocationRows=" + I(allocationRows),
            "mainThreadAllocatedBytes=" + L(allocationBytes),
            "timedAllocationFree=" + B(allocationRows == 0),
            "slotWaitFrames=" + L(slotWaitFrames),
            "measurementReadbackBytes=" + L(measurementReadbackBytes),
            "timestampInstrumentationReadbackBytes=" +
                L(timestampInstrumentationBytes),
            "logicalUploadBytes=" + L(logicalUploadBytes),
            "uploadCallCount=" + L(uploadCallCount),
            "validationCount=" + I(validationResultCount),
            "expectedValidationCount=" + I(ValidationCount),
            "validationFailures=" + I(validationFailures),
            "validationReadbackBytes=" + L(validationReadbackBytes),
            "presentationValidationCount=" +
                I(enginePresentationValidationResultCount),
            "expectedPresentationValidationCount=" +
                I(ValidationCount),
            "presentationValidationFailures=" +
                I(presentationValidationFailures),
            "presentationValidationReadbackBytes=" +
                L(presentationValidationReadbackBytes),
            "deterministicRenderTargetHash=" +
                (string.IsNullOrEmpty(deterministicRenderTargetHash)
                    ? "unavailable"
                    : deterministicRenderTargetHash),
            "minimumRenderTargetNonBlackPixels=" +
                (minimumRenderTargetNonBlackPixels == int.MaxValue
                    ? "unavailable"
                    : I(minimumRenderTargetNonBlackPixels)),
            "completionFencesComplete=" +
                B(allCompletionFencesPassed),
            "profilePath=" + (profilePath ?? string.Empty),
            "profileSha256=" + profileSha256,
            "profileAccepted=" + B(profileAccepted),
            "profileValidationError=" + profileValidationError,
            "pipelineContractFingerprint=" +
                pipelineContractFingerprint,
            "shaderContractFingerprint=" + shaderContractFingerprint,
            "calibrationProtocol=" + calibrationProtocol,
            "measurementContractFingerprint=" +
                measurementContractFingerprint,
            "buildCommit=" + buildCommit,
            "buildCommitValid=" + B(IsHex(buildCommit, 40)),
            "selectorOverheadIterations=" +
                I(selectorOverhead.Iterations),
            "selectorOverheadMeanNs=" +
                D(selectorOverhead.MeanNanoseconds),
            "selectorOverheadP50Ns=" +
                D(selectorOverhead.P50Nanoseconds),
            "selectorOverheadP95Ns=" +
                D(selectorOverhead.P95Nanoseconds),
            "selectorOverheadP99Ns=" +
                D(selectorOverhead.P99Nanoseconds),
            "selectorOverheadAllocatedBytes=" +
                L(selectorOverhead.AllocatedBytes),
            "selectorOverheadUnstableDecisionCount=" +
                I(selectorOverhead.UnstableDecisionCount),
            "nativeTimestampWarmupPassed=" +
                B(timestampWarmupPassed),
            "nativeTimestampWarmupStatus=" + timestampWarmupStatus,
            "nativeTimestampWarmupElapsedMs=" +
                D(timestampWarmupElapsedMs),
            "nativeTimestampWarmupFrequency=" +
                U64(timestampWarmupFrequency),
            "nativeTimestampWarmupFenceValue=" +
                U64(timestampWarmupFence),
            "nativeTimestampWarmupDeviceGeneration=" +
                U(timestampWarmupGeneration),
            "nativeTimestampAcquireFailures=" +
                I(timestampAcquireFailures),
            "nativeTimestampResultFailures=" +
                I(timestampResultFailures),
            "nativeTimestampTimeouts=" + I(timestampTimeouts),
            "evidenceGatePassed=" + B(passed && EvidenceGatePassed()),
        };
        File.WriteAllLines(
            Path.Combine(reportDirectory, "run-summary.txt"),
            lines,
            new UTF8Encoding(false));
    }

    private static BlockPlan[] BuildBlockPlans(
        GpuDrivenInstancePolicyBenchmarkCase left,
        GpuDrivenInstancePolicyBenchmarkCase right)
    {
        GpuDrivenInstancePolicyBenchmarkCase[] cases =
        {
            left, right, right, left,
            right, left, left, right,
        };
        var result = new BlockPlan[MeasurementBlockCount];
        int pairIndex = 1;
        for (int index = 0; index < result.Length; index++)
        {
            int withinRound = index % 4;
            int withinPair = (withinRound % 2) + 1;
            bool firstOfPairIsLeft = index < 4
                ? withinRound < 2
                : withinRound >= 2;
            result[index] = new BlockPlan
            {
                BlockIndex = index + 1,
                SuperRound = index / 4 + 1,
                SequencePosition = withinRound + 1,
                PairIndex = pairIndex,
                PairOrder = firstOfPairIsLeft ? "AB" : "BA",
                WithinPairPosition = withinPair,
                Side = CaseEquals(cases[index], left) ? "A" : "B",
                BenchmarkCase = cases[index],
            };
            if (withinPair == 2)
            {
                pairIndex++;
            }
        }
        return result;
    }

    private GpuDrivenInstancePolicyBenchmarkCase ParseCase(
        string value,
        in GpuDrivenInstancePolicyDecision selectedDecision)
    {
        string normalized = NormalizeId(value);
        switch (normalized)
        {
            case "safe-baseline":
                return GpuDrivenInstancePolicyBenchmarkCase.SafeBaseline();
            case "forced-selected":
                return GpuDrivenInstancePolicyBenchmarkCase.ForcedSelected(
                    in selectedDecision);
            case "actual-auto":
                return GpuDrivenInstancePolicyBenchmarkCase.ActualAuto();
            case "full-flat":
                return GpuDrivenInstancePolicyBenchmarkCase.ForcedFullFlat();
            case "full-hierarchy":
                return GpuDrivenInstancePolicyBenchmarkCase
                    .ForcedFullHierarchy();
            case "dirty-flat":
                return GpuDrivenInstancePolicyBenchmarkCase
                    .ForcedDirtyFlat();
            case "dirty-hierarchy":
                return GpuDrivenInstancePolicyBenchmarkCase
                    .ForcedDirtyHierarchy();
            case "none-flat":
                return GpuDrivenInstancePolicyBenchmarkCase.ForcedNoneFlat();
            case "none-hierarchy":
                return GpuDrivenInstancePolicyBenchmarkCase
                    .ForcedNoneHierarchy();
            default:
                throw new ArgumentException(
                    "Unsupported policy benchmark case: " + value + ".");
        }
    }

    private void ValidateCaseCompatibility(
        GpuDrivenInstancePolicyBenchmarkCase benchmarkCase)
    {
        switch (benchmarkCase.Kind)
        {
            case GpuDrivenInstancePolicyBenchmarkCaseKind
                .ForcedFullHierarchy:
            case GpuDrivenInstancePolicyBenchmarkCaseKind
                .ForcedDirtyHierarchy:
            case GpuDrivenInstancePolicyBenchmarkCaseKind
                .ForcedNoneHierarchy:
                if (requiredOutputMode !=
                    GpuDrivenInstanceOutputMode.VisibleOnly)
                {
                    throw new InvalidOperationException(
                        "Hierarchy cases require VisibleOnly output.");
                }
                break;
        }
        if ((benchmarkCase.Kind ==
                GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedNoneFlat ||
             benchmarkCase.Kind ==
                GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedNoneHierarchy) &&
            dirtyBasisPoints != 0)
        {
            throw new InvalidOperationException(
                "Forced-none cases require dirty-bps=0.");
        }
    }

    private static bool CaseRequiresAcceptedProfile(string value)
    {
        string normalized = NormalizeId(value);
        return normalized == "actual-auto" ||
            normalized == "forced-selected";
    }

    internal static bool CasesRequireAcceptedSelectorEvidence(
        string leftCase,
        string rightCase)
    {
        return CaseRequiresAcceptedProfile(leftCase) ||
            CaseRequiresAcceptedProfile(rightCase);
    }

    private static string NormalizeId(string value)
    {
        string normalized = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('_', '-');
        const string prefix = "gpu-driven-policy/";
        if (normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            normalized = normalized.Substring(prefix.Length);
        }
        const string calibrationPrefix = "calibration/";
        if (normalized.StartsWith(
            calibrationPrefix,
            StringComparison.Ordinal))
        {
            normalized = normalized.Substring(calibrationPrefix.Length);
        }
        return normalized;
    }

    private static GpuDrivenInstanceOutputMode ParseRequiredOutput(
        string value)
    {
        string normalized = NormalizeId(value);
        if (normalized == "visible-only" || normalized == "visibleonly")
        {
            return GpuDrivenInstanceOutputMode.VisibleOnly;
        }
        if (normalized == "culled-tail" || normalized == "culledtail")
        {
            return GpuDrivenInstanceOutputMode.CulledTail;
        }
        throw new ArgumentException(
            "Required output must be visible-only or culled-tail.");
    }

    private static int VisibilityBasisPoints(string value)
    {
        switch (NormalizeId(value))
        {
            case "visible5":
            case "5":
            case "500":
                return 500;
            case "visible25":
            case "25":
            case "2500":
                return 2500;
            case "visible75":
            case "75":
            case "7500":
                return 7500;
            case "visible100":
            case "100":
            case "10000":
                return 10000;
            default:
                throw new ArgumentException(
                    "Visibility must be visible5, visible25, visible75, " +
                    "visible100, or the corresponding exact basis points.");
        }
    }

    private static string VisibilityId(int basisPoints)
    {
        switch (basisPoints)
        {
            case 500:
                return "visible5";
            case 2500:
                return "visible25";
            case 7500:
                return "visible75";
            case 10000:
                return "visible100";
            default:
                throw new ArgumentException(
                    "Visibility basis points must be 500, 2500, 7500, " +
                    "or 10000.");
        }
    }

    private static bool CaseEquals(
        GpuDrivenInstancePolicyBenchmarkCase left,
        GpuDrivenInstancePolicyBenchmarkCase right)
    {
        return left.Kind == right.Kind &&
            string.Equals(left.CaseId, right.CaseId, StringComparison.Ordinal);
    }

    private static bool PolicyDecisionEquals(
        in GpuDrivenInstancePolicyDecision left,
        in GpuDrivenInstancePolicyDecision right)
    {
        return left.UploadMode == right.UploadMode &&
            left.OutputMode == right.OutputMode &&
            left.CullingMode == right.CullingMode &&
            left.PrimitiveBackend == right.PrimitiveBackend &&
            left.ProfileRuleIndex == right.ProfileRuleIndex &&
            left.Flags == right.Flags;
    }

    private static bool SelectorOverheadDecisionAccepted(
        in GpuDrivenInstancePolicyDecision decision)
    {
        return DecisionFlagsPassEvidenceGate(decision.Flags);
    }

    private static bool ResolvedDecisionAccepted(
        in GpuDrivenInstancePolicyResolvedDecision decision)
    {
        return DecisionFlagsPassEvidenceGate(decision.Flags);
    }

    internal static bool DecisionFlagsPassEvidenceGate(
        GpuDrivenInstancePolicyDecisionFlags flags)
    {
        const GpuDrivenInstancePolicyDecisionFlags rejected =
            GpuDrivenInstancePolicyDecisionFlags.ProfileFallback |
            GpuDrivenInstancePolicyDecisionFlags.InvalidObservation |
            GpuDrivenInstancePolicyDecisionFlags.NoMatchingRule |
            GpuDrivenInstancePolicyDecisionFlags.HysteresisPending |
            GpuDrivenInstancePolicyDecisionFlags.InvalidOverride |
            GpuDrivenInstancePolicyDecisionFlags.UploadGateFallback |
            GpuDrivenInstancePolicyDecisionFlags.CullingGateFallback |
            GpuDrivenInstancePolicyDecisionFlags.BackendGateFallback;
        return (flags & rejected) == 0;
    }

    private static bool ResolvedDecisionEquals(
        in GpuDrivenInstancePolicyResolvedDecision left,
        in GpuDrivenInstancePolicyResolvedDecision right)
    {
        return left.UploadMode == right.UploadMode &&
            left.OutputMode == right.OutputMode &&
            left.CullingMode == right.CullingMode &&
            left.PrimitiveBackend == right.PrimitiveBackend &&
            left.ProfileRuleIndex == right.ProfileRuleIndex &&
            left.Flags == right.Flags &&
            left.Source == right.Source;
    }

    private string RuleId(int ruleIndex)
    {
        if (profile?.rules == null ||
            ruleIndex < 0 ||
            ruleIndex >= profile.rules.Length ||
            profile.rules[ruleIndex] == null)
        {
            return string.Empty;
        }
        return profile.rules[ruleIndex].ruleId ?? string.Empty;
    }

    private static string UploadAmplificationBasisPoints(RawSample row)
    {
        if (row.ChangedInstanceCount == 0)
        {
            return row.RecordedUpload.UploadedRecordCount == 0
                ? "0"
                : "unavailable";
        }
        long basisPoints = checked(
            (long)row.RecordedUpload.UploadedRecordCount *
            GpuDrivenInstancePolicyContract.BasisPointScale /
            row.ChangedInstanceCount);
        return L(basisPoints);
    }

    private static string MetricHeader(string prefix)
    {
        return prefix + "ValidCount," + prefix + "MeanMs," +
            prefix + "P50Ms," + prefix + "P95Ms," +
            prefix + "P99Ms";
    }

    private static void AddMetric(
        List<string> cells,
        MetricStats metric)
    {
        cells.Add(I(metric.ValidCount));
        cells.Add(M(metric.Mean));
        cells.Add(M(metric.P50));
        cells.Add(M(metric.P95));
        cells.Add(M(metric.P99));
    }

    private int CountPairedInputIssues()
    {
        int issues = 0;
        int pairCount = MeasurementBlockCount / 2;
        for (int pairIndex = 1; pairIndex <= pairCount; pairIndex++)
        {
            int firstBlockStart = checked(
                (pairIndex - 1) * 2 * sampleFrames);
            int secondBlockStart = checked(
                firstBlockStart + sampleFrames);
            for (int sampleIndex = 1;
                 sampleIndex <= sampleFrames;
                 sampleIndex++)
            {
                int firstIndex = checked(
                    firstBlockStart + sampleIndex - 1);
                int secondIndex = checked(
                    secondBlockStart + sampleIndex - 1);
                if (firstIndex >= rawSampleCount ||
                    secondIndex >= rawSampleCount)
                {
                    issues++;
                    continue;
                }
                RawSample first = rawSamples[firstIndex];
                RawSample second = rawSamples[secondIndex];
                if (first.PairIndex != pairIndex ||
                    second.PairIndex != pairIndex ||
                    first.SampleIndex != sampleIndex ||
                    second.SampleIndex != sampleIndex ||
                    !PairedInputFieldsMatch(
                        first.LogicalOrdinal,
                        first.UpdateHash,
                        first.ExpectedStateHash,
                        second.LogicalOrdinal,
                        second.UpdateHash,
                        second.ExpectedStateHash))
                {
                    issues++;
                }
            }
        }
        return issues;
    }

    internal static bool PairedInputFieldsMatch(
        uint leftLogicalOrdinal,
        ulong leftUpdateHash,
        ulong leftExpectedStateHash,
        uint rightLogicalOrdinal,
        ulong rightUpdateHash,
        ulong rightExpectedStateHash)
    {
        return leftLogicalOrdinal == rightLogicalOrdinal &&
            leftUpdateHash == rightUpdateHash &&
            leftExpectedStateHash == rightExpectedStateHash;
    }

    internal static bool DeadlineHasExpired(
        double currentTime,
        double deadline)
    {
        return double.IsNaN(currentTime) ||
            double.IsNaN(deadline) ||
            currentTime >= deadline;
    }

    internal static bool ValidateExactEngineIndirectWords(
        uint[] expectedWords,
        uint[] actualWords,
        out ulong expectedHash,
        out ulong actualHash,
        out int mismatchCount)
    {
        expectedHash = HashUInt32Words(expectedWords);
        actualHash = HashUInt32Words(actualWords);
        if (expectedWords == null || actualWords == null)
        {
            mismatchCount = -1;
            return false;
        }
        int sharedLength = Math.Min(
            expectedWords.Length,
            actualWords.Length);
        mismatchCount = Math.Abs(
            expectedWords.Length - actualWords.Length);
        for (int index = 0; index < sharedLength; index++)
        {
            if (expectedWords[index] != actualWords[index])
            {
                mismatchCount++;
            }
        }
        return mismatchCount == 0;
    }

    internal static bool ValidateRenderTargetRgba32(
        byte[] rgba32,
        int width,
        int height,
        out ulong renderTargetHash,
        out ulong blackReferenceHash,
        out int nonBlackPixelCount)
    {
        renderTargetHash = HashBytes(rgba32);
        blackReferenceHash = FnvOffsetBasis;
        nonBlackPixelCount = 0;
        if (rgba32 == null || width <= 0 || height <= 0)
        {
            return false;
        }
        long expectedLength = checked((long)width * height * 4L);
        if (rgba32.LongLength != expectedLength)
        {
            return false;
        }
        for (int offset = 0; offset < rgba32.Length; offset += 4)
        {
            if ((rgba32[offset] |
                 rgba32[offset + 1] |
                 rgba32[offset + 2]) != 0)
            {
                nonBlackPixelCount++;
            }
            blackReferenceHash = HashByte(blackReferenceHash, 0);
            blackReferenceHash = HashByte(blackReferenceHash, 0);
            blackReferenceHash = HashByte(blackReferenceHash, 0);
            blackReferenceHash = HashByte(blackReferenceHash, 255);
        }
        return nonBlackPixelCount > 0 &&
            renderTargetHash != blackReferenceHash;
    }

    private static T[] CopyReadback<T>(NativeArray<T> data)
        where T : struct
    {
        var copy = new T[data.Length];
        data.CopyTo(copy);
        return copy;
    }

    private static ulong HashUInt32Words(uint[] words)
    {
        if (words == null)
        {
            return 0UL;
        }
        ulong hash = FnvOffsetBasis;
        for (int index = 0; index < words.Length; index++)
        {
            uint value = words[index];
            hash = HashByte(hash, (byte)value);
            hash = HashByte(hash, (byte)(value >> 8));
            hash = HashByte(hash, (byte)(value >> 16));
            hash = HashByte(hash, (byte)(value >> 24));
        }
        return hash;
    }

    private static ulong HashBytes(byte[] bytes)
    {
        if (bytes == null)
        {
            return 0UL;
        }
        ulong hash = FnvOffsetBasis;
        for (int index = 0; index < bytes.Length; index++)
        {
            hash = HashByte(hash, bytes[index]);
        }
        return hash;
    }

    private static ulong HashByte(ulong hash, byte value)
    {
        hash ^= value;
        return unchecked(hash * FnvPrime);
    }

    internal static uint MeasuredLogicalOrdinal(
        int pairIndex,
        int sampleIndex,
        int samplesPerBlock)
    {
        if (pairIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pairIndex));
        }
        if (samplesPerBlock <= 0 ||
            sampleIndex <= 0 ||
            sampleIndex > samplesPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleIndex));
        }
        ulong ordinal = checked(
            (ulong)(pairIndex - 1) * (ulong)samplesPerBlock +
            (ulong)sampleIndex);
        if (ordinal == 0UL || ordinal >= UnmeasuredOrdinalNamespace)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pairIndex),
                "Measured ordinal exceeds its deterministic namespace.");
        }
        return checked((uint)ordinal);
    }

    internal static uint UnmeasuredLogicalOrdinal(uint sequence)
    {
        if (sequence == 0u || sequence > UnmeasuredOrdinalSequenceMask)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }
        return UnmeasuredOrdinalNamespace | sequence;
    }

    private uint NextUnmeasuredLogicalOrdinal()
    {
        if (nextUnmeasuredOrdinalSequence ==
            UnmeasuredOrdinalSequenceMask)
        {
            throw new InvalidOperationException(
                "Unmeasured logical ordinal space is exhausted.");
        }
        nextUnmeasuredOrdinalSequence++;
        return UnmeasuredLogicalOrdinal(nextUnmeasuredOrdinalSequence);
    }

    private static string ComputeSha256(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            byte[] digest = sha.ComputeHash(stream);
            var text = new StringBuilder(digest.Length * 2);
            for (int index = 0; index < digest.Length; index++)
            {
                text.Append(digest[index].ToString("x2", Invariant));
            }
            return text.ToString();
        }
    }

    private static bool IsSha256(string value)
    {
        return IsHex(value, 64);
    }

    internal static bool IsHex(string value, int expectedLength)
    {
        if (expectedLength <= 0 ||
            value == null ||
            value.Length != expectedLength)
        {
            return false;
        }
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool hexadecimal =
                (character >= '0' && character <= '9') ||
                (character >= 'a' && character <= 'f') ||
                (character >= 'A' && character <= 'F');
            if (!hexadecimal)
            {
                return false;
            }
        }
        return true;
    }

    private void WriteText(string fileName, string content)
    {
        File.WriteAllText(
            Path.Combine(reportDirectory, fileName),
            content,
            new UTF8Encoding(false));
    }

    private StreamWriter CreateWriter(string fileName)
    {
        return new StreamWriter(
            Path.Combine(reportDirectory, fileName),
            false,
            new UTF8Encoding(false));
    }

    private void Finish(bool passed, string status)
    {
        if (finished)
        {
            return;
        }
        finished = true;
        Debug.LogWarning(
            "GPU driven-instance policy benchmark " + status +
            ". Report: " + reportDirectory);
        try
        {
            DisposeResources();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
        finally
        {
            Application.Quit(passed ? 0 : 1);
        }
    }

    private void DisposeResources()
    {
        if (timestampBackend != null)
        {
            if (pendingTimestamps != null)
            {
                for (int scope = 0;
                     scope < pendingTimestamps.Length;
                     scope++)
                {
                    if (pendingTimestamps[scope].InUse)
                    {
                        timestampBackend.Cancel(
                            pendingTimestamps[scope].Token);
                        pendingTimestamps[scope] = default;
                    }
                }
            }
            pendingTimestampCount = 0;
            timestampBackend.Dispose();
            timestampBackend = null;
        }
        GpuDrivenInstancePolicyBenchmarkAdapter current = adapter;
        adapter = null;
        validationPipelineIndirectArguments = null;
        validationEngineIndirectArguments = null;
        if (validationLifecycleDrainIncomplete)
        {
            Debug.LogError(
                "Skipping adapter disposal after a bounded validation " +
                "drain failure; the Player is terminating without " +
                "submitting another workload.");
            return;
        }
        current?.Dispose();
    }

    private static bool HasArgument(string[] args, string name)
    {
        if (args == null)
        {
            return false;
        }
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(
                args[index],
                name,
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
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(
                args[index],
                name,
                StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return fallback;
    }

    private static int ReadInt(
        string[] args,
        string name,
        int fallback)
    {
        return int.TryParse(
            ReadString(args, name, string.Empty),
            NumberStyles.Integer,
            Invariant,
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
            Invariant,
            out float value)
                ? value
                : fallback;
    }

    private static double Milliseconds(long ticks)
    {
        return (double)ticks * 1000.0 /
            GpuDrivenInstancePolicyBenchmarkAdapter.StopwatchFrequency;
    }

    private static double TicksToNanoseconds(double ticks)
    {
        return ticks * 1000000000.0 / Stopwatch.Frequency;
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        int index = Math.Max(
            0,
            Math.Min(
                sorted.Length - 1,
                (int)Math.Ceiling(sorted.Length * percentile) - 1));
        return sorted[index];
    }

    private static double Percentile(
        double[] sorted,
        int count,
        double percentile)
    {
        int index = Math.Max(
            0,
            Math.Min(
                count - 1,
                (int)Math.Ceiling(count * percentile) - 1));
        return sorted[index];
    }

    private static string TimestampStatusName(GpuTimestampStatus status)
    {
        switch (status)
        {
            case GpuTimestampStatus.Ready:
                return "ready";
            case GpuTimestampStatus.Pending:
                return "pending";
            case GpuTimestampStatus.Error:
                return "error";
            case GpuTimestampStatus.InvalidArgument:
                return "invalid-argument";
            case GpuTimestampStatus.InvalidToken:
                return "invalid-token";
            case GpuTimestampStatus.RingFull:
                return "ring-full";
            case GpuTimestampStatus.NotInitialized:
                return "not-initialized";
            case GpuTimestampStatus.Unsupported:
                return "unsupported";
            case GpuTimestampStatus.DeviceLost:
                return "device-lost";
            case GpuTimestampStatus.CallbackError:
                return "callback-error";
            case GpuTimestampStatus.FrequencyUnavailable:
                return "frequency-unavailable";
            case GpuTimestampStatus.StaleManagedToken:
                return "stale-managed-token";
            case GpuTimestampStatus.MalformedNativeResult:
                return "malformed-native-result";
            default:
                return "unknown";
        }
    }

    private static string Csv(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string I(int value) => value.ToString(Invariant);

    private static string U(uint value) => value.ToString(Invariant);

    private static string U64(ulong value) => value.ToString(Invariant);

    private static string L(long value) => value.ToString(Invariant);

    private static string B(bool value) => value ? "1" : "0";

    private static string D(double value) =>
        value.ToString("R", Invariant);

    private static string M(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? "unavailable"
            : value.ToString("R", Invariant);
    }

    private static string H(ulong value) =>
        value.ToString("x16", Invariant);

    private enum MetricKind
    {
        State,
        Plan,
        Selector,
        Record,
        Fence,
        Enqueue,
        Total,
        NativeGpu,
        CpuFrame,
        CpuMainThreadFrame,
        CpuRenderThreadFrame,
        GpuFrame,
        SubmissionWindow,
    }

    private struct PendingTimestamp
    {
        public bool InUse;
        public GpuTimestampToken Token;
        public int RowIndex;
        public GpuTimestampSampleFlags ExpectedFlags;
    }

    private struct BlockPlan
    {
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public string Side;
        public GpuDrivenInstancePolicyBenchmarkCase BenchmarkCase;
    }

    private struct RawSample
    {
        public int SourceRowIndex;
        public int ProcessId;
        public string ScenarioId;
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public string Side;
        public GpuDrivenInstancePolicyBenchmarkCase BenchmarkCase;
        public int SampleIndex;
        public uint LogicalOrdinal;
        public int SourceUnityFrame;
        public double ElapsedSeconds;
        public int SlotIndex;
        public int SlotWaitFrames;
        public double StateCpuMs;
        public double PlanCpuMs;
        public double SelectorCpuMs;
        public double RecordCpuMs;
        public double FenceCpuMs;
        public double EnqueueCpuMs;
        public double TotalCpuMs;
        public long SelectorCpuTicks;
        public bool SelectorInvoked;
        public long MainThreadAllocatedBytes;
        public int StateRecordsWritten;
        public int ChangedInstanceCount;
        public int DirtyRangeCount;
        public ulong RangePlanHash;
        public ulong UpdateHash;
        public ulong StateRevision;
        public ulong ExpectedStateHash;
        public GpuDrivenInstancePolicyResolvedDecision Decision;
        public bool DecisionStableExpected;
        public GpuInstanceUploadReceipt PlannedUpload;
        public GpuInstanceUploadReceipt RecordedUpload;
        public int RenderApiCallCount;
        public int LogicalDrawCommandCount;
        public bool CompletionFenceAppended;
        public ulong NativeTimestampToken;
        public ulong NativeTimestampUserTag;
        public uint NativeTimestampFlags;
        public string NativeTimestampStatus;
        public int TimestampResultUnityFrame;
        public ulong NativeTimestampBeginTicks;
        public ulong NativeTimestampEndTicks;
        public ulong NativeTimestampElapsedTicks;
        public ulong NativeTimestampFrequency;
        public long NativeTimestampElapsedNanoseconds;
        public ulong NativeTimestampFenceValue;
        public uint NativeTimestampDeviceGeneration;
        public double GpuRegionElapsedMs;
        public bool FrameTimingValid;
        public GpuDrivenInstanceFrameTimingStatus FrameTimingStatus;
        public int FrameTimingResultUnityFrame;
        public int FrameTimingCaptureLatencyFrames;
        public bool CpuRenderThreadFrameValid;
        public bool GpuFrameValid;
        public bool SubmissionWindowValid;
        public GpuDrivenInstanceSubmissionWindowStatus
            SubmissionWindowStatus;
        public ulong FrameStartTimestamp;
        public ulong FirstSubmitTimestamp;
        public ulong CpuTimePresentCalled;
        public ulong CpuTimeFrameComplete;
        public double CpuFrameMs;
        public double CpuMainThreadFrameMs;
        public double CpuRenderThreadFrameMs;
        public double GpuFrameMs;
        public double CpuSubmissionWindowMs;
        public long MeasurementReadbackBytes;
        public long TimestampInstrumentationReadbackBytes;
    }

    private struct MetricStats
    {
        public int ValidCount;
        public double Mean;
        public double P50;
        public double P95;
        public double P99;

        public static MetricStats Unavailable => new MetricStats
        {
            ValidCount = 0,
            Mean = double.NaN,
            P50 = double.NaN,
            P95 = double.NaN,
            P99 = double.NaN,
        };
    }

    private struct BlockSummary
    {
        public int BlockIndex;
        public int SuperRound;
        public int SequencePosition;
        public int PairIndex;
        public string PairOrder;
        public int WithinPairPosition;
        public string Side;
        public GpuDrivenInstancePolicyBenchmarkCase BenchmarkCase;
        public int SampleCount;
        public GpuDrivenInstancePolicyResolvedDecision ExpectedDecision;
        public MetricStats State;
        public MetricStats Plan;
        public MetricStats Selector;
        public MetricStats Record;
        public MetricStats Fence;
        public MetricStats Enqueue;
        public MetricStats Total;
        public MetricStats NativeGpu;
        public MetricStats CpuFrame;
        public MetricStats CpuMainThreadFrame;
        public MetricStats CpuRenderThreadFrame;
        public MetricStats GpuFrame;
        public MetricStats SubmissionWindow;
        public int TimestampReadyRows;
        public int FrameTimingReadyRows;
        public int SubmissionWindowReadyRows;
        public int StableDecisionRows;
        public int MainThreadAllocationRows;
        public long MainThreadAllocatedBytes;
        public long SlotWaitFrames;
        public long LogicalUploadBytes;
        public long UploadCallCount;
        public ulong ResidentStateHashBeforeMeasured;
        public ulong FirstMeasuredExpectedStateHash;
        public bool FirstMeasuredResidentDifferenceRequired;
        public bool FirstMeasuredStateDiffersFromResident;
        public bool CompletionFencesPassed;
    }

    private struct SelectorOverheadSummary
    {
        public int Iterations;
        public int WarmupCalls;
        public long TotalTicks;
        public long StopwatchFrequency;
        public double MeanNanoseconds;
        public double P50Nanoseconds;
        public double P95Nanoseconds;
        public double P99Nanoseconds;
        public long AllocatedBytes;
        public int UnstableDecisionCount;
        public ulong DecisionChecksum;
        public GpuDrivenInstancePolicyDecision Decision;
    }

    private struct EnginePresentationValidationReceipt
    {
        public bool Passed;
        public string Message;
        public long ReadbackBytes;
        public int EngineIndirectWordCount;
        public long EngineIndirectReadbackBytes;
        public string EngineIndirectExpectedHash;
        public string EngineIndirectActualHash;
        public int EngineIndirectMismatchCount;
        public bool EngineIndirectExact;
        public string RenderTargetFormat;
        public int RenderTargetWidth;
        public int RenderTargetHeight;
        public long RenderTargetReadbackBytes;
        public string RenderTargetHash;
        public string RenderTargetBlackReferenceHash;
        public int RenderTargetNonBlackPixelCount;
        public bool RenderTargetHashConsistent;

        public static EnginePresentationValidationReceipt Failed(
            string message)
        {
            return new EnginePresentationValidationReceipt
            {
                Passed = false,
                Message = message ?? string.Empty,
                EngineIndirectMismatchCount = -1,
                EngineIndirectExpectedHash = "unavailable",
                EngineIndirectActualHash = "unavailable",
                RenderTargetFormat = "unavailable",
                RenderTargetHash = "unavailable",
                RenderTargetBlackReferenceHash = "unavailable",
            };
        }
    }

    [Serializable]
    private sealed class Configuration
    {
        public int schemaVersion;
        public string suite;
        public int processId;
        public string benchmarkStartedUtc;
        public string unityVersion;
        public string scenarioId;
        public int instanceCount;
        public int viewCount;
        public string visibility;
        public int visibilityBasisPoints;
        public int visibleInstanceCount;
        public int visiblePairCount;
        public int dirtyBasisPoints;
        public int seed;
        public string requiredOutputMode;
        public string inputLayout;
        public int clusterCount;
        public uint expectedCoarseVisibleClusterViewCount;
        public uint expectedCandidateInstanceViewCount;
        public int hierarchyCandidateBasisPoints;
        public int warmupFramesRequested;
        public int convergenceFramesPerBlock;
        public int sampleFramesPerBlock;
        public int measurementBlocks;
        public string scheduleContract;
        public int frameTimingResultLatencyFrames;
        public string gpuFrameUnavailableLiteral;
        public int gpuFrameBlockMinimumCoveragePercent;
        public int gpuFramePairedMinimumCoveragePercent;
        public int otherTimedMetricCoveragePercent;
        public bool stateResetOutsideMeasuredWindow;
        public bool selectorResetOutsideMeasuredWindow;
        public bool caseLocalConvergenceOutsideMeasuredWindow;
        public string perFrameExecutionContract;
        public long measurementReadbackBytesPerFrame;
        public int timestampInstrumentationBytesPerCompletedSample;
        public int rawSamplePoolCapacity;
        public int timestampResultPoolCapacity;
        public int blockSummaryPoolCapacity;
        public int validationResultPoolCapacity;
        public int enginePresentationValidationResultPoolCapacity;
        public string measuredLogicalOrdinalContract;
        public string unmeasuredLogicalOrdinalContract;
        public string pairedInputContract;
        public string presentationValidationContract;
        public string validationTimeoutContract;
        public bool validationLifecycleComplete;
        public string validationLifecycleDrainStatus;
        public int validationTimeoutCount;
        public int validationDrainTimeoutCount;
        public int presentationValidationTimeoutCount;
        public int selectorOverheadIterations;
        public string selectorOverheadObservation;
        public string leftCase;
        public string rightCase;
        public string profilePath;
        public string profileSha256;
        public bool profileAccepted;
        public string profileValidationError;
        public int profileRevision;
        public string profileHoldoutEvidenceSetId;
        public string deviceFingerprint;
        public string pipelineContractFingerprint;
        public string shaderContractFingerprint;
        public string calibrationProtocol;
        public string measurementContractFingerprint;
        public string buildCommit;
        public string nativeTimestampAvailability;
        public uint nativeTimestampAbiVersion;
        public uint nativeTimestampCapabilityFlags;
        public int nativeTimestampRingCapacity;
        public bool nativeTimestampWarmupPassed;
        public string nativeTimestampWarmupStatus;
        public string[] commandLineArguments;
        public ConfigurationBlock[] blocks;
    }

    [Serializable]
    private sealed class ConfigurationBlock
    {
        public int blockIndex;
        public int superRound;
        public int sequencePosition;
        public int pairIndex;
        public string pairOrder;
        public int withinPairPosition;
        public string side;
        public string caseId;
        public string marker;
        public uint measuredLogicalOrdinalFirst;
        public uint measuredLogicalOrdinalLast;
    }

    [Serializable]
    private sealed class DeviceMetadata
    {
        public int schemaVersion;
        public string suite;
        public int processId;
        public string operatingSystem;
        public string processorType;
        public int processorCount;
        public int systemMemorySizeMb;
        public string graphicsDeviceName;
        public string graphicsDeviceVendor;
        public int graphicsDeviceVendorId;
        public int graphicsDeviceId;
        public string graphicsDeviceType;
        public string graphicsDeviceVersion;
        public int graphicsMemorySizeMb;
        public int graphicsShaderLevel;
        public bool graphicsMultiThreaded;
        public bool supportsComputeShaders;
        public bool supportsGraphicsFence;
        public bool supportsAsyncGpuReadback;
        public bool supportsWaveOperations;
        public bool frameTimingFeatureEnabled;
        public string deviceFingerprint;
        public string unityVersion;
    }
}
