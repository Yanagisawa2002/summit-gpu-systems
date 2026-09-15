using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
namespace Summit.WholeTaskMD
{
    public static class BuildMolecularPlayer
    {
        public static void Run()
        {
            try
            {
                string output = Environment.GetEnvironmentVariable("SUMMIT_MD_PLAYER");
                if (string.IsNullOrEmpty(output) || File.Exists(output)) throw new ArgumentException("New Player output required");
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
                PlayerSettings.runInBackground = true; PlayerSettings.companyName = "PublicWholeTask"; PlayerSettings.productName = "SummitMolecularStep";
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, "Assets/Molecular.unity");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Molecular.unity" }, locationPathName = output,
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.None });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException(report.summary.result.ToString());
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(2); }
        }
    }
}
