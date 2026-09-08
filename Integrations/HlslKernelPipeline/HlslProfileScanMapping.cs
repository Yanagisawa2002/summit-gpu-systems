using System;
using System.Collections.Generic;
using EdwinLiu.HlslPerf;
using Summit.GpuPrimitives;

namespace Summit.HlslIntegration
{
    public static class HlslProfileScanMapping
    {
        static readonly Dictionary<string,int> FixedDefines=new Dictionary<string,int>(StringComparer.Ordinal)
        {
            {"HLSLPERF_SCAN_WAVE_TILED",1},{"HLSLPERF_SCAN_BACKEND",3},{"HLSLPERF_SCAN_OPERATOR",1},
            {"HLSLPERF_GROUP_SIZE",256},{"HLSLPERF_ELEMENTS_PER_THREAD",4},{"HLSLPERF_SINGLE_PASS_ITEMS_SCALE",4},
            {"HLSLPERF_SINGLE_PASS_GROUPS",256},{"HLSLPERF_VECTOR_WIDTH",4},{"HLSLPERF_WAVE_SIZE",32},{"HLSLPERF_WAVE_TILED_MAX_POLLS",4}
        };
        // All expected identities and mappedAssetSha256 come from a reviewed, app-owned
        // deployment catalog. Never populate them by copying fields from profileJson.
        // Revalidate here with strict policy so a weak/historical resolution cannot bypass it.
        public static bool TryResolveAndMap(string profileJson,HlslPerfRuntimeFingerprint runtime,
            string expectedManifestSha256,string expectedKernelSha256,HlslPerfDeploymentIdentity deployment,
            string mappedAssetSha256,HlslScanArtifact artifact,string runtimeDeviceDriverId,
            out HlslScanSelection selection,out string reason)
        {
            selection=null;
            if(artifact==null||deployment==null||mappedAssetSha256!=artifact.AssetSha256)
            {reason="app-owned-profile-to-artifact-mapping-missing";return false;}
            if(!HlslPerfProfileConsumer.TryResolveJson(profileJson,runtime,"exclusive-scan-u32-v1",expectedManifestSha256,expectedKernelSha256,
                HlslPerfCompatibilityPolicy.ExactDeviceAndDriver,out var resolved,deployment,false))
            {reason="upstream-scan-profile-not-validated: "+resolved.Reason;return false;}
            if(deployment.KernelAbiVersion!=HlslScanArtifact.Abi || resolved.CandidateId!=deployment.CandidateId ||
                string.IsNullOrEmpty(deployment.DefinesSha256) || HlslPerfProfileConsumer.ComputeDefinesSha256(resolved.Defines)!=deployment.DefinesSha256)
            {reason="upstream-deployment-mismatch";return false;}
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var define in resolved.Defines)
            {
                if(define==null || !seen.Add(define.name) || !FixedDefines.TryGetValue(define.name,out int value) || value!=define.value)
                {reason="profile-has-no-exact-unity-variant";return false;}
            }
            if(seen.Count!=FixedDefines.Count){reason="profile-variant-defines-incomplete";return false;}
            var request=new HlslScanSelection{SourceCommit=artifact.SourceCommit,AssetSha256=artifact.AssetSha256,
                VariantId=HlslScanArtifact.Variant,KernelAbi=HlslScanArtifact.Abi,DeviceDriverId=runtimeDeviceDriverId};
            if(!artifact.Accepts(request,runtimeDeviceDriverId,out reason))return false;
            selection=request;return true;
        }
    }
}
