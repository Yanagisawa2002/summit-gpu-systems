using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Human-facing side-by-side preview of the engine-native CPU reference and
/// GPU-driven indirect path. This controller never writes benchmark timings;
/// formal measurement remains in GpuDrivenInstanceMacrobenchmarkController.
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class GpuSystemsShowcaseController : MonoBehaviour
{
    private const string InstanceCountArgument =
        "-gpu-systems-showcase-instance-count";
    private const string ViewCountArgument =
        "-gpu-systems-showcase-view-count";
    private const string VisibilityArgument =
        "-gpu-systems-showcase-visibility";
    private const string SeedArgument = "-gpu-systems-showcase-seed";
    private const string WarmupArgument =
        "-gpu-systems-showcase-warmup-frames";
    private const string ScreenshotArgument =
        "-gpu-systems-showcase-screenshot";
    private const string FrameDirectoryArgument =
        "-gpu-systems-showcase-frame-dir";
    private const string FrameCountArgument =
        "-gpu-systems-showcase-frame-count";
    private const string FrameIntervalArgument =
        "-gpu-systems-showcase-frame-interval";
    private const string ReceiptArgument =
        "-gpu-systems-showcase-receipt";
    private const string BuildCommitArgument =
        "-gpu-systems-showcase-build-commit";

    private GpuDrivenInstanceMacrobenchmarkAdapter baselineAdapter;
    private GpuDrivenInstanceMacrobenchmarkAdapter selectedAdapter;
    private CommandBuffer baselineCommands;
    private CommandBuffer selectedCommands;
    private AsyncGPUReadbackRequest baselineReadback;
    private AsyncGPUReadbackRequest selectedReadback;
    private readonly List<string> requestedFramePaths = new List<string>();

    private int instanceCount = 65536;
    private int viewCount = 4;
    private string visibility = "visible25";
    private int seed = 20260831;
    private int warmupFrames = 60;
    private int requestedFrameCount;
    private int frameInterval = 3;
    private string screenshotPath = string.Empty;
    private string frameDirectory = string.Empty;
    private string receiptPath = string.Empty;
    private string buildCommit = "unknown";

    private bool initialized;
    private bool readbackRequested;
    private bool readbackComplete;
    private bool imageParity;
    private bool screenshotRequested;
    private bool finished;
    private int showcaseFrame;
    private string baselineImageSha256 = string.Empty;
    private string selectedImageSha256 = string.Empty;
    private string failure = string.Empty;
    private GUIStyle titleStyle;
    private GUIStyle panelStyle;
    private GUIStyle bodyStyle;
    private GUIStyle footerStyle;

    private void Awake()
    {
        Configure(Environment.GetCommandLineArgs());
    }

    private void Start()
    {
        try
        {
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;

            baselineAdapter = new GpuDrivenInstanceMacrobenchmarkAdapter(
                instanceCount,
                viewCount,
                visibility,
                seed);
            selectedAdapter = new GpuDrivenInstanceMacrobenchmarkAdapter(
                instanceCount,
                viewCount,
                visibility,
                seed);
            baselineAdapter.PresentationCamera.enabled = true;
            selectedAdapter.PresentationCamera.enabled = false;
            baselineCommands = new CommandBuffer
            {
                name = "GPU Systems Showcase / CPU Reference"
            };
            selectedCommands = new CommandBuffer
            {
                name = "GPU Systems Showcase / GPU Driven"
            };
            initialized = true;
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Debug.LogException(exception);
            Finish(false);
        }
    }

    private void Update()
    {
        if (!initialized || finished)
        {
            return;
        }

        try
        {
            baselineAdapter.CullAndPackCpu();
            baselineCommands.Clear();
            baselineAdapter.RecordCpuDraws(baselineCommands);
            Graphics.ExecuteCommandBuffer(baselineCommands);

            selectedCommands.Clear();
            selectedAdapter.RecordGpuPipelineAndDraws(selectedCommands);
            Graphics.ExecuteCommandBuffer(selectedCommands);
            showcaseFrame++;

            if (showcaseFrame >= warmupFrames && !readbackRequested)
            {
                baselineReadback = AsyncGPUReadback.Request(
                    baselineAdapter.RenderTarget,
                    0);
                selectedReadback = AsyncGPUReadback.Request(
                    selectedAdapter.RenderTarget,
                    0);
                readbackRequested = true;
            }
            CompleteReadbackWhenReady();
            CaptureWhenReady();
            FinishWhenCaptureFilesExist();
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Debug.LogException(exception);
            Finish(false);
        }
    }

    private void CompleteReadbackWhenReady()
    {
        if (!readbackRequested || readbackComplete ||
            !baselineReadback.done || !selectedReadback.done)
        {
            return;
        }
        if (baselineReadback.hasError || selectedReadback.hasError)
        {
            throw new InvalidOperationException(
                "Showcase image-parity readback failed.");
        }
        NativeArray<byte> baseline = baselineReadback.GetData<byte>();
        NativeArray<byte> selected = selectedReadback.GetData<byte>();
        baselineImageSha256 = HashBytes(baseline);
        selectedImageSha256 = HashBytes(selected);
        imageParity = string.Equals(
            baselineImageSha256,
            selectedImageSha256,
            StringComparison.Ordinal);
        readbackComplete = true;
        if (!imageParity)
        {
            throw new InvalidOperationException(
                "CPU reference and GPU-driven preview images differ.");
        }
    }

    private void CaptureWhenReady()
    {
        if (!readbackComplete)
        {
            return;
        }
        if (!screenshotRequested && !string.IsNullOrWhiteSpace(screenshotPath))
        {
            EnsureParentDirectory(screenshotPath);
            ScreenCapture.CaptureScreenshot(screenshotPath, 1);
            screenshotRequested = true;
            // Unity accepts only one ScreenCapture request reliably per frame.
            // Keep the poster and video-frame queues explicitly serialized.
            return;
        }
        if (screenshotRequested && !IsNonEmptyFile(screenshotPath))
        {
            return;
        }
        if (requestedFramePaths.Count > 0 &&
            !IsNonEmptyFile(requestedFramePaths[requestedFramePaths.Count - 1]))
        {
            return;
        }
        if (requestedFramePaths.Count < requestedFrameCount &&
            showcaseFrame % frameInterval == 0)
        {
            Directory.CreateDirectory(frameDirectory);
            string path = Path.Combine(
                frameDirectory,
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "frame-{0:D4}.png",
                    requestedFramePaths.Count));
            ScreenCapture.CaptureScreenshot(path, 1);
            requestedFramePaths.Add(path);
        }
    }

    private void FinishWhenCaptureFilesExist()
    {
        bool captureRequested =
            !string.IsNullOrWhiteSpace(screenshotPath) ||
            requestedFrameCount > 0;
        if (!captureRequested || !readbackComplete)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(screenshotPath) &&
            !IsNonEmptyFile(screenshotPath))
        {
            return;
        }
        if (requestedFramePaths.Count < requestedFrameCount)
        {
            return;
        }
        for (int i = 0; i < requestedFramePaths.Count; i++)
        {
            if (!IsNonEmptyFile(requestedFramePaths[i]))
            {
                return;
            }
        }
        Finish(true);
    }

    private void OnGUI()
    {
        if (!initialized)
        {
            return;
        }
        EnsureStyles();
        Rect header = new Rect(24f, 18f, Screen.width - 48f, 64f);
        Rect footer = new Rect(
            24f,
            Screen.height - 58f,
            Screen.width - 48f,
            42f);
        float panelTop = 94f;
        float panelBottom = 72f;
        float gutter = 24f;
        float availableWidth = Screen.width - 48f - gutter;
        float availableHeight = Screen.height - panelTop - panelBottom;
        float panelWidth = availableWidth * 0.5f;
        float imageSize = Mathf.Max(
            1f,
            Mathf.Min(panelWidth, availableHeight));
        float imageYOffset = (availableHeight - imageSize) * 0.5f;
        Rect left = new Rect(
            24f + (panelWidth - imageSize) * 0.5f,
            panelTop + imageYOffset,
            imageSize,
            imageSize);
        Rect right = new Rect(
            24f + panelWidth + gutter + (panelWidth - imageSize) * 0.5f,
            panelTop + imageYOffset,
            imageSize,
            imageSize);

        GUI.DrawTexture(
            new Rect(0f, 0f, Screen.width, Screen.height),
            Texture2D.blackTexture,
            ScaleMode.StretchToFill);
        GUI.DrawTexture(left, baselineAdapter.RenderTarget, ScaleMode.StretchToFill);
        GUI.DrawTexture(right, selectedAdapter.RenderTarget, ScaleMode.StretchToFill);

        bool selectedActive = requestedFrameCount > 0
            ? (requestedFramePaths.Count / 8) % 2 != 0
            : (showcaseFrame / 90) % 2 != 0;
        DrawBorder(left, selectedActive ? new Color(0.35f, 0.42f, 0.5f) :
            new Color(1f, 0.55f, 0.2f), selectedActive ? 2f : 5f);
        DrawBorder(right, selectedActive ? new Color(0.2f, 0.85f, 1f) :
            new Color(0.35f, 0.42f, 0.5f), selectedActive ? 5f : 2f);

        GUI.Label(
            header,
            "GPU SYSTEMS TOOLKIT  /  SAME INPUT, TWO EXECUTION PATHS",
            titleStyle);
        GUI.Label(
            new Rect(left.x, left.y + 12f, left.width, 42f),
            "CPU REFERENCE",
            panelStyle);
        GUI.Label(
            new Rect(right.x, right.y + 12f, right.width, 42f),
            "GPU-DRIVEN SELECTED",
            panelStyle);
        GUI.Label(
            new Rect(left.x + 20f, left.yMax - 72f, left.width - 40f, 58f),
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Engine-native batches: {0}\nCPU cull + pack + draw submission",
                baselineAdapter.CpuDrawCallCount),
            bodyStyle);
        GUI.Label(
            new Rect(right.x + 20f, right.yMax - 72f, right.width - 40f, 58f),
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Indirect commands: {0}\nGPU visibility + CSR + indirect args",
                selectedAdapter.GpuLogicalDrawCommandCount),
            bodyStyle);
        GUI.Label(
            footer,
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "PREVIEW ONLY — NOT FORMAL TIMING  |  {0:N0} instances × {1} views  |  {2}  |  image parity: {3}",
                instanceCount,
                viewCount,
                visibility,
                readbackComplete ? (imageParity ? "PASS" : "FAIL") : "checking"),
            footerStyle);
    }

    private void OnDisable()
    {
        DisposeResources();
    }

    private void Configure(string[] args)
    {
        instanceCount = Mathf.Clamp(
            ReadInt(args, InstanceCountArgument, instanceCount),
            1,
            16776960);
        viewCount = Mathf.Clamp(
            ReadInt(args, ViewCountArgument, viewCount),
            1,
            Summit.GpuDrivenInstances.GpuDrivenInstancePipeline
                .MaximumViewCount);
        visibility = ReadString(args, VisibilityArgument, visibility);
        seed = ReadInt(args, SeedArgument, seed);
        warmupFrames = Mathf.Clamp(
            ReadInt(args, WarmupArgument, warmupFrames),
            5,
            600);
        screenshotPath = NormalizeOptionalPath(
            ReadString(args, ScreenshotArgument, string.Empty));
        frameDirectory = NormalizeOptionalPath(
            ReadString(args, FrameDirectoryArgument, string.Empty));
        requestedFrameCount = Mathf.Clamp(
            ReadInt(args, FrameCountArgument, 0),
            0,
            300);
        frameInterval = Mathf.Clamp(
            ReadInt(args, FrameIntervalArgument, frameInterval),
            1,
            60);
        receiptPath = NormalizeOptionalPath(
            ReadString(args, ReceiptArgument, string.Empty));
        buildCommit = ReadString(args, BuildCommitArgument, buildCommit);
        if (requestedFrameCount > 0 && string.IsNullOrWhiteSpace(frameDirectory))
        {
            throw new ArgumentException(
                "Frame capture count requires a frame directory.");
        }
    }

    private void Finish(bool accepted)
    {
        if (finished)
        {
            return;
        }
        finished = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(receiptPath))
            {
                EnsureParentDirectory(receiptPath);
                var receipt = new ShowcaseReceipt
                {
                    schemaVersion = 1,
                    suite = "gpu-systems-toolkit.visible-showcase",
                    accepted = accepted && imageParity,
                    formalTiming = false,
                    timingClaimsAllowed = false,
                    buildCommit = buildCommit,
                    unityVersion = Application.unityVersion,
                    graphicsDeviceName = SystemInfo.graphicsDeviceName,
                    graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
                    graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                    instanceCount = instanceCount,
                    viewCount = viewCount,
                    visibility = visibility,
                    seed = seed,
                    visiblePairCount = baselineAdapter?.VisiblePairCount ?? 0,
                    baselineDrawCalls = baselineAdapter?.CpuDrawCallCount ?? 0,
                    selectedIndirectCommands =
                        selectedAdapter?.GpuLogicalDrawCommandCount ?? 0,
                    imageParity = imageParity,
                    baselineImageSha256 = baselineImageSha256,
                    selectedImageSha256 = selectedImageSha256,
                    screenshotPath = screenshotPath,
                    screenshotSha256 = IsNonEmptyFile(screenshotPath)
                        ? HashFile(screenshotPath)
                        : string.Empty,
                    frameDirectory = frameDirectory,
                    requestedFrameCount = requestedFrameCount,
                    writtenFrameCount = CountNonEmptyFiles(requestedFramePaths),
                    failure = failure,
                    generatedUtc = DateTime.UtcNow.ToString("O"),
                    evidenceBoundary =
                        "Human-facing preview only. It validates identical " +
                        "offscreen images for the same procedural input and " +
                        "must not be used as formal timing evidence."
                };
                File.WriteAllText(
                    receiptPath,
                    JsonUtility.ToJson(receipt, true));
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            accepted = false;
        }
        finally
        {
            DisposeResources();
            if (!string.IsNullOrWhiteSpace(receiptPath))
            {
                Application.Quit(accepted && imageParity ? 0 : 2);
            }
        }
    }

    private void DisposeResources()
    {
        initialized = false;
        baselineCommands?.Dispose();
        baselineCommands = null;
        selectedCommands?.Dispose();
        selectedCommands = null;
        baselineAdapter?.Dispose();
        baselineAdapter = null;
        selectedAdapter?.Dispose();
        selectedAdapter = null;
    }

    private void EnsureStyles()
    {
        if (titleStyle != null)
        {
            return;
        }
        titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Clamp(Screen.height / 30, 20, 34),
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        panelStyle = new GUIStyle(titleStyle)
        {
            fontSize = Mathf.Clamp(Screen.height / 38, 18, 28)
        };
        bodyStyle = new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Clamp(Screen.height / 62, 13, 18),
            normal = { textColor = Color.white }
        };
        footerStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Clamp(Screen.height / 58, 13, 18),
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.78f, 0.84f, 0.9f) }
        };
    }

    private static void DrawBorder(Rect rect, Color color, float width)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, width),
            Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - width, rect.width, width),
            Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.y, width, rect.height),
            Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMax - width, rect.y, width, rect.height),
            Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private static string HashBytes(NativeArray<byte> bytes)
    {
        using (SHA256 algorithm = SHA256.Create())
        {
            return ToHex(algorithm.ComputeHash(bytes.ToArray()));
        }
    }

    private static string HashFile(string path)
    {
        using (SHA256 algorithm = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            return ToHex(algorithm.ComputeHash(stream));
        }
    }

    private static string ToHex(byte[] bytes)
    {
        var result = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            result.Append(bytes[i].ToString("X2"));
        }
        return result.ToString();
    }

    private static bool IsNonEmptyFile(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            File.Exists(path) &&
            new FileInfo(path).Length > 0L;
    }

    private static int CountNonEmptyFiles(IReadOnlyList<string> paths)
    {
        int count = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            if (IsNonEmptyFile(paths[i]))
            {
                count++;
            }
        }
        return count;
    }

    private static void EnsureParentDirectory(string path)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static string NormalizeOptionalPath(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Path.GetFullPath(value);
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        string value = ReadString(args, name, string.Empty);
        return int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed)
                ? parsed
                : fallback;
    }

    private static string ReadString(
        string[] args,
        string name,
        string fallback)
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

    [Serializable]
    private sealed class ShowcaseReceipt
    {
        public int schemaVersion;
        public string suite;
        public bool accepted;
        public bool formalTiming;
        public bool timingClaimsAllowed;
        public string buildCommit;
        public string unityVersion;
        public string graphicsDeviceName;
        public string graphicsDeviceVendor;
        public string graphicsApi;
        public int instanceCount;
        public int viewCount;
        public string visibility;
        public int seed;
        public int visiblePairCount;
        public int baselineDrawCalls;
        public int selectedIndirectCommands;
        public bool imageParity;
        public string baselineImageSha256;
        public string selectedImageSha256;
        public string screenshotPath;
        public string screenshotSha256;
        public string frameDirectory;
        public int requestedFrameCount;
        public int writtenFrameCount;
        public string failure;
        public string generatedUtc;
        public string evidenceBoundary;
    }
}
