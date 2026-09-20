using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.VisibleTiles.Editor
{
    public static class Build
    {
        [Serializable] public sealed class StagedFile { public string path, sha256; }
        [Serializable] public sealed class StagedManifest { public StagedFile[] files; }
        static void VerifyStaging()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            var manifest=JsonUtility.FromJson<StagedManifest>(File.ReadAllText("Assets/Resources/staging.json"));
            if(manifest==null || manifest.files==null || manifest.files.Length!=9)
                throw new InvalidOperationException("Missing or incomplete staging manifest.");
            foreach(var entry in manifest.files)
            {
                string path=Path.GetFullPath(Path.Combine(root,entry.path));
                if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Staging path escapes project.");
                using(var sha=System.Security.Cryptography.SHA256.Create()) using(var stream=File.OpenRead(path))
                    if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant()!=entry.sha256)
                        throw new InvalidOperationException("Staged source drift: "+entry.path+". Prepare a fresh project.");
            }
        }
        [MenuItem("Tools/Visible Tiles/Create validation scene")]
        public static void Scene()
        {
            VerifyStaging();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            new GameObject("Visible Tiles Validation").AddComponent<VisibleTilesDemo>();
            Directory.CreateDirectory("Assets/Generated");
            if(!EditorSceneManager.SaveScene(scene,"Assets/Generated/VisibleTiles.unity"))
                throw new InvalidOperationException("Could not save generated validation scene.");
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene("Assets/Generated/VisibleTiles.unity",true)};
        }
        public static void Windows()
        {
            string[] args=Environment.GetCommandLineArgs(); int i=Array.IndexOf(args,"-visible-tiles-build");
            if(i<0 || i+1>=args.Length) throw new ArgumentException("Supply -visible-tiles-build <new-directory>/VisibleTiles.exe");
            string path=Path.GetFullPath(args[i+1]); string folder=Path.GetDirectoryName(path);
            if(Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Build output directory must be NEW.");
            if(!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone,BuildTarget.StandaloneWindows64))
                throw new NotSupportedException("Install Windows x64 build support for this Unity Editor.");
            Scene();
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            PlayerSettings.colorSpace=ColorSpace.Linear;
            Directory.CreateDirectory(folder);
            BuildReport result=BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes=new[]{"Assets/Generated/VisibleTiles.unity"}, locationPathName=path,
                target=BuildTarget.StandaloneWindows64, options=BuildOptions.None });
            if(result.summary.result!=BuildResult.Succeeded) throw new InvalidOperationException("Player build failed: "+result.summary.result);
            // Capture bytes actually shipped. This is build identity, not runtime acceptance.
            var lines=new System.Collections.Generic.List<string>();
            foreach(string file in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))
            {
                using(var sha=System.Security.Cryptography.SHA256.Create()) using(var input=File.OpenRead(file))
                    lines.Add(BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant()+"  "+file.Substring(folder.Length+1));
            }
            lines.Sort(StringComparer.Ordinal); File.WriteAllLines(Path.Combine(folder,"player-sha256.txt"),lines);
        }
    }
}
