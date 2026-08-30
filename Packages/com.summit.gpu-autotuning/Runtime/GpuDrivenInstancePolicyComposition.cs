using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning
{
    /// <summary>
    /// Final execution policy after composing the independently measured
    /// instance upload/culling policy with the primitive-backend profile.
    /// </summary>
    public readonly struct GpuDrivenInstanceExecutionPolicy
    {
        internal GpuDrivenInstanceExecutionPolicy(
            GpuDrivenInstanceUploadMode uploadMode,
            GpuDrivenInstanceOutputMode outputMode,
            GpuDrivenInstanceCullingMode cullingMode,
            GpuPrimitiveBackend primitiveBackend,
            int instancePolicyRuleIndex,
            GpuDrivenInstancePolicyDecisionFlags flags,
            bool outputConstraintAccepted,
            bool primitiveProfileAccepted)
        {
            UploadMode = uploadMode;
            OutputMode = outputMode;
            CullingMode = cullingMode;
            PrimitiveBackend = primitiveBackend;
            InstancePolicyRuleIndex = instancePolicyRuleIndex;
            Flags = flags;
            OutputConstraintAccepted = outputConstraintAccepted;
            PrimitiveProfileAccepted = primitiveProfileAccepted;
        }

        public GpuDrivenInstanceUploadMode UploadMode { get; }
        public GpuDrivenInstanceOutputMode OutputMode { get; }
        public GpuDrivenInstanceCullingMode CullingMode { get; }
        public GpuPrimitiveBackend PrimitiveBackend { get; }
        public int InstancePolicyRuleIndex { get; }
        public GpuDrivenInstancePolicyDecisionFlags Flags { get; }
        public bool OutputConstraintAccepted { get; }
        public bool PrimitiveProfileAccepted { get; }
    }

    /// <summary>
    /// Explicit boundary between two evidence contracts: PR7 selects only
    /// upload and culling, while PR1 resolves the primitive backend. Output is
    /// caller-owned semantics and is never treated as a tunable axis here.
    /// </summary>
    public static class GpuDrivenInstancePolicyComposition
    {
        public static GpuDrivenInstanceExecutionPolicy Compose(
            in GpuDrivenInstancePolicyDecision instancePolicy,
            GpuDrivenInstanceOutputMode requiredOutputMode,
            GpuPrimitiveBackend measuredPrimitiveBackend,
            bool supportsWaveOps)
        {
            GpuDrivenInstanceOutputMode safeOutput =
                IsOutputModeValid(requiredOutputMode)
                    ? requiredOutputMode
                    : GpuDrivenInstanceOutputMode.CulledTail;
            bool outputAccepted =
                IsOutputModeValid(requiredOutputMode) &&
                instancePolicy.OutputMode == requiredOutputMode;
            if (!outputAccepted)
            {
                return new GpuDrivenInstanceExecutionPolicy(
                    GpuDrivenInstanceUploadMode.Full,
                    safeOutput,
                    GpuDrivenInstanceCullingMode.Flat,
                    GpuPrimitiveBackend.Portable,
                    -1,
                    instancePolicy.Flags |
                        GpuDrivenInstancePolicyDecisionFlags.InvalidObservation |
                        GpuDrivenInstancePolicyDecisionFlags.ProfileFallback,
                    false,
                    false);
            }

            bool primitiveAccepted =
                measuredPrimitiveBackend == GpuPrimitiveBackend.Portable ||
                (measuredPrimitiveBackend == GpuPrimitiveBackend.WaveOps &&
                 supportsWaveOps);
            GpuPrimitiveBackend primitiveBackend = primitiveAccepted
                ? measuredPrimitiveBackend
                : GpuPrimitiveBackend.Portable;
            GpuDrivenInstancePolicyDecisionFlags flags = instancePolicy.Flags;
            if (!primitiveAccepted)
            {
                flags |= GpuDrivenInstancePolicyDecisionFlags
                    .BackendGateFallback;
            }

            return new GpuDrivenInstanceExecutionPolicy(
                instancePolicy.UploadMode,
                requiredOutputMode,
                instancePolicy.CullingMode,
                primitiveBackend,
                instancePolicy.ProfileRuleIndex,
                flags,
                true,
                primitiveAccepted);
        }

        private static bool IsOutputModeValid(
            GpuDrivenInstanceOutputMode outputMode)
        {
            return outputMode == GpuDrivenInstanceOutputMode.CulledTail ||
                outputMode == GpuDrivenInstanceOutputMode.VisibleOnly;
        }
    }
}
