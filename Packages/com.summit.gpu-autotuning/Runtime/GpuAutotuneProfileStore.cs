using System;
using System.IO;
using UnityEngine;

namespace Summit.GpuAutotuning
{
    public static class GpuAutotuneProfileStore
    {
        public static string GetDefaultPath(GpuDeviceFingerprint device)
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }
            return Path.Combine(
                Application.persistentDataPath,
                "GpuAutotuning",
                device.StableKey + ".json");
        }

        public static void Save(string path, GpuAutotuneProfile profile)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A profile path is required.", nameof(path));
            }
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath) ??
                throw new InvalidOperationException("Profile path has no directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = fullPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(profile, true));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            File.Move(temporaryPath, fullPath);
        }

        public static bool TryLoad(
            string path,
            GpuDeviceFingerprint currentDevice,
            out GpuAutotuneProfile profile)
        {
            profile = null;
            return false; // Missing independent compiler/shader/build identity: recalibrate.
        }

        public static bool TryLoad(string path, GpuDeviceFingerprint currentDevice,
            GpuCalibrationEnvironment currentEnvironment, out GpuAutotuneProfile profile)
        {
            profile = null;
            if (string.IsNullOrWhiteSpace(path) || currentDevice == null ||
                !File.Exists(path))
            {
                return false;
            }
            try
            {
                GpuAutotuneProfile loaded =
                    JsonUtility.FromJson<GpuAutotuneProfile>(File.ReadAllText(path));
                if (loaded == null ||
                    !loaded.IsCompatible(currentDevice, currentEnvironment))
                {
                    return false;
                }
                profile = loaded;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
