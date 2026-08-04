using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Summit.GpuTimestamps.Tests
{
    internal sealed class FakeGpuTimestampNativeApi : IGpuTimestampNativeApi
    {
        private enum SlotState
        {
            Free,
            Reserved,
            Submitted
        }

        private sealed class Slot
        {
            public readonly int Index;
            public readonly IntPtr Payload;
            public uint Generation = 1;
            public SlotState State;
            public ulong Token;
            public ulong UserTag;
            public uint Flags;

            public Slot(int index)
            {
                Index = index;
                Payload = new IntPtr(0x1000 + index * 0x100);
            }
        }

        private sealed class ConsumeOutcome
        {
            public int Status;
            public NativeTimestampResult Result;
        }

        private readonly Slot[] slots;
        private readonly Dictionary<ulong, ConsumeOutcome> outcomes =
            new Dictionary<ulong, ConsumeOutcome>();

        public FakeGpuTimestampNativeApi(int capacity = 2)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            slots = new Slot[capacity];
            for (int i = 0; i < capacity; i++)
            {
                slots[i] = new Slot(i);
            }

            EventIds = new NativeEventIds
            {
                Begin = 1001,
                End = 1002,
                Frequency = 1003,
                Completion = 1004
            };
            SupportInfo = new NativeSupportInfo
            {
                StructSize = (uint)Marshal.SizeOf(typeof(NativeSupportInfo)),
                AbiVersion = GpuTimestampSession.AbiVersion,
                Supported = 1,
                CapabilityFlags = 0x1Fu,
                RingCapacity = checked((uint)capacity),
                RendererType = 18,
                DeviceGeneration = 7,
                FrequencyReady = 1,
                TimestampFrequency = 1000000000UL
            };
        }

        public uint AbiVersion = GpuTimestampSession.AbiVersion;
        public NativeEventIds EventIds;
        public NativeSupportInfo SupportInfo;
        public int EventIdsStatus = (int)GpuTimestampStatus.Ready;
        public int SupportStatus = (int)GpuTimestampStatus.Ready;
        public IntPtr Callback = new IntPtr(0x123456);
        public Exception GetAbiVersionException;
        public Exception AcquireException;
        public Exception MarkSubmittedException;
        public Exception CancelException;
        public Exception ConsumeException;
        public Action<ulong> MarkSubmittedObserver;
        public bool DuplicatePayloadOnSecondAcquire;
        public bool ZeroTokenOnNextAcquire;
        public IntPtr? PayloadOnNextAcquire;
        public GpuTimestampStatus? StatusOnNextAcquire;
        public GpuTimestampStatus? StatusOnNextMarkSubmitted;

        public int AcquireCallCount { get; private set; }
        public int MarkSubmittedCallCount { get; private set; }
        public int CancelCallCount { get; private set; }
        public int ConsumeCallCount { get; private set; }
        public List<ulong> SubmittedTokens { get; } = new List<ulong>();
        public List<ulong> CancelledTokens { get; } = new List<ulong>();

        public int ActiveNativeSampleCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < slots.Length; i++)
                {
                    count += slots[i].State == SlotState.Free ? 0 : 1;
                }
                return count;
            }
        }

        public uint GetAbiVersion()
        {
            if (GetAbiVersionException != null)
            {
                throw GetAbiVersionException;
            }
            return AbiVersion;
        }

        public IntPtr GetRenderEventAndDataFunc()
        {
            return Callback;
        }

        public int GetEventIds(ref NativeEventIds eventIds)
        {
            eventIds = EventIds;
            return EventIdsStatus;
        }

        public int GetSupportInfo(ref NativeSupportInfo supportInfo)
        {
            supportInfo = SupportInfo;
            return SupportStatus;
        }

        public int AcquireSample(
            ulong userTag,
            uint flags,
            out ulong token,
            out IntPtr payload)
        {
            AcquireCallCount++;
            if (AcquireException != null)
            {
                throw AcquireException;
            }
            if (StatusOnNextAcquire.HasValue)
            {
                GpuTimestampStatus status = StatusOnNextAcquire.Value;
                StatusOnNextAcquire = null;
                token = 0;
                payload = IntPtr.Zero;
                return (int)status;
            }

            Slot slot = FindFreeSlot();
            if (slot == null)
            {
                token = 0;
                payload = IntPtr.Zero;
                return (int)GpuTimestampStatus.RingFull;
            }

            slot.State = SlotState.Reserved;
            slot.UserTag = userTag;
            slot.Flags = flags;
            slot.Token = ((ulong)slot.Generation << 32) | checked((uint)slot.Index + 1u);
            token = ZeroTokenOnNextAcquire ? 0UL : slot.Token;
            ZeroTokenOnNextAcquire = false;

            if (PayloadOnNextAcquire.HasValue)
            {
                payload = PayloadOnNextAcquire.Value;
                PayloadOnNextAcquire = null;
            }
            else if (DuplicatePayloadOnSecondAcquire && AcquireCallCount == 2)
            {
                payload = slots[0].Payload;
            }
            else
            {
                payload = slot.Payload;
            }
            return (int)GpuTimestampStatus.Ready;
        }

        public int MarkSubmitted(ulong token)
        {
            MarkSubmittedCallCount++;
            if (MarkSubmittedException != null)
            {
                throw MarkSubmittedException;
            }

            Slot slot = FindToken(token);
            if (slot == null)
            {
                return (int)GpuTimestampStatus.InvalidToken;
            }
            if (slot.State != SlotState.Reserved)
            {
                return (int)GpuTimestampStatus.InvalidArgument;
            }
            if (StatusOnNextMarkSubmitted.HasValue)
            {
                GpuTimestampStatus status = StatusOnNextMarkSubmitted.Value;
                StatusOnNextMarkSubmitted = null;
                return (int)status;
            }

            MarkSubmittedObserver?.Invoke(token);
            slot.State = SlotState.Submitted;
            SubmittedTokens.Add(token);
            return (int)GpuTimestampStatus.Ready;
        }

        public int CancelSample(ulong token)
        {
            CancelCallCount++;
            if (CancelException != null)
            {
                throw CancelException;
            }

            Slot slot = FindToken(token);
            if (slot == null)
            {
                return (int)GpuTimestampStatus.InvalidToken;
            }
            if (slot.State != SlotState.Reserved)
            {
                return (int)GpuTimestampStatus.InvalidArgument;
            }
            CancelledTokens.Add(token);
            Release(slot);
            return (int)GpuTimestampStatus.Ready;
        }

        public int TryConsumeResult(ulong token, ref NativeTimestampResult result)
        {
            ConsumeCallCount++;
            if (ConsumeException != null)
            {
                throw ConsumeException;
            }

            Slot slot = FindToken(token);
            if (slot == null)
            {
                return (int)GpuTimestampStatus.InvalidToken;
            }
            if (slot.State != SlotState.Submitted)
            {
                return (int)GpuTimestampStatus.InvalidArgument;
            }
            if (!outcomes.TryGetValue(token, out ConsumeOutcome outcome))
            {
                return (int)GpuTimestampStatus.Pending;
            }
            if (outcome.Status == (int)GpuTimestampStatus.Pending)
            {
                return outcome.Status;
            }

            if (outcome.Status == (int)GpuTimestampStatus.Ready)
            {
                result = outcome.Result;
            }
            Release(slot);
            return outcome.Status;
        }

        public void MakeReady(
            ulong token,
            ulong beginTicks = 1000UL,
            ulong endTicks = 1250UL,
            ulong frequency = 10000000UL,
            ulong fenceValue = 42UL)
        {
            Slot slot = RequireSubmittedToken(token);
            ulong elapsedTicks = checked(endTicks - beginTicks);
            outcomes[token] = new ConsumeOutcome
            {
                Status = (int)GpuTimestampStatus.Ready,
                Result = new NativeTimestampResult
                {
                    StructSize = (uint)Marshal.SizeOf(typeof(NativeTimestampResult)),
                    Flags = slot.Flags,
                    Token = token,
                    UserTag = slot.UserTag,
                    BeginTicks = beginTicks,
                    EndTicks = endTicks,
                    ElapsedTicks = elapsedTicks,
                    TimestampFrequency = frequency,
                    ElapsedMilliseconds = elapsedTicks * 1000.0 / frequency,
                    FenceValue = fenceValue,
                    DeviceGeneration = SupportInfo.DeviceGeneration,
                    Reserved = 0
                }
            };
        }

        public void SetConsumeStatus(ulong token, GpuTimestampStatus status)
        {
            RequireSubmittedToken(token);
            outcomes[token] = new ConsumeOutcome
            {
                Status = (int)status
            };
        }

        public void MutateReadyResult(
            ulong token,
            Func<NativeTimestampResult, NativeTimestampResult> mutation)
        {
            if (mutation == null)
            {
                throw new ArgumentNullException(nameof(mutation));
            }
            if (!outcomes.TryGetValue(token, out ConsumeOutcome outcome) ||
                outcome.Status != (int)GpuTimestampStatus.Ready)
            {
                throw new InvalidOperationException("Token does not have a ready result.");
            }
            outcome.Result = mutation(outcome.Result);
        }

        public void ResetCallCounts()
        {
            AcquireCallCount = 0;
            MarkSubmittedCallCount = 0;
            CancelCallCount = 0;
            ConsumeCallCount = 0;
            SubmittedTokens.Clear();
            CancelledTokens.Clear();
        }

        private Slot FindFreeSlot()
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].State == SlotState.Free)
                {
                    return slots[i];
                }
            }
            return null;
        }

        private Slot FindToken(ulong token)
        {
            if (token == 0)
            {
                return null;
            }
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].State != SlotState.Free && slots[i].Token == token)
                {
                    return slots[i];
                }
            }
            return null;
        }

        private Slot RequireToken(ulong token)
        {
            Slot slot = FindToken(token);
            if (slot == null)
            {
                throw new InvalidOperationException("Unknown fake native token.");
            }
            return slot;
        }

        private Slot RequireSubmittedToken(ulong token)
        {
            Slot slot = RequireToken(token);
            if (slot.State != SlotState.Submitted)
            {
                throw new InvalidOperationException(
                    "Fake native callbacks completed before submission.");
            }
            return slot;
        }

        private void Release(Slot slot)
        {
            outcomes.Remove(slot.Token);
            slot.State = SlotState.Free;
            slot.Token = 0;
            slot.UserTag = 0;
            slot.Flags = 0;
            slot.Generation = slot.Generation == uint.MaxValue
                ? 1u
                : slot.Generation + 1u;
        }
    }
}
