using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSkinningDemo
{
    public sealed class SkinningDemo : MonoBehaviour
    {
        public GpuAnimationAsset animationAsset;
        public Camera demoCamera;
        private const int BatchSize = 500;
        private readonly List<GameObject> references = new List<GameObject>();
        private readonly List<Matrix4x4[]> batches = new List<Matrix4x4[]>();
        private readonly List<Vector4[]> playback = new List<Vector4[]>();
        private MaterialPropertyBlock properties;
        private int count = 1;
        private int clipIndex;
        private int mode;
        private bool paused;
        private bool variedPhase;
        private float speed = 1;
        private float time;
        private float smoothedDelta;
        private string[] clipNames;
        private GUIStyle titleStyle;
        private int viewportWidth;
        private int viewportHeight;

        private void Start()
        {
            if (!SystemInfo.supportsInstancing)
                throw new System.NotSupportedException("GPU instancing is required for this demo.");
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            properties = new MaterialPropertyBlock();
            clipNames = new string[animationAsset.clips.Length];
            for (int i = 0; i < clipNames.Length; i++) clipNames[i] = animationAsset.clips[i].name;
            Rebuild();
        }

        private Vector3 Position(int index, float side)
        {
            int width = Mathf.CeilToInt(Mathf.Sqrt(count));
            return new Vector3((index % width - (width - 1) * 0.5f) * 1.5f + side,
                0, (index / width) * 1.5f);
        }

        private void Rebuild()
        {
            foreach (var item in references) { item.SetActive(false); Destroy(item); }
            references.Clear();
            batches.Clear();
            playback.Clear();
            time = 0;
            int width = Mathf.CeilToInt(Mathf.Sqrt(count));
            float separation = mode == 0 ? (width * 1.5f + 2) * 0.5f : 0;
            var clip = animationAsset.clips[clipIndex];
            for (int i = 0; i < count; i++)
            {
                float phase = variedPhase ? (i * 0.61803398875f) % 1 : 0;
                if (mode != 2)
                {
                    var item = Instantiate(animationAsset.sourcePrefab, Position(i, -separation), Quaternion.identity);
                    item.name = "Animator reference " + i;
                    var animator = item.GetComponent<Animator>();
                    if (animator == null) animator = item.AddComponent<Animator>();
                    animator.runtimeAnimatorController = animationAsset.referenceController;
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.speed = paused ? 0 : speed;
                    animator.Play("Clip" + clipIndex, 0, phase);
                    animator.Update(0);
                    foreach (var smr in item.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        smr.updateWhenOffscreen = true;
                        smr.quality = SkinQuality.Bone4;
                    }
                    references.Add(item);
                }
                if (mode != 1)
                {
                    if (i % BatchSize == 0)
                    {
                        int length = Mathf.Min(BatchSize, count - i);
                        batches.Add(new Matrix4x4[length]);
                        // Unity fixes a property array's capacity on its first upload.
                        // Keep the same capacity when switching between 1, 100 and 1000 instances.
                        playback.Add(new Vector4[BatchSize]);
                    }
                    batches[i / BatchSize][i % BatchSize] = Matrix4x4.TRS(Position(i, separation), Quaternion.identity, Vector3.one)
                        * animationAsset.rendererToRoot;
                    playback[i / BatchSize][i % BatchSize] = clip.Playback(phase);
                }
            }
            FrameCamera();
        }

        private void FrameCamera()
        {
            int width = Mathf.CeilToInt(Mathf.Sqrt(count));
            var center = new Vector3(0, 0.9f, (width - 1) * 0.75f);
            float halfWidth = mode == 0 ? width * 1.5f + 1.3f : width * 0.75f + 0.6f;
            float halfDepth = width * 0.75f + 0.6f;
            float verticalTangent = Mathf.Tan(demoCamera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            float distance = Mathf.Max((halfWidth / Mathf.Max(0.2f, demoCamera.aspect) + halfDepth * 0.4f) / verticalTangent,
                (1.3f + halfDepth * 0.5f) / verticalTangent) * 1.2f;
            demoCamera.transform.position = center + new Vector3(0, distance * 0.3f, -distance);
            demoCamera.transform.LookAt(center);
            demoCamera.farClipPlane = 1000;
            viewportWidth = Screen.width;
            viewportHeight = Screen.height;
        }

        public void Configure(int renderMode, int instanceCount, int animationIndex, bool differentPhases = false)
        {
            mode = Mathf.Clamp(renderMode, 0, 2);
            count = Mathf.Clamp(instanceCount, 1, 1000);
            clipIndex = Mathf.Clamp(animationIndex, 0, animationAsset.clips.Length - 1);
            variedPhase = differentPhases;
            Rebuild();
        }

        public void SetPlayback(bool pause, float playbackSpeed)
        {
            paused = pause;
            speed = Mathf.Clamp(playbackSpeed, 0.1f, 2);
            SetSpeed();
        }

        private void Update()
        {
            if (!paused) time += Time.deltaTime * speed;
            if (viewportWidth != Screen.width || viewportHeight != Screen.height) FrameCamera();
            smoothedDelta = Mathf.Lerp(smoothedDelta, Time.unscaledDeltaTime, 0.05f);
            if (mode == 1) return;
            var clip = animationAsset.clips[clipIndex];
            properties.SetFloat("_DemoTime", time);
            properties.SetFloat("_Loop", clip.loop ? 1 : 0);
            for (int i = 0; i < batches.Count; i++)
            {
                properties.SetVectorArray("_Playback", playback[i]);
                var parameters = new RenderParams(animationAsset.material)
                {
                    matProps = properties,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    layer = gameObject.layer
                };
                Graphics.RenderMeshInstanced(parameters, animationAsset.mesh, 0, batches[i], batches[i].Length);
            }
        }

        private void SetSpeed()
        {
            foreach (var item in references) item.GetComponent<Animator>().speed = paused ? 0 : speed;
        }

        private void OnGUI()
        {
            if (clipNames == null) return;
            Matrix4x4 previousMatrix = GUI.matrix;
            float scale = Mathf.Min(Screen.width / 1440f, Screen.height / 900f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (titleStyle == null) titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            GUILayout.BeginArea(new Rect(20, 16, 670, 270), GUI.skin.box);
            GUILayout.Label("GPU SKINNING / ANIMATION TEXTURE LAB", titleStyle);
            GUILayout.Label(mode == 0 ? "LEFT  Animator + SkinnedMeshRenderer     |     RIGHT  Baked GPU instances" : mode == 1 ? "Animator only" : "GPU instances only");
            int newMode = GUILayout.Toolbar(mode, new[] { "Visual comparison", "Animator only", "GPU only" });
            int newClip = GUILayout.Toolbar(clipIndex, clipNames);
            GUILayout.BeginHorizontal();
            foreach (int amount in new[] { 1, 100, 1000 })
                if (GUILayout.Button(amount + " / side")) { count = amount; Rebuild(); }
            bool newPaused = GUILayout.Toggle(paused, "Pause", GUI.skin.button);
            bool newPhase = GUILayout.Toggle(variedPhase, "Different phases", GUI.skin.button);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Speed " + speed.ToString("0.00"), GUILayout.Width(90));
            float newSpeed = GUILayout.HorizontalSlider(speed, 0.1f, 2, GUILayout.Width(220));
            GUILayout.EndHorizontal();
            GUILayout.Label($"Frame: {smoothedDelta * 1000:0.00} ms   FPS: {(smoothedDelta > 0 ? 1 / smoothedDelta : 0):0}   GPU submissions: {batches.Count}");
            GUILayout.Label($"{animationAsset.mesh.vertexCount} vertices / {animationAsset.boneCount} bones   |   {animationAsset.boneTexture.width} x {animationAsset.boneTexture.height} {animationAsset.boneTexture.format}");
            GUILayout.Label("FPS is whole-frame throughput. Use Profiler for CPU / render thread / GPU timings.");
            GUILayout.EndArea();
            GUI.matrix = previousMatrix;
            if (newPaused != paused || !Mathf.Approximately(newSpeed, speed))
            { paused = newPaused; speed = newSpeed; SetSpeed(); }
            if (newMode != mode || newClip != clipIndex || newPhase != variedPhase)
            { mode = newMode; clipIndex = newClip; variedPhase = newPhase; Rebuild(); }
        }
    }
}
