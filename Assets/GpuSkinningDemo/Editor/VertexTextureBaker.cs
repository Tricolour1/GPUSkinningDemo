using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GpuSkinningDemo.Editor
{
    public static class VertexTextureBaker
    {
        public static void Bake(GpuAnimationAsset data, GameObject source, AnimationClip[] clips)
        {
            int frames = 0;
            foreach (var clip in data.clips) frames += clip.frameCount;
            int count = data.mesh.vertexCount;
            int used = checked(frames * count * 2);
            int width = 256;
            int height = Mathf.CeilToInt((float)used / width);
            int max = Mathf.Min(4096, SystemInfo.maxTextureSize);
            if (used > (long)max * max) throw new InvalidOperationException("VAT exceeds texture limit.");
            while (height > max) { width *= 2; height = Mathf.CeilToInt((float)used / width); }
            var pixels = new Color[width * height];
            var parent = new GameObject("VAT sampling") { hideFlags = HideFlags.HideAndDontSave };
            parent.SetActive(false);
            var reference = new Mesh();
            double squaredError = 0;
            float maxError = 0;
            try
            {
                for (int c = 0; c < clips.Length; c++)
                {
                    var instance = Object.Instantiate(source, parent.transform);
                    try
                    {
                        instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                        instance.transform.localScale = Vector3.one;
                        foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);
                        var smr = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                        smr.quality = SkinQuality.Bone4;
                        var clip = data.clips[c];
                        for (int f = 0; f < clip.frameCount; f++)
                        {
                            clips[c].SampleAnimation(instance, clip.duration * f / (clip.frameCount - 1));
                            smr.BakeMesh(reference);
                            var positions = reference.vertices;
                            var normals = reference.normals;
                            for (int v = 0; v < count; v++)
                            {
                                int index = ((clip.startFrame + f) * count + v) * 2;
                                Vector3 p = positions[v];
                                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z) || p.magnitude > 60000)
                                    throw new InvalidOperationException("VAT sample exceeds Half precision range.");
                                pixels[index] = new Color(p.x, p.y, p.z, 1);
                                pixels[index + 1] = new Color(normals[v].x, normals[v].y, normals[v].z, 0);
                            }
                        }
                    }
                    finally { Object.DestroyImmediate(instance); }
                }
                var texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true)
                { name = "Vertex position and normal", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                texture.SetPixels(pixels);
                texture.Apply(false, false);
                var decoded = texture.GetPixels();
                for (int i = 0; i < used; i += 2)
                {
                    Vector3 delta = new Vector3(decoded[i].r - pixels[i].r, decoded[i].g - pixels[i].g, decoded[i].b - pixels[i].b);
                    maxError = Mathf.Max(maxError, delta.magnitude);
                    squaredError += delta.sqrMagnitude;
                }
                if (maxError > 0.005f) throw new InvalidOperationException("VAT precision validation failed: " + maxError);
                string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(data)).Replace('\\', '/');
                AssetDatabase.CreateAsset(texture, folder + "/VertexPositions.asset");
                data.vertexTexture = texture;
                var material = new Material(data.material) { name = "GPU Vertex Texture", enableInstancing = true };
                material.EnableKeyword("_VERTEX_ANIMATION");
                material.SetTexture("_BoneTexture", texture);
                material.SetFloat("_VertexCount", count);
                AssetDatabase.CreateAsset(material, folder + "/VertexMaterial.mat");
                data.vertexMaterial = material;
                string report = $"VAT encoding: PASS\nVertex samples: {used / 2}\nMaximum position error: {maxError:G9}\n" +
                    $"RMS position error: {Math.Sqrt(squaredError / (used / 2)):G9}\n" +
                    $"Texture: {width} x {height} RGBAHalf\nAllocated bytes: {(long)width * height * 8}\n";
                File.WriteAllText(folder + "/VertexValidation.txt", report);
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            }
            finally { Object.DestroyImmediate(reference); Object.DestroyImmediate(parent); }
        }
    }
}
