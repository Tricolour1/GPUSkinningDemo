using System;
using UnityEngine;

namespace GpuSkinningDemo
{
    [Serializable]
    public struct BakedClip
    {
        public string name;
        public float duration;
        public int startFrame;
        public int frameCount;
        public bool loop;

        public Vector4 Playback(float phase = 0) => new Vector4(startFrame, frameCount, duration, phase);
    }

    public sealed class GpuAnimationAsset : ScriptableObject
    {
        public Mesh mesh;
        public Texture2D boneTexture;
        public Material material;
        public Texture2D vertexTexture;
        public Material vertexMaterial;
        public int boneCount;
        public BakedClip[] clips;
        public GameObject sourcePrefab;
        public RuntimeAnimatorController referenceController;
        public Matrix4x4 rendererToRoot = Matrix4x4.identity;
        [TextArea(4, 12)] public string validation;
    }
}
