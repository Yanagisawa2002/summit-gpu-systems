using System;
using UnityEngine;

namespace Summit.GpuAutotuning
{
    [Serializable]
    public sealed class GpuDeviceFingerprint : IEquatable<GpuDeviceFingerprint>
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;
        public int vendorId;
        public int deviceId;
        public string vendor = string.Empty;
        public string deviceName = string.Empty;
        public string graphicsApi = string.Empty;
        public string graphicsVersion = string.Empty;
        public int shaderLevel;

        public string StableKey => string.Format(
            "v{0}-{1:X4}-{2:X4}-{3}-sm{4}",
            schemaVersion,
            vendorId,
            deviceId,
            Sanitize(graphicsApi),
            shaderLevel);

        public static GpuDeviceFingerprint Capture()
        {
            return new GpuDeviceFingerprint
            {
                vendorId = SystemInfo.graphicsDeviceVendorID,
                deviceId = SystemInfo.graphicsDeviceID,
                vendor = SystemInfo.graphicsDeviceVendor ?? string.Empty,
                deviceName = SystemInfo.graphicsDeviceName ?? string.Empty,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                graphicsVersion = SystemInfo.graphicsDeviceVersion ?? string.Empty,
                shaderLevel = SystemInfo.graphicsShaderLevel
            };
        }

        public bool Equals(GpuDeviceFingerprint other)
        {
            return other != null &&
                schemaVersion == other.schemaVersion &&
                vendorId == other.vendorId &&
                deviceId == other.deviceId &&
                shaderLevel == other.shaderLevel &&
                string.Equals(graphicsApi, other.graphicsApi,
                    StringComparison.Ordinal) &&
                string.Equals(deviceName, other.deviceName,
                    StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as GpuDeviceFingerprint);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + schemaVersion;
                hash = hash * 31 + vendorId;
                hash = hash * 31 + deviceId;
                hash = hash * 31 + shaderLevel;
                hash = hash * 31 + (graphicsApi?.GetHashCode() ?? 0);
                hash = hash * 31 + (deviceName?.GetHashCode() ?? 0);
                return hash;
            }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }
            char[] result = value.ToLowerInvariant().ToCharArray();
            for (int i = 0; i < result.Length; i++)
            {
                if (!char.IsLetterOrDigit(result[i]))
                {
                    result[i] = '-';
                }
            }
            return new string(result).Trim('-');
        }
    }
}
