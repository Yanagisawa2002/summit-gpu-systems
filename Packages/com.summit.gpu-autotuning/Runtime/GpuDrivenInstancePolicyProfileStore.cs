using System;
using System.IO;
using UnityEngine;

namespace Summit.GpuAutotuning
{
    public static class GpuDrivenInstancePolicyProfileStore
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
                "gpu-driven-instance-policy-" + device.StableKey + ".json");
        }

        public static void Save(
            string path,
            GpuDrivenInstancePolicyProfile profile)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "A profile path is required.",
                    nameof(path));
            }
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath) ??
                throw new InvalidOperationException(
                    "Profile path has no directory.");
            Directory.CreateDirectory(directory);
            string temporaryPath = fullPath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonUtility.ToJson(profile, true));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            File.Move(temporaryPath, fullPath);
        }

        /// <summary>
        /// Loads and compiles a profile in one fail-closed operation. A false
        /// result still returns a selector that produces safe fallback choices.
        /// </summary>
        public static bool TryLoad(
            string path,
            in GpuDrivenInstancePolicyEnvironment environment,
            out GpuDrivenInstancePolicyProfile profile,
            out GpuDrivenInstancePolicySelector selector,
            out GpuDrivenInstancePolicyValidationError validationError)
        {
            profile = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return GpuDrivenInstancePolicySelector.TryCreate(
                    null,
                    in environment,
                    out selector,
                    out validationError);
            }

            try
            {
                profile = JsonUtility.FromJson<GpuDrivenInstancePolicyProfile>(
                    File.ReadAllText(path));
            }
            catch (Exception)
            {
                profile = null;
            }

            GpuDrivenInstancePolicyProfile loaded = profile;
            bool accepted = GpuDrivenInstancePolicySelector.TryCreate(
                loaded,
                in environment,
                out selector,
                out validationError);
            if (!accepted)
            {
                profile = null;
            }
            return accepted;
        }
    }
}
