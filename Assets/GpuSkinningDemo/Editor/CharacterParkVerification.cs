using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GpuSkinningDemo.Editor
{
    public static class CharacterParkVerification
    {
        private static readonly List<string> results = new List<string>();
        private static readonly List<string> errors = new List<string>();
        private static int stage, warnings;
        private static double next;
        private static bool running;
        private static Transform probe;
        private static Vector3 probeStart;

        [MenuItem("Tools/GPU Skinning/Verify Character Park")]
        public static void Run()
        {
            if (!EditorApplication.isPlaying || Object.FindObjectOfType<CharacterParkDemo>() == null)
                throw new InvalidOperationException("Play CharacterPark scene first.");
            if (running) throw new InvalidOperationException("Verification already running.");
            results.Clear(); errors.Clear(); stage = 0; warnings = 0; running = true;
            next = EditorApplication.timeSinceStartup;
            Application.logMessageReceived += Log;
            EditorApplication.update += Tick;
        }

        private static void Log(string message, string trace, LogType type)
        {
            if (type == LogType.Warning) warnings++;
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception) errors.Add(message);
        }

        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            try
            {
                var demo = Object.FindObjectOfType<CharacterParkDemo>();
                if (demo == null || !EditorApplication.isPlaying) throw new InvalidOperationException("CharacterPark stopped during verification.");
                if (stage == 0)
                {
                    VerifyTextures(demo.characters);
                    VerifyRoutes();
                    demo.SetPlayback(false, 1);
                    demo.Configure(0, 1);
                    demo.SetView(0);
                    probe = Object.FindObjectOfType<Animator>().transform;
                    probeStart = probe.position;
                }
                else if (stage == 1)
                {
                    if (Vector3.Distance(probe.position, probeStart) < 0.05f) throw new InvalidOperationException("Root did not move on its path.");
                    results.Add("Root movement: PASS.");
                    DemoVerification.Capture(demo.demoCamera, "Park_Overview");
                    demo.SetView(1);
                }
                else if (stage == 2) { DemoVerification.Capture(demo.demoCamera, "Park_Animator"); demo.SetView(2); }
                else if (stage == 3) { DemoVerification.Capture(demo.demoCamera, "Park_BAT"); demo.SetView(3); }
                else if (stage == 4) { DemoVerification.Capture(demo.demoCamera, "Park_VAT"); demo.Configure(0, 1, 1); }
                else if (stage == 5) { DemoVerification.Capture(demo.demoCamera, "Park_Idle"); demo.Configure(0, 10, 2); demo.SetView(0); }
                else if (stage == 6) { DemoVerification.Capture(demo.demoCamera, "Park_Walk_90"); demo.Configure(0, 100, 3); }
                else if (stage == 7)
                {
                    DemoVerification.Capture(demo.demoCamera, "Park_Run_900");
                    if (Object.FindObjectsOfType<Animator>().Length != 300) throw new InvalidOperationException("Expected 300 native Animator instances.");
                    results.Add("Idle / Walk / Run, 9 / 90 / 900 characters: rendered.");
                    demo.SetPlayback(true, 1);
                    DemoVerification.Capture(demo.demoCamera, "Park_Pause_A");
                }
                else if (stage == 8)
                {
                    DemoVerification.Capture(demo.demoCamera, "Park_Pause_B");
                    if (Convert.ToBase64String(File.ReadAllBytes("Docs/Verification/Park_Pause_A.png")) !=
                        Convert.ToBase64String(File.ReadAllBytes("Docs/Verification/Park_Pause_B.png")))
                        throw new InvalidOperationException("Paused park images differ.");
                    results.Add("Pause: identical URP camera PNGs after two editor seconds.");
                    demo.Configure(1, 1, 2);
                    demo.SetPlayback(false, 0.5f);
                    demo.SetView(1);
                }
                else if (stage == 9) { DemoVerification.Capture(demo.demoCamera, "Park_Animator_Only"); demo.Configure(2, 1, 2); demo.SetView(2); }
                else if (stage == 10) { DemoVerification.Capture(demo.demoCamera, "Park_BAT_Only"); demo.Configure(3, 1, 2); demo.SetView(3); }
                else if (stage == 11)
                {
                    DemoVerification.Capture(demo.demoCamera, "Park_VAT_Only");
                    results.Add("Animator-only / BAT-only / VAT-only rendering at speed 0.5: PASS.");
                    demo.SetPlayback(false, 1); demo.Configure(0, 1); demo.SetView(2);
                    Finish(); return;
                }
                stage++;
                next = EditorApplication.timeSinceStartup + 2;
            }
            catch (Exception e) { errors.Add(e.ToString()); Finish(); Debug.LogException(e); }
        }

        private static void VerifyRoutes()
        {
            for (int kind = 0; kind < 3; kind++)
            {
                var path = new ParkMotionPath(kind);
                path.Evaluate(0, out var start, out _);
                path.Evaluate(path.Length, out var end, out _);
                if (Vector3.Distance(start, end) > 0.0001f) throw new InvalidOperationException("Route is not closed.");
                for (int i = 0; i < 100; i++)
                {
                    float d = path.Length * (i + 0.35f) / 100;
                    path.Evaluate(d, out var a, out _); path.Evaluate(d + 0.001f, out var b, out _);
                    if (Mathf.Abs(Vector3.Distance(a, b) - 0.001f) > 0.0001f)
                        throw new InvalidOperationException("Route arc-length lookup failed.");
                }
                results.Add($"Route {kind}: closed, constant-distance sampling PASS; length={path.Length:F4}.");
            }
        }

        private static void VerifyTextures(GpuAnimationAsset[] assets)
        {
            foreach (var asset in assets)
            {
                if (ShaderUtil.ShaderHasError(asset.material.shader)) throw new InvalidOperationException("Animation shader has errors.");
                var bones = asset.boneTexture.GetPixels(); var vertices = asset.vertexTexture.GetPixels();
                var source = asset.mesh.vertices;
                var indices = new List<Vector4>(); var weights = new List<Vector4>();
                asset.mesh.GetUVs(1, indices); asset.mesh.GetUVs(2, weights);
                float max = 0;
                foreach (var clip in asset.clips)
                    for (int f = 0; f < clip.frameCount; f++)
                        for (int v = 0; v < source.Length; v++)
                        {
                            int frame = clip.startFrame + f;
                            Vector3 bat = Vector3.zero;
                            for (int influence = 0; influence < 4; influence++)
                                bat += AnimationBaker.Transform(bones, asset.boneCount, frame, (int)indices[v][influence], source[v]) * weights[v][influence];
                            Color vat = vertices[(frame * source.Length + v) * 2];
                            max = Mathf.Max(max, Vector3.Distance(bat, new Vector3(vat.r, vat.g, vat.b)));
                        }
                if (max > 0.005f) throw new InvalidOperationException("BAT/VAT cross-validation failed: " + asset.name);
                results.Add($"{asset.name}: {source.Length} vertices, {asset.boneCount} bones, BAT/VAT max error={max:G9} local units PASS.");
            }
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            Application.logMessageReceived -= Log;
            running = false;
            results.Add("Runtime warnings: " + warnings);
            results.Add("Runtime errors: " + errors.Count);
            results.AddRange(errors);
            Directory.CreateDirectory("Docs/Verification");
            File.WriteAllLines("Docs/Verification/CharacterParkValidation.txt", results);
            Debug.Log("CharacterPark verification " + (errors.Count == 0 && warnings == 0 ? "PASS" : "FAIL"));
        }
    }
}
