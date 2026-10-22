using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GpuSkinningDemo.Editor
{
    public static class CharacterParkBuilder
    {
        private const string Source = "Assets/SourceCharacters";
        public const string Output = "Assets/GpuSkinningDemo/Generated/CharacterPark";
        private static readonly string[] Roles = { "Passerby_01", "Passerby_04", "Passerby_09" };
        private static readonly string[] Animations = { "Common_Idle_01", "Common_Walk_01", "Common_Run_01" };

        [MenuItem("Tools/GPU Skinning/Inspect Landlord Characters")]
        public static void Inspect()
        {
            var report = new StringBuilder();
            foreach (string role in Roles)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(Source + "/Models/" + role + ".fbx");
                if (model == null) throw new InvalidOperationException("Missing model " + role);
                foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    report.AppendLine($"{role}: renderer={smr.name}, vertices={smr.sharedMesh.vertexCount}, bones={smr.bones.Length}, submeshes={smr.sharedMesh.subMeshCount}, blendshapes={smr.sharedMesh.blendShapeCount}, bounds={smr.sharedMesh.bounds}, transform={smr.transform.localScale}");
                foreach (string name in Animations)
                {
                    var clip = LoadClip(name);
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    report.AppendLine($"{name}: length={clip.length}, loop={clip.isLooping}, human={clip.isHumanMotion}, curves={bindings.Length}");
                    foreach (var binding in bindings)
                        if (binding.path.Length == 0 || AnimationUtility.GetAnimatedObject(model, binding) == null)
                            report.AppendLine($"  binding: {binding.path} / {binding.propertyName} / resolved={AnimationUtility.GetAnimatedObject(model, binding) != null}");
                }
            }
            Directory.CreateDirectory("Docs/Verification");
            File.WriteAllText("Docs/Verification/SourceCharacters.txt", report.ToString());
            Debug.Log(report);
        }

        private static AnimationClip LoadClip(string name) => AssetDatabase.LoadAllAssetsAtPath(Source + "/Animations/" + name + ".fbx")
            .OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal));

        [MenuItem("Tools/GPU Skinning/Refresh Character Park Scene")]
        public static void RefreshScene()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play first.");
            var characters = new List<GpuAnimationAsset>();
            foreach (string role in Roles)
            {
                string folder = Output + "/" + role + "_Baked";
                string path = AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("t:GpuAnimationAsset", new[] { folder }).Single());
                string error = AssetDatabase.RenameAsset(path, role + "_Animation");
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                characters.Add(AssetDatabase.LoadAssetAtPath<GpuAnimationAsset>(folder + "/" + role + "_Animation.asset"));
            }
            BuildScene(characters.ToArray(), Animations.Select(LoadClip).ToArray());
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Tools/GPU Skinning/Build Character Park")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play before building the park.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (AssetDatabase.IsValidFolder(Output)) throw new InvalidOperationException("CharacterPark already exists. Open its saved scene instead of overwriting baked results.");
            Directory.CreateDirectory(Output);
            AssetDatabase.Refresh();
            var clips = Animations.Select(LoadClip).ToArray();
            var palette = AssetDatabase.LoadAssetAtPath<Texture2D>(Source + "/Textures/T_Actor.tga");
            var data = new GpuAnimationAsset[3];
            for (int i = 0; i < Roles.Length; i++)
            {
                string role = Roles[i];
                string path = Source + "/Models/" + role + ".fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.isReadable = true;
                importer.optimizeGameObjects = false;
                importer.importNormals = ModelImporterNormals.Calculate;
                importer.SaveAndReimport();
                var instance = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                try
                {
                    instance.name = role;
                    instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    instance.transform.localScale = Vector3.one;
                    foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);
                    var renderer = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    var material = new Material(Shader.Find("GpuSkinningDemo/Reference")) { name = role + " surface" };
                    material.SetTexture("_BaseMap", palette);
                    AssetDatabase.CreateAsset(material, Output + "/" + role + ".mat");
                    renderer.sharedMaterial = material;
                    renderer.quality = SkinQuality.Bone4;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.updateWhenOffscreen = true;
                    var source = PrefabUtility.SaveAsPrefabAsset(instance, Output + "/" + role + ".prefab");
                    data[i] = AnimationBaker.Bake(source, clips, 30, TextureFormat.RGBAHalf, Output);
                    string renameError = AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(data[i]), role + "_Animation");
                    if (!string.IsNullOrEmpty(renameError)) throw new InvalidOperationException(renameError);
                    VertexTextureBaker.Bake(data[i], source, clips);
                }
                finally { Object.DestroyImmediate(instance); }
            }
            BuildScene(data, clips);
            AssetDatabase.SaveAssets();
            Debug.Log("Character park ready: " + Output + "/CharacterPark.unity");
        }

        private static void BuildScene(GpuAnimationAsset[] characters, AnimationClip[] clips)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(DemoBuilder.Generated + "/DemoPipeline.asset");
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            float s = ParkMotionPath.Scale;
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.71f, 0.83f, 0.84f);
            camera.nearClipPlane = 0.1f; camera.farClipPlane = 150 * s;
            camera.transform.position = new Vector3(0, 25, -29) * s;
            camera.transform.LookAt(new Vector3(0, 0, 0.5f * s));
            camera.orthographic = true; camera.orthographicSize = 12.5f * s;
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            var sun = new GameObject("Warm afternoon sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.15f;
            sun.color = new Color(1, 0.94f, 0.8f);
            sun.transform.rotation = Quaternion.Euler(48, -35, 0);
            sun.shadows = LightShadows.Soft;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.7f, 0.76f, 0.8f);
            RenderSettings.fog = false;
            var scene = new GameObject("Lowpoly Animation Park");
            var stone = Material("Ivory stone", new Color(0.81f, 0.82f, 0.73f));
            var grass = Material("Sage grass", new Color(0.43f, 0.62f, 0.43f));
            var pathMaterial = Material("Warm paving", new Color(0.91f, 0.82f, 0.63f));
            var bark = Material("Wood", new Color(0.33f, 0.23f, 0.17f));
            var foliage = Material("Tree green", new Color(0.17f, 0.4f, 0.28f));
            var lightLeaves = Material("Tree light", new Color(0.34f, 0.55f, 0.3f));
            var teal = Material("Park teal", new Color(0.09f, 0.32f, 0.34f));
            var water = Material("Water", new Color(0.18f, 0.64f, 0.75f));
            var amber = Material("Circle amber", new Color(0.9f, 0.5f, 0.12f));
            var blue = Material("Figure eight blue", new Color(0.2f, 0.54f, 0.82f));
            var violet = Material("Waypoints violet", new Color(0.62f, 0.35f, 0.63f));
            var routeMaterials = new[] { amber, blue, violet };
            Box("Plaza foundation", scene.transform, new Vector3(0, -0.45f, 0), new Vector3(36 * s, 0.6f, 13 * s), teal);
            string[] names = { "01   ANIMATOR", "02   BAT", "03   VAT" };
            for (int p = 0; p < 3; p++)
            {
                var island = new GameObject(names[p]).transform;
                island.SetParent(scene.transform, false);
                island.localPosition = new Vector3((p - 1) * ParkMotionPath.ZoneSpacing, 0, 0);
                Box("Raised stone border", island, new Vector3(0, -0.12f, 0), new Vector3(10.7f * s, 0.35f, 10.6f * s), stone);
                Box("Lawn", island, new Vector3(0, 0.045f, 0), new Vector3(10.2f * s, 0.1f, 10.1f * s), grass);
                for (int r = 0; r < 3; r++)
                {
                    var path = new ParkMotionPath(r);
                    Ribbon("Paved route " + r, island, path, 1.35f, 0.102f, pathMaterial, p == 0);
                    Ribbon("Route marker " + r, island, path, 0.06f, 0.108f, routeMaterials[r], p == 0);
                }
                for (int side = -1; side <= 1; side += 2)
                {
                    Tree(island, new Vector3(side * 4.15f * s, 0.1f, 3.65f * s), bark, foliage, lightLeaves, side * 13);
                    Tree(island, new Vector3(side * 4.25f * s, 0.1f, -3.55f * s), bark, foliage, lightLeaves, side * 23);
                    float bench = side * 4.0f * s;
                    Box("Bench seat", island, new Vector3(bench, 0.48f, 0), new Vector3(0.55f, 0.12f, 1.35f), bark);
                    Box("Bench back", island, new Vector3(bench + side * 0.24f, 0.78f, 0), new Vector3(0.1f, 0.65f, 1.35f), bark);
                    Box("Bench foot", island, new Vector3(bench, 0.25f, -0.45f), new Vector3(0.45f, 0.4f, 0.12f), teal);
                    Box("Bench foot", island, new Vector3(bench, 0.25f, 0.45f), new Vector3(0.45f, 0.4f, 0.12f), teal);
                }
                Cylinder("Fountain stone", island, new Vector3(0, 0.22f, 0.4f * s), new Vector3(1.35f * s, 0.15f, 1.35f * s), stone);
                Cylinder("Still water", island, new Vector3(0, 0.38f, 0.4f * s), new Vector3(1.13f * s, 0.01f, 1.13f * s), water);
                Cylinder("Fountain center", island, new Vector3(0, 0.66f, 0.4f * s), new Vector3(0.17f * s, 0.29f, 0.17f * s), stone);
                Box("Pipeline sign", island, new Vector3(0, 0.41f, -4.25f * s), new Vector3(7.1f * s, 0.72f, 0.16f), teal);
                Text(names[p], island, new Vector3(0, 0.8f, -4.25f * s - 0.35f), 0.32f * s, Color.white, camera.transform.rotation);
            }
            Text("ANIMATION PARK", scene.transform, new Vector3(0, 1.3f * s, 6.5f * s), 0.72f * s, teal.color, camera.transform.rotation);
            var demo = new GameObject("Character Park Controller").AddComponent<CharacterParkDemo>();
            demo.characters = characters; demo.demoCamera = camera;
            demo.shadowMaterial = Material("Character contact shadows", new Color(0.24f, 0.3f, 0.23f));
            demo.shadowMaterial.enableInstancing = true;
            demo.shadowMesh = Disk();
            var previews = new GameObject("Editor character preview").AddComponent<EditorPreviewOnly>();
            for (int p = 0; p < 3; p++)
                for (int r = 0; r < 3; r++)
                {
                    var path = new ParkMotionPath(r);
                    path.Evaluate(r * 0.23f * path.Length, out var position, out var rotation);
                    position += new Vector3((p - 1) * ParkMotionPath.ZoneSpacing, 0.13f, 0);
                    if (p == 0)
                    {
                        var root = (GameObject)PrefabUtility.InstantiatePrefab(characters[r].sourcePrefab);
                        root.transform.SetParent(previews.transform);
                        root.transform.SetPositionAndRotation(position, rotation);
                        clips[r == 1 ? 2 : 1].SampleAnimation(root, 0);
                    }
                    else
                    {
                        var root = new GameObject(Roles[r] + (p == 1 ? " BAT" : " VAT"));
                        root.transform.SetParent(previews.transform);
                        root.transform.SetPositionAndRotation(position, rotation);
                        var child = new GameObject("Baked mesh");
                        child.transform.SetParent(root.transform, false);
                        var matrix = characters[r].rendererToRoot;
                        child.transform.localPosition = matrix.GetColumn(3);
                        child.transform.localRotation = matrix.rotation;
                        child.transform.localScale = matrix.lossyScale;
                        child.AddComponent<MeshFilter>().sharedMesh = characters[r].mesh;
                        child.AddComponent<MeshRenderer>().sharedMaterial = p == 1 ? characters[r].material : characters[r].vertexMaterial;
                    }
                }
            string scenePath = Output + "/CharacterPark.unity";
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scenePath);
            // The park must stay the first build scene; keep any other scenes already listed.
            const string samplePath = "Assets/GpuSkinningDemo/Generated/Sample/GpuSkinningDemo.unity";
            var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(scenePath, true), new EditorBuildSettingsScene(samplePath, true) };
            scenes.AddRange(EditorBuildSettings.scenes.Where(scene => scene.path != scenePath && scene.path != samplePath));
            EditorBuildSettings.scenes = scenes.ToArray();
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.Frame(new Bounds(Vector3.zero, new Vector3(36 * s, 6, 16 * s)), false);
        }

        private static Material Material(string name, Color color)
        {
            string path = Output + "/" + name.Replace(' ', '_') + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name, color = color };
            material.SetFloat("_Smoothness", 0);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static GameObject Box(string name, Transform parent, Vector3 position, Vector3 size, Material material)
            => Primitive(PrimitiveType.Cube, name, parent, position, size, material);
        private static GameObject Cylinder(string name, Transform parent, Vector3 position, Vector3 size, Material material)
            => Primitive(PrimitiveType.Cylinder, name, parent, position, size, material);
        private static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 size, Material material)
        {
            var item = GameObject.CreatePrimitive(type); item.name = name;
            item.transform.SetParent(parent, false); item.transform.localPosition = position; item.transform.localScale = size;
            item.GetComponent<Renderer>().sharedMaterial = material;
            Object.DestroyImmediate(item.GetComponent<Collider>());
            return item;
        }

        private static void Text(string text, Transform parent, Vector3 position, float size, Color color, Quaternion rotation)
        {
            var item = new GameObject(text); item.transform.SetParent(parent, false);
            item.transform.localPosition = position; item.transform.rotation = rotation;
            var label = item.AddComponent<TextMesh>();
            label.text = text; label.fontSize = 100; label.characterSize = size * 0.1f;
            label.anchor = TextAnchor.MiddleCenter; label.alignment = TextAlignment.Center; label.color = color;
        }

        private static void Tree(Transform parent, Vector3 position, Material bark, Material leaves, Material light, float rotation)
        {
            var tree = new GameObject("Faceted tree").transform; tree.SetParent(parent, false); tree.localPosition = position;
            tree.localRotation = Quaternion.Euler(0, rotation, 0);
            Cylinder("Trunk", tree, new Vector3(0, 0.75f, 0), new Vector3(0.22f, 0.75f, 0.22f), bark);
            Cone("Lower canopy", tree, 1.05f, 0.8f, 1.4f, leaves);
            Cone("Upper canopy", tree, 1.75f, 0.58f, 1.2f, light);
        }

        private static void Cone(string name, Transform parent, float bottom, float radius, float height, Material material)
        {
            string path = Output + "/" + name.Replace(' ', '_') + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                var vertices = new List<Vector3>(); var triangles = new List<int>();
                for (int i = 0; i < 7; i++)
                {
                    float a = (float)i / 7 * Mathf.PI * 2, b = (float)(i + 1) / 7 * Mathf.PI * 2;
                    int start = vertices.Count;
                    vertices.Add(new Vector3(Mathf.Cos(a) * radius, bottom, Mathf.Sin(a) * radius));
                    vertices.Add(new Vector3(0, bottom + height, 0));
                    vertices.Add(new Vector3(Mathf.Cos(b) * radius, bottom, Mathf.Sin(b) * radius));
                    triangles.AddRange(new[] { start, start + 1, start + 2 });
                }
                mesh = new Mesh { name = name }; mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals();
                AssetDatabase.CreateAsset(mesh, path);
            }
            var item = new GameObject(name); item.transform.SetParent(parent, false);
            item.AddComponent<MeshFilter>().sharedMesh = mesh; item.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void Ribbon(string name, Transform parent, ParkMotionPath path, float width, float y, Material material, bool create)
        {
            string assetPath = Output + "/" + name.Replace(' ', '_') + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (create)
            {
                // Rebuild every time so route meshes follow the current layout scale.
                if (mesh != null) AssetDatabase.DeleteAsset(assetPath);
                var vertices = new Vector3[path.PointCount * 2]; var triangles = new List<int>();
                for (int i = 0; i < path.PointCount; i++)
                {
                    var point = path.Point(i); var next = path.Point(i == path.PointCount - 1 ? 1 : i + 1);
                    var perpendicular = Vector3.Cross(Vector3.up, (next - point).normalized) * width * 0.5f;
                    vertices[i * 2] = point - perpendicular + Vector3.up * y;
                    vertices[i * 2 + 1] = point + perpendicular + Vector3.up * y;
                    if (i > 0) { int a = (i - 1) * 2; triangles.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 }); }
                }
                mesh = new Mesh { name = name }; mesh.vertices = vertices; mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals();
                AssetDatabase.CreateAsset(mesh, assetPath);
            }
            var root = new GameObject(name); root.transform.SetParent(parent, false);
            root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static Mesh Disk()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(Output + "/ContactShadow.asset");
            if (existing != null) return existing;
            var vertices = new List<Vector3> { Vector3.zero }; var triangles = new List<int>();
            for (int i = 0; i < 16; i++) vertices.Add(new Vector3(Mathf.Cos(i * Mathf.PI / 8), 0, Mathf.Sin(i * Mathf.PI / 8)));
            for (int i = 0; i < 16; i++) triangles.AddRange(new[] { 0, (i + 1) % 16 + 1, i + 1 });
            var mesh = new Mesh { name = "Contact shadow disk" }; mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals();
            AssetDatabase.CreateAsset(mesh, Output + "/ContactShadow.asset");
            return mesh;
        }
    }
}
