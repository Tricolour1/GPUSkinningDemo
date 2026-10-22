using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GpuSkinningDemo.Editor
{
    // Generates a low-poly signalised junction and reuses the Character Park bakes (idle, walk, run).
    public static class CrossroadsBuilder
    {
        public const string Output = "Assets/GpuSkinningDemo/Generated/Crossroads";
        private const string ScenePath = Output + "/Crossroads.unity";
        private static readonly string[] Roles = { "Passerby_01", "Passerby_04", "Passerby_09" };

        private const float R = CrossroadsCrowd.RoadHalfWidth;
        private const float S = CrossroadsCrowd.SidewalkLine;
        private const float E = CrossroadsCrowd.Extent;
        private const float Curb = CrossroadsCrowd.CurbHeight;

        [MenuItem("Tools/GPU Skinning/Build Crossroads")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) throw new System.InvalidOperationException("Stop Play before building the crossroads.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!AssetDatabase.IsValidFolder(Output)) AssetDatabase.CreateFolder("Assets/GpuSkinningDemo/Generated", "Crossroads");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Load after NewScene: opening a scene unloads assets that nothing references yet.
            var characters = Roles.Select(role => AssetDatabase.LoadAssetAtPath<GpuAnimationAsset>(
                $"{CharacterParkBuilder.Output}/{role}_Baked/{role}_Animation.asset")).ToArray();
            if (characters.Any(c => c == null)) throw new System.InvalidOperationException("Build the Character Park first; its bakes are reused here.");

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(DemoBuilder.Generated + "/DemoPipeline.asset");
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.62f, 0.78f, 0.9f);
            camera.fieldOfView = 45; camera.nearClipPlane = 0.3f; camera.farClipPlane = 400;
            camera.transform.position = new Vector3(19.5f, 30, 27.9f);
            camera.transform.LookAt(Vector3.zero);
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.15f;
            sun.color = new Color(1, 0.95f, 0.86f);
            sun.transform.rotation = Quaternion.Euler(52, -40, 0);
            sun.shadows = LightShadows.Soft;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.7f, 0.74f, 0.8f);
            RenderSettings.fog = false;

            var root = new GameObject("Lowpoly Crossroads").transform;
            BuildStreets(root);
            BuildBlocks(root);
            var crowd = new GameObject("Crossroads Crowd").AddComponent<CrossroadsCrowd>();
            crowd.characters = characters;
            crowd.crowdCamera = camera;
            crowd.signalRed = Material("Signal red", new Color(1, 0.18f, 0.12f), new Color(1, 0.15f, 0.1f) * 2);
            crowd.signalGreen = Material("Signal green", new Color(0.2f, 1, 0.35f), new Color(0.15f, 1, 0.3f) * 2);
            BuildSignals(root, crowd);

            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath);
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.All(scene => scene.path != ScenePath)) scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log("Crossroads ready: " + ScenePath);
        }

        private static void BuildStreets(Transform root)
        {
            var asphalt = Material("Asphalt", new Color(0.24f, 0.25f, 0.27f));
            var paint = Material("Road paint", new Color(0.93f, 0.93f, 0.9f));
            var yellow = Material("Road yellow", new Color(0.95f, 0.78f, 0.25f));
            var streets = new GameObject("Streets").transform; streets.SetParent(root, false);
            Box("Ground", streets, new Vector3(0, -0.6f, 0), new Vector3(E * 2 + 60, 1, E * 2 + 60), asphalt);
            for (int axis = 0; axis < 2; axis++)
            {
                // Dashed centre line and stop lines on both approaches.
                for (float d = R + 6; d < E + 20; d += 4)
                    for (int side = -1; side <= 1; side += 2)
                        Box("Centre dash", streets, Along(axis, side * (d + 1), 0, 0.01f), Size(axis, 2, 0.15f), yellow);
                for (int side = -1; side <= 1; side += 2)
                    Box("Stop line", streets, Along(axis, side * (S + 2.8f), side * R / 2, 0.01f), Size(axis, 0.4f, R), paint);
            }
            // Zebra crossings: stripes run in the walking direction across each road.
            for (int side = -1; side <= 1; side += 2)
                for (int k = -3; k <= 3; k++)
                {
                    Box("Zebra stripe", streets, new Vector3(side * S + k * 0.6f, 0.01f, 0), new Vector3(0.35f, 0.02f, R * 2 - 0.6f), paint);
                    Box("Zebra stripe", streets, new Vector3(0, 0.01f, side * S + k * 0.6f), new Vector3(R * 2 - 0.6f, 0.02f, 0.35f), paint);
                }
        }

        // Axis 0: road along X (centre line runs in X); axis 1: road along Z.
        private static Vector3 Along(int axis, float along, float across, float y) => axis == 0 ? new Vector3(along, y, across) : new Vector3(across, y, along);
        private static Vector3 Size(int axis, float along, float across) => axis == 0 ? new Vector3(along, 0.02f, across) : new Vector3(across, 0.02f, along);

        private static void BuildBlocks(Transform root)
        {
            var pavement = Material("Pavement", new Color(0.74f, 0.74f, 0.72f));
            var curb = Material("Curb", new Color(0.58f, 0.58f, 0.57f));
            var bark = Material("Bark", new Color(0.36f, 0.25f, 0.18f));
            var leaves = Material("Leaves", new Color(0.3f, 0.55f, 0.32f));
            var lamp = Material("Lamp post", new Color(0.2f, 0.22f, 0.25f));
            Color[] palette =
            {
                new Color(0.86f, 0.64f, 0.5f), new Color(0.6f, 0.72f, 0.82f), new Color(0.9f, 0.84f, 0.66f),
                new Color(0.7f, 0.62f, 0.76f), new Color(0.62f, 0.78f, 0.68f), new Color(0.82f, 0.56f, 0.56f),
            };
            var facades = palette.Select((c, i) => Material("Facade " + i, c)).ToArray();
            var roofs = Material("Roof", new Color(0.32f, 0.33f, 0.36f));
            var windows = Material("Windows", new Color(0.3f, 0.42f, 0.55f));
            var random = new System.Random(7);
            float Range(float min, float max) => min + (max - min) * (float)random.NextDouble();

            for (int q = 0; q < 4; q++)
            {
                int sx = (q & 1) == 0 ? 1 : -1, sz = (q & 2) == 0 ? 1 : -1;
                var block = new GameObject("Block " + q).transform; block.SetParent(root, false);
                float size = E + 30 - R;
                Box("Pavement", block, new Vector3(sx * (R + size / 2), Curb / 2 - 0.05f, sz * (R + size / 2)), new Vector3(size, Curb + 0.1f, size), pavement);
                Box("Curb", block, new Vector3(sx * (R + size / 2), Curb / 2, sz * (R + 0.1f)), new Vector3(size, Curb + 0.02f, 0.2f), curb);
                Box("Curb", block, new Vector3(sx * (R + 0.1f), Curb / 2, sz * (R + size / 2)), new Vector3(0.2f, Curb + 0.02f, size), curb);

                // Buildings line both street fronts behind the sidewalk.
                float front = R + CrossroadsCrowd.SidewalkWidth + 1;
                for (int axis = 0; axis < 2; axis++)
                {
                    float a = axis == 0 ? front : front + 13;
                    while (a < E + 20)
                    {
                        // Low corner shops keep the junction visible from the orbiting camera.
                        float width = Range(7, 13), depth = Range(8, 12), height = a < 30 ? Range(4.5f, 7) : Range(8, 18);
                        var center = axis == 0
                            ? new Vector3(sx * (a + width / 2), height / 2 + Curb, sz * (front + depth / 2))
                            : new Vector3(sx * (front + depth / 2), height / 2 + Curb, sz * (a + width / 2));
                        var dims = axis == 0 ? new Vector3(width, height, depth) : new Vector3(depth, height, width);
                        Box("Building", block, center, dims, facades[random.Next(facades.Length)]);
                        Box("Roof slab", block, center + Vector3.up * (height / 2 + 0.15f), dims + new Vector3(0.4f, -height + 0.3f, 0.4f), roofs);
                        // A band of windows on the street-facing side for every storey.
                        for (float y = 2.2f; y < height - 1; y += 3)
                        {
                            var face = axis == 0 ? new Vector3(center.x, y + Curb, sz * (front - 0.02f)) : new Vector3(sx * (front - 0.02f), y + Curb, center.z);
                            var band = axis == 0 ? new Vector3(width - 1.2f, 1.1f, 0.06f) : new Vector3(0.06f, 1.1f, width - 1.2f);
                            Box("Windows", block, face, band, windows);
                        }
                        a += width + 0.6f;
                    }
                }
                // Street trees and lamps along the building side of the sidewalk.
                float treeLine = R + CrossroadsCrowd.SidewalkWidth + 0.3f;
                for (float d = R + 12; d < E; d += 9)
                    for (int axis = 0; axis < 2; axis++)
                    {
                        var p = axis == 0 ? new Vector3(sx * d, Curb, sz * treeLine) : new Vector3(sx * treeLine, Curb, sz * d);
                        if (((int)d / 9) % 3 == 0)
                        {
                            Cylinder("Lamp post", block, p + Vector3.up * 2.5f, new Vector3(0.12f, 2.5f, 0.12f), lamp);
                            Box("Lamp head", block, p + Vector3.up * 5.05f, new Vector3(0.5f, 0.15f, 0.5f), lamp);
                            continue;
                        }
                        Cylinder("Trunk", block, p + Vector3.up * 1.1f, new Vector3(0.22f, 1.1f, 0.22f), bark);
                        Primitive(PrimitiveType.Sphere, "Crown", block, p + Vector3.up * 3.0f, Quaternion.Euler(0, Range(0, 90), 0),
                            Vector3.one * Range(2.2f, 2.8f), leaves);
                    }
            }
        }

        // One pedestrian signal head at each end of each crossing, facing the people waiting opposite.
        private static void BuildSignals(Transform root, CrossroadsCrowd crowd)
        {
            var pole = Material("Signal pole", new Color(0.18f, 0.2f, 0.22f));
            var parent = new GameObject("Pedestrian signals").transform; parent.SetParent(root, false);
            var acrossX = new List<Renderer>();
            var acrossZ = new List<Renderer>();
            for (int s1 = -1; s1 <= 1; s1 += 2)
                for (int s2 = -1; s2 <= 1; s2 += 2)
                {
                    // Crossing over the X road at x = s1 * S; this end sits on side s2 of that road.
                    var footX = new Vector3(s1 * (S + 2.6f), Curb, s2 * (R + 0.5f));
                    acrossX.Add(Signal(parent, footX, Vector3.forward * -s2, pole, crowd.signalRed));
                    // Crossing over the Z road at z = s1 * S.
                    var footZ = new Vector3(s2 * (R + 0.5f), Curb, s1 * (S + 2.6f));
                    acrossZ.Add(Signal(parent, footZ, Vector3.right * -s2, pole, crowd.signalRed));
                }
            crowd.signalsAcrossX = acrossX.ToArray();
            crowd.signalsAcrossZ = acrossZ.ToArray();
        }

        private static Renderer Signal(Transform parent, Vector3 foot, Vector3 facingRoad, Material pole, Material light)
        {
            Cylinder("Signal pole", parent, foot + Vector3.up * 1.4f, new Vector3(0.1f, 1.4f, 0.1f), pole);
            Box("Signal housing", parent, foot + Vector3.up * 2.75f, Quaternion.LookRotation(facingRoad), new Vector3(0.45f, 0.7f, 0.3f), pole);
            // The lit face points across the road so it reads from the opposite curb and from above.
            return Box("Signal light", parent, foot + Vector3.up * 2.75f + facingRoad * 0.16f, Quaternion.LookRotation(facingRoad),
                new Vector3(0.36f, 0.58f, 0.04f), light).GetComponent<Renderer>();
        }

        private static Material Material(string name, Color color, Color? emission = null)
        {
            string path = Output + "/" + name.Replace(' ', '_') + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = color;
            material.SetFloat("_Smoothness", 0);
            if (emission.HasValue)
            {
                // Without clearing EmissiveIsBlack, URP's material validation turns _EMISSION back off.
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission.Value);
            }
            return material;
        }

        private static GameObject Box(string name, Transform parent, Vector3 position, Vector3 size, Material material)
            => Primitive(PrimitiveType.Cube, name, parent, position, Quaternion.identity, size, material);
        private static GameObject Box(string name, Transform parent, Vector3 position, Quaternion rotation, Vector3 size, Material material)
            => Primitive(PrimitiveType.Cube, name, parent, position, rotation, size, material);
        private static GameObject Cylinder(string name, Transform parent, Vector3 position, Vector3 size, Material material)
            => Primitive(PrimitiveType.Cylinder, name, parent, position, Quaternion.identity, size, material);

        private static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 position, Quaternion rotation, Vector3 size, Material material)
        {
            var item = GameObject.CreatePrimitive(type); item.name = name;
            item.transform.SetParent(parent, false);
            item.transform.SetLocalPositionAndRotation(position, rotation);
            item.transform.localScale = size;
            var renderer = item.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            Object.DestroyImmediate(item.GetComponent<Collider>());
            return item;
        }
    }
}
