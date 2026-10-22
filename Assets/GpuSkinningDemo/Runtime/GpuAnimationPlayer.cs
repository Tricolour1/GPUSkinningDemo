using UnityEngine;

namespace GpuSkinningDemo
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class GpuAnimationPlayer : MonoBehaviour
    {
        public GpuAnimationAsset animationAsset;
        public int clipIndex;
        public float speed = 1;
        public bool paused;
        private float time;
        private MaterialPropertyBlock properties;
        private MeshRenderer targetRenderer;

        private void OnEnable()
        {
            if (animationAsset == null) return;
            properties = new MaterialPropertyBlock();
            targetRenderer = GetComponent<MeshRenderer>();
            GetComponent<MeshFilter>().sharedMesh = animationAsset.mesh;
            targetRenderer.sharedMaterial = animationAsset.material;
            Apply();
        }

        private void Update()
        {
            if (animationAsset == null) return;
            if (!paused) time += Time.deltaTime * speed;
            Apply();
        }

        public void Play(int index)
        {
            clipIndex = Mathf.Clamp(index, 0, animationAsset.clips.Length - 1);
            time = 0;
            Apply();
        }

        private void Apply()
        {
            var clip = animationAsset.clips[Mathf.Clamp(clipIndex, 0, animationAsset.clips.Length - 1)];
            properties.SetVector("_Playback", clip.Playback());
            properties.SetFloat("_Loop", clip.loop ? 1 : 0);
            properties.SetFloat("_DemoTime", time);
            targetRenderer.SetPropertyBlock(properties);
        }
    }
}
