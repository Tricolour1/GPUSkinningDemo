using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GpuSkinningDemo.Editor
{
    public static class AnimationBaker
    {
        public static GpuAnimationAsset Bake(GameObject source, AnimationClip[] clips, int sampleRate,
            TextureFormat format, string outputRoot)
        {
            Validate(source, clips, sampleRate, format, outputRoot);
            string folder = AssetDatabase.GenerateUniqueAssetPath(outputRoot + "/" + source.name + "_Baked");
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            var parent = new GameObject("Bake temporary objects") { hideFlags = HideFlags.HideAndDontSave };
            parent.SetActive(false);
            GameObject instance = null;
            Mesh reference = new Mesh();
            try
            {
                instance = Clone(source, parent.transform);
                var smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Mesh original = smr.sharedMesh;
                var data = ScriptableObject.CreateInstance<GpuAnimationAsset>();
                data.name = source.name + " Animation";
                data.boneCount = smr.bones.Length;
                data.rendererToRoot = instance.transform.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                data.clips = new BakedClip[clips.Length];
                int totalFrames = 0;
                for (int i = 0; i < clips.Length; i++)
                {
                    int frames = Mathf.CeilToInt(clips[i].length * sampleRate) + 1;
                    data.clips[i] = new BakedClip { name = clips[i].name, duration = clips[i].length,
                        startFrame = totalFrames, frameCount = frames, loop = clips[i].isLooping };
                    totalFrames = checked(totalFrames + frames);
                }
                int usedPixels = checked(totalFrames * data.boneCount * 3);
                int maxSize = Mathf.Min(SystemInfo.maxTextureSize, 4096);
                if (usedPixels > (long)maxSize * maxSize)
                    throw new InvalidOperationException("Animation exceeds one 4096x4096 texture. Reduce sample rate or clip count.");
                int width = Mathf.Min(256, maxSize);
                int height = Mathf.CeilToInt((float)usedPixels / width);
                while (height > maxSize) { width *= 2; height = Mathf.CeilToInt((float)usedPixels / width); }
                var texture = new Texture2D(width, height, format, false, true)
                { name = "BoneMatrices", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                var pixels = new Color[width * height];
                var bindposes = original.bindposes;
                bool hasBounds = false;
                Bounds bounds = default;
                for (int c = 0; c < clips.Length; c++)
                {
                    Object.DestroyImmediate(instance);
                    instance = Clone(source, parent.transform);
                    smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    var clip = data.clips[c];
                    for (int f = 0; f < clip.frameCount; f++)
                    {
                        Progress(c, f, clips.Length, clip.frameCount);
                        clips[c].SampleAnimation(instance, clip.duration * f / (clip.frameCount - 1));
                        for (int b = 0; b < data.boneCount; b++)
                        {
                            Matrix4x4 matrix = smr.transform.worldToLocalMatrix * smr.bones[b].localToWorldMatrix * bindposes[b];
                            int index = ((clip.startFrame + f) * data.boneCount + b) * 3;
                            for (int row = 0; row < 3; row++)
                            {
                                Vector4 value = matrix.GetRow(row);
                                for (int component = 0; component < 4; component++)
                                    if (float.IsNaN(value[component]) || float.IsInfinity(value[component]) ||
                                        (format == TextureFormat.RGBAHalf && Mathf.Abs(value[component]) > 65504))
                                        throw new InvalidOperationException("Matrix contains invalid values or exceeds Half range. Use Float or fix input.");
                                pixels[index + row] = value;
                            }
                        }
                        smr.BakeMesh(reference);
                        foreach (var vertex in reference.vertices)
                        {
                            if (!hasBounds) { bounds = new Bounds(vertex, Vector3.zero); hasBounds = true; }
                            else bounds.Encapsulate(vertex);
                        }
                    }
                }
                texture.SetPixels(pixels);
                texture.Apply(false, false);
                AssetDatabase.CreateAsset(texture, folder + "/BoneMatrices.asset");
                data.boneTexture = texture;
                var mesh = Object.Instantiate(original);
                mesh.name = original.name + " GPU";
                var weights = original.boneWeights;
                var indices = new List<Vector4>(weights.Length);
                var influences = new List<Vector4>(weights.Length);
                foreach (var w in weights)
                {
                    indices.Add(new Vector4(w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3));
                    influences.Add(new Vector4(w.weight0, w.weight1, w.weight2, w.weight3));
                }
                mesh.SetUVs(1, indices);
                mesh.SetUVs(2, influences);
                mesh.boneWeights = Array.Empty<BoneWeight>();
                mesh.bindposes = Array.Empty<Matrix4x4>();
                if (mesh.colors.Length == 0)
                {
                    var colors = new Color[mesh.vertexCount];
                    for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
                    mesh.colors = colors;
                }
                bounds.Expand(0.04f);
                mesh.bounds = bounds;
                AssetDatabase.CreateAsset(mesh, folder + "/GpuMesh.asset");
                data.mesh = mesh;
                var shader = Shader.Find("GpuSkinningDemo/BakedSkinning");
                if (shader == null) throw new InvalidOperationException("GPU shader is missing.");
                var material = new Material(shader) { name = "Baked GPU", enableInstancing = true };
                CopySurface(smr.sharedMaterial, material);
                material.SetTexture("_BoneTexture", texture);
                material.SetFloat("_BoneCount", data.boneCount);
                material.SetVector("_Playback", data.clips[0].Playback());
                AssetDatabase.CreateAsset(material, folder + "/GpuMaterial.mat");
                data.material = material;
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Reference.controller");
                for (int i = 0; i < clips.Length; i++)
                {
                    var state = controller.layers[0].stateMachine.AddState("Clip" + i);
                    state.motion = clips[i];
                    if (i == 0) controller.layers[0].stateMachine.defaultState = state;
                }
                data.referenceController = controller;
                Object.DestroyImmediate(instance);
                instance = Clone(source, parent.transform);
                smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                var referenceMaterial = new Material(Shader.Find("GpuSkinningDemo/Reference")) { name = "Reference surface" };
                CopySurface(smr.sharedMaterial, referenceMaterial);
                AssetDatabase.CreateAsset(referenceMaterial, folder + "/ReferenceMaterial.mat");
                smr.sharedMaterial = referenceMaterial;
                smr.quality = SkinQuality.Bone4;
                var animator = instance.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                data.sourcePrefab = PrefabUtility.SaveAsPrefabAsset(instance, folder + "/Reference.prefab");
                data.validation = ValidateEncoding(source, clips, data, parent.transform, reference);
                AssetDatabase.CreateAsset(data, folder + "/Animation.asset");
                SaveGpuPrefab(data, folder);
                File.WriteAllText(folder + "/Validation.txt", data.validation, Encoding.UTF8);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log(data.validation, data);
                return data;
            }
            catch (Exception exception)
            {
                File.WriteAllText(folder + "/FAILED.txt", exception.ToString());
                throw;
            }
            finally
            {
                Object.DestroyImmediate(reference);
                Object.DestroyImmediate(parent);
                EditorUtility.ClearProgressBar();
            }
        }

        private static GameObject Clone(GameObject source, Transform parent)
        {
            var instance = Object.Instantiate(source, parent);
            instance.name = source.name;
            instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            instance.transform.localScale = Vector3.one;
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);
            foreach (var animation in instance.GetComponentsInChildren<Animation>(true)) Object.DestroyImmediate(animation);
            return instance;
        }

        private static void Progress(int clip, int frame, int clipCount, int frameCount)
        {
            if (EditorUtility.DisplayCancelableProgressBar("Baking animation matrices",
                $"Clip {clip + 1}/{clipCount}, frame {frame + 1}/{frameCount}", (clip + (float)frame / frameCount) / clipCount))
                throw new OperationCanceledException("Bake cancelled; incomplete output marked FAILED.txt.");
        }

        private static void CopySurface(Material source, Material target)
        {
            if (source.HasProperty("_BaseMap"))
            {
                target.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
                target.SetTextureScale("_BaseMap", source.GetTextureScale("_BaseMap"));
                target.SetTextureOffset("_BaseMap", source.GetTextureOffset("_BaseMap"));
            }
            else if (source.HasProperty("_MainTex")) target.SetTexture("_BaseMap", source.GetTexture("_MainTex"));
            if (source.HasProperty("_BaseColor")) target.SetColor("_BaseColor", source.GetColor("_BaseColor"));
            else if (source.HasProperty("_Color")) target.SetColor("_BaseColor", source.GetColor("_Color"));
        }

        private static void Validate(GameObject source, AnimationClip[] clips, int fps, TextureFormat format, string output)
        {
            if (source == null || !AssetDatabase.Contains(source)) throw new ArgumentException("Choose a project Prefab or imported model.");
            if (fps < 1 || fps > 120) throw new ArgumentException("Sample rate must be 1 to 120.");
            if (format != TextureFormat.RGBAHalf && format != TextureFormat.RGBAFloat) throw new ArgumentException("Choose Half or Float.");
            if (!output.StartsWith("Assets/", StringComparison.Ordinal) || output.Contains("..") || !AssetDatabase.IsValidFolder(output))
                throw new ArgumentException("Output must be an existing folder below Assets.");
            var renderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length != 1) throw new ArgumentException("Exactly one SkinnedMeshRenderer is supported.");
            if (source.GetComponentsInChildren<Renderer>(true).Length != 1)
                throw new ArgumentException("Additional renderers and rigid accessories are unsupported. Use a single skinned renderer.");
            if (source.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
                throw new ArgumentException("Use a model-only Prefab without gameplay scripts.");
            var smr = renderers[0];
            var mesh = smr.sharedMesh;
            if (mesh == null || !mesh.isReadable) throw new ArgumentException("Mesh is missing or Read/Write is disabled.");
            if (mesh.subMeshCount != 1 || smr.sharedMaterials.Length != 1 || smr.sharedMaterial == null)
                throw new ArgumentException("Exactly one submesh and material are supported.");
            if (mesh.blendShapeCount > 0) throw new ArgumentException("BlendShapes are outside this demo's scope.");
            if (mesh.normals.Length != mesh.vertexCount) throw new ArgumentException("Mesh must have normals.");
            if (smr.bones.Length == 0 || mesh.bindposes.Length != smr.bones.Length) throw new ArgumentException("Bone / bind pose counts do not match.");
            foreach (var bone in smr.bones)
                if (bone == null || !bone.IsChildOf(source.transform)) throw new ArgumentException("Bones must belong to the source hierarchy.");
            var perVertex = mesh.GetBonesPerVertex();
            try { foreach (byte count in perVertex) if (count > 4) throw new ArgumentException("More than four bone influences per vertex are unsupported."); }
            finally { perVertex.Dispose(); }
            var weights = mesh.boneWeights;
            if (weights.Length != mesh.vertexCount) throw new ArgumentException("Every vertex must have skin weights.");
            foreach (var w in weights)
            {
                float total = w.weight0 + w.weight1 + w.weight2 + w.weight3;
                if (float.IsNaN(total) || Mathf.Abs(total - 1) > 0.001f || Mathf.Min(w.weight0, w.weight1, w.weight2, w.weight3) < 0)
                    throw new ArgumentException("Weights must be finite, nonnegative, and sum to one.");
                if (w.boneIndex0 < 0 || w.boneIndex1 < 0 || w.boneIndex2 < 0 || w.boneIndex3 < 0 ||
                    Mathf.Max(w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3) >= smr.bones.Length)
                    throw new ArgumentException("Bone index out of range.");
            }
            if (clips == null || clips.Length == 0) throw new ArgumentException("Add at least one animation clip.");
            foreach (var clip in clips)
            {
                if (clip == null || clip.legacy || clip.isHumanMotion || clip.length <= 0)
                    throw new ArgumentException("Only nonempty Generic clips are supported; no Legacy or Humanoid.");
                if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Length > 0)
                    throw new ArgumentException("Object reference animation curves are unsupported.");
                var bindings = AnimationUtility.GetCurveBindings(clip);
                if (bindings.Length == 0) throw new ArgumentException("Clip has no transform curves: " + clip.name);
                foreach (var binding in bindings)
                {
                    if (binding.type != typeof(Transform) || binding.path.Length == 0 ||
                        AnimationUtility.GetAnimatedObject(source, binding) == null)
                        throw new ArgumentException($"Unsupported or missing curve: {clip.name} / {binding.path} / {binding.propertyName}. Use in-place Generic transform curves.");
                    var animated = source.transform.Find(binding.path);
                    if (animated == smr.transform || smr.transform.IsChildOf(animated))
                        throw new ArgumentException("The renderer and its ancestors must remain stationary; animate bones instead: " + binding.path);
                }
            }
            var relative = source.transform.worldToLocalMatrix * smr.transform.localToWorldMatrix;
            var reconstructed = Matrix4x4.TRS(relative.GetColumn(3), relative.rotation, relative.lossyScale);
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(relative[i] - reconstructed[i]) > 0.0001f)
                    throw new ArgumentException("Renderer transform contains shear or reflection; use a regular positive TRS hierarchy.");
        }

        private static string ValidateEncoding(GameObject source, AnimationClip[] clips, GpuAnimationAsset data, Transform parent, Mesh reference)
        {
            var pixels = data.boneTexture.GetPixels();
            var original = source.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMesh;
            var vertices = original.vertices;
            var weights = original.boneWeights;
            double sumSquared = 0;
            float maxError = 0;
            long samples = 0;
            for (int c = 0; c < clips.Length; c++)
            {
                var instance = Clone(source, parent);
                try
                {
                    var smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    smr.quality = SkinQuality.Bone4;
                    var clip = data.clips[c];
                    for (int f = 0; f < clip.frameCount; f++)
                    {
                        clips[c].SampleAnimation(instance, clip.duration * f / (clip.frameCount - 1));
                        smr.BakeMesh(reference);
                        var expected = reference.vertices;
                        for (int v = 0; v < vertices.Length; v++)
                        {
                            var w = weights[v];
                            int frame = clip.startFrame + f;
                            Vector3 actual = Transform(pixels, data.boneCount, frame, w.boneIndex0, vertices[v]) * w.weight0
                                + Transform(pixels, data.boneCount, frame, w.boneIndex1, vertices[v]) * w.weight1
                                + Transform(pixels, data.boneCount, frame, w.boneIndex2, vertices[v]) * w.weight2
                                + Transform(pixels, data.boneCount, frame, w.boneIndex3, vertices[v]) * w.weight3;
                            float error = (actual - expected[v]).magnitude;
                            maxError = Mathf.Max(maxError, error);
                            sumSquared += error * error;
                            samples++;
                        }
                    }
                }
                finally { Object.DestroyImmediate(instance); }
            }
            float tolerance = data.boneTexture.format == TextureFormat.RGBAHalf ? 0.005f : 0.0001f;
            string report = $"Encoding validation: {(maxError <= tolerance ? "PASS" : "FAIL")}\n" +
                $"Reference: SkinnedMeshRenderer.BakeMesh; decoded actual saved texture precision.\n" +
                $"Vertex samples: {samples}\nMaximum position error: {maxError:G9} local units\n" +
                $"RMS position error: {Math.Sqrt(sumSquared / samples):G9} local units\nTolerance: {tolerance} local units\n" +
                $"Texture: {data.boneTexture.width} x {data.boneTexture.height}, {data.boneTexture.format}\n" +
                $"Allocated texture bytes: {(long)data.boneTexture.width * data.boneTexture.height * (data.boneTexture.format == TextureFormat.RGBAHalf ? 8 : 16)}\n" +
                "This validates sampled positions and texture encoding; runtime shader and interpolation require rendering verification.\n";
            if (maxError > tolerance) throw new InvalidOperationException(report);
            return report;
        }

        internal static Vector3 Transform(Color[] pixels, int boneCount, int frame, int bone, Vector3 position)
        {
            int index = (frame * boneCount + bone) * 3;
            var p = new Vector4(position.x, position.y, position.z, 1);
            return new Vector3(Vector4.Dot(pixels[index], p), Vector4.Dot(pixels[index + 1], p), Vector4.Dot(pixels[index + 2], p));
        }

        private static void SaveGpuPrefab(GpuAnimationAsset data, string folder)
        {
            var root = new GameObject("GPU " + data.name);
            try
            {
                var child = new GameObject("Baked renderer");
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = data.rendererToRoot.GetColumn(3);
                child.transform.localRotation = data.rendererToRoot.rotation;
                child.transform.localScale = data.rendererToRoot.lossyScale;
                child.AddComponent<MeshFilter>().sharedMesh = data.mesh;
                var renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = data.material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var player = child.AddComponent<GpuAnimationPlayer>();
                player.animationAsset = data;
                PrefabUtility.SaveAsPrefabAsset(root, folder + "/Gpu.prefab");
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
