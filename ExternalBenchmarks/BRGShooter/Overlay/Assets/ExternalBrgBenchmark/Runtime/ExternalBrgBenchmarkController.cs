using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

public sealed class ExternalBrgBenchmarkController : MonoBehaviour
{
    private const string ShaderResource =
        "ExternalBrgBenchmark/ExternalBrgBenchmark";
    private const string UpstreamCommit =
        "f55f6e985bf73b0a3c23b95030e890874e552c45";
    private const int RenderTargetSize = 512;
    private const int BrgInstanceBytes = 7 * 16;
    private const int ViewCount = 1;
    private const int DrawGroupCount = 1;

    private enum BenchmarkPath
    {
        EngineBrg,
        GpuSystems,
    }

    private BenchmarkPath path;
    private string cell;
    private string outputPath;
    private string packageCommit;
    private int instanceCount;
    private int visiblePercent;
    private int dirtyPercent;
    private int warmupFrames;
    private int convergenceFrames;
    private int measuredFrames;
    private int completedFrames;
    private int measurementIndex;
    private int expectedVisibleCount;
    private bool measurementComplete;
    private bool disposed;
    private bool motionPending;
    private int dirtyStart;
    private int dirtyCount;

    private Camera benchmarkCamera;
    private RenderTexture renderTarget;
    private Mesh mesh;
    private Material material;
    private Vector3[] basePositions;
    private NativeArray<GpuInstanceState> states;
    private NativeArray<GpuInstanceDirtyRange> dirtyRanges;

    private global::BRG_Container brgContainer;
    private NativeArray<float4> brgSysmem;
    private int brgAlignedWindowBytes;

    private GpuDrivenInstancePipeline pipeline;
    private GpuInstanceStateUploader uploader;
    private GraphicsBuffer instanceBuffer;
    private GraphicsBuffer viewPlanes;
    private GraphicsBuffer viewParameters;
    private GraphicsBuffer drawTemplates;
    private GraphicsBuffer groupCounts;
    private GraphicsBuffer groupOffsets;
    private GraphicsBuffer groupedInstanceIndices;
    private GraphicsBuffer indirectArgumentWords;
    private GraphicsBuffer renderIndirectArguments;
    private GraphicsBuffer diagnostics;
    private MaterialPropertyBlock gpuProperties;
    private CommandBuffer commands;

    private double statePreparationMs;
    private double adapterCpuMs;
    private long logicalUploadBytes;
    private double[] adapterCpuSamples;
    private double[] frameCpuSamples;
    private double[] frameGpuSamples;
    private long[] uploadByteSamples;
    private bool[] frameCpuAvailable;
    private bool[] frameGpuAvailable;
    private readonly FrameTiming[] latestFrameTiming = new FrameTiming[1];

    private void Awake()
    {
        try
        {
            ParseCommandLine(Environment.GetCommandLineArgs());
            InitializeScene();
            RenderPipelineManager.endCameraRendering +=
                OnEndCameraRendering;
            FrameTimingManager.CaptureFrameTimings();
        }
        catch (Exception exception)
        {
            Fail("initialization", exception);
        }
    }

    private void LateUpdate()
    {
        if (measurementComplete || disposed)
        {
            return;
        }

        long start = Stopwatch.GetTimestamp();
        PrepareStateForFrame(completedFrames + 1);
        long afterState = Stopwatch.GetTimestamp();
        statePreparationMs = ElapsedMilliseconds(start, afterState);
        logicalUploadBytes = 0;

        if (path == BenchmarkPath.EngineBrg && motionPending)
        {
            brgContainer.UploadGpuData(instanceCount);
            logicalUploadBytes = checked((long)instanceCount * BrgInstanceBytes);
        }

        adapterCpuMs = ElapsedMilliseconds(start, Stopwatch.GetTimestamp());
    }

    private void OnEndCameraRendering(
        ScriptableRenderContext context,
        Camera renderedCamera)
    {
        if (measurementComplete || disposed ||
            renderedCamera != benchmarkCamera)
        {
            return;
        }

        try
        {
            if (path == BenchmarkPath.GpuSystems)
            {
                long submitStart = Stopwatch.GetTimestamp();
                RecordGpuFrame();
                Graphics.ExecuteCommandBuffer(commands);
                adapterCpuMs = statePreparationMs +
                    ElapsedMilliseconds(
                        submitStart,
                        Stopwatch.GetTimestamp());
            }

            completedFrames++;
            int measurementStart = checked(
                warmupFrames + convergenceFrames);
            if (completedFrames > measurementStart)
            {
                RecordMeasurement();
            }
            FrameTimingManager.CaptureFrameTimings();

            if (completedFrames >=
                checked(measurementStart + measuredFrames))
            {
                measurementComplete = true;
                RenderPipelineManager.endCameraRendering -=
                    OnEndCameraRendering;
                StartCoroutine(FinalizeAfterFrame());
            }
        }
        catch (Exception exception)
        {
            measurementComplete = true;
            RenderPipelineManager.endCameraRendering -=
                OnEndCameraRendering;
            Fail("measured-frame", exception);
        }
    }

    private void OnGUI()
    {
        if (renderTarget == null)
        {
            return;
        }
        GUI.DrawTexture(
            new Rect(0f, 0f, Screen.width, Screen.height),
            renderTarget,
            ScaleMode.ScaleToFit,
            false);
        GUI.Label(
            new Rect(12f, 12f, 620f, 24f),
            $"External BRG benchmark | {cell} | {PathLabel(path)}");
    }

    private void ParseCommandLine(string[] args)
    {
        if (!HasFlag(args, "-external-brg-benchmark"))
        {
            throw new InvalidOperationException(
                "The generated benchmark requires " +
                "-external-brg-benchmark.");
        }

        string pathValue = ReadString(
            args,
            "-external-brg-path",
            string.Empty);
        if (string.Equals(
                pathValue,
                "engine-brg",
                StringComparison.OrdinalIgnoreCase))
        {
            path = BenchmarkPath.EngineBrg;
        }
        else if (string.Equals(
                     pathValue,
                     "gpu-systems",
                     StringComparison.OrdinalIgnoreCase))
        {
            path = BenchmarkPath.GpuSystems;
        }
        else
        {
            throw new ArgumentOutOfRangeException(
                "-external-brg-path",
                pathValue,
                "Expected engine-brg or gpu-systems.");
        }

        cell = ReadString(args, "-external-brg-cell", "E1")
            .ToUpperInvariant();
        switch (cell)
        {
            case "U0":
                instanceCount = 3200 + 16384;
                visiblePercent = 100;
                dirtyPercent = 100;
                break;
            case "E1":
                instanceCount = 131072;
                visiblePercent = 25;
                dirtyPercent = 1;
                break;
            case "E2":
                instanceCount = 131072;
                visiblePercent = 25;
                dirtyPercent = 10;
                break;
            case "E3":
                instanceCount = 131072;
                visiblePercent = 100;
                dirtyPercent = 100;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    "-external-brg-cell",
                    cell,
                    "Expected U0, E1, E2, or E3.");
        }

        warmupFrames = ReadInt(
            args,
            "-external-brg-warmup-frames",
            60,
            1,
            10000);
        convergenceFrames = ReadInt(
            args,
            "-external-brg-convergence-frames",
            120,
            0,
            10000);
        measuredFrames = ReadInt(
            args,
            "-external-brg-measured-frames",
            900,
            1,
            100000);
        outputPath = Path.GetFullPath(ReadString(
            args,
            "-external-brg-output",
            Path.Combine(
                Application.persistentDataPath,
                "external-brg-result.json")));
        packageCommit = ReadString(
            args,
            "-gpu-systems-commit",
            "unavailable");

        adapterCpuSamples = new double[measuredFrames];
        frameCpuSamples = new double[measuredFrames];
        frameGpuSamples = new double[measuredFrames];
        uploadByteSamples = new long[measuredFrames];
        frameCpuAvailable = new bool[measuredFrames];
        frameGpuAvailable = new bool[measuredFrames];
    }

    private void InitializeScene()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Application.runInBackground = true;
        Screen.SetResolution(
            RenderTargetSize,
            RenderTargetSize,
            FullScreenMode.Windowed);

        renderTarget = new RenderTexture(
            RenderTargetSize,
            RenderTargetSize,
            24,
            RenderTextureFormat.ARGB32)
        {
            name = "External BRG Benchmark Target",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false
        };
        renderTarget.Create();

        GameObject cameraHost = new GameObject("External BRG Camera");
        benchmarkCamera = cameraHost.AddComponent<Camera>();
        benchmarkCamera.targetTexture = renderTarget;
        benchmarkCamera.clearFlags = CameraClearFlags.SolidColor;
        benchmarkCamera.backgroundColor = Color.black;
        benchmarkCamera.allowHDR = false;
        benchmarkCamera.allowMSAA = false;
        benchmarkCamera.useOcclusionCulling = false;
        benchmarkCamera.depthTextureMode = DepthTextureMode.None;
        benchmarkCamera.orthographic = true;
        benchmarkCamera.orthographicSize = 100f;
        benchmarkCamera.nearClipPlane = 0.1f;
        benchmarkCamera.farClipPlane = 400f;
        benchmarkCamera.transform.position = new Vector3(0f, 0f, -150f);
        benchmarkCamera.transform.rotation = Quaternion.identity;

        Shader shader = Resources.Load<Shader>(ShaderResource);
        if (shader == null)
        {
            throw new InvalidOperationException(
                "Missing shared benchmark shader: " + ShaderResource);
        }
        material = new Material(shader)
        {
            name = "External BRG Benchmark Material",
            enableInstancing = true
        };
        material.SetColor(
            "_BaseColor",
            new Color(0.10f, 0.80f, 0.95f, 1f));
        if (path == BenchmarkPath.GpuSystems)
        {
            material.EnableKeyword("GPU_SYSTEMS_PATH");
        }

        mesh = CreateCubeMesh();
        states = new NativeArray<GpuInstanceState>(
            instanceCount,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        dirtyRanges = new NativeArray<GpuInstanceDirtyRange>(
            1,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);
        basePositions = new Vector3[instanceCount];
        PopulateInitialState();

        if (path == BenchmarkPath.EngineBrg)
        {
            InitializeBrg();
        }
        else
        {
            InitializeGpuSystems();
        }
    }

    private void PopulateInitialState()
    {
        expectedVisibleCount = checked(
            (int)((long)instanceCount * visiblePercent / 100L));
        int columns = Mathf.CeilToInt(
            Mathf.Sqrt(Mathf.Max(1, expectedVisibleCount)));
        for (int index = 0; index < instanceCount; index++)
        {
            Vector3 position;
            if (index < expectedVisibleCount)
            {
                int column = index % columns;
                int row = index / columns;
                float x = columns <= 1
                    ? 0f
                    : Mathf.Lerp(-90f, 90f, column / (float)(columns - 1));
                int rows = Mathf.CeilToInt(
                    expectedVisibleCount / (float)columns);
                float y = rows <= 1
                    ? 0f
                    : Mathf.Lerp(-90f, 90f, row / (float)(rows - 1));
                float z = (index % 17) * 0.01f;
                position = new Vector3(x, y, z);
            }
            else
            {
                position = new Vector3(
                    1000f + (index % 1024),
                    (index % 181) - 90f,
                    0f);
            }
            basePositions[index] = position;
            states[index] = CreateState(index, position);
        }
    }

    private void InitializeBrg()
    {
        brgContainer = new global::BRG_Container();
        if (!brgContainer.Init(
                mesh,
                material,
                instanceCount,
                BrgInstanceBytes,
                false))
        {
            throw new InvalidOperationException(
                "The upstream BRG_Container rejected initialization.");
        }
        brgSysmem = brgContainer.GetSysmemBuffer(
            out _,
            out brgAlignedWindowBytes);
        for (int index = 0; index < instanceCount; index++)
        {
            WriteBrgRecord(index, basePositions[index]);
        }
        if (!brgContainer.UploadGpuData(instanceCount))
        {
            throw new InvalidOperationException(
                "The upstream BRG_Container rejected the initial upload.");
        }
    }

    private void InitializeGpuSystems()
    {
        instanceBuffer = CreateStructured(
            instanceCount,
            GpuInstanceState.Stride,
            "External BRG Instance States");
        viewPlanes = CreateStructured(
            GpuDrivenInstancePipeline.FrustumPlaneCount,
            sizeof(float) * 4,
            "External BRG View Planes");
        viewParameters = CreateStructured(
            ViewCount,
            sizeof(float) * 4,
            "External BRG View Parameters");
        drawTemplates = CreateStructured(
            DrawGroupCount,
            GpuDrawTemplate.Stride,
            "External BRG Draw Templates");
        groupCounts = CreateStructured(
            DrawGroupCount,
            sizeof(uint),
            "External BRG Group Counts");
        groupOffsets = CreateStructured(
            DrawGroupCount + 1,
            sizeof(uint),
            "External BRG Group Offsets");
        groupedInstanceIndices = CreateStructured(
            instanceCount,
            sizeof(uint),
            "External BRG Grouped Indices");
        indirectArgumentWords = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured |
            GraphicsBuffer.Target.IndirectArguments |
            GraphicsBuffer.Target.CopySource,
            GpuDrivenInstancePipeline.IndirectArgumentWordCount,
            sizeof(uint))
        {
            name = "External BRG Pipeline Indirect Words"
        };
        renderIndirectArguments = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments |
            GraphicsBuffer.Target.CopyDestination,
            1,
            GraphicsBuffer.IndirectDrawIndexedArgs.size)
        {
            name = "External BRG Engine Indirect Arguments"
        };
        diagnostics = CreateStructured(
            GpuDrivenInstancePipeline.DiagnosticWordCount,
            sizeof(uint),
            "External BRG Diagnostics");

        instanceBuffer.SetData(states);
        SetData(
            viewPlanes,
            new[]
            {
                new Vector4(1f, 0f, 0f, 100f),
                new Vector4(-1f, 0f, 0f, 100f),
                new Vector4(0f, 1f, 0f, 100f),
                new Vector4(0f, -1f, 0f, 100f),
                new Vector4(0f, 0f, 1f, 100f),
                new Vector4(0f, 0f, -1f, 100f),
            });
        SetData(
            viewParameters,
            new[] { new Vector4(0f, 0f, -150f, 1f) });
        SetData(
            drawTemplates,
            new[]
            {
                new GpuDrawTemplate(
                    mesh.GetIndexCount(0),
                    mesh.GetIndexStart(0),
                    checked((uint)mesh.GetBaseVertex(0)))
            });

        pipeline = new GpuDrivenInstancePipeline(
            instanceCount,
            ViewCount,
            DrawGroupCount,
            emitProfilerMarkers: true);
        uploader = new GpuInstanceStateUploader(1);
        gpuProperties = new MaterialPropertyBlock();
        gpuProperties.SetBuffer("_GpuInstanceStates", instanceBuffer);
        gpuProperties.SetBuffer(
            "_GpuGroupedInstanceIndices",
            groupedInstanceIndices);
        gpuProperties.SetBuffer("_GpuGroupOffsets", groupOffsets);
        gpuProperties.SetInt("_GpuBinIndex", 0);
        commands = new CommandBuffer
        {
            name = "External BRG gpu-systems frame"
        };
    }

    private void PrepareStateForFrame(int frameOrdinal)
    {
        dirtyCount = checked(
            (int)((long)instanceCount * dirtyPercent / 100L));
        if (dirtyCount == 0)
        {
            motionPending = false;
            return;
        }
        dirtyCount = Mathf.Max(1, dirtyCount);
        dirtyStart = dirtyCount >= instanceCount
            ? 0
            : checked((int)(
                (long)(frameOrdinal - 1) * dirtyCount %
                (instanceCount - dirtyCount + 1)));
        float offset = (frameOrdinal & 1) == 0 ? 0.125f : -0.125f;
        int end = checked(dirtyStart + dirtyCount);
        for (int index = dirtyStart; index < end; index++)
        {
            Vector3 position = basePositions[index];
            position.y += offset;
            states[index] = CreateState(index, position);
            if (path == BenchmarkPath.EngineBrg)
            {
                WriteBrgRecord(index, position);
            }
        }
        dirtyRanges[0] = new GpuInstanceDirtyRange(dirtyStart, dirtyCount);
        motionPending = true;
    }

    private void RecordGpuFrame()
    {
        commands.Clear();
        logicalUploadBytes = 0;
        if (motionPending)
        {
            GpuInstanceUploadReceipt uploadReceipt;
            if (dirtyCount >= instanceCount)
            {
                uploadReceipt = uploader.RecordFull(
                    commands,
                    instanceBuffer,
                    states,
                    instanceCount);
            }
            else
            {
                uploadReceipt = uploader.RecordDirty(
                    commands,
                    instanceBuffer,
                    states,
                    instanceCount,
                    dirtyRanges,
                    1);
            }
            logicalUploadBytes = uploadReceipt.LogicalUploadBytes;
        }

        pipeline.Record(
            commands,
            instanceBuffer,
            viewPlanes,
            viewParameters,
            drawTemplates,
            groupCounts,
            groupOffsets,
            groupedInstanceIndices,
            indirectArgumentWords,
            diagnostics,
            instanceCount,
            ViewCount,
            DrawGroupCount,
            GpuPrimitiveBackend.Auto,
            GpuDrivenInstanceOutputMode.VisibleOnly);
        commands.CopyBuffer(
            indirectArgumentWords,
            renderIndirectArguments);
        commands.SetRenderTarget(renderTarget);
        commands.SetViewport(
            new Rect(0f, 0f, RenderTargetSize, RenderTargetSize));
        commands.ClearRenderTarget(true, true, Color.black);
        commands.SetViewProjectionMatrices(
            benchmarkCamera.worldToCameraMatrix,
            GL.GetGPUProjectionMatrix(
                benchmarkCamera.projectionMatrix,
                true));
        commands.DrawMeshInstancedIndirect(
            mesh,
            0,
            material,
            0,
            renderIndirectArguments,
            0,
            gpuProperties);
    }

    private void RecordMeasurement()
    {
        if (measurementIndex >= measuredFrames)
        {
            throw new InvalidOperationException(
                "Measured-frame accounting exceeded the frozen count.");
        }
        int index = measurementIndex++;
        adapterCpuSamples[index] = adapterCpuMs;
        uploadByteSamples[index] = logicalUploadBytes;

        uint timingCount = FrameTimingManager.GetLatestTimings(
            1,
            latestFrameTiming);
        if (timingCount > 0)
        {
            FrameTiming timing = latestFrameTiming[0];
            if (timing.cpuFrameTime > 0d &&
                !double.IsNaN(timing.cpuFrameTime) &&
                !double.IsInfinity(timing.cpuFrameTime))
            {
                frameCpuSamples[index] = timing.cpuFrameTime;
                frameCpuAvailable[index] = true;
            }
            if (timing.gpuFrameTime > 0d &&
                !double.IsNaN(timing.gpuFrameTime) &&
                !double.IsInfinity(timing.gpuFrameTime))
            {
                frameGpuSamples[index] = timing.gpuFrameTime;
                frameGpuAvailable[index] = true;
            }
        }
    }

    private IEnumerator FinalizeAfterFrame()
    {
        yield return new WaitForEndOfFrame();

        uint observedVisible = uint.MaxValue;
        uint diagnosticViolations = 0;
        uint diagnosticFlags = 0;
        if (path == BenchmarkPath.GpuSystems)
        {
            var countData = new uint[1];
            var diagnosticData = new uint[
                GpuDrivenInstancePipeline.DiagnosticWordCount];
            groupCounts.GetData(countData);
            diagnostics.GetData(diagnosticData);
            observedVisible = countData[0];
            diagnosticViolations = diagnosticData[
                GpuDrivenInstancePipeline.ContractViolationCountWord];
            diagnosticFlags = diagnosticData[
                GpuDrivenInstancePipeline.ErrorFlagsWord];
        }

        Texture2D image = new Texture2D(
            RenderTargetSize,
            RenderTargetSize,
            TextureFormat.RGBA32,
            false,
            true);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = renderTarget;
        image.ReadPixels(
            new Rect(0f, 0f, RenderTargetSize, RenderTargetSize),
            0,
            0,
            false);
        image.Apply(false, false);
        RenderTexture.active = previous;

        NativeArray<byte> pixels = image.GetRawTextureData<byte>();
        string imageHash = HashBytes(pixels);
        bool imageContainsGeometry = HasNonBlackPixel(pixels);
        string pngPath = Path.ChangeExtension(outputPath, ".png");
        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath) ?? ".");
        File.WriteAllBytes(pngPath, image.EncodeToPNG());
        Destroy(image);

        int cpuCoverage = CountTrue(frameCpuAvailable);
        int gpuCoverage = CountTrue(frameGpuAvailable);
        bool candidateCorrect = path != BenchmarkPath.GpuSystems ||
            (observedVisible == checked((uint)expectedVisibleCount) &&
             diagnosticViolations == 0u &&
             diagnosticFlags == 0u);
        bool passed = measurementIndex == measuredFrames &&
            imageContainsGeometry && candidateCorrect;

        var receipt = new BenchmarkReceipt
        {
            schemaVersion = 1,
            suite = "gpu-systems.external-brg-shooter.run",
            accepted = passed,
            upstreamCommit = UpstreamCommit,
            packageCommit = packageCommit,
            unityVersion = Application.unityVersion,
            graphicsDeviceName = SystemInfo.graphicsDeviceName,
            graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
            graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
            graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
            operatingSystem = SystemInfo.operatingSystem,
            path = PathLabel(path),
            cell = cell,
            instanceCount = instanceCount,
            visiblePercent = visiblePercent,
            dirtyPercent = dirtyPercent,
            expectedVisibleCount = expectedVisibleCount,
            observedVisibleCount = path == BenchmarkPath.GpuSystems
                ? observedVisible.ToString(CultureInfo.InvariantCulture)
                : "unavailable",
            diagnosticViolationCount = diagnosticViolations,
            diagnosticFlags = diagnosticFlags,
            warmupFrames = warmupFrames,
            convergenceFrames = convergenceFrames,
            measuredFrames = measuredFrames,
            adapterCpuP50Ms = FormatMetric(
                Percentile(adapterCpuSamples, 0.50d)),
            adapterCpuP95Ms = FormatMetric(
                Percentile(adapterCpuSamples, 0.95d)),
            adapterCpuP99Ms = FormatMetric(
                Percentile(adapterCpuSamples, 0.99d)),
            frameCpuP50Ms = FormatAvailableMetric(
                frameCpuSamples,
                frameCpuAvailable,
                0.50d),
            frameCpuP95Ms = FormatAvailableMetric(
                frameCpuSamples,
                frameCpuAvailable,
                0.95d),
            frameCpuP99Ms = FormatAvailableMetric(
                frameCpuSamples,
                frameCpuAvailable,
                0.99d),
            frameGpuP50Ms = FormatAvailableMetric(
                frameGpuSamples,
                frameGpuAvailable,
                0.50d),
            frameGpuP95Ms = FormatAvailableMetric(
                frameGpuSamples,
                frameGpuAvailable,
                0.95d),
            frameGpuP99Ms = FormatAvailableMetric(
                frameGpuSamples,
                frameGpuAvailable,
                0.99d),
            frameCpuCoverage = cpuCoverage / (double)measuredFrames,
            frameGpuCoverage = gpuCoverage / (double)measuredFrames,
            uploadBytesP50 = FormatMetric(
                Percentile(uploadByteSamples, 0.50d)),
            uploadBytesP95 = FormatMetric(
                Percentile(uploadByteSamples, 0.95d)),
            imageHash = imageHash,
            imageContainsGeometry = imageContainsGeometry,
            imagePath = pngPath,
            stateHash = HashState(),
            rawCsvPath = Path.ChangeExtension(outputPath, ".csv"),
            evidenceBoundary =
                "adapterCpu excludes the opaque engine BRG callback; " +
                "frame CPU/GPU are whole-frame metrics; E1-E3 are " +
                "generated extensions, not the unmodified game."
        };

        WriteRawCsv(receipt.rawCsvPath);
        WriteJsonAtomic(outputPath, JsonUtility.ToJson(receipt, true));
        Debug.Log(
            "EXTERNAL_BRG_RESULT " +
            JsonUtility.ToJson(receipt, false));
        Application.Quit(passed ? 0 : 2);
    }

    private void WriteBrgRecord(int index, Vector3 position)
    {
        int maxPerWindow = brgAlignedWindowBytes / BrgInstanceBytes;
        int window = Math.DivRem(index, maxPerWindow, out int localIndex);
        int windowOffset = checked(window * brgAlignedWindowBytes / 16);
        int objectOffset = checked(windowOffset + localIndex * 3);
        int inverseOffset = checked(
            windowOffset + maxPerWindow * 3 + localIndex * 3);
        int colorOffset = checked(
            windowOffset + maxPerWindow * 6 + localIndex);

        brgSysmem[objectOffset + 0] = new float4(1f, 0f, 0f, 0f);
        brgSysmem[objectOffset + 1] = new float4(1f, 0f, 0f, 0f);
        brgSysmem[objectOffset + 2] = new float4(
            1f,
            position.x,
            position.y,
            position.z);
        brgSysmem[inverseOffset + 0] = new float4(1f, 0f, 0f, 0f);
        brgSysmem[inverseOffset + 1] = new float4(1f, 0f, 0f, 0f);
        brgSysmem[inverseOffset + 2] = new float4(
            1f,
            -position.x,
            -position.y,
            -position.z);
        brgSysmem[colorOffset] = new float4(0.10f, 0.80f, 0.95f, 1f);
    }

    private static GpuInstanceState CreateState(int index, Vector3 position)
    {
        return new GpuInstanceState(
            position,
            0.5f,
            new Vector4(4096f, 0f, 0f, 0f),
            checked((uint)index),
            0u,
            1u,
            1u);
    }

    private static Mesh CreateCubeMesh()
    {
        var result = new Mesh
        {
            name = "External BRG Procedural Cube",
            indexFormat = IndexFormat.UInt16
        };
        result.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f,  0.5f,  0.5f)
        };
        result.triangles = new[]
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            2, 3, 7, 2, 7, 6,
            0, 4, 7, 0, 7, 3,
            1, 2, 6, 1, 6, 5
        };
        result.bounds = new Bounds(Vector3.zero, Vector3.one);
        result.UploadMeshData(true);
        return result;
    }

    private static GraphicsBuffer CreateStructured(
        int count,
        int stride,
        string name)
    {
        return new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            count,
            stride)
        {
            name = name
        };
    }

    private static void SetData<T>(GraphicsBuffer buffer, T[] values)
        where T : struct
    {
        var data = new NativeArray<T>(values, Allocator.Temp);
        try
        {
            buffer.SetData(data);
        }
        finally
        {
            data.Dispose();
        }
    }

    private void WriteRawCsv(string pathValue)
    {
        var text = new StringBuilder();
        text.AppendLine(
            "frame,adapter_cpu_ms,frame_cpu_ms,frame_gpu_ms," +
            "logical_upload_bytes");
        for (int index = 0; index < measuredFrames; index++)
        {
            text.Append(index.ToString(CultureInfo.InvariantCulture));
            text.Append(',');
            text.Append(FormatMetric(adapterCpuSamples[index]));
            text.Append(',');
            if (frameCpuAvailable[index])
            {
                text.Append(FormatMetric(frameCpuSamples[index]));
            }
            text.Append(',');
            if (frameGpuAvailable[index])
            {
                text.Append(FormatMetric(frameGpuSamples[index]));
            }
            text.Append(',');
            text.Append(uploadByteSamples[index].ToString(
                CultureInfo.InvariantCulture));
            text.AppendLine();
        }
        File.WriteAllText(pathValue, text.ToString(), new UTF8Encoding(false));
    }

    private string HashState()
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        for (int index = 0; index < states.Length; index++)
        {
            Vector4 value = states[index].PositionRadius;
            hash = (hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(
                value.x))) * prime;
            hash = (hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(
                value.y))) * prime;
            hash = (hash ^ unchecked((uint)BitConverter.SingleToInt32Bits(
                value.z))) * prime;
            hash = (hash ^ states[index].ApplicationId) * prime;
        }
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static string HashBytes(NativeArray<byte> bytes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        for (int index = 0; index < bytes.Length; index++)
        {
            hash = (hash ^ bytes[index]) * prime;
        }
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static bool HasNonBlackPixel(NativeArray<byte> bytes)
    {
        for (int index = 0; index + 3 < bytes.Length; index += 4)
        {
            if (bytes[index] != 0 ||
                bytes[index + 1] != 0 ||
                bytes[index + 2] != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static int CountTrue(bool[] values)
    {
        int count = 0;
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index])
            {
                count++;
            }
        }
        return count;
    }

    private static string FormatAvailableMetric(
        double[] values,
        bool[] available,
        double percentile)
    {
        int count = CountTrue(available);
        if (count == 0)
        {
            return "unavailable";
        }
        var compact = new double[count];
        int target = 0;
        for (int index = 0; index < values.Length; index++)
        {
            if (available[index])
            {
                compact[target++] = values[index];
            }
        }
        return FormatMetric(Percentile(compact, percentile));
    }

    private static double Percentile(double[] source, double percentile)
    {
        var values = (double[])source.Clone();
        Array.Sort(values);
        int index = Mathf.Clamp(
            Mathf.CeilToInt((float)(percentile * values.Length)) - 1,
            0,
            values.Length - 1);
        return values[index];
    }

    private static double Percentile(long[] source, double percentile)
    {
        var values = (long[])source.Clone();
        Array.Sort(values);
        int index = Mathf.Clamp(
            Mathf.CeilToInt((float)(percentile * values.Length)) - 1,
            0,
            values.Length - 1);
        return values[index];
    }

    private static string FormatMetric(double value)
    {
        return value.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static double ElapsedMilliseconds(long start, long end)
    {
        return (end - start) * 1000d / Stopwatch.Frequency;
    }

    private static string PathLabel(BenchmarkPath value)
    {
        return value == BenchmarkPath.EngineBrg
            ? "engine-brg"
            : "gpu-systems";
    }

    private static bool HasFlag(string[] args, string name)
    {
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
        for (int index = 0; index < args.Length - 1; index++)
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
        int fallback,
        int minimum,
        int maximum)
    {
        string raw = ReadString(
            args,
            name,
            fallback.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value) ||
            value < minimum ||
            value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                raw,
                $"Expected an integer in [{minimum}, {maximum}].");
        }
        return value;
    }

    private static void WriteJsonAtomic(string pathValue, string json)
    {
        string directory = Path.GetDirectoryName(pathValue) ?? ".";
        Directory.CreateDirectory(directory);
        string temporary = pathValue + ".partial";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        if (File.Exists(pathValue))
        {
            File.Delete(pathValue);
        }
        File.Move(temporary, pathValue);
    }

    private void Fail(string stage, Exception exception)
    {
        Debug.LogException(exception);
        try
        {
            string failurePath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(
                    Application.persistentDataPath,
                    "external-brg-failure.txt")
                : outputPath + ".failure.txt";
            Directory.CreateDirectory(
                Path.GetDirectoryName(failurePath) ?? ".");
            File.WriteAllText(
                failurePath,
                stage + Environment.NewLine + exception,
                new UTF8Encoding(false));
        }
        catch (Exception writeException)
        {
            Debug.LogException(writeException);
        }
        Application.Quit(3);
    }

    private void OnDestroy()
    {
        DisposeResources();
    }

    private void DisposeResources()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        commands?.Dispose();
        uploader?.Dispose();
        pipeline?.Dispose();
        diagnostics?.Dispose();
        renderIndirectArguments?.Dispose();
        indirectArgumentWords?.Dispose();
        groupedInstanceIndices?.Dispose();
        groupOffsets?.Dispose();
        groupCounts?.Dispose();
        drawTemplates?.Dispose();
        viewParameters?.Dispose();
        viewPlanes?.Dispose();
        instanceBuffer?.Dispose();
        if (brgContainer != null)
        {
            brgContainer.Shutdown();
        }
        if (dirtyRanges.IsCreated)
        {
            dirtyRanges.Dispose();
        }
        if (states.IsCreated)
        {
            states.Dispose();
        }
        if (renderTarget != null)
        {
            renderTarget.Release();
            Destroy(renderTarget);
        }
        if (material != null)
        {
            Destroy(material);
        }
        if (mesh != null)
        {
            Destroy(mesh);
        }
    }

    [Serializable]
    private sealed class BenchmarkReceipt
    {
        public int schemaVersion;
        public string suite;
        public bool accepted;
        public string upstreamCommit;
        public string packageCommit;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceVendor;
        public string graphicsDeviceVersion;
        public string graphicsDeviceType;
        public string operatingSystem;
        public string path;
        public string cell;
        public int instanceCount;
        public int visiblePercent;
        public int dirtyPercent;
        public int expectedVisibleCount;
        public string observedVisibleCount;
        public uint diagnosticViolationCount;
        public uint diagnosticFlags;
        public int warmupFrames;
        public int convergenceFrames;
        public int measuredFrames;
        public string adapterCpuP50Ms;
        public string adapterCpuP95Ms;
        public string adapterCpuP99Ms;
        public string frameCpuP50Ms;
        public string frameCpuP95Ms;
        public string frameCpuP99Ms;
        public string frameGpuP50Ms;
        public string frameGpuP95Ms;
        public string frameGpuP99Ms;
        public double frameCpuCoverage;
        public double frameGpuCoverage;
        public string uploadBytesP50;
        public string uploadBytesP95;
        public string imageHash;
        public bool imageContainsGeometry;
        public string imagePath;
        public string stateHash;
        public string rawCsvPath;
        public string evidenceBoundary;
    }
}
