using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuTimestamps
{
    public sealed class GpuTimestampSession : IDisposable
    {
        public const uint AbiVersion = 2;

        private static readonly uint NativeSupportInfoSize =
            (uint)Marshal.SizeOf<NativeSupportInfo>();
        private const uint RequiredCapabilityFlags = 0x1Fu;
        private static readonly uint NativeResultSize =
            (uint)Marshal.SizeOf<NativeTimestampResult>();
        private static int activePublicSession;
        private static int nextSessionId;

        private readonly IGpuTimestampNativeApi native;
        private readonly int sessionId;
        private readonly bool ownsPublicSession;
        private readonly IntPtr callback;
        private readonly NativeEventIds eventIds;
        private readonly GpuTimestampScope[] scopes;
        private readonly Dictionary<IntPtr, int> scopeByPayload;
        private readonly Dictionary<ulong, ActiveSample> activeSamples;
        private readonly ulong[] activeTokenByScope;
        private bool disposed;
        private GpuTimestampStatus terminalStatus;
        private int reservedSampleCount;
        private int submittedSampleCount;
        private bool unsafeNativeState;

        private GpuTimestampSession(
            IGpuTimestampNativeApi native,
            bool ownsPublicSession,
            GpuTimestampSupport support,
            IntPtr callback,
            NativeEventIds eventIds,
            IntPtr[] payloads)
        {
            this.native = native;
            this.ownsPublicSession = ownsPublicSession;
            Support = support;
            this.callback = callback;
            this.eventIds = eventIds;
            sessionId = NextSessionId();
            scopes = new GpuTimestampScope[payloads.Length];
            scopeByPayload = new Dictionary<IntPtr, int>(payloads.Length);
            activeSamples = new Dictionary<ulong, ActiveSample>(payloads.Length);
            activeTokenByScope = new ulong[payloads.Length];
            for (int i = 0; i < payloads.Length; i++)
            {
                scopes[i] = new GpuTimestampScope(
                    sessionId,
                    i,
                    callback,
                    payloads[i],
                    eventIds.Begin,
                    eventIds.End,
                    eventIds.Completion);
                scopeByPayload.Add(payloads[i], i);
            }
        }

        public GpuTimestampSupport Support { get; }

        public int Capacity => scopes.Length;

        public int ActiveSampleCount => activeSamples.Count;

        public int ReservedSampleCount => reservedSampleCount;

        public int SubmittedSampleCount => submittedSampleCount;

        public bool IsTerminal => terminalStatus != 0;

        public GpuTimestampStatus TerminalStatus => terminalStatus;

        public IReadOnlyList<GpuTimestampScope> Scopes => scopes;

        public static bool TryCreate(
            out GpuTimestampSession session,
            out GpuTimestampSupport support)
        {
            session = null;
            support = default;
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
            {
                support = GpuTimestampSupport.Unavailable(
                    GpuTimestampAvailability.UnsupportedPlatform,
                    "Native Direct3D 12 timestamps require Windows.");
                return false;
            }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                support = GpuTimestampSupport.Unavailable(
                    GpuTimestampAvailability.UnsupportedGraphicsApi,
                    "Native timestamps require Unity's Direct3D 12 graphics backend.");
                return false;
            }
            if (Interlocked.CompareExchange(ref activePublicSession, 1, 0) != 0)
            {
                support = GpuTimestampSupport.Unavailable(
                    GpuTimestampAvailability.SessionAlreadyActive,
                    "Only one native GPU timestamp session may be active.");
                return false;
            }

            bool created = TryCreateCore(
                PInvokeGpuTimestampNativeApi.Instance,
                true,
                out session,
                out support,
                out bool unsafeNativeState);
            if (!created && !unsafeNativeState)
            {
                Interlocked.Exchange(ref activePublicSession, 0);
            }
            return created;
        }

        internal static bool TryCreateForTesting(
            IGpuTimestampNativeApi native,
            out GpuTimestampSession session,
            out GpuTimestampSupport support)
        {
            return TryCreateCore(
                native,
                false,
                out session,
                out support,
                out _);
        }

        public void RecordFrequencyInitialization(CommandBuffer commands)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            commands.IssuePluginEventAndData(
                callback,
                eventIds.Frequency,
                IntPtr.Zero);
        }

        public GpuTimestampStatus Acquire(
            ulong userTag,
            GpuTimestampSampleFlags flags,
            int sourceFrame,
            out GpuTimestampToken token)
        {
            ThrowIfDisposed();
            if (IsTerminal)
            {
                token = default;
                return terminalStatus;
            }
            token = default;
            if (userTag == 0 || sourceFrame < 0 ||
                (flags & ~GpuTimestampSampleFlags.EmptyScope) != 0)
            {
                return GpuTimestampStatus.InvalidArgument;
            }

            ulong nativeToken;
            IntPtr payload;
            int nativeStatus;
            try
            {
                nativeStatus = native.AcquireSample(
                    userTag,
                    (uint)flags,
                    out nativeToken,
                    out payload);
            }
            catch
            {
                unsafeNativeState = true;
                MarkTerminal(GpuTimestampStatus.Error);
                return GpuTimestampStatus.Error;
            }
            GpuTimestampStatus status = ToStatus(nativeStatus);
            if (status != GpuTimestampStatus.Ready)
            {
                return status;
            }
            if (nativeToken == 0 ||
                payload == IntPtr.Zero ||
                !scopeByPayload.TryGetValue(payload, out int scopeIndex) ||
                activeTokenByScope[scopeIndex] != 0 ||
                activeSamples.ContainsKey(nativeToken))
            {
                if (!TryCancelNative(nativeToken))
                {
                    unsafeNativeState = true;
                }
                MarkTerminal(GpuTimestampStatus.MalformedNativeResult);
                return GpuTimestampStatus.MalformedNativeResult;
            }

            token = new GpuTimestampToken(
                sessionId,
                nativeToken,
                userTag,
                flags,
                scopeIndex,
                sourceFrame);
            activeTokenByScope[scopeIndex] = nativeToken;
            activeSamples.Add(
                nativeToken,
                new ActiveSample
                {
                    Token = token,
                    State = ActiveSampleState.Reserved
                });
            reservedSampleCount++;
            return GpuTimestampStatus.Ready;
        }

        public GpuTimestampScope GetScope(GpuTimestampToken token)
        {
            ThrowIfDisposed();
            if (!IsCurrent(token))
            {
                throw new InvalidOperationException(
                    "GPU timestamp token is stale or belongs to another session.");
            }
            return scopes[token.ScopeIndex];
        }

        /// <summary>
        /// Transitions an acquired sample from reserved to submitted. Call this immediately
        /// before queueing the pre-recorded begin/core/end CommandBuffers, never while merely
        /// recording those CommandBuffers.
        /// </summary>
        public GpuTimestampStatus MarkSubmitted(GpuTimestampToken token)
        {
            ThrowIfDisposed();
            if (!IsCurrent(token))
            {
                return GpuTimestampStatus.StaleManagedToken;
            }
            ActiveSample active = activeSamples[token.Value];
            if (active.State != ActiveSampleState.Reserved)
            {
                return GpuTimestampStatus.InvalidArgument;
            }

            int nativeStatus;
            try
            {
                nativeStatus = native.MarkSubmitted(token.Value);
            }
            catch
            {
                active.State = ActiveSampleState.Indeterminate;
                activeSamples[token.Value] = active;
                reservedSampleCount--;
                unsafeNativeState = true;
                MarkTerminal(GpuTimestampStatus.Error);
                return GpuTimestampStatus.Error;
            }

            GpuTimestampStatus status = ToStatus(nativeStatus);
            if (status != GpuTimestampStatus.Ready)
            {
                active.State = ActiveSampleState.Indeterminate;
                activeSamples[token.Value] = active;
                reservedSampleCount--;
                unsafeNativeState = true;
                GpuTimestampStatus failure =
                    status == GpuTimestampStatus.Pending
                        ? GpuTimestampStatus.Error
                        : status;
                MarkTerminal(failure);
                return failure;
            }

            active.State = ActiveSampleState.Submitted;
            activeSamples[token.Value] = active;
            reservedSampleCount--;
            submittedSampleCount++;
            return GpuTimestampStatus.Ready;
        }

        public GpuTimestampStatus TryConsume(
            GpuTimestampToken token,
            int resultFrame,
            out GpuTimestampResult result)
        {
            ThrowIfDisposed();
            result = default;
            if (resultFrame < token.SourceFrame)
            {
                return GpuTimestampStatus.InvalidArgument;
            }
            if (!IsCurrent(token))
            {
                return GpuTimestampStatus.StaleManagedToken;
            }
            if (activeSamples[token.Value].State !=
                ActiveSampleState.Submitted)
            {
                return GpuTimestampStatus.InvalidArgument;
            }

            NativeTimestampResult nativeResult = new NativeTimestampResult
            {
                StructSize = NativeResultSize
            };
            int nativeStatus;
            try
            {
                nativeStatus = native.TryConsumeResult(token.Value, ref nativeResult);
            }
            catch
            {
                unsafeNativeState = true;
                MarkTerminal(GpuTimestampStatus.Error);
                return GpuTimestampStatus.Error;
            }

            GpuTimestampStatus status = ToStatus(nativeStatus);
            if (status == GpuTimestampStatus.Pending)
            {
                return status;
            }
            if (status != GpuTimestampStatus.Ready)
            {
                MarkTerminal(status);
                return status;
            }

            if (!TryValidateResult(token, nativeResult, out long elapsedNanoseconds))
            {
                ReleaseManaged(token);
                MarkTerminal(GpuTimestampStatus.MalformedNativeResult);
                return GpuTimestampStatus.MalformedNativeResult;
            }

            result = new GpuTimestampResult(
                token,
                resultFrame,
                nativeResult.Flags,
                nativeResult.BeginTicks,
                nativeResult.EndTicks,
                nativeResult.ElapsedTicks,
                nativeResult.TimestampFrequency,
                elapsedNanoseconds,
                nativeResult.ElapsedMilliseconds,
                nativeResult.FenceValue,
                nativeResult.DeviceGeneration);
            ReleaseManaged(token);
            return GpuTimestampStatus.Ready;
        }

        public GpuTimestampStatus Cancel(GpuTimestampToken token)
        {
            ThrowIfDisposed();
            if (!IsCurrent(token))
            {
                return GpuTimestampStatus.StaleManagedToken;
            }
            if (activeSamples[token.Value].State !=
                ActiveSampleState.Reserved)
            {
                return GpuTimestampStatus.InvalidArgument;
            }

            int nativeStatus;
            try
            {
                nativeStatus = native.CancelSample(token.Value);
            }
            catch
            {
                unsafeNativeState = true;
                MarkTerminal(GpuTimestampStatus.Error);
                return GpuTimestampStatus.Error;
            }
            GpuTimestampStatus status = ToStatus(nativeStatus);
            if (status == GpuTimestampStatus.Ready)
            {
                ReleaseManaged(token);
            }
            else
            {
                MarkTerminal(status);
            }
            return status;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;

            ulong[] tokens = new ulong[activeSamples.Count];
            activeSamples.Keys.CopyTo(tokens, 0);
            for (int i = 0; i < tokens.Length; i++)
            {
                ActiveSample active = activeSamples[tokens[i]];
                if (active.State == ActiveSampleState.Reserved)
                {
                    if (!TryCancelNative(tokens[i]))
                    {
                        unsafeNativeState = true;
                    }
                }
            }
            bool hasSubmittedSamples = submittedSampleCount != 0;
            activeSamples.Clear();
            Array.Clear(activeTokenByScope, 0, activeTokenByScope.Length);
            reservedSampleCount = 0;
            submittedSampleCount = 0;
            if (ownsPublicSession && !hasSubmittedSamples && !unsafeNativeState)
            {
                Interlocked.Exchange(ref activePublicSession, 0);
            }
        }

        internal static long TicksToNanoseconds(ulong elapsedTicks, ulong frequency)
        {
            if (frequency == 0)
            {
                throw new DivideByZeroException("Timestamp frequency is zero.");
            }

            decimal nanoseconds =
                (decimal)elapsedTicks * 1000000000m / (decimal)frequency;
            decimal rounded = decimal.Round(
                nanoseconds,
                0,
                MidpointRounding.AwayFromZero);
            if (rounded > long.MaxValue)
            {
                throw new OverflowException("Timestamp duration exceeds Int64 nanoseconds.");
            }
            return decimal.ToInt64(rounded);
        }

        private static bool TryCreateCore(
            IGpuTimestampNativeApi native,
            bool ownsPublicSession,
            out GpuTimestampSession session,
            out GpuTimestampSupport support,
            out bool unsafeNativeState)
        {
            if (native == null)
            {
                throw new ArgumentNullException(nameof(native));
            }
            session = null;
            support = default;
            unsafeNativeState = false;
            try
            {
                uint abiVersion = native.GetAbiVersion();
                if (abiVersion != AbiVersion)
                {
                    support = GpuTimestampSupport.Unavailable(
                        GpuTimestampAvailability.AbiMismatch,
                        $"Native timestamp ABI is {abiVersion}; managed code requires {AbiVersion}.");
                    return false;
                }

                NativeEventIds eventIds = default;
                int eventStatus = native.GetEventIds(ref eventIds);
                if (eventStatus != (int)GpuTimestampStatus.Ready ||
                    eventIds.Begin == eventIds.End ||
                    eventIds.Begin == eventIds.Frequency ||
                    eventIds.Begin == eventIds.Completion ||
                    eventIds.End == eventIds.Frequency ||
                    eventIds.End == eventIds.Completion ||
                    eventIds.Frequency == eventIds.Completion)
                {
                    support = GpuTimestampSupport.Unavailable(
                        GpuTimestampAvailability.InitializationFailed,
                        "Native timestamp event IDs are unavailable or invalid.");
                    return false;
                }

                NativeSupportInfo nativeSupport = new NativeSupportInfo
                {
                    StructSize = NativeSupportInfoSize
                };
                int supportStatus = native.GetSupportInfo(ref nativeSupport);
                if (supportStatus != (int)GpuTimestampStatus.Ready ||
                    nativeSupport.Supported == 0)
                {
                    support = new GpuTimestampSupport(
                        GpuTimestampAvailability.NativeUnsupported,
                        "Native plugin loaded but Direct3D 12 timestamp queries are unsupported.",
                        nativeSupport.AbiVersion,
                        nativeSupport.CapabilityFlags,
                        checked((int)nativeSupport.RingCapacity),
                        nativeSupport.RendererType,
                        nativeSupport.DeviceGeneration,
                        nativeSupport.FrequencyReady != 0,
                        nativeSupport.TimestampFrequency);
                    return false;
                }
                if (nativeSupport.StructSize != NativeSupportInfoSize ||
                    nativeSupport.AbiVersion != AbiVersion ||
                    nativeSupport.RingCapacity == 0 ||
                    nativeSupport.RingCapacity > 65536 ||
                    (nativeSupport.CapabilityFlags & RequiredCapabilityFlags) !=
                        RequiredCapabilityFlags)
                {
                    support = GpuTimestampSupport.Unavailable(
                        GpuTimestampAvailability.AbiMismatch,
                        "Native timestamp support metadata does not match ABI v2.");
                    return false;
                }

                IntPtr callback = native.GetRenderEventAndDataFunc();
                if (callback == IntPtr.Zero)
                {
                    support = GpuTimestampSupport.Unavailable(
                        GpuTimestampAvailability.InitializationFailed,
                        "Native render-event callback is unavailable.");
                    return false;
                }

                int capacity = checked((int)nativeSupport.RingCapacity);
                if (!TryDiscoverPayloads(
                    native,
                    capacity,
                    out IntPtr[] payloads,
                    out unsafeNativeState))
                {
                    support = GpuTimestampSupport.Unavailable(
                        GpuTimestampAvailability.InitializationFailed,
                        "Unable to discover and recycle every native timestamp ring payload.");
                    return false;
                }

                support = new GpuTimestampSupport(
                    GpuTimestampAvailability.Available,
                    "Native Direct3D 12 timestamp queries are available.",
                    nativeSupport.AbiVersion,
                    nativeSupport.CapabilityFlags,
                    capacity,
                    nativeSupport.RendererType,
                    nativeSupport.DeviceGeneration,
                    nativeSupport.FrequencyReady != 0,
                    nativeSupport.TimestampFrequency);
                session = new GpuTimestampSession(
                    native,
                    ownsPublicSession,
                    support,
                    callback,
                    eventIds,
                    payloads);
                return true;
            }
            catch (DllNotFoundException exception)
            {
                support = GpuTimestampSupport.Unavailable(
                    GpuTimestampAvailability.PluginNotFound,
                    exception.Message);
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                support = GpuTimestampSupport.Unavailable(
                    GpuTimestampAvailability.EntryPointMissing,
                    exception.Message);
                return false;
            }
            catch (BadImageFormatException exception)
            {
                support = GpuTimestampSupport.Unavailable(
                    GpuTimestampAvailability.PluginNotFound,
                    exception.Message);
                return false;
            }
            catch (Exception exception)
            {
                support = GpuTimestampSupport.Unavailable(
                    GpuTimestampAvailability.InitializationFailed,
                    exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        private static bool TryDiscoverPayloads(
            IGpuTimestampNativeApi native,
            int capacity,
            out IntPtr[] payloads,
            out bool cleanupFailed)
        {
            payloads = new IntPtr[capacity];
            ulong[] tokens = new ulong[capacity];
            HashSet<IntPtr> uniquePayloads = new HashSet<IntPtr>();
            int acquired = 0;
            int tokenCount = 0;
            bool valid = true;
            cleanupFailed = false;
            try
            {
                for (; acquired < capacity;)
                {
                    int status = native.AcquireSample(
                        checked((ulong)acquired + 1UL),
                        0,
                        out ulong token,
                        out payloads[acquired]);
                    bool acquiredNativeToken =
                        status == (int)GpuTimestampStatus.Ready &&
                        token != 0;
                    if (acquiredNativeToken)
                    {
                        tokens[tokenCount++] = token;
                    }
                    if (!acquiredNativeToken ||
                        payloads[acquired] == IntPtr.Zero ||
                        !uniquePayloads.Add(payloads[acquired]))
                    {
                        valid = false;
                        break;
                    }
                    acquired++;
                }
            }
            catch
            {
                valid = false;
            }
            finally
            {
                for (int i = 0; i < tokenCount; i++)
                {
                    try
                    {
                        if (native.CancelSample(tokens[i]) !=
                            (int)GpuTimestampStatus.Ready)
                        {
                            cleanupFailed = true;
                        }
                    }
                    catch
                    {
                        cleanupFailed = true;
                    }
                }
            }
            return valid && !cleanupFailed && acquired == capacity;
        }

        private bool TryValidateResult(
            GpuTimestampToken token,
            NativeTimestampResult nativeResult,
            out long elapsedNanoseconds)
        {
            elapsedNanoseconds = 0;
            if (nativeResult.StructSize != NativeResultSize ||
                nativeResult.Token != token.Value ||
                nativeResult.UserTag != token.UserTag ||
                nativeResult.Flags != (uint)token.Flags ||
                nativeResult.TimestampFrequency == 0 ||
                nativeResult.EndTicks < nativeResult.BeginTicks ||
                nativeResult.ElapsedTicks !=
                    nativeResult.EndTicks - nativeResult.BeginTicks ||
                double.IsNaN(nativeResult.ElapsedMilliseconds) ||
                double.IsInfinity(nativeResult.ElapsedMilliseconds) ||
                nativeResult.ElapsedMilliseconds < 0.0 ||
                nativeResult.FenceValue == 0 ||
                nativeResult.DeviceGeneration != Support.DeviceGeneration)
            {
                return false;
            }

            try
            {
                elapsedNanoseconds = TicksToNanoseconds(
                    nativeResult.ElapsedTicks,
                    nativeResult.TimestampFrequency);
            }
            catch (Exception)
            {
                return false;
            }
            double managedMilliseconds = elapsedNanoseconds * 1.0e-6;
            double tolerance = Math.Max(1.0e-6, managedMilliseconds * 1.0e-6);
            return Math.Abs(
                managedMilliseconds - nativeResult.ElapsedMilliseconds) <= tolerance;
        }

        private bool IsCurrent(GpuTimestampToken token)
        {
            return token.IsValid &&
                token.SessionId == sessionId &&
                token.ScopeIndex < activeTokenByScope.Length &&
                activeTokenByScope[token.ScopeIndex] == token.Value &&
                activeSamples.TryGetValue(token.Value, out ActiveSample active) &&
                active.Token.UserTag == token.UserTag &&
                active.Token.SourceFrame == token.SourceFrame;
        }

        private void ReleaseManaged(GpuTimestampToken token)
        {
            if (activeSamples.TryGetValue(token.Value, out ActiveSample active))
            {
                if (active.State == ActiveSampleState.Reserved)
                {
                    reservedSampleCount--;
                }
                else if (active.State == ActiveSampleState.Submitted)
                {
                    submittedSampleCount--;
                }
            }
            activeSamples.Remove(token.Value);
            if (token.ScopeIndex >= 0 && token.ScopeIndex < activeTokenByScope.Length &&
                activeTokenByScope[token.ScopeIndex] == token.Value)
            {
                activeTokenByScope[token.ScopeIndex] = 0;
            }
        }

        private bool TryCancelNative(ulong token)
        {
            if (token == 0)
            {
                return true;
            }
            try
            {
                return native.CancelSample(token) ==
                    (int)GpuTimestampStatus.Ready;
            }
            catch
            {
                return false;
            }
        }

        private void MarkTerminal(GpuTimestampStatus status)
        {
            if (terminalStatus == 0)
            {
                terminalStatus = status;
            }
        }

        private static GpuTimestampStatus ToStatus(int nativeStatus)
        {
            switch (nativeStatus)
            {
                case 1: return GpuTimestampStatus.Ready;
                case 0: return GpuTimestampStatus.Pending;
                case -1: return GpuTimestampStatus.Error;
                case -2: return GpuTimestampStatus.InvalidArgument;
                case -3: return GpuTimestampStatus.InvalidToken;
                case -4: return GpuTimestampStatus.RingFull;
                case -5: return GpuTimestampStatus.NotInitialized;
                case -6: return GpuTimestampStatus.Unsupported;
                case -7: return GpuTimestampStatus.DeviceLost;
                case -8: return GpuTimestampStatus.CallbackError;
                case -9: return GpuTimestampStatus.FrequencyUnavailable;
                default: return GpuTimestampStatus.Error;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(GpuTimestampSession));
            }
        }

        private static int NextSessionId()
        {
            int value = Interlocked.Increment(ref nextSessionId);
            if (value == 0)
            {
                value = Interlocked.Increment(ref nextSessionId);
            }
            return value;
        }

        private enum ActiveSampleState
        {
            Reserved,
            Submitted,
            Indeterminate
        }

        private struct ActiveSample
        {
            public GpuTimestampToken Token;
            public ActiveSampleState State;
        }
    }
}
