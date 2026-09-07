using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
namespace Summit.IndexCostDiagnostics
{
    public static class IndexCostBuild
    {
        public static void BuildRelease()
        {
            Directory.CreateDirectory("Assets/Generated");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            new GameObject("Index Cost Diagnostics").AddComponent<IndexCostPlayer>();
            EditorSceneManager.SaveScene(scene,"Assets/Generated/IndexCosts.unity");
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            PlayerSettings.fullScreenMode=FullScreenMode.Windowed;PlayerSettings.defaultScreenWidth=1280;PlayerSettings.defaultScreenHeight=720;
            PlayerSettings.SplashScreen.show=false;PlayerSettings.enableFrameTimingStats=true;
            PlayerSettings.runInBackground=true;PlayerSettings.companyName="Yanagisawa2002";PlayerSettings.productName="Index Cost Diagnostics";
            var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-index-cost-player");
            if(at<0)throw new Exception("Output path required");
            var options=new BuildPlayerOptions{scenes=new[]{"Assets/Generated/IndexCosts.unity"},locationPathName=args[at+1],target=BuildTarget.StandaloneWindows64,options=BuildOptions.None};
            var report=BuildPipeline.BuildPlayer(options);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[at+1]),"build-summary.json"),JsonUtility.ToJson(new Receipt{result=report.summary.result.ToString(),errors=report.summary.totalErrors,warnings=report.summary.totalWarnings,buildGuid=report.summary.guid.ToString(),unity=Application.unityVersion},true));
            if(report.summary.result!=BuildResult.Succeeded)throw new Exception("Release build failed");
        }
        [Serializable] public class Receipt {public string result,buildGuid,unity;public int errors,warnings;}
    }
}
