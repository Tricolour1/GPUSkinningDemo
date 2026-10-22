using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace GpuSkinningDemo
{
    // Pedestrians at a signalised junction. Routes, waiting and signals run on the CPU; skinning and
    // the cross-fade between idle, walk and run happen in the BAT vertex shader.
    public sealed class CrossroadsCrowd : MonoBehaviour
    {
        public const float RoadHalfWidth = 6, SidewalkWidth = 5, Extent = 60;
        public const float SidewalkLine = RoadHalfWidth + SidewalkWidth / 2;
        public const float CurbHeight = 0.15f;
        private const int BatchSize = 500;
        private const int Idle = 0, Walk = 1, Run = 2;
        // Speeds at which the walk and run clips' strides match the ground.
        private const float WalkReference = 1.25f, RunReference = 2.35f;
        private const float GreenTime = 14, ClearTime = 3, Cycle = 2 * (GreenTime + ClearTime);
        private const float CurbOffset = RoadHalfWidth + 0.4f;

        public GpuAnimationAsset[] characters;
        public Camera crowdCamera;
        public Renderer[] signalsAcrossX;
        public Renderer[] signalsAcrossZ;
        public Material signalRed, signalGreen;
        public int pedestrians = 400;
        public float orbitRadius = 34, orbitHeight = 30, orbitSpeed = 4;

        private readonly List<Agent> agents = new List<Agent>();
        private readonly List<Batch>[] batches = { new List<Batch>(), new List<Batch>(), new List<Batch>() };
        private MaterialPropertyBlock properties;
        private System.Random random;
        private bool crossFade = true, orbit = true;
        private float angle = 35, smoothedDelta;
        private bool lastGreenX, lastGreenZ;
        private ProfilerRecorder drawCalls, batchesCount, setPass;
        private GUIStyle titleStyle;

        private struct Waypoint
        {
            public Vector3 Point;
            public int Crossing;   // -1: sidewalk; 0: curb before crossing the X road; 1: the Z road
            public int Quadrant;   // quadrant this waypoint belongs to (used when a route ends)
            public int EndType;    // for street ends: 0 along X, 1 along Z
        }

        private sealed class Agent
        {
            public int Role;
            public Vector3 Position;
            public float Yaw, Speed, WalkSpeed, RunSpeed, Lane, IdleScale;
            public readonly List<Waypoint> Route = new List<Waypoint>();
            public int Next;
            public int ActiveCrossing = -1;
            public bool Waiting, Running;
            public int WaitCrossing;
            public Vector3 WaitPosition;
            public float ReleaseTime;
            public int Clip = -1;
            public Vector4 Playback, Previous, Blend;
        }

        private sealed class Batch
        {
            public int Count;
            public readonly Matrix4x4[] Matrices = new Matrix4x4[BatchSize];
            // Unity fixes a property array's capacity on its first upload, so always upload BatchSize.
            public readonly Vector4[] Playback = new Vector4[BatchSize];
            public readonly Vector4[] Previous = new Vector4[BatchSize];
            public readonly Vector4[] Blend = new Vector4[BatchSize];
        }

        private void OnEnable()
        {
            drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            batchesCount = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        }

        private void OnDisable()
        {
            drawCalls.Dispose(); batchesCount.Dispose(); setPass.Dispose();
        }

        private void Start()
        {
            if (!SystemInfo.supportsInstancing) throw new System.NotSupportedException("GPU instancing required.");
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            properties = new MaterialPropertyBlock();
            Spawn(pedestrians);
        }

        private void Spawn(int count)
        {
            random = new System.Random(20261022);
            agents.Clear();
            pedestrians = count;
            for (int i = 0; i < count; i++)
            {
                var agent = new Agent
                {
                    Role = random.Next(characters.Length),
                    WalkSpeed = Range(1.1f, 1.55f),
                    RunSpeed = Range(2.4f, 3.0f),
                    Lane = Range(0.3f, 1.9f),
                    IdleScale = Range(0.85f, 1.15f),
                };
                int quadrant = random.Next(4), endType = random.Next(2);
                BuildRoute(agent, quadrant, endType);
                // Start somewhere along a sidewalk part of the route, never in the middle of a road.
                int index;
                do index = 1 + random.Next(agent.Route.Count - 1);
                while (agent.Route[index - 1].Crossing >= 0);
                agent.Position = Vector3.Lerp(agent.Route[index - 1].Point, agent.Route[index].Point, (float)random.NextDouble());
                agent.Next = index;
                agent.Speed = agent.WalkSpeed;
                var heading = agent.Route[index].Point - agent.Route[index - 1].Point;
                agent.Yaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
                SetClip(agent, Walk, Time.time, false);
                agents.Add(agent);
            }
        }

        private float Range(float min, float max) => min + (max - min) * (float)random.NextDouble();

        private static Vector3 Corner(int q) => new Vector3(Sx(q) * SidewalkLine, 0, Sz(q) * SidewalkLine);
        private static int Sx(int q) => (q & 1) == 0 ? 1 : -1;
        private static int Sz(int q) => (q & 2) == 0 ? 1 : -1;
        private static int Quadrant(int sx, int sz) => (sx > 0 ? 0 : 1) | (sz > 0 ? 0 : 2);

        private static Vector3 End(int q, int endType) => endType == 0
            ? new Vector3(Sx(q) * Extent, 0, Sz(q) * SidewalkLine)
            : new Vector3(Sx(q) * SidewalkLine, 0, Sz(q) * Extent);

        // Street end -> corner -> 0..2 signalised crossings -> another street end.
        private void BuildRoute(Agent agent, int quadrant, int endType)
        {
            agent.Route.Clear();
            agent.Route.Add(new Waypoint { Point = End(quadrant, endType), Crossing = -1, Quadrant = quadrant, EndType = endType });
            agent.Route.Add(new Waypoint { Point = Corner(quadrant), Crossing = -1, Quadrant = quadrant, EndType = -1 });
            double roll = random.NextDouble();
            int crossings = roll < 0.2 ? 0 : roll < 0.75 ? 1 : 2;
            int last = -1;
            for (int c = 0; c < crossings; c++)
            {
                int type = last < 0 ? random.Next(2) : 1 - last;
                last = type;
                int sx = Sx(quadrant), sz = Sz(quadrant);
                Vector3 near, far;
                if (type == 0)
                {
                    near = new Vector3(sx * SidewalkLine, 0, sz * CurbOffset);
                    far = new Vector3(sx * SidewalkLine, 0, -sz * CurbOffset);
                    sz = -sz;
                }
                else
                {
                    near = new Vector3(sx * CurbOffset, 0, sz * SidewalkLine);
                    far = new Vector3(-sx * CurbOffset, 0, sz * SidewalkLine);
                    sx = -sx;
                }
                quadrant = Quadrant(sx, sz);
                agent.Route.Add(new Waypoint { Point = near, Crossing = type, Quadrant = -1, EndType = -1 });
                agent.Route.Add(new Waypoint { Point = far, Crossing = -1, Quadrant = -1, EndType = -1 });
                agent.Route.Add(new Waypoint { Point = Corner(quadrant), Crossing = -1, Quadrant = quadrant, EndType = -1 });
            }
            int finalEnd = crossings == 0 ? 1 - endType : random.Next(2);
            agent.Route.Add(new Waypoint { Point = End(quadrant, finalEnd), Crossing = -1, Quadrant = quadrant, EndType = finalEnd });
            agent.Next = 1;
        }

        private static bool IsGreen(int crossing, float time)
        {
            float t = Mathf.Repeat(time, Cycle);
            return crossing == 0 ? t < GreenTime : t >= GreenTime + ClearTime && t < 2 * GreenTime + ClearTime;
        }

        private static float GreenRemaining(int crossing, float time)
        {
            float t = Mathf.Repeat(time, Cycle);
            return crossing == 0 ? GreenTime - t : 2 * GreenTime + ClearTime - t;
        }

        private static bool CanStart(int crossing, float time) => IsGreen(crossing, time) && GreenRemaining(crossing, time) > 5;

        // Each agent keeps to the right of its direction of travel, so opposite flows pass each other.
        private Vector3 Target(Agent agent)
        {
            var from = agent.Route[agent.Next - 1].Point;
            var to = agent.Route[agent.Next].Point;
            var direction = (to - from).normalized;
            return to + new Vector3(direction.z, 0, -direction.x) * agent.Lane;
        }

        private void Step(Agent agent, float time, float dt)
        {
            Vector3 target;
            float desired;
            if (agent.Waiting)
            {
                if (CanStart(agent.WaitCrossing, time))
                {
                    if (agent.ReleaseTime <= 0) agent.ReleaseTime = time + Range(0.1f, 1.4f);
                    if (time >= agent.ReleaseTime) agent.Waiting = false;
                }
                target = agent.WaitPosition;
                desired = Vector3.Distance(agent.Position, target) > 0.05f ? agent.WalkSpeed * 0.7f : 0;
            }
            else
            {
                var waypoint = agent.Route[agent.Next];
                target = Target(agent);
                if (waypoint.Crossing >= 0 && agent.ActiveCrossing < 0 && !CanStart(waypoint.Crossing, time)
                    && Vector3.Distance(agent.Position, target) < 4)
                {
                    // Queue at the curb in a loose cluster instead of a single point.
                    var back = (agent.Route[agent.Next - 1].Point - waypoint.Point).normalized;
                    var side = new Vector3(back.z, 0, -back.x);
                    agent.Waiting = true;
                    agent.WaitCrossing = waypoint.Crossing;
                    agent.ReleaseTime = 0;
                    agent.WaitPosition = target + back * Range(0.2f, 3.2f) + side * Range(-1.4f, 1.4f);
                    target = agent.WaitPosition;
                }
                if (agent.ActiveCrossing >= 0 && !IsGreen(agent.ActiveCrossing, time)) agent.Running = true;
                desired = agent.Waiting ? agent.WalkSpeed * 0.7f : agent.Running ? agent.RunSpeed : agent.WalkSpeed;
            }

            agent.Speed = Mathf.MoveTowards(agent.Speed, desired, 4 * dt);
            var offset = target - agent.Position;
            offset.y = 0;
            float distance = offset.magnitude;
            float step = agent.Speed * dt;
            if (distance > 1e-4f)
            {
                float targetYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                agent.Yaw = Mathf.MoveTowardsAngle(agent.Yaw, targetYaw, 300 * dt);
            }
            if (agent.Waiting)
            {
                agent.Position = Vector3.MoveTowards(agent.Position, target, step);
                if (distance < 0.05f)
                {
                    // Face the road while waiting.
                    var across = agent.Route[agent.Next].Point - agent.Route[agent.Next - 1].Point;
                    agent.Yaw = Mathf.MoveTowardsAngle(agent.Yaw, Mathf.Atan2(across.x, across.z) * Mathf.Rad2Deg, 200 * dt);
                }
            }
            else if (step >= distance) Arrive(agent, time);
            else agent.Position += offset / distance * step;

            int clip = desired <= 0 ? Idle : agent.Running ? Run : Walk;
            if (clip != agent.Clip) SetClip(agent, clip, time, crossFade);
        }

        private void Arrive(Agent agent, float time)
        {
            var waypoint = agent.Route[agent.Next];
            agent.Position = Target(agent);
            if (waypoint.Crossing >= 0) agent.ActiveCrossing = waypoint.Crossing;
            else if (agent.ActiveCrossing >= 0 && agent.Route[agent.Next - 1].Crossing >= 0)
            {
                agent.ActiveCrossing = -1;
                agent.Running = false;
            }
            agent.Next++;
            if (agent.Next < agent.Route.Count) return;
            // Reached a street end: turn around with a new route that starts here.
            BuildRoute(agent, waypoint.Quadrant, waypoint.EndType);
        }

        // Keeps the normalised clip phase so walk <-> run changes stay in step.
        private void SetClip(Agent agent, int clip, float time, bool blend)
        {
            var data = characters[agent.Role].clips[clip];
            float duration = clip == Idle ? data.duration * agent.IdleScale
                : data.duration * (clip == Run ? RunReference / agent.RunSpeed : WalkReference / agent.WalkSpeed);
            float phase = agent.Clip < 0 ? (float)random.NextDouble()
                : Mathf.Repeat(time / agent.Playback.z + agent.Playback.w, 1);
            agent.Previous = agent.Clip < 0 ? Vector4.zero : agent.Playback;
            agent.Blend = new Vector4(time, blend && agent.Clip >= 0 ? 0.3f : 0, 0, 0);
            agent.Playback = new Vector4(data.startFrame, data.frameCount, duration, phase - time / duration);
            agent.Clip = clip;
        }

        private void Update()
        {
            float time = Time.time, dt = Time.deltaTime;
            smoothedDelta = Mathf.Lerp(smoothedDelta, Time.unscaledDeltaTime, 0.05f);
            foreach (var agent in agents) Step(agent, time, dt);
            UpdateSignals(time);
            UpdateCamera(dt);
            Draw(time);
        }

        private void UpdateSignals(float time)
        {
            bool greenX = IsGreen(0, time), greenZ = IsGreen(1, time);
            if (greenX == lastGreenX && greenZ == lastGreenZ && Time.frameCount > 1) return;
            lastGreenX = greenX; lastGreenZ = greenZ;
            foreach (var head in signalsAcrossX) head.sharedMaterial = greenX ? signalGreen : signalRed;
            foreach (var head in signalsAcrossZ) head.sharedMaterial = greenZ ? signalGreen : signalRed;
        }

        private void UpdateCamera(float dt)
        {
            if (orbit) angle += orbitSpeed * dt;
            float radians = angle * Mathf.Deg2Rad;
            crowdCamera.transform.position = new Vector3(Mathf.Sin(radians) * orbitRadius, orbitHeight, Mathf.Cos(radians) * orbitRadius);
            crowdCamera.transform.LookAt(new Vector3(0, 0, 0));
        }

        private void Draw(float time)
        {
            foreach (var list in batches) foreach (var batch in list) batch.Count = 0;
            var open = new Batch[batches.Length];
            foreach (var agent in agents)
            {
                var list = batches[agent.Role];
                var batch = open[agent.Role];
                if (batch == null || batch.Count == BatchSize)
                {
                    int index = batch == null ? 0 : list.IndexOf(batch) + 1;
                    if (index == list.Count) list.Add(new Batch());
                    batch = open[agent.Role] = list[index];
                }
                var asset = characters[agent.Role];
                // Sidewalks are raised; the road surface between the curbs is at zero.
                bool onRoad = Mathf.Abs(agent.Position.x) < RoadHalfWidth || Mathf.Abs(agent.Position.z) < RoadHalfWidth;
                var position = agent.Position + Vector3.up * (onRoad ? 0 : CurbHeight);
                batch.Matrices[batch.Count] = Matrix4x4.TRS(position, Quaternion.Euler(0, agent.Yaw, 0), Vector3.one) * asset.rendererToRoot;
                batch.Playback[batch.Count] = agent.Playback;
                batch.Previous[batch.Count] = agent.Previous;
                batch.Blend[batch.Count] = agent.Blend;
                batch.Count++;
            }
            properties.SetFloat("_DemoTime", time);
            properties.SetFloat("_Loop", 1);
            for (int role = 0; role < batches.Length; role++)
                foreach (var batch in batches[role])
                {
                    if (batch.Count == 0) continue;
                    properties.SetVectorArray("_Playback", batch.Playback);
                    properties.SetVectorArray("_PlaybackPrev", batch.Previous);
                    properties.SetVectorArray("_Blend", batch.Blend);
                    var parameters = new RenderParams(characters[role].material)
                    {
                        matProps = properties,
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = false,
                        layer = gameObject.layer
                    };
                    Graphics.RenderMeshInstanced(parameters, characters[role].mesh, 0, batch.Matrices, batch.Count);
                }
        }

        private int InstancedDraws()
        {
            int count = 0;
            foreach (var list in batches) foreach (var batch in list) if (batch.Count > 0) count++;
            return count;
        }

        private static string Counter(ProfilerRecorder recorder) => recorder.Valid ? recorder.LastValue.ToString() : "n/a";

        private void OnGUI()
        {
            float scale = Mathf.Min(Screen.width / 1440f, Screen.height / 900f);
            var previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (titleStyle == null) titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            GUILayout.BeginArea(new Rect(20, 16, 560, 190), GUI.skin.box);
            GUILayout.Label("CROSSROADS  /  BONE ANIMATION TEXTURE", titleStyle);
            float fps = smoothedDelta > 0 ? 1 / smoothedDelta : 0;
            GUILayout.Label($"{agents.Count} pedestrians   |   {fps:0} FPS   ({smoothedDelta * 1000:0.0} ms)");
            GUILayout.Label($"Crowd instanced draws {InstancedDraws()}   |   Draw calls {Counter(drawCalls)}   |   Batches {Counter(batchesCount)}   |   SetPass {Counter(setPass)}");
            GUILayout.BeginHorizontal();
            foreach (int amount in new[] { 200, 400, 1000, 3000 })
                if (GUILayout.Button(amount.ToString(), GUILayout.Width(70))) Spawn(amount);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            crossFade = GUILayout.Toggle(crossFade, "Cross-fade 0.3 s", GUI.skin.button, GUILayout.Width(150));
            orbit = GUILayout.Toggle(orbit, "Orbit camera", GUI.skin.button, GUILayout.Width(130));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.matrix = previous;
        }
    }
}
