using System;
using System.IO;
using Summit.GpuPrimitives;
using UnityEditor;
using UnityEngine;

namespace Summit.HlslIntegration
{
    // Install outside portable Assets; copy this optional Editor adapter to a host.
    public static class HlslScanConsumerFactory
    {
        public static bool TryCreate(string artifactDirectory,string pinnedCommit,string pinnedAssets,
            HlslScanSelection selection,string runtimeDeviceDriverId,int capacity,bool allowUnmeasured,
            out GpuHlslScanConsumer consumer,out string reason)
        {
            string directory=Path.GetFullPath(artifactDirectory);
            string manifestPath=Path.Combine(directory,"consumer-manifest.json");
            var manifest=JsonUtility.FromJson<HlslScanManifest>(File.ReadAllText(manifestPath));
            var artifact=HlslScanArtifact.Verify(manifest,path=>File.ReadAllBytes(Path.Combine(directory,path)),pinnedCommit,pinnedAssets);
            string assetRoot=Path.GetFullPath(Application.dataPath)+Path.DirectorySeparatorChar;
            string shaderPath=Path.GetFullPath(Path.Combine(directory,manifest.shader));
            if(!shaderPath.StartsWith(assetRoot,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Verified shader must be installed below this host's Assets.");
            string unityPath="Assets/"+shaderPath.Substring(assetRoot.Length).Replace('\\','/');
            var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>(unityPath);
            // Asset identity is checked from files selected by the consumer, not from
            // profile defines or a Resource path supplied by an incoming profile.
            return GpuHlslScanConsumer.TryCreate(artifact,shader,selection,runtimeDeviceDriverId,capacity,allowUnmeasured,out consumer,out reason);
        }
    }
}
