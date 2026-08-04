using System;
using System.Runtime.InteropServices;

namespace Summit.GpuTimestamps
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeEventIds
    {
        public int Begin;
        public int End;
        public int Frequency;
        public int Completion;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeSupportInfo
    {
        public uint StructSize;
        public uint AbiVersion;
        public uint Supported;
        public uint CapabilityFlags;
        public uint RingCapacity;
        public int RendererType;
        public uint DeviceGeneration;
        public uint FrequencyReady;
        public ulong TimestampFrequency;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeTimestampResult
    {
        public uint StructSize;
        public uint Flags;
        public ulong Token;
        public ulong UserTag;
        public ulong BeginTicks;
        public ulong EndTicks;
        public ulong ElapsedTicks;
        public ulong TimestampFrequency;
        public double ElapsedMilliseconds;
        public ulong FenceValue;
        public uint DeviceGeneration;
        public uint Reserved;
    }

    internal interface IGpuTimestampNativeApi
    {
        uint GetAbiVersion();

        IntPtr GetRenderEventAndDataFunc();

        int GetEventIds(ref NativeEventIds eventIds);

        int GetSupportInfo(ref NativeSupportInfo supportInfo);

        int AcquireSample(
            ulong userTag,
            uint flags,
            out ulong token,
            out IntPtr payload);

        int MarkSubmitted(ulong token);

        int CancelSample(ulong token);

        int TryConsumeResult(ulong token, ref NativeTimestampResult result);
    }

    internal sealed class PInvokeGpuTimestampNativeApi : IGpuTimestampNativeApi
    {
        private const string Library = "SummitGpuTimestamps";

        internal static readonly PInvokeGpuTimestampNativeApi Instance =
            new PInvokeGpuTimestampNativeApi();

        private PInvokeGpuTimestampNativeApi()
        {
        }

        public uint GetAbiVersion()
        {
            return NativeMethods.GetAbiVersion();
        }

        public IntPtr GetRenderEventAndDataFunc()
        {
            return NativeMethods.GetRenderEventAndDataFunc();
        }

        public int GetEventIds(ref NativeEventIds eventIds)
        {
            return NativeMethods.GetEventIds(ref eventIds);
        }

        public int GetSupportInfo(ref NativeSupportInfo supportInfo)
        {
            return NativeMethods.GetSupportInfo(ref supportInfo);
        }

        public int AcquireSample(
            ulong userTag,
            uint flags,
            out ulong token,
            out IntPtr payload)
        {
            return NativeMethods.AcquireSample(userTag, flags, out token, out payload);
        }

        public int MarkSubmitted(ulong token)
        {
            return NativeMethods.MarkSubmitted(token);
        }

        public int CancelSample(ulong token)
        {
            return NativeMethods.CancelSample(token);
        }

        public int TryConsumeResult(ulong token, ref NativeTimestampResult result)
        {
            return NativeMethods.TryConsumeResult(token, ref result);
        }

        private static class NativeMethods
        {
            [DllImport(
                Library,
                EntryPoint = "SGT_GetAbiVersion",
                CallingConvention = CallingConvention.StdCall)]
            internal static extern uint GetAbiVersion();

            [DllImport(
                Library,
                EntryPoint = "SGT_GetRenderEventAndDataFunc",
                CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr GetRenderEventAndDataFunc();

            [DllImport(
                Library,
                EntryPoint = "SGT_GetEventIds",
                CallingConvention = CallingConvention.StdCall)]
            internal static extern int GetEventIds(ref NativeEventIds eventIds);

            [DllImport(
                Library,
                EntryPoint = "SGT_GetSupportInfo",
                CallingConvention = CallingConvention.StdCall)]
            internal static extern int GetSupportInfo(ref NativeSupportInfo supportInfo);

            [DllImport(
                Library,
                EntryPoint = "SGT_AcquireSample",
                CallingConvention = CallingConvention.StdCall)]
            internal static extern int AcquireSample(
                ulong userTag,
                uint flags,
                out ulong token,
                out IntPtr payload);

            [DllImport(
                Library,
                EntryPoint = "SGT_MarkSubmitted",
                CallingConvention = CallingConvention.StdCall)]
            internal static extern int MarkSubmitted(ulong token);

            [DllImport(
                Library,
                EntryPoint = "SGT_CancelSample",
                CallingConvention = CallingConvention.StdCall)]
            internal static extern int CancelSample(ulong token);

            [DllImport(
                Library,
                EntryPoint = "SGT_TryConsumeResult",
                CallingConvention = CallingConvention.StdCall)]
            internal static extern int TryConsumeResult(
                ulong token,
                ref NativeTimestampResult result);
        }
    }
}
