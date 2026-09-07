using System;
using UnityEngine;

namespace Summit.GpuAutotuning
{
    /// <summary>Identity supplied independently by the running build, never copied from a profile.
    /// Compiler identity must include backend, compiler/toolchain version and relevant flags.
    /// Shader identity is a content digest of shaders and includes; build identity is the artifact digest.
    /// sourceCommit is provenance only and cannot substitute for any of these fields.</summary>
    [Serializable]
    public sealed class GpuCalibrationEnvironment
    {
        public string unityVersion = string.Empty;
        public string compilerIdentity = string.Empty;
        public string shaderIdentity = string.Empty;
        public string buildIdentity = string.Empty;

        public bool IsValid => !string.IsNullOrWhiteSpace(unityVersion) &&
            !string.IsNullOrWhiteSpace(compilerIdentity) &&
            !string.IsNullOrWhiteSpace(shaderIdentity) &&
            !string.IsNullOrWhiteSpace(buildIdentity);

        public static GpuCalibrationEnvironment Capture(string compilerIdentity,
            string shaderIdentity, string buildIdentity) => new GpuCalibrationEnvironment
        {
            unityVersion = Application.unityVersion,
            compilerIdentity = compilerIdentity,
            shaderIdentity = shaderIdentity,
            buildIdentity = buildIdentity
        };

        public bool Matches(GpuCalibrationEnvironment current) => IsValid &&
            current != null && current.IsValid &&
            string.Equals(unityVersion, current.unityVersion, StringComparison.Ordinal) &&
            string.Equals(compilerIdentity, current.compilerIdentity, StringComparison.Ordinal) &&
            string.Equals(shaderIdentity, current.shaderIdentity, StringComparison.Ordinal) &&
            string.Equals(buildIdentity, current.buildIdentity, StringComparison.Ordinal);

        public GpuCalibrationEnvironment Copy() => (GpuCalibrationEnvironment)MemberwiseClone();
    }
}
