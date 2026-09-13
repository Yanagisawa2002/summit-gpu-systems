using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
namespace Summit.ActualWorkloads
{
    public static class BuildActualPlayer
    {
        public static void Run()
        {
            try {
                string output=Environment.GetEnvironmentVariable("SUMMIT_ACTUAL_PLAYER");
                if(string.IsNullOrEmpty(output)||File.Exists(output))throw new ArgumentException("A new Player output is required");
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
                PlayerSettings.runInBackground=true;PlayerSettings.companyName="SUMMIT";PlayerSettings.productName="SummitExternalReplay";
                var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene,"Assets/ExternalReplay.unity");
                var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{"Assets/ExternalReplay.unity"},locationPathName=output,target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
                if(report.summary.result!=BuildResult.Succeeded)throw new InvalidOperationException("Player build: "+report.summary.result);
                EditorApplication.Exit(0);
            }catch(Exception e){Debug.LogException(e);EditorApplication.Exit(2);}
        }
    }
}
