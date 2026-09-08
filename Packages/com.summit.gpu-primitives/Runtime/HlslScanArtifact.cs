using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Summit.GpuPrimitives
{
    [Serializable] public sealed class HlslScanFile { public string path, sha256; }
    [Serializable] public sealed class HlslScanManifest
    {
        public string schema, performanceStatus, variantId, sourceCommit, assetSha256, semantic, kernelAbi;
        public string shader, requiredBackend, requiredShaderModel, inputTarget, outputTarget, scratchTarget;
        public int requiredWaveSize, elementStrideBytes, maximumConsumerCount, minimumBufferBytes, elementsPerBlock;
        public HlslScanFile[] files;
    }

    /// <summary>Verified installed SOURCE artifact, independently pinned by the
    /// consumer. Incoming tuning profiles cannot act as their own deployment proof.</summary>
    public sealed class HlslScanArtifact
    {
        public const string Variant = "hlslperf.scan-wave-tiled.u32.wave32.g256.b4096.v1";
        public const string Abi = "hlslperf.raw-buffer.v1";
        public const string Semantic = "exclusive-u32-sum-modulo-2^32";
        public const int TileSize = 4096;
        public const int MaxCount = 256 * 65535;
        public string SourceCommit { get; }
        public string AssetSha256 { get; }
        private HlslScanArtifact(string source, string assets) { SourceCommit=source; AssetSha256=assets; }
        static readonly string[] Paths = { "LICENSE.md", "kernels/consumer/ScanWaveTiled.compute", "kernels/include/hlslperf/scan_wave_tiled_u32.hlsli" };

        public static HlslScanArtifact Verify(HlslScanManifest manifest, Func<string, byte[]> readInstalledFile,
            string pinnedSourceCommit, string pinnedAssetSha256)
        {
            if(manifest==null || readInstalledFile==null)throw new ArgumentNullException();
            if(!Hex(pinnedSourceCommit,40)||!Hex(pinnedAssetSha256,64)||manifest.sourceCommit!=pinnedSourceCommit || manifest.assetSha256!=pinnedAssetSha256)
                throw new InvalidDataException("Source/asset deployment pin mismatch.");
            if(manifest.schema!="hlslperf.unity-scan-consumer.v1" || manifest.variantId!=Variant ||
                manifest.kernelAbi!=Abi || manifest.semantic!=Semantic || manifest.shader!=Paths[1] || manifest.requiredBackend!="D3D12" ||
                manifest.requiredShaderModel!="6_6" || manifest.requiredWaveSize!=32 || manifest.elementStrideBytes!=4 ||
                manifest.maximumConsumerCount!=MaxCount || manifest.minimumBufferBytes!=4 || manifest.elementsPerBlock!=TileSize ||
                manifest.inputTarget!="Raw" || manifest.outputTarget!="Raw" || manifest.scratchTarget!="Raw")
                throw new InvalidDataException("Unsupported consumer ABI or variant.");
            if(manifest.files==null || manifest.files.Length!=Paths.Length)throw new InvalidDataException("Complete dependency set required.");
            var files=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var file in manifest.files)
            {
                if(file==null || Array.IndexOf(Paths,file.path)<0 || !Hex(file.sha256,64) || files.ContainsKey(file.path))
                    throw new InvalidDataException("Unexpected/duplicate dependency.");
                string actual=Sha256(readInstalledFile(file.path));
                if(actual!=file.sha256)throw new InvalidDataException("Installed source differs from manifest: "+file.path);
                files.Add(file.path,actual);
            }
            var identity=new StringBuilder();
            foreach(string path in Paths)identity.Append(path).Append('\0').Append(files[path]).Append('\n');
            if(Sha256(Encoding.UTF8.GetBytes(identity.ToString()))!=pinnedAssetSha256)throw new InvalidDataException("Aggregate asset identity mismatch.");
            return new HlslScanArtifact(pinnedSourceCommit,pinnedAssetSha256);
        }
        public static string Sha256(byte[] bytes)
        { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        static bool Hex(string s,int length)
        { if(s==null||s.Length!=length)return false;foreach(char c in s)if(!(c>='0'&&c<='9'||c>='a'&&c<='f'))return false;return true; }
        public int ScratchBytes(int capacity)
        { if(capacity<1||capacity>MaxCount)throw new ArgumentOutOfRangeException(nameof(capacity));return 8+12*((capacity+TileSize-1)/TileSize); }
        public static int ScanGroups(int count)
        { if(count<0||count>MaxCount)throw new ArgumentOutOfRangeException(nameof(count));return Math.Min((count+TileSize-1)/TileSize,256); }
        public bool Accepts(HlslScanSelection selection,string runtimeDeviceDriverId,out string reason)
        {
            if(selection==null){reason="no-profile-selection";return false;}
            if(string.IsNullOrWhiteSpace(runtimeDeviceDriverId)||runtimeDeviceDriverId=="unknown"||selection.DeviceDriverId!=runtimeDeviceDriverId)
            {reason="device-driver-identity-mismatch";return false;}
            if(selection.SourceCommit!=SourceCommit||selection.AssetSha256!=AssetSha256||selection.KernelAbi!=Abi||selection.VariantId!=Variant)
            {reason="profile-deployment-identity-mismatch";return false;}
            reason="explicit-unmeasured-selection";return true;
        }
    }
    // Populate only after upstream SDK profile validation (or an explicit unmeasured
    // request). This is selection data, never used to supply the artifact's file pins.
    public sealed class HlslScanSelection
    {
        public string SourceCommit,AssetSha256,KernelAbi,VariantId,DeviceDriverId;
    }
}
