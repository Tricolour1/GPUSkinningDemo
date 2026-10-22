using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSkinningDemo
{
    public sealed class CharacterParkDemo : MonoBehaviour
    {
        public GpuAnimationAsset[] characters;
        public Camera demoCamera;
        public Mesh shadowMesh;
        public Material shadowMaterial;
        private const int Capacity = 500;
        private readonly ParkMotionPath[] paths = { new ParkMotionPath(0), new ParkMotionPath(1), new ParkMotionPath(2) };
        private readonly List<Group> groups = new List<Group>();
        private int renderSelection;
        private int copies = 1;
        private int animationSelection;
        private int view;
        private float clock;
        private float speed = 1;
        private bool paused;
        private float delta;
        private int screenWidth, screenHeight;
        private GUIStyle title;

        private sealed class Group
        {
            public int pipeline, role, clip;
            public Matrix4x4[] matrices, shadows;
            public Vector4[] playback = new Vector4[Capacity];
            public GameObject[] roots;
            public MaterialPropertyBlock properties = new MaterialPropertyBlock();
        }

        private void Start()
        {
            if (!SystemInfo.supportsInstancing) throw new System.NotSupportedException("GPU instancing required.");
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Rebuild();
        }

        public void Configure(int pipelineSelection, int countPerCharacter, int animation = 0)
        {
            renderSelection = Mathf.Clamp(pipelineSelection, 0, 3);
            copies = Mathf.Clamp(countPerCharacter, 1, Capacity);
            animationSelection = Mathf.Clamp(animation, 0, 3);
            Rebuild();
        }

        public void SetView(int index)
        {
            view = Mathf.Clamp(index, 0, 3);
            float scale = ParkMotionPath.Scale;
            Vector3 center = view == 0 ? new Vector3(0, 0, 0.5f * scale) : new Vector3((view - 2) * ParkMotionPath.ZoneSpacing, 0.6f, 0.5f * scale);
            demoCamera.transform.position = center + new Vector3(0, 25, -29) * scale;
            demoCamera.transform.LookAt(center);
            demoCamera.orthographic = true;
            demoCamera.orthographicSize = (view == 0 ? Mathf.Max(12.2f, 20 / demoCamera.aspect) : Mathf.Max(5.8f, 6 / demoCamera.aspect)) * scale;
            screenWidth = Screen.width; screenHeight = Screen.height;
        }

        public void SetPlayback(bool pause, float multiplier)
        {
            paused = pause; speed = Mathf.Clamp(multiplier, 0.1f, 2);
            foreach (var group in groups)
                if (group.roots != null)
                    foreach (var root in group.roots) root.GetComponent<Animator>().speed = paused ? 0 : speed;
        }

        private void Rebuild()
        {
            foreach (var group in groups)
                if (group.roots != null) foreach (var root in group.roots) { root.SetActive(false); Destroy(root); }
            groups.Clear();
            clock = 0;
            for (int pipeline = 0; pipeline < 3; pipeline++)
            {
                if (renderSelection != 0 && renderSelection != pipeline + 1) continue;
                for (int role = 0; role < characters.Length; role++)
                {
                    var asset = characters[role];
                    int clip = animationSelection == 0 ? (role == 1 ? 2 : 1) : animationSelection - 1;
                    var group = new Group { pipeline = pipeline, role = role, clip = clip,
                        matrices = new Matrix4x4[copies], shadows = new Matrix4x4[copies] };
                    if (pipeline == 0) group.roots = new GameObject[copies];
                    for (int i = 0; i < copies; i++)
                    {
                        float phase = (i * 0.61803398875f + role * 0.23f) % 1;
                        group.playback[i] = asset.clips[clip].Playback(phase);
                        if (pipeline == 0)
                        {
                            var root = Instantiate(asset.sourcePrefab, transform);
                            root.name = asset.name + " Animator " + i;
                            var animator = root.GetComponent<Animator>();
                            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                            animator.applyRootMotion = false;
                            animator.speed = paused ? 0 : speed;
                            animator.Play("Clip" + clip, 0, phase);
                            animator.Update(0);
                            group.roots[i] = root;
                        }
                    }
                    groups.Add(group);
                }
            }
            SetView(view);
            UpdateTransforms();
        }

        private void UpdateTransforms()
        {
            foreach (var group in groups)
            {
                var asset = characters[group.role];
                var path = paths[group.role];
                float moveSpeed = group.clip == 0 ? 0 : group.clip == 2 ? 2.35f : 1.25f;
                for (int i = 0; i < copies; i++)
                {
                    float phase = group.playback[i].w;
                    path.Evaluate(clock * moveSpeed + phase * path.Length, out var position, out var rotation);
                    position += new Vector3((group.pipeline - 1) * ParkMotionPath.ZoneSpacing, 0.13f, 0);
                    // Copies use two small lateral offsets to keep stress-test crowds readable.
                    position += rotation * Vector3.right * ((i % 3 - 1) * (copies == 1 ? 0 : 0.42f));
                    group.matrices[i] = Matrix4x4.TRS(position, rotation, Vector3.one) * asset.rendererToRoot;
                    group.shadows[i] = Matrix4x4.TRS(position + Vector3.up * 0.008f, Quaternion.identity, new Vector3(0.28f, 1, 0.22f));
                    if (group.roots != null) group.roots[i].transform.SetPositionAndRotation(position, rotation);
                }
            }
        }

        private void Update()
        {
            if (!paused) clock += Time.deltaTime * speed;
            delta = Mathf.Lerp(delta, Time.unscaledDeltaTime, 0.05f);
            if (screenWidth != Screen.width || screenHeight != Screen.height) SetView(view);
            UpdateTransforms();
            foreach (var group in groups)
            {
                var asset = characters[group.role];
                if (group.pipeline != 0)
                {
                    group.properties.SetFloat("_DemoTime", clock);
                    group.properties.SetFloat("_Loop", asset.clips[group.clip].loop ? 1 : 0);
                    group.properties.SetVectorArray("_Playback", group.playback);
                    var parameters = new RenderParams(group.pipeline == 1 ? asset.material : asset.vertexMaterial)
                    { matProps = group.properties, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
                    Graphics.RenderMeshInstanced(parameters, asset.mesh, 0, group.matrices, copies);
                }
                var shadowParams = new RenderParams(shadowMaterial) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
                Graphics.RenderMeshInstanced(shadowParams, shadowMesh, 0, group.shadows, copies);
            }
        }

        private void OnGUI()
        {
            var previous = GUI.matrix;
            float scale = Mathf.Min(Screen.width / 1440f, Screen.height / 900f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (title == null) title = new GUIStyle(GUI.skin.label) { fontSize = 23, fontStyle = FontStyle.Bold };
            GUILayout.BeginArea(new Rect(24, 672, 1392, 208), GUI.skin.box);
            GUILayout.Label("ANIMATION PARK  /  LANDLORD CHARACTERS", title);
            GUILayout.Label("Passerby 01 / circle    |    Passerby 04 / figure eight    |    Passerby 09 / waypoints");
            int selection = GUILayout.Toolbar(renderSelection, new[] { "All three pipelines", "Animator", "GPU bone texture", "GPU vertex texture" });
            int animation = GUILayout.Toolbar(animationSelection, new[] { "Auto: walk / run", "Idle", "Walk", "Run" });
            GUILayout.BeginHorizontal();
            foreach (int amount in new[] { 1, 10, 50, 100 })
                if (GUILayout.Button(amount + " / character")) { copies = amount; Rebuild(); }
            bool pause = GUILayout.Toggle(paused, "Pause", GUI.skin.button);
            GUILayout.EndHorizontal();
            int newView = GUILayout.Toolbar(view, new[] { "Overview", "Focus Animator", "Focus BAT", "Focus VAT" });
            GUILayout.BeginHorizontal();
            GUILayout.Label("Speed " + speed.ToString("0.0"), GUILayout.Width(85));
            float multiplier = GUILayout.HorizontalSlider(speed, 0.1f, 2, GUILayout.Width(160));
            GUILayout.Label($"{copies * 3 * (renderSelection == 0 ? 3 : 1)} characters  |  frame {delta * 1000:0.0} ms  |  root paths on CPU");
            GUILayout.EndHorizontal();
            long boneBytes = 0, vertexBytes = 0;
            foreach (var asset in characters)
            {
                boneBytes += (long)asset.boneTexture.width * asset.boneTexture.height * 8;
                vertexBytes += (long)asset.vertexTexture.width * asset.vertexTexture.height * 8;
            }
            GUILayout.Label($"Baked Half data: BAT {boneBytes / 1024f:0} KiB  /  VAT {vertexBytes / 1048576f:0.00} MiB   |   Circle / figure eight / waypoint routes use arc-length lookup.");
            GUILayout.EndArea();
            GUI.matrix = previous;
            if (selection != renderSelection || animation != animationSelection) Configure(selection, copies, animation);
            if (newView != view) SetView(newView);
            if (pause != paused || !Mathf.Approximately(multiplier, speed)) SetPlayback(pause, multiplier);
        }
    }
}
