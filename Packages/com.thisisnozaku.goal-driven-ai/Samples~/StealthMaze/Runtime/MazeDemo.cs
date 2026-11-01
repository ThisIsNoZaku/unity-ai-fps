using System.Collections.Generic;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Tactics;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoalDrivenAI.Samples.StealthMaze
{
    /// <summary>Builds the shipped demo scene at play time; no assets or navigation bake required.</summary>
    public sealed class MazeDemo : MonoBehaviour
    {
        public int seed = 42;
        [Range(1, 6)] public int enemyCount = 4;
        [Header("Enemy senses (copied to spawned components)")]
        [Min(0)] public float sightRange = 15;
        [Range(1, 360)] public float fieldOfView = 105;
        [Range(0, 3)] public float sightSensitivity = 1;
        [Min(0)] public float hearingRange = 18;
        [Range(0, 3)] public float hearingSensitivity = 1;
        [Min(0)] public float shoutRange = 16;
        public MazeMap Map { get; private set; }
        public MazePlayer Player { get; private set; }
        public Vector3 Exit => Point((Map.Width - 2, Map.Height - 2));
        public bool Won { get; private set; }
        public string Message { get; set; } = "Click to begin. Reach the green exit.";
        private const float CellSize = 3;
        private readonly List<MazeGuard> guards = new List<MazeGuard>();
        private readonly List<Material> materials = new List<Material>();
        private Transform generated;
        private int captures;
        private float resetGrace;
        private bool capturePending;
        private AmbientMode previousAmbientMode;
        private Color previousAmbient;
        public Vector3 Point((int x, int y) cell) => new Vector3(cell.x * CellSize, .02f, cell.y * CellSize);
        public (int x, int y) Cell(Vector3 point) => (Mathf.RoundToInt(point.x / CellSize), Mathf.RoundToInt(point.z / CellSize));

        private void Start()
        {
            Map = new MazeMap(seed);
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
            var patrolCells = new List<(int x, int y)>();
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
                    if (Vector3.Distance(point, Point((1, 1))) > 12) patrolCells.Add((x, y));
                    if ((x + y) % 4 == 0 && (x != 1 || y != 1) && (x != Map.Width - 2 || y != Map.Height - 2))
                        Box("Low crouch cover", point + new Vector3(.95f, .48f, 0), new Vector3(.65f, .96f, 1.6f), cover);
                }
            }
            Box("Entry — blue", Point((1, 1)) + Vector3.up * .015f, new Vector3(2.6f, .03f, 2.6f), MakeMaterial(new Color(.1f, .5f, 1)));
            Box("Exit — green", Exit + Vector3.up * .015f, new Vector3(2.6f, .03f, 2.6f), MakeMaterial(new Color(.2f, 1, .35f)));
            var playerObject = new GameObject("Player — WASD / Ctrl to crouch"); playerObject.transform.SetParent(generated);
            var playerController = playerObject.AddComponent<CharacterController>();
            playerController.height = 1.8f; playerController.center = Vector3.up * .9f; playerController.radius = .3f; playerController.stepOffset = .15f;
            var camera = new GameObject("Player camera").AddComponent<Camera>();
            camera.tag = "MainCamera"; camera.transform.SetParent(playerObject.transform, false);
            camera.nearClipPlane = .05f; camera.farClipPlane = 100; camera.fieldOfView = 78;
            camera.backgroundColor = new Color(.05f, .08f, .12f); camera.clearFlags = CameraClearFlags.SolidColor;
            camera.gameObject.AddComponent<AudioListener>();
            Player = playerObject.AddComponent<MazePlayer>(); Player.Initialize(this, camera); Player.ResetTo(Point((1, 1)));
            for (int i = 0; i < enemyCount; i++)
            {
                int index = i * patrolCells.Count / enemyCount;
                var go = new GameObject("Guard " + (i + 1)); go.transform.SetParent(generated); go.transform.position = Point(patrolCells[index]);
                var motor = go.AddComponent<CharacterController>(); motor.height = 1.8f; motor.center = Vector3.up * .9f; motor.radius = .38f; motor.stepOffset = .1f;
                var model = GameObject.CreatePrimitive(PrimitiveType.Capsule); model.name = "Guard body"; model.transform.SetParent(go.transform, false);
                model.transform.localPosition = Vector3.up * .9f; model.transform.localScale = new Vector3(.7f, .9f, .7f);
                // The controller is the sole collision shape. Disable the primitive collider before deferred destruction.
                model.GetComponent<Collider>().enabled = false; Destroy(model.GetComponent<Collider>());
                model.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(.25f, .65f, .9f));
                Box("Facing visor", go.transform.position + new Vector3(0, 1.5f, .36f), new Vector3(.45f, .15f, .12f), cover).transform.SetParent(go.transform, true);
                var sight = go.AddComponent<MazeSight>(); sight.range = sightRange; sight.fieldOfView = fieldOfView; sight.sensitivity = sightSensitivity;
                var hearing = go.AddComponent<MazeHearing>(); hearing.range = hearingRange; hearing.sensitivity = hearingSensitivity;
                var guard = go.AddComponent<MazeGuard>(); guard.shoutRadius = shoutRange;
                guard.Initialize(this, new[] { Point(patrolCells[index]), Point(patrolCells[(index + 3) % patrolCells.Count]), Point(patrolCells[(index + 6) % patrolCells.Count]) });
                foreach (var other in guards)
                    Physics.IgnoreCollision(motor, other.GetComponent<CharacterController>());
                guards.Add(guard);
            }
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
            var noise = new MazeObservation(position, Time.timeAsDouble, EvidenceKind.Hearing);
            foreach (var guard in guards) guard.Hear(position, radius, noise);
        }
        public void Shout(MazeGuard source, MazeObservation report)
        {
            // No relay: only the guard that actually sees the player shouts. The target position is a snapshot.
            foreach (var guard in guards) if (guard != source) guard.Hear(source.transform.position, source.shoutRadius, report);
        }
        public void Capture()
        {
            if (Won || Time.time < resetGrace) return;
            capturePending = true;
        }
        private void LateUpdate()
        {
            // Defer controller resets until all collision callbacks have returned.
            if (!capturePending) return;
            capturePending = false; captures++; Restart();
            Message = "Caught! Back at the entry. Crouch to hide and make less noise.";
        }
        public void Restart()
        {
            Won = false; capturePending = false; resetGrace = Time.time + 1; Player.ResetTo(Point((1, 1)));
            foreach (var guard in guards) guard.ResetState();
            Physics.SyncTransforms(); Message = "Reach the green exit. Watch corners and listen for guards.";
        }
        public void Win()
        {
            if (capturePending) return;
            Won = true; Message = "ESCAPED! Press R to play again.";
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private void OnGUI()
        {
            if (Player == null) return;
            GUI.Box(new Rect(16, 16, 570, 133), "STEALTH MAZE");
            GUI.Label(new Rect(30, 43, 540, 24), Message);
            GUI.Label(new Rect(30, 68, 540, 24), "WASD move • Mouse look • Ctrl / C crouch • R restart • Esc cursor");
            GUI.Label(new Rect(30, 93, 540, 24), "Teal openings: crouch only. Amber crates: crouch cover. Green floor: exit.");
            GUI.Label(new Rect(30, 118, 540, 24), (Player.Crouching ? "CROUCHED • quiet" : "STANDING • footsteps carry") + "     Captures: " + captures);
            int row = 0;
            foreach (var guard in guards)
                GUI.Label(new Rect(20, Screen.height - 28 * (guards.Count - row++), 360, 24), guard.name + "  " + guard.Goal + (guard.Shouting ? "  [SHOUT: PLAYER SPOTTED!]" : ""));
            if (!Won) GUI.Label(new Rect(Screen.width / 2 - 4, Screen.height / 2 - 10, 20, 24), "+");
        }
        private void OnDestroy()
        {
            RenderSettings.ambientMode = previousAmbientMode; RenderSettings.ambientLight = previousAmbient;
            foreach (var material in materials) if (material != null) Destroy(material);
        }
    }
}
