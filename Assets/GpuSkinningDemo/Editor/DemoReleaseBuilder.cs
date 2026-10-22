using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSkinningDemo.Editor
{
    public static class DemoReleaseBuilder
    {
        [MenuItem("Tools/GPU Skinning/Build Windows Release")]
        public static void BuildWindows()
        {
            const string requiredVersion = "2022.3.62f3";
            if (Application.unityVersion != requiredVersion)
                throw new InvalidOperationException("Release requires Unity " + requiredVersion);
            PreparePortableSources();
            string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (scenes.Length == 0 || !scenes[0].EndsWith("CharacterPark.unity", StringComparison.Ordinal))
                throw new InvalidOperationException("CharacterPark must be the first enabled build scene.");
            string output = Path.GetFullPath("Builds/Windows/GpuSkinningDemo.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1440;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.resizableWindow = true;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.StrictMode
            });
            Directory.CreateDirectory("Docs/Verification");
            string summary = $"Unity: {Application.unityVersion}\nResult: {report.summary.result}\n" +
                $"Errors: {report.summary.totalErrors}\nWarnings: {report.summary.totalWarnings}\n" +
                $"Bytes: {report.summary.totalSize}\nScenes:\n{string.Join("\n", scenes)}\n";
            File.WriteAllText("Docs/Verification/WindowsBuild.txt", summary);
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException(summary);
            Debug.Log(summary);
        }

        private static void PreparePortableSources()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets/SourceCharacters" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) continue;
                // The copied FBX metadata kept a historical Avatar pointer into Landlord.
                // Generic transform animation in this demo uses its own hierarchy.
                importer.sourceAvatar = null;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                var serialized = new SerializedObject(importer);
                var previousAvatar = serialized.FindProperty("m_LastHumanDescriptionAvatarSource");
                if (previousAvatar != null) previousAvatar.objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (path.Contains("/Models/"))
                {
                    string role = Path.GetFileNameWithoutExtension(path);
                    var surface = AssetDatabase.LoadAssetAtPath<Material>(CharacterParkBuilder.Output + "/" + role + ".mat");
                    foreach (var remap in importer.GetExternalObjectMap())
                        if (remap.Key.type == typeof(Material)) importer.AddRemap(remap.Key, surface);
                }
                importer.SaveAndReimport();
            }
        }
    }
}
