using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.PublicIntegration
{
    public static class IntegrationBuild
    {
        [Serializable] public class ContentRecord { public string file,sha256; public long bytes; public uint crc; }
        [Serializable] public class BuildReceipt { public string unityVersion,sourceCommit,buildGuid,buildOptions,player,completedUtc; public bool development; public ContentRecord[] content; }
        public static void BuildRelease()
        {
            Directory.CreateDirectory("Assets/Generated");
            Directory.CreateDirectory("Assets/StreamingAssets/IntegrationContent");
            var bundles=new AssetBundleBuild[2];
            for(int b=0;b<2;b++)
            {
                string bytes="Assets/Generated/content-"+b+".bytes";
                using(var stream=new BinaryWriter(File.Create(bytes)))
                    for(int i=0;i<IntegrationFixture.ContentSlots;i++)
                    {
                        var sample=IntegrationFixture.ContentSample(b,i);
                        stream.Write(sample.X);stream.Write(sample.Y);stream.Write(sample.Z);stream.Write(sample.Payload);
                    }
                var texture=new Texture2D(256,256,TextureFormat.RGBA32,false);
                var pixels=new Color32[256*256];
                for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32((byte)(i*17+b*61),(byte)(i/256+b*23),(byte)(i*31),255);
                texture.SetPixels32(pixels);texture.Apply();
                string tex="Assets/Generated/palette-"+b+".asset";
                if(File.Exists(tex))AssetDatabase.DeleteAsset(tex);
                AssetDatabase.CreateAsset(texture,tex);
                bundles[b]=new AssetBundleBuild{assetBundleName="content-"+b,assetNames=new[]{bytes,tex}};
            }
            AssetDatabase.Refresh();
            if(BuildPipeline.BuildAssetBundles("Assets/StreamingAssets/IntegrationContent",bundles,
                BuildAssetBundleOptions.ChunkBasedCompression|BuildAssetBundleOptions.ForceRebuildAssetBundle,
                BuildTarget.StandaloneWindows64)==null)throw new Exception("AssetBundle build failed");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var host=new GameObject("Public GPU Integration");host.AddComponent<IntegrationPlayer>();
            EditorSceneManager.SaveScene(scene,"Assets/Generated/Integration.unity");
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            PlayerSettings.fullScreenMode=FullScreenMode.Windowed;PlayerSettings.defaultScreenWidth=1280;PlayerSettings.defaultScreenHeight=720;
            PlayerSettings.SplashScreen.show=false;PlayerSettings.enableFrameTimingStats=true;
            PlayerSettings.runInBackground=true;PlayerSettings.companyName="Yanagisawa2002";PlayerSettings.productName="Public GPU Integration";
            var args=Environment.GetCommandLineArgs();
            int pathIndex=Array.IndexOf(args,"-integration-player");
            string output=pathIndex>=0?args[pathIndex+1]:"Builds/Release/Integration.exe";
            var options=new BuildPlayerOptions{scenes=new[]{"Assets/Generated/Integration.unity"},locationPathName=output,
                target=BuildTarget.StandaloneWindows64,options=BuildOptions.None};
            var report=BuildPipeline.BuildPlayer(options);
            if(report.summary.result!=BuildResult.Succeeded)throw new Exception("Release Player build failed");
            var content=bundles.Select(b=>{
                string p="Assets/StreamingAssets/IntegrationContent/"+b.assetBundleName;
                BuildPipeline.GetCRCForAssetBundle(p,out uint crc);
                using(var sha=SHA256.Create())return new ContentRecord{file=b.assetBundleName,bytes=new FileInfo(p).Length,
                    sha256=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(p))).Replace("-","").ToLowerInvariant(),crc=crc};
            }).ToArray();
            Directory.CreateDirectory("Artifacts");
            File.WriteAllText("Artifacts/release-build.json",JsonUtility.ToJson(new BuildReceipt{unityVersion=Application.unityVersion,
                sourceCommit=Environment.GetEnvironmentVariable("INTEGRATION_SOURCE_SHA"),buildGuid=report.summary.guid.ToString(),
                buildOptions=options.options.ToString(),development=false,player=output,completedUtc=DateTime.UtcNow.ToString("O"),content=content},true));
        }
    }
}
