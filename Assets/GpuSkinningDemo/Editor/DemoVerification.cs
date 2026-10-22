using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace GpuSkinningDemo.Editor
{
    // Repeatable integration checks, intentionally separate from the demo's runtime UI.
    public static class DemoVerification
    {
        private static int stage;
        private static double nextStep;
        private static readonly List<string> results = new List<string>();
        private static readonly List<string> errors = new List<string>();
        private static readonly string Output = "Docs/Verification";
        private static bool running;

        [MenuItem("Tools/GPU Skinning/Verify Running Demo")]
        public static void VerifyRunningDemo()
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play mode first.");
            if (running) throw new InvalidOperationException("Verification is already running.");
            Directory.CreateDirectory(Output);
            results.Clear(); errors.Clear(); stage = 0;
            running = true;
            Application.logMessageReceived += CaptureError;
            nextStep = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }

        private static void CaptureError(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message);
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextStep) return;
            try
            {
                var demo = Object.FindObjectOfType<SkinningDemo>();
                if (demo == null || !EditorApplication.isPlaying) throw new InvalidOperationException("Running demo was not found.");
                if (stage == 0)
                {
                    foreach (var shader in new[] { demo.animationAsset.material.shader, Shader.Find("GpuSkinningDemo/Reference") })
                        if (ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Shader compilation failed: " + shader.name);
                    results.Add("Unity " + Application.unityVersion + "; " + SystemInfo.graphicsDeviceName + "; " + SystemInfo.graphicsDeviceType);
                    demo.Configure(0, 1, 0);
                }
                else if (stage <= 3)
                {
                    Capture(demo.demoCamera, "Comparison_" + demo.animationAsset.clips[stage - 1].name);
                    results.Add("Rendered comparison: " + demo.animationAsset.clips[stage - 1].name);
                    if (stage < 3) demo.Configure(0, 1, stage);
                    else demo.Configure(0, 100, 1, true);
                }
                else if (stage == 4)
                {
                    Capture(demo.demoCamera, "Comparison_100_Phases");
                    results.Add("Rendered 100 characters per side, different animation phases.");
                    demo.Configure(2, 1000, 2, true);
                }
                else if (stage == 5)
                {
                    Capture(demo.demoCamera, "GPU_1000");
                    results.Add("Rendered 1000 GPU instances, two submissions of 500 instances.");
                    demo.SetPlayback(true, 1);
                    Capture(demo.demoCamera, "Paused_A");
                }
                else if (stage == 6)
                {
                    Capture(demo.demoCamera, "Paused_B");
                    var first = File.ReadAllBytes(Output + "/Paused_A.png");
                    var second = File.ReadAllBytes(Output + "/Paused_B.png");
                    if (Convert.ToBase64String(first) != Convert.ToBase64String(second))
                        throw new InvalidOperationException("Paused camera frames differ.");
                    results.Add("Pause: camera images remained identical across two editor seconds.");
                    demo.SetPlayback(false, 0.5f);
                    demo.Configure(1, 1000, 2, true);
                }
                else if (stage == 7)
                {
                    Capture(demo.demoCamera, "Animator_1000");
                    results.Add("Rendered 1000 native Animator references at speed 0.5.");
                    demo.SetPlayback(false, 1);
                    demo.Configure(0, 1, 1);
                    Finish();
                    return;
                }
                stage++;
                nextStep = EditorApplication.timeSinceStartup + 2;
            }
            catch (Exception exception)
            {
                errors.Add(exception.ToString());
                Finish();
                Debug.LogException(exception);
            }
        }

        internal static void Capture(Camera camera, string name)
        {
            const int width = 1440, height = 900;
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            float aspect = camera.aspect;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.aspect = (float)width / height;
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                var pixels = texture.GetPixels32();
                int colored = 0, magenta = 0;
                foreach (var pixel in pixels)
                {
                    if (Mathf.Max(pixel.r, pixel.g, pixel.b) - Mathf.Min(pixel.r, pixel.g, pixel.b) > 45) colored++;
                    if (pixel.r > 230 && pixel.b > 230 && pixel.g < 30) magenta++;
                }
                if (colored < 200 || magenta > 50) throw new InvalidOperationException("Empty frame or error shader in " + name);
                File.WriteAllBytes(Output + "/" + name + ".png", texture.EncodeToPNG());
            }
            finally
            {
                camera.aspect = aspect;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(texture);
            }
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= CaptureError;
            running = false;
            results.Add("Runtime errors: " + errors.Count);
            results.AddRange(errors);
            results.Add("Screenshots use the real URP camera rendering path; visual comparison and Profiler analysis are separate checks.");
            File.WriteAllLines(Output + "/RuntimeValidation.txt", results);
            Debug.Log("Runtime verification " + (errors.Count == 0 ? "PASS" : "FAIL") + ". See " + Output);
        }

        [MenuItem("Tools/GPU Skinning/Verify Float Bake")]
        public static void VerifyFloatBake()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before baking.");
            var demo = Object.FindObjectOfType<SkinningDemo>();
            var clips = new List<AnimationClip>();
            foreach (var clip in demo.animationAsset.referenceController.animationClips) if (!clips.Contains(clip)) clips.Add(clip);
            // The source without Animator or any runtime script is stored alongside the sample scene.
            string sceneFolder = Path.GetDirectoryName(demo.gameObject.scene.path).Replace('\\', '/');
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sceneFolder + "/Source.prefab");
            var data = AnimationBaker.Bake(source, clips.ToArray(), 30, TextureFormat.RGBAFloat, sceneFolder);
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + "/FloatValidation.txt", data.validation);
        }
    }
}
