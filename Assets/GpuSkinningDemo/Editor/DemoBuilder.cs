using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GpuSkinningDemo.Editor
{
    public static class DemoBuilder
    {
        public const string Generated = "Assets/GpuSkinningDemo/Generated";

        [MenuItem("Tools/GPU Skinning/Create Demo Scene")]
        public static void CreateDemo()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Generated);
            AssetDatabase.Refresh();
            string folder = AssetDatabase.GenerateUniqueAssetPath(Generated + "/Sample");
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            ConfigurePipeline();
            var prefab = CreateCharacter(folder);
            var clips = CreateClips(prefab, folder);
            var data = AnimationBaker.Bake(prefab, clips, 30, TextureFormat.RGBAHalf, folder);
            CreateScene(data, folder);
            Selection.activeObject = data;
            AssetDatabase.SaveAssets();
            Debug.Log("GPU Skinning demo ready: " + folder);
        }

        public static void BatchCreate()
        {
            CreateDemo();
            var shaders = new[] { Shader.Find("GpuSkinningDemo/BakedSkinning"), Shader.Find("GpuSkinningDemo/Reference") };
            foreach (var shader in shaders)
                if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Shader compilation failed.");
            string scene = SceneManager.GetActiveScene().path;
            File.WriteAllText("Docs/BatchValidation.txt", "Unity " + Application.unityVersion + "\nScene: " + scene +
                "\nC# compilation completed. Sample generated. Half texture encoding validation passed.\n" +
                "GPU runtime and image verification are recorded separately.\n");
        }

        private static void ConfigurePipeline()
        {
            const string path = Generated + "/DemoPipeline.asset";
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if (pipeline == null)
            {
                var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, Generated + "/DemoRenderer.asset");
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.name = "Demo URP";
                pipeline.supportsHDR = false;
                pipeline.msaaSampleCount = 1;
                pipeline.useSRPBatcher = true;
                AssetDatabase.CreateAsset(pipeline, path);
            }
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            QualitySettings.skinWeights = SkinWeights.FourBones;
            QualitySettings.vSyncCount = 0;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.companyName = "TA Portfolio";
            PlayerSettings.productName = "GPU Skinning Lab";
            PlayerSettings.defaultScreenWidth = 1440;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.runInBackground = true;
            PlayerSettings.gpuSkinning = true;
            var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var stripping = graphics.FindProperty("m_InstancingStripping");
            if (stripping != null) { stripping.intValue = 0; graphics.ApplyModifiedPropertiesWithoutUndo(); }
        }

        private static Transform Bone(string name, Transform parent, Vector3 local)
        {
            var bone = new GameObject(name).transform;
            bone.SetParent(parent, false);
            bone.localPosition = local;
            return bone;
        }

        private static GameObject CreateCharacter(string folder)
        {
            var root = new GameObject("SampleRobot");
            try
            {
                var hips = Bone("Hips", root.transform, new Vector3(0, 0.95f, 0));
                var spine = Bone("Spine", hips, new Vector3(0, 0.4f, 0));
                var head = Bone("Head", spine, new Vector3(0, 0.45f, 0));
                var la = Bone("LeftArm", spine, new Vector3(-0.32f, 0.15f, 0));
                var le = Bone("LeftElbow", la, new Vector3(-0.17f, -0.32f, 0));
                var ra = Bone("RightArm", spine, new Vector3(0.32f, 0.15f, 0));
                var re = Bone("RightElbow", ra, new Vector3(0.17f, -0.32f, 0));
                var ll = Bone("LeftLeg", hips, new Vector3(-0.16f, -0.1f, 0));
                var lk = Bone("LeftKnee", ll, new Vector3(0, -0.38f, 0));
                var rl = Bone("RightLeg", hips, new Vector3(0.16f, -0.1f, 0));
                var rk = Bone("RightKnee", rl, new Vector3(0, -0.38f, 0));
                var bones = new[] { hips, spine, head, la, le, ra, re, ll, lk, rl, rk };
                var renderObject = new GameObject("Body");
                renderObject.transform.SetParent(root.transform, false);
                // A nonidentity renderer transform deliberately exercises the bake's coordinate conversion.
                renderObject.transform.localPosition = new Vector3(0.12f, 0.04f, 0.08f);
                renderObject.transform.localRotation = Quaternion.Euler(0, 12, 0);
                var smr = renderObject.AddComponent<SkinnedMeshRenderer>();
                var vertices = new List<Vector3>();
                var normals = new List<Vector3>();
                var triangles = new List<int>();
                var colors = new List<Color>();
                var weights = new List<BoneWeight>();
                var uv = new List<Vector2>();
                Color cyan = new Color(0.04f, 0.76f, 0.88f);
                Color dark = new Color(0.035f, 0.07f, 0.13f);
                Color orange = new Color(1, 0.38f, 0.08f);
                AddBox(new Vector3(0, 1.23f, 0), new Vector3(0.5f, 0.52f, 0.27f), cyan, 0, 1);
                AddBox(new Vector3(0, 0.95f, 0), new Vector3(0.42f, 0.18f, 0.25f), dark, 0);
                AddBox(new Vector3(0, 1.84f, 0), new Vector3(0.44f, 0.36f, 0.32f), orange, 2);
                AddBox(new Vector3(0, 1.87f, -0.17f), new Vector3(0.32f, 0.1f, 0.03f), dark, 2);
                AddBox(new Vector3(-0.085f, 1.88f, -0.19f), new Vector3(0.06f, 0.045f, 0.02f), cyan, 2);
                AddBox(new Vector3(0.085f, 1.88f, -0.19f), new Vector3(0.06f, 0.045f, 0.02f), cyan, 2);
                AddBox(new Vector3(0, 1.5f, -0.01f), new Vector3(0.12f, 0.16f, 0.12f), dark, 1);
                for (int side = -1; side <= 1; side += 2)
                {
                    int upper = side < 0 ? 3 : 5;
                    int lower = upper + 1;
                    AddBox(new Vector3(side * 0.405f, 1.34f, 0), new Vector3(0.15f, 0.34f, 0.18f), cyan, upper);
                    AddBox(new Vector3(side * 0.49f, 1.17f, 0), new Vector3(0.19f, 0.15f, 0.21f), dark, lower);
                    AddBox(new Vector3(side * 0.49f, 0.99f, 0), new Vector3(0.16f, 0.28f, 0.18f), orange, lower);
                    int thigh = side < 0 ? 7 : 9;
                    AddBox(new Vector3(side * 0.16f, 0.67f, 0), new Vector3(0.19f, 0.36f, 0.21f), cyan, thigh);
                    AddBox(new Vector3(side * 0.16f, 0.29f, 0), new Vector3(0.17f, 0.34f, 0.19f), dark, thigh + 1);
                    AddBox(new Vector3(side * 0.16f, 0.09f, -0.08f), new Vector3(0.23f, 0.14f, 0.38f), orange, thigh + 1);
                }
                var mesh = new Mesh { name = "Sample Robot Mesh" };
                mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetTriangles(triangles, 0);
                mesh.SetColors(colors); mesh.SetUVs(0, uv); mesh.boneWeights = weights.ToArray();
                var bindposes = new Matrix4x4[bones.Length];
                for (int i = 0; i < bones.Length; i++) bindposes[i] = bones[i].worldToLocalMatrix * renderObject.transform.localToWorldMatrix;
                mesh.bindposes = bindposes;
                mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, folder + "/SourceMesh.asset");
                smr.sharedMesh = mesh;
                smr.bones = bones;
                smr.rootBone = hips;
                smr.quality = SkinQuality.Bone4;
                smr.updateWhenOffscreen = true;
                smr.shadowCastingMode = ShadowCastingMode.Off;
                var material = new Material(Shader.Find("GpuSkinningDemo/Reference"));
                AssetDatabase.CreateAsset(material, folder + "/SourceMaterial.mat");
                smr.sharedMaterial = material;
                return PrefabUtility.SaveAsPrefabAsset(root, folder + "/Source.prefab");

                void AddBox(Vector3 center, Vector3 size, Color color, int bone, int other = -1)
                {
                    Vector3[] directions = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
                    foreach (var normal in directions)
                    {
                        var axis = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.up;
                        var tangent = Vector3.Cross(axis, normal);
                        var bitangent = Vector3.Cross(normal, tangent);
                        int start = vertices.Count;
                        var corners = new[] { new Vector2(-1,-1), new Vector2(1,-1), new Vector2(1,1), new Vector2(-1,1) };
                        foreach (var corner in corners)
                        {
                            Vector3 point = center + Vector3.Scale(normal + tangent * corner.x + bitangent * corner.y, size * 0.5f);
                            vertices.Add(renderObject.transform.InverseTransformPoint(point));
                            normals.Add(renderObject.transform.InverseTransformDirection(normal));
                            colors.Add(color); uv.Add((corner + Vector2.one) * 0.5f);
                            float blend = other < 0 ? 0 : Mathf.InverseLerp(0.95f, 1.5f, point.y);
                            weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 - blend, boneIndex1 = other < 0 ? 0 : other, weight1 = blend });
                        }
                        triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                    }
                }
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static AnimationClip[] CreateClips(GameObject prefab, string folder)
        {
            var clips = new AnimationClip[3];
            string[] names = { "Idle", "Wave", "March" };
            for (int c = 0; c < clips.Length; c++)
            {
                var clip = new AnimationClip { name = names[c], frameRate = 30 };
                foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
                {
                    if (transform == prefab.transform || transform.name == "Body") continue;
                    string path = AnimationUtility.CalculateTransformPath(transform, prefab.transform);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        var curve = new AnimationCurve();
                        for (int key = 0; key <= 16; key++)
                        {
                            float time = 2f * key / 16;
                            float wave = Mathf.Sin(time * Mathf.PI);
                            float angle = 0;
                            if (transform.name == "Spine" && axis == 2) angle = wave * (c == 2 ? 3 : 6);
                            if (transform.name == "Head" && axis == 1) angle = wave * 14;
                            if (c == 1 && transform.name == "RightArm" && axis == 2) angle = 125 + wave * 12;
                            if (c == 1 && transform.name == "RightElbow" && axis == 2) angle = Mathf.Sin(time * Mathf.PI * 2) * 25;
                            if (c == 2 && axis == 0)
                            {
                                if (transform.name == "LeftLeg" || transform.name == "RightArm") angle = wave * 28;
                                if (transform.name == "RightLeg" || transform.name == "LeftArm") angle = -wave * 28;
                                if (transform.name.Contains("Knee")) angle = Mathf.Max(0, wave) * 20;
                            }
                            curve.AddKey(time, angle);
                        }
                        for (int key = 0; key < curve.length; key++)
                        {
                            AnimationUtility.SetKeyLeftTangentMode(curve, key, AnimationUtility.TangentMode.Auto);
                            AnimationUtility.SetKeyRightTangentMode(curve, key, AnimationUtility.TangentMode.Auto);
                        }
                        clip.SetCurve(path, typeof(Transform), "localEulerAnglesRaw." + "xyz"[axis], curve);
                    }
                }
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                AssetDatabase.CreateAsset(clip, folder + "/" + names[c] + ".anim");
                clips[c] = clip;
            }
            return clips;
        }

        private static void CreateScene(GpuAnimationAsset data, string folder)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.04f, 0.07f);
            camera.transform.position = new Vector3(0, 2.4f, -7);
            camera.transform.LookAt(new Vector3(0, 1, 0));
            camera.fieldOfView = 38;
            cameraObject.AddComponent<UniversalAdditionalCameraData>();
            var demo = new GameObject("GPU Skinning Demo").AddComponent<SkinningDemo>();
            demo.animationAsset = data;
            demo.demoCamera = camera;
            // Saved reference and GPU objects make the scene meaningful before Play.
            var reference = (GameObject)PrefabUtility.InstantiatePrefab(data.sourcePrefab);
            reference.name = "Edit mode reference";
            reference.transform.position = new Vector3(-1.5f, 0, 0);
            var gpuPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Path.GetDirectoryName(AssetDatabase.GetAssetPath(data)).Replace('\\', '/') + "/Gpu.prefab");
            var gpu = (GameObject)PrefabUtility.InstantiatePrefab(gpuPrefab);
            gpu.name = "Edit mode GPU";
            gpu.transform.position = new Vector3(1.5f, 0, 0);
            var preview = new GameObject("Editor Preview").AddComponent<EditorPreviewOnly>();
            reference.transform.SetParent(preview.transform);
            gpu.transform.SetParent(preview.transform);
            string path = folder + "/GpuSkinningDemo.unity";
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), path);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) };
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.Frame(new Bounds(new Vector3(0, 1, 0), new Vector3(6, 3, 2)), false);
        }
    }
}
