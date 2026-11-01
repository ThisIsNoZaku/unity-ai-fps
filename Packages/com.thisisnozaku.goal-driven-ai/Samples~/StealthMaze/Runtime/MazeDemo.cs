using System.Collections.Generic;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Tactics;
using GoalDrivenAI.Unity;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoalDrivenAI.Samples.StealthMaze
{
    /// <summary>Builds the shipped demo scene at play time; no assets or navigation bake required.</summary>
    public sealed partial class MazeDemo : MonoBehaviour
    {
        public int seed = 42;
        [Range(1, 6)] public int enemyCount = 4;
        [Range(2, 5)] public int checkpointCount = 3;
        [Header("Enemy senses (copied to spawned components)")]
        [Min(0)] public float sightRange = 15;
        [Range(1, 360)] public float fieldOfView = 105;
        [Range(0, 3)] public float sightSensitivity = 1;
        [Min(0)] public float hearingRange = 18;
        [Range(0, 3)] public float hearingSensitivity = 1;
        [Min(0)] public float shoutRange = 16;
        public MazeMap Map { get; private set; }
        public MazePlayer Player { get; private set; }
        public RuntimeNavMesh GuardNavigation { get; private set; }
        public RuntimeNavMesh InfiltratorNavigation { get; private set; }
        public Vector3 Exit => LocationPosition(Scenario.ExitLocation.Id);
        public bool Won { get; private set; }
        public string Message { get; set; } = "Choose a role to join the maze.";
        private const float CellSize = MazeScenario.CellSize;
        private readonly List<MazeGuard> guards = new List<MazeGuard>();
        private readonly List<Material> materials = new List<Material>();
        private Transform generated;
        private AudioClip footstepSound, shoutSound;
        private int captures;
        private float resetGrace;
        private bool capturePending;
        private AmbientMode previousAmbientMode;
        private Color previousAmbient;
        public Vector3 Point((int x, int y) cell) => new Vector3(cell.x * CellSize, .02f, cell.y * CellSize);
        public (int x, int y) Cell(Vector3 point) => (Mathf.RoundToInt(point.x / CellSize), Mathf.RoundToInt(point.z / CellSize));

        private void Start()
        {
            footstepSound = Cue("Footstep", .09f, 95);
            shoutSound = Cue("Guard shout cue", .22f, 520);
            Map = new MazeMap(seed);
            Scenario = new MazeScenario(new UnityClock(), Map, Mathf.Clamp(enemyCount, 1, 6), Mathf.Clamp(checkpointCount, 2, 5));
            generated = new GameObject("Generated stealth maze").transform; generated.SetParent(transform, false);
            previousAmbientMode = RenderSettings.ambientMode; previousAmbient = RenderSettings.ambientLight;
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(.48f, .53f, .65f);
            var light = new GameObject("Sun").AddComponent<Light>(); light.transform.SetParent(generated);
            light.type = LightType.Directional; light.intensity = 1.1f; light.transform.rotation = Quaternion.Euler(55, -30, 0);
            var wall = MakeMaterial(new Color(.19f, .26f, .32f));
            var floor = MakeMaterial(new Color(.11f, .15f, .19f));
            var crawl = MakeMaterial(new Color(.25f, .65f, .65f));
            var cover = MakeMaterial(new Color(.6f, .42f, .2f));
            Box("Floor", new Vector3((Map.Width - 1) * 1.5f, -.2f, (Map.Height - 1) * 1.5f),
                new Vector3(Map.Width * CellSize, .4f, Map.Height * CellSize), floor);
            var spawnCells = new List<(int x, int y)>();
            for (int y = 0; y < Map.Height; y++) for (int x = 0; x < Map.Width; x++)
            {
                var point = Point((x, y));
                if (Map[x, y] == '#') Box("Maze wall", point + Vector3.up * 1.5f, new Vector3(3, 3, 3), wall);
                else if (Map[x, y] == 'c')
                {
                    // A 1.02 m opening: standing players and the 1.8 m guard capsule cannot enter.
                    Box("Crouch passage — teal lintel", point + Vector3.up * 2.01f, new Vector3(3, 1.98f, 3), crawl);
                }
                else if (x % 2 == 1 && y % 2 == 1)
                {
                    if (Vector3.Distance(point, Point((1, 1))) > 12) spawnCells.Add((x, y));
                    if ((x + y) % 4 == 0 && (x != 1 || y != 1) && (x != Map.Width - 2 || y != Map.Height - 2))
                        // Leave a guard-width route on either side of cover within the corridor.
                        Box("Low crouch cover", point + new Vector3(.95f, .48f, 0), new Vector3(.65f, .96f, 1.0f), cover);
                }
            }
            // Bake after static geometry, before spawning any actors or decorative markers.
            Physics.SyncTransforms();
            var sources = RuntimeNavMesh.CollectSources(generated);
            var navigationBounds = new Bounds(
                new Vector3((Map.Width - 1) * 1.5f, 2, (Map.Height - 1) * 1.5f),
                new Vector3(Map.Width * CellSize + 2, 8, Map.Height * CellSize + 2));
            GuardNavigation = new RuntimeNavMesh(sources, navigationBounds, new NavMeshAgentProfile(1.8f, .42f));
            InfiltratorNavigation = new RuntimeNavMesh(sources, navigationBounds, new NavMeshAgentProfile(.9f, .34f));
            Box("Entry — blue", Point((1, 1)) + Vector3.up * .015f, new Vector3(2.6f, .03f, 2.6f), MakeMaterial(new Color(.1f, .5f, 1)));
            Box("Exit — green", Exit + Vector3.up * .015f, new Vector3(2.6f, .03f, 2.6f), MakeMaterial(new Color(.2f, 1, .35f)));
            var playerObject = new GameObject("Player — WASD / Ctrl to crouch"); playerObject.transform.SetParent(generated);
            var playerController = playerObject.AddComponent<CharacterController>();
            playerController.minMoveDistance = 0; // Keep small frame steps at high frame rates.
            playerController.height = 1.8f; playerController.center = Vector3.up * .9f; playerController.radius = .3f; playerController.stepOffset = .15f;
            var camera = new GameObject("Player camera").AddComponent<Camera>();
            camera.tag = "MainCamera"; camera.transform.SetParent(playerObject.transform, false);
            camera.nearClipPlane = .05f; camera.farClipPlane = 100; camera.fieldOfView = 78;
            camera.backgroundColor = new Color(.05f, .08f, .12f); camera.clearFlags = CameraClearFlags.SolidColor;
            camera.gameObject.AddComponent<AudioListener>();
            Player = playerObject.AddComponent<MazePlayer>(); Player.Initialize(this, camera); Player.ResetTo(Point((1, 1)));
            BuildRoleViews(camera);
            for (int i = 0; i < Scenario.GuardIds.Count; i++)
            {
                int index = i * spawnCells.Count / Scenario.GuardIds.Count;
                var go = new GameObject("Guard " + (i + 1)); go.transform.SetParent(generated); go.transform.position = Point(spawnCells[index]);
                var motor = go.AddComponent<CharacterController>(); motor.minMoveDistance = 0; motor.height = 1.8f; motor.center = Vector3.up * .9f; motor.radius = .38f; motor.stepOffset = .1f;
                var model = GameObject.CreatePrimitive(PrimitiveType.Capsule); model.name = "Guard body"; model.transform.SetParent(go.transform, false);
                model.transform.localPosition = Vector3.up * .9f; model.transform.localScale = new Vector3(.7f, .9f, .7f);
                // The controller is the sole collision shape. Disable the primitive collider before deferred destruction.
                model.GetComponent<Collider>().enabled = false; Destroy(model.GetComponent<Collider>());
                model.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(.25f, .65f, .9f));
                var visor = Box("Facing visor", go.transform.position + new Vector3(0, 1.5f, .36f), new Vector3(.45f, .15f, .12f), cover);
                visor.transform.SetParent(go.transform, true);
                // Decorative geometry must not block this guard's sight ray or other actors.
                visor.GetComponent<Collider>().enabled = false; Destroy(visor.GetComponent<Collider>());
                var sight = go.AddComponent<MazeSight>(); sight.range = sightRange; sight.fieldOfView = fieldOfView; sight.sensitivity = sightSensitivity;
                var hearing = go.AddComponent<MazeHearing>(); hearing.range = hearingRange; hearing.sensitivity = hearingSensitivity;
                var guard = go.AddComponent<MazeGuard>(); guard.shoutRadius = shoutRange;
                guard.Initialize(this);
                guard.SetIdentity(i);
                go.AddComponent<PerspectiveDebug>().Bind(guard);
                foreach (var other in guards)
                    Physics.IgnoreCollision(motor, other.GetComponent<CharacterController>());
                guards.Add(guard);
            }
            BuildCheckpoints();
            Physics.SyncTransforms(); resetGrace = Time.time + 1;
        }
        private Material MakeMaterial(Color color)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            string shaderName = pipeline == null ? "Standard" :
                pipeline.GetType().Name.Contains("HDRender") ? "HDRP/Lit" : "Universal Render Pipeline/Lit";
            var shader = Shader.Find(shaderName) ?? Shader.Find("Standard");
            var material = new Material(shader); material.color = color; materials.Add(material); return material;
        }
        private GameObject Box(string label, Vector3 position, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = label; go.transform.SetParent(generated);
            go.transform.position = position; go.transform.localScale = size; go.GetComponent<Renderer>().sharedMaterial = material; return go;
        }
        public void EmitNoise(Vector3 position, float radius)
        {
            float volume = radius <= 2 ? .14f : .35f;
            var noise = new MazeObservation(position, Time.timeAsDouble, EvidenceKind.Hearing, clip: footstepSound, volume: volume);
            AudioSource.PlayClipAtPoint(footstepSound, position, volume);
            foreach (var guard in guards) guard.Hear(position, radius, noise);
        }
        public void Shout(MazeGuard source, MazeObservation report)
        {
            // No relay: only the guard that actually sees the player shouts. The target position is a snapshot.
            var audible = new MazeObservation(report.Position, report.Time, report.Kind, report.Confidence, report.Id, shoutSound, .5f);
            AudioSource.PlayClipAtPoint(shoutSound, source.Eye.position, .5f);
            foreach (var guard in guards) if (guard != source) guard.Hear(source.transform.position, source.shoutRadius, audible);
        }
        public void Capture()
        {
            if (!Running || Time.time < resetGrace) return;
            capturePending = true;
        }
        private void LateUpdate()
        {
            // Defer controller resets until all collision callbacks have returned.
            if (!capturePending) return;
            capturePending = false; captures++; ResetEncounter();
            Message = "Caught! Back at the entry. Crouch to hide and make less noise.";
        }
        public void Restart()
        {
            if (!RoleSelected || Role == MazeRole.Observer) return;
            ResetEncounter();
        }
        private void ResetEncounter()
        {
            Won = false; capturePending = false; resetGrace = Time.time + 1; Player.ResetTo(Point((1, 1)));
            Scenario.Reset();
            RefreshCheckpoints();
            foreach (var guard in guards) guard.ResetState();
            Physics.SyncTransforms(); Message = "Visit every numbered checkpoint, then reach the green exit.";
        }
        public void Win()
        {
            if (capturePending || !Running || !Scenario.CanEscape(MazeScenario.InfiltratorId, ToPosition(Player.transform.position))) return;
            Won = true; Message = "INFILTRATOR ESCAPED! All checkpoints reached.";
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        // Self-contained placeholder sounds; hearing replays these same clips, without a voice asset dependency.
        private static AudioClip Cue(string name, float seconds, float frequency)
        {
            const int rate = 16000;
            var samples = new float[Mathf.CeilToInt(rate * seconds)];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = (float)i / rate;
                samples[i] = Mathf.Sin(2 * Mathf.PI * frequency * t) * Mathf.Exp(-t * 24) * .6f;
            }
            var clip = AudioClip.Create(name, samples.Length, 1, rate, false); clip.SetData(samples, 0); return clip;
        }
        private void OnDestroy()
        {
            foreach (var perspective in GetComponentsInChildren<PerspectiveDebug>()) perspective.StopObserving();
            if (footstepSound != null) Destroy(footstepSound);
            if (shoutSound != null) Destroy(shoutSound);
            GuardNavigation?.Dispose();
            InfiltratorNavigation?.Dispose();
            RenderSettings.ambientMode = previousAmbientMode; RenderSettings.ambientLight = previousAmbient;
            foreach (var material in materials) if (material != null) Destroy(material);
        }
    }
}
