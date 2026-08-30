using System;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning
{
    [Serializable]
    public sealed class GpuDrivenInstancePolicyProfile
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;
        public int policyContractVersion =
            GpuDrivenInstancePolicyContract.CurrentPolicyContractVersion;
        public int profileRevision = 1;
        public string generatedUtc = string.Empty;
        public string sourceCommit = string.Empty;
        public string calibrationProtocol = string.Empty;
        public string measurementContractFingerprint = string.Empty;
        public string holdoutEvidenceSetId = string.Empty;
        public bool holdoutAccepted;

        public string unityVersion = string.Empty;
        public string autotuningPackageVersion =
            GpuDrivenInstancePolicyContract.CurrentAutotuningPackageVersion;
        public string gpuDrivenInstancesPackageVersion =
            GpuDrivenInstancePolicyContract
                .CurrentGpuDrivenInstancesPackageVersion;
        public string processorType = string.Empty;
        public string operatingSystem = string.Empty;
        public string pipelineContractFingerprint = string.Empty;
        public string shaderContractFingerprint = string.Empty;
        public GpuDeviceFingerprint device = new GpuDeviceFingerprint();
        public GpuDrivenInstancePolicyRule[] rules =
            Array.Empty<GpuDrivenInstancePolicyRule>();
    }

    /// <summary>
    /// An inclusive integer range over workload scale, upload fragmentation,
    /// upload amplification, visibility, hierarchy coherence, and view count.
    /// Ratios use basis points to avoid floating-point boundary drift.
    /// </summary>
    [Serializable]
    public sealed class GpuDrivenInstancePolicyRuleRange
    {
        public int minActiveInstanceCount;
        public int maxActiveInstanceCount = int.MaxValue;
        public int minDirtyBasisPoints;
        public int maxDirtyBasisPoints =
            GpuDrivenInstancePolicyContract.BasisPointScale;
        public int minUploadCallCount;
        public int maxUploadCallCount = int.MaxValue;
        public int minUploadAmplificationBasisPoints;
        public int maxUploadAmplificationBasisPoints = int.MaxValue;
        public int minVisibleBasisPoints;
        public int maxVisibleBasisPoints =
            GpuDrivenInstancePolicyContract.BasisPointScale;
        public int minHierarchyCandidateBasisPoints;
        public int maxHierarchyCandidateBasisPoints =
            GpuDrivenInstancePolicyContract.BasisPointScale;
        public int minClusterCount;
        public int maxClusterCount = int.MaxValue;
        public int minViewCount = 1;
        public int maxViewCount = 32;
    }

    [Serializable]
    public sealed class GpuDrivenInstancePolicyRule
    {
        public string ruleId = string.Empty;
        public bool holdoutAccepted;
        public string holdoutEvidenceId = string.Empty;
        public int holdoutSampleCount;

        public GpuDrivenInstanceOutputMode requiredOutputMode =
            GpuDrivenInstanceOutputMode.CulledTail;
        public GpuDrivenInstanceUploadMode uploadMode =
            GpuDrivenInstanceUploadMode.Full;
        public GpuDrivenInstanceCullingMode cullingMode =
            GpuDrivenInstanceCullingMode.Flat;
        public GpuPrimitiveBackend primitiveBackend =
            GpuPrimitiveBackend.Portable;

        public int requiredConsecutiveFrames = 2;
        public GpuDrivenInstancePolicyRuleRange enter =
            new GpuDrivenInstancePolicyRuleRange();
        public GpuDrivenInstancePolicyRuleRange exit =
            new GpuDrivenInstancePolicyRuleRange();
    }
}
