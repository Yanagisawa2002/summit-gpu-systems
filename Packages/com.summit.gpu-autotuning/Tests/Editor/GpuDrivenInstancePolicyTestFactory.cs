using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning.Tests
{
    internal static class GpuDrivenInstancePolicyTestFactory
    {
        public static GpuDeviceFingerprint Device()
        {
            return new GpuDeviceFingerprint
            {
                schemaVersion = GpuDeviceFingerprint.CurrentSchemaVersion,
                vendorId = 0x10DE,
                deviceId = 0x2B85,
                vendor = "NVIDIA",
                deviceName = "NVIDIA GeForce RTX 4090",
                graphicsApi = "Direct3D12",
                graphicsVersion = "Direct3D 12.0 [level 12.2]",
                shaderLevel = 60
            };
        }

        public static GpuDrivenInstancePolicyEnvironment Environment(
            GpuDeviceFingerprint device = null,
            string unityVersion = "6000.5.2f1",
            string packageVersion =
                GpuDrivenInstancePolicyContract
                    .CurrentAutotuningPackageVersion,
            string processorType = "AMD Ryzen 9 7950X3D 16-Core Processor",
            string operatingSystem = "Windows 11  (10.0.26100) 64bit",
            string pipelineFingerprint = "pipeline-contract-sha256",
            string shaderFingerprint = "shader-contract-sha256",
            string calibrationProtocol =
                "gpu-driven-policy-calibration-v1-holdout-v1",
            string measurementFingerprint = null)
        {
            return new GpuDrivenInstancePolicyEnvironment(
                device ?? Device(),
                unityVersion,
                packageVersion,
                GpuDrivenInstancePolicyContract
                    .CurrentGpuDrivenInstancesPackageVersion,
                processorType,
                operatingSystem,
                pipelineFingerprint,
                shaderFingerprint,
                calibrationProtocol,
                measurementFingerprint ?? new string('b', 64));
        }

        public static GpuDrivenInstancePolicyProfile Profile(
            params GpuDrivenInstancePolicyRule[] rules)
        {
            return new GpuDrivenInstancePolicyProfile
            {
                schemaVersion =
                    GpuDrivenInstancePolicyProfile.CurrentSchemaVersion,
                policyContractVersion = GpuDrivenInstancePolicyContract
                    .CurrentPolicyContractVersion,
                profileRevision = 7,
                generatedUtc = "2026-08-30T00:00:00.0000000Z",
                sourceCommit = new string('c', 40),
                calibrationProtocol =
                    "gpu-driven-policy-calibration-v1-holdout-v1",
                measurementContractFingerprint = new string('b', 64),
                holdoutEvidenceSetId = new string('d', 64),
                holdoutAccepted = true,
                unityVersion = "6000.5.2f1",
                autotuningPackageVersion = GpuDrivenInstancePolicyContract
                    .CurrentAutotuningPackageVersion,
                gpuDrivenInstancesPackageVersion =
                    GpuDrivenInstancePolicyContract
                        .CurrentGpuDrivenInstancesPackageVersion,
                processorType = "AMD Ryzen 9 7950X3D 16-Core Processor",
                operatingSystem = "Windows 11  (10.0.26100) 64bit",
                pipelineContractFingerprint = "pipeline-contract-sha256",
                shaderContractFingerprint = "shader-contract-sha256",
                device = Device(),
                rules = rules
            };
        }

        public static GpuDrivenInstancePolicyRule Rule(
            string id,
            int minDirtyBasisPoints = 0,
            int maxDirtyBasisPoints = 10000,
            int minUploadCallCount = 0,
            int maxUploadCallCount = int.MaxValue,
            int minUploadAmplificationBasisPoints = 0,
            int maxUploadAmplificationBasisPoints = int.MaxValue,
            int minVisibleBasisPoints = 0,
            int maxVisibleBasisPoints = 10000,
            int minHierarchyCandidateBasisPoints = 0,
            int maxHierarchyCandidateBasisPoints = 10000,
            int minClusterCount = 0,
            int maxClusterCount = int.MaxValue,
            GpuDrivenInstanceOutputMode outputMode =
                GpuDrivenInstanceOutputMode.CulledTail,
            GpuDrivenInstanceUploadMode uploadMode =
                GpuDrivenInstanceUploadMode.Full,
            GpuDrivenInstanceCullingMode cullingMode =
                GpuDrivenInstanceCullingMode.Flat,
            GpuPrimitiveBackend primitiveBackend =
                GpuPrimitiveBackend.Portable,
            int requiredConsecutiveFrames = 1,
            int exitDirtyExpansion = 100,
            int exitVisibleExpansion = 100,
            int exitUploadCallExpansion = 1,
            int exitUploadAmplificationExpansion = 100,
            int exitHierarchyCandidateExpansion = 100,
            int exitClusterExpansion = 1)
        {
            return new GpuDrivenInstancePolicyRule
            {
                ruleId = id,
                holdoutAccepted = true,
                holdoutEvidenceId = new string('e', 64),
                holdoutSampleCount = 900,
                requiredOutputMode = outputMode,
                uploadMode = uploadMode,
                cullingMode = cullingMode,
                primitiveBackend = primitiveBackend,
                requiredConsecutiveFrames = requiredConsecutiveFrames,
                enter = new GpuDrivenInstancePolicyRuleRange
                {
                    minActiveInstanceCount = 100,
                    maxActiveInstanceCount = 100000,
                    minDirtyBasisPoints = minDirtyBasisPoints,
                    maxDirtyBasisPoints = maxDirtyBasisPoints,
                    minUploadCallCount = minUploadCallCount,
                    maxUploadCallCount = maxUploadCallCount,
                    minUploadAmplificationBasisPoints =
                        minUploadAmplificationBasisPoints,
                    maxUploadAmplificationBasisPoints =
                        maxUploadAmplificationBasisPoints,
                    minVisibleBasisPoints = minVisibleBasisPoints,
                    maxVisibleBasisPoints = maxVisibleBasisPoints,
                    minHierarchyCandidateBasisPoints =
                        minHierarchyCandidateBasisPoints,
                    maxHierarchyCandidateBasisPoints =
                        maxHierarchyCandidateBasisPoints,
                    minClusterCount = minClusterCount,
                    maxClusterCount = maxClusterCount,
                    minViewCount = 1,
                    maxViewCount = 8
                },
                exit = new GpuDrivenInstancePolicyRuleRange
                {
                    minActiveInstanceCount = 0,
                    maxActiveInstanceCount = 200000,
                    minDirtyBasisPoints = Max(
                        0,
                        minDirtyBasisPoints - exitDirtyExpansion),
                    maxDirtyBasisPoints = Min(
                        10000,
                        maxDirtyBasisPoints + exitDirtyExpansion),
                    minUploadCallCount = Max(
                        0,
                        minUploadCallCount - exitUploadCallExpansion),
                    maxUploadCallCount = SaturatingAdd(
                        maxUploadCallCount,
                        exitUploadCallExpansion),
                    minUploadAmplificationBasisPoints = Max(
                        0,
                        minUploadAmplificationBasisPoints -
                            exitUploadAmplificationExpansion),
                    maxUploadAmplificationBasisPoints = SaturatingAdd(
                        maxUploadAmplificationBasisPoints,
                        exitUploadAmplificationExpansion),
                    minVisibleBasisPoints = Max(
                        0,
                        minVisibleBasisPoints - exitVisibleExpansion),
                    maxVisibleBasisPoints = Min(
                        10000,
                        maxVisibleBasisPoints + exitVisibleExpansion),
                    minHierarchyCandidateBasisPoints = Max(
                        0,
                        minHierarchyCandidateBasisPoints -
                            exitHierarchyCandidateExpansion),
                    maxHierarchyCandidateBasisPoints = Min(
                        10000,
                        maxHierarchyCandidateBasisPoints +
                            exitHierarchyCandidateExpansion),
                    minClusterCount = Max(
                        0,
                        minClusterCount - exitClusterExpansion),
                    maxClusterCount = SaturatingAdd(
                        maxClusterCount,
                        exitClusterExpansion),
                    minViewCount = 1,
                    maxViewCount = 16
                }
            };
        }

        public static GpuDrivenInstancePolicyObservation Observation(
            GpuDrivenInstanceOutputMode outputMode =
                GpuDrivenInstanceOutputMode.CulledTail,
            int activeCount = 1000,
            int dirtyCount = 10,
            long visiblePairCount = 1000)
        {
            int dirtyRangeCount = dirtyCount == 0 ? 0 : 2;
            GpuInstanceUploadMode plannedMode = dirtyCount == 0
                ? GpuInstanceUploadMode.None
                : GpuInstanceUploadMode.DirtyRanges;
            return new GpuDrivenInstancePolicyObservation
            {
                RequiredOutputMode = outputMode,
                ActiveInstanceCount = activeCount,
                DirtyInstanceCount = dirtyCount,
                DirtyRangeCount = dirtyRangeCount,
                ViewCount = 4,
                VisiblePairCount = visiblePairCount,
                HasResidentState = true,
                ResidentInstanceCount = activeCount,
                StateRevision = 2,
                ResidentStateRevision = 1,
                SupportsDirtyRangeUpload = true,
                UploadPlan = new GpuDrivenInstanceUploadPlanFacts(
                    true,
                    true,
                    2,
                    plannedMode,
                    activeCount,
                    dirtyRangeCount,
                    dirtyCount,
                    true,
                    dirtyCount,
                    dirtyCount == 0 ? 0 : dirtyRangeCount),
                SupportsHierarchy = true,
                ClusterMetadataValid = true,
                ClusterCount = 32,
                ClusterInstanceCount = activeCount,
                InstanceLayoutRevision = 10,
                ClusterLayoutRevision = 10,
                VisibilityEstimateValid = true,
                VisibilityInputRevision = 20,
                VisibilityEstimateRevision = 20,
                HierarchyCandidateEstimateValid = true,
                HierarchyCandidatePairCount =
                    (long)activeCount * 2,
                HierarchyCandidateEstimateRevision = 20,
                HierarchyCandidateLayoutRevision = 10,
                HierarchyInstanceCapacity = activeCount,
                HierarchyClusterCapacity = 32,
                HierarchyViewCapacity = 4,
                HierarchyPairCapacity = (long)activeCount * 4,
                SupportsWaveOps = true
            };
        }

        public static GpuDrivenInstancePolicySelector Selector(
            GpuDrivenInstancePolicyProfile profile)
        {
            GpuDrivenInstancePolicyEnvironment environment = Environment();
            bool accepted = GpuDrivenInstancePolicySelector.TryCreate(
                profile,
                in environment,
                out GpuDrivenInstancePolicySelector selector,
                out GpuDrivenInstancePolicyValidationError error);
            if (!accepted)
            {
                throw new System.InvalidOperationException(
                    "Test profile was rejected: " + error);
            }
            return selector;
        }

        private static int Min(int left, int right)
        {
            return left < right ? left : right;
        }

        private static int Max(int left, int right)
        {
            return left > right ? left : right;
        }

        private static int SaturatingAdd(int value, int increment)
        {
            return value > int.MaxValue - increment
                ? int.MaxValue
                : value + increment;
        }
    }
}
