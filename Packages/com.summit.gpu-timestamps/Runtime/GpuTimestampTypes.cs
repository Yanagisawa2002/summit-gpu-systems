using System;
using UnityEngine.Rendering;

namespace Summit.GpuTimestamps
{
    public enum GpuTimestampAvailability
    {
        Available = 1,
        UnsupportedPlatform = -1,
        UnsupportedGraphicsApi = -2,
        PluginNotFound = -3,
        EntryPointMissing = -4,
        AbiMismatch = -5,
        NativeUnsupported = -6,
        SessionAlreadyActive = -7,
        InitializationFailed = -8
    }

    public enum GpuTimestampStatus
    {
        Ready = 1,
        Pending = 0,
        Error = -1,
        InvalidArgument = -2,
        InvalidToken = -3,
        RingFull = -4,
        NotInitialized = -5,
        Unsupported = -6,
        DeviceLost = -7,
        CallbackError = -8,
        FrequencyUnavailable = -9,
        StaleManagedToken = -100,
        MalformedNativeResult = -101
    }

    [Flags]
    public enum GpuTimestampSampleFlags : uint
    {
        None = 0,
        EmptyScope = 1u << 0
    }

    public readonly struct GpuTimestampSupport
    {
        internal GpuTimestampSupport(
            GpuTimestampAvailability availability,
            string message,
            uint abiVersion,
            uint capabilityFlags,
            int ringCapacity,
            int rendererType,
            uint deviceGeneration,
            bool frequencyReady,
            ulong timestampFrequency)
        {
            Availability = availability;
            Message = message ?? string.Empty;
            AbiVersion = abiVersion;
            CapabilityFlags = capabilityFlags;
            RingCapacity = ringCapacity;
            RendererType = rendererType;
            DeviceGeneration = deviceGeneration;
            FrequencyReady = frequencyReady;
            TimestampFrequency = timestampFrequency;
        }

        public GpuTimestampAvailability Availability { get; }

        public bool IsAvailable => Availability == GpuTimestampAvailability.Available;

        public string Message { get; }

        public uint AbiVersion { get; }

        public uint CapabilityFlags { get; }

        public int RingCapacity { get; }

        public int RendererType { get; }

        public uint DeviceGeneration { get; }

        public bool FrequencyReady { get; }

        public ulong TimestampFrequency { get; }

        internal static GpuTimestampSupport Unavailable(
            GpuTimestampAvailability availability,
            string message)
        {
            return new GpuTimestampSupport(
                availability,
                message,
                0,
                0,
                0,
                0,
                0,
                false,
                0);
        }
    }

    public readonly struct GpuTimestampScope
    {
        private readonly int sessionId;
        private readonly IntPtr callback;
        private readonly IntPtr payload;
        private readonly int beginEventId;
        private readonly int endEventId;
        private readonly int completionEventId;

        internal GpuTimestampScope(
            int sessionId,
            int index,
            IntPtr callback,
            IntPtr payload,
            int beginEventId,
            int endEventId,
            int completionEventId)
        {
            this.sessionId = sessionId;
            Index = index;
            this.callback = callback;
            this.payload = payload;
            this.beginEventId = beginEventId;
            this.endEventId = endEventId;
            this.completionEventId = completionEventId;
        }

        public int Index { get; }

        public bool IsValid =>
            sessionId != 0 &&
            Index >= 0 &&
            callback != IntPtr.Zero &&
            payload != IntPtr.Zero;

        internal int SessionId => sessionId;

        internal IntPtr Payload => payload;

        public void RecordBegin(CommandBuffer commands)
        {
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (!IsValid)
            {
                throw new InvalidOperationException("GPU timestamp scope is not valid.");
            }
            commands.IssuePluginEventAndData(callback, beginEventId, payload);
        }

        public void RecordEnd(CommandBuffer commands)
        {
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (!IsValid)
            {
                throw new InvalidOperationException("GPU timestamp scope is not valid.");
            }
            commands.IssuePluginEventAndData(callback, endEventId, payload);
            commands.IssuePluginEventAndData(
                callback,
                completionEventId,
                payload);
        }
    }

    public readonly struct GpuTimestampToken
    {
        internal GpuTimestampToken(
            int sessionId,
            ulong value,
            ulong userTag,
            GpuTimestampSampleFlags flags,
            int scopeIndex,
            int sourceFrame)
        {
            SessionId = sessionId;
            Value = value;
            UserTag = userTag;
            Flags = flags;
            ScopeIndex = scopeIndex;
            SourceFrame = sourceFrame;
        }

        internal int SessionId { get; }

        public ulong Value { get; }

        public ulong UserTag { get; }

        public GpuTimestampSampleFlags Flags { get; }

        public int ScopeIndex { get; }

        public int SourceFrame { get; }

        public bool IsValid => SessionId != 0 && Value != 0 && ScopeIndex >= 0;
    }

    public readonly struct GpuTimestampResult
    {
        internal GpuTimestampResult(
            GpuTimestampToken token,
            int resultFrame,
            uint nativeFlags,
            ulong beginTicks,
            ulong endTicks,
            ulong elapsedTicks,
            ulong timestampFrequency,
            long elapsedNanoseconds,
            double elapsedMilliseconds,
            ulong fenceValue,
            uint deviceGeneration)
        {
            Token = token;
            ResultFrame = resultFrame;
            NativeFlags = nativeFlags;
            BeginTicks = beginTicks;
            EndTicks = endTicks;
            ElapsedTicks = elapsedTicks;
            TimestampFrequency = timestampFrequency;
            ElapsedNanoseconds = elapsedNanoseconds;
            ElapsedMilliseconds = elapsedMilliseconds;
            FenceValue = fenceValue;
            DeviceGeneration = deviceGeneration;
        }

        public GpuTimestampToken Token { get; }

        public int SourceFrame => Token.SourceFrame;

        public int ResultFrame { get; }

        public uint NativeFlags { get; }

        public ulong BeginTicks { get; }

        public ulong EndTicks { get; }

        public ulong ElapsedTicks { get; }

        public ulong TimestampFrequency { get; }

        public long ElapsedNanoseconds { get; }

        public double ElapsedMilliseconds { get; }

        public ulong FenceValue { get; }

        public uint DeviceGeneration { get; }
    }
}
