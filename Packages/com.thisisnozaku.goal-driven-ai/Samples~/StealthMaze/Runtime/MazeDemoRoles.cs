using System;
using GoalDrivenAI.Stealth;
using UnityEngine;

namespace GoalDrivenAI.Samples.StealthMaze
{
    public sealed partial class MazeDemo
    {
        public MazeScenario Scenario { get; private set; }
        public MazeRole Role { get; private set; }
        public bool RoleSelected { get; private set; }
        public bool Running => RoleSelected && !Won;
        private Camera firstPerson, overhead;
        private Transform infiltratorVisual;
        private Renderer[] checkpointMarkers;
        public Vector3 CheckpointPosition(int index)
        { var p = Scenario.Checkpoints[index]; return new Vector3(p.X, p.Y, p.Z); }
        private static GoalDrivenAI.Position ToPosition(Vector3 p) => new GoalDrivenAI.Position(p.x, p.y, p.z);

        private void BuildRoleViews(Camera camera)
        {
            firstPerson = camera; firstPerson.enabled = false;
            overhead = new GameObject("Commander and observer camera").AddComponent<Camera>();
            overhead.transform.SetParent(generated, false);
            overhead.transform.SetPositionAndRotation(new Vector3((Map.Width - 1) * 1.5f, 60, (Map.Height - 1) * 1.5f), Quaternion.Euler(90, 0, 0));
            overhead.orthographic = true; overhead.farClipPlane = 100;
            overhead.clearFlags = CameraClearFlags.SolidColor; overhead.backgroundColor = new Color(.05f, .08f, .12f);
            var model = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            model.name = "Infiltrator (yellow)"; infiltratorVisual = model.transform;
            infiltratorVisual.SetParent(Player.transform, false);
            model.GetComponent<Collider>().enabled = false; Destroy(model.GetComponent<Collider>());
            model.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(1, .9f, .2f));
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private void BuildCheckpoints()
        {
            checkpointMarkers = new Renderer[Scenario.Checkpoints.Count];
            for (int i = 0; i < checkpointMarkers.Length; i++)
            {
                var marker = Box("Checkpoint " + (i + 1), CheckpointPosition(i) + Vector3.up * .04f,
                    new Vector3(2, .05f, 2), MakeMaterial(Color.white));
                marker.GetComponent<Collider>().enabled = false; Destroy(marker.GetComponent<Collider>());
                checkpointMarkers[i] = marker.GetComponent<Renderer>();
            }
            RefreshCheckpoints();
        }
        private void RefreshCheckpoints()
        {
            for (int i = 0; i < checkpointMarkers.Length; i++)
                checkpointMarkers[i].sharedMaterial.color = i < Scenario.Reached ? Color.green :
                    i == Scenario.Reached ? new Color(1, .25f, .85f) : new Color(.55f, .35f, .8f);
        }
        public void JoinRole(MazeRole role)
        {
            if (Scenario == null || !Enum.IsDefined(typeof(MazeRole), role)) return;
            Role = role; RoleSelected = true;
            firstPerson.enabled = role == MazeRole.Infiltrator; overhead.enabled = !firstPerson.enabled;
            infiltratorVisual.gameObject.SetActive(!firstPerson.enabled);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            Message = Won ? "Infiltrator escaped. Switch to an active role to restart." : role == MazeRole.Infiltrator ? "Click the maze to play. Visit checkpoints in order, then escape." :
                role == MazeRole.Commander ? "Select a checkpoint for each guard. Stop the yellow AI infiltrator." :
                "Observing the AI infiltrator and AI commander. No game controls.";
        }
        public bool OrderProtection(int guard, int checkpoint)
        {
            if (!Running || Role != MazeRole.Commander) return false;
            bool accepted = Scenario.Protect(MazeScenario.CommanderId, guard, checkpoint);
            if (accepted) Message = "Commander: Guard " + (guard + 1) + " — protect checkpoint " + (checkpoint + 1) + ".";
            return accepted;
        }
        private void Update()
        {
            if (Scenario == null) return;
            overhead.orthographicSize = Mathf.Max(Map.Height * 1.5f + 2, Map.Width * 1.5f / Mathf.Max(.1f, overhead.aspect) + 2);
            float height = Player.Crouching ? .9f : 1.8f;
            infiltratorVisual.localPosition = Vector3.up * height * .5f;
            infiltratorVisual.localScale = new Vector3(.6f, height * .5f, .6f);
            if (!Running || capturePending) return;
            var position = ToPosition(Player.transform.position);
            if (Scenario.Visit(MazeScenario.InfiltratorId, position))
            {
                RefreshCheckpoints();
                Message = "Checkpoint " + Scenario.Reached + " reached. " + (Scenario.AllReached ? "The exit is unlocked!" : "Continue to the next checkpoint.");
                // The AI commander reacts to objective events, never to the unseen infiltrator's transform.
                if (Role != MazeRole.Commander)
                    for (int i = 0; i < guards.Count; i++)
                        Scenario.Protect(MazeScenario.CommanderId, i, Scenario.AllReached ? Scenario.Checkpoints.Count - 1 :
                            Scenario.Reached + i % (Scenario.Checkpoints.Count - Scenario.Reached));
            }
            if (Scenario.CanEscape(MazeScenario.InfiltratorId, position)) Win();
        }
        public void ShowRoleMenu()
        {
            RoleSelected = false; firstPerson.enabled = false; overhead.enabled = true;
            infiltratorVisual.gameObject.SetActive(true);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        private void OnGUI()
        {
            if (Player == null) return;
            if (!RoleSelected)
            {
                float left = Mathf.Max(8, (Screen.width - 510) * .5f);
                GUI.Box(new Rect(left, 170, 510, 190), "JOIN STEALTH MAZE — simulation paused");
                GUI.Label(new Rect(left + 16, 205, 480, 24), "Reach every numbered checkpoint, then the green exit.");
                if (GUI.Button(new Rect(left + 16, 240, 475, 30), "Infiltrator — first person stealth")) JoinRole(MazeRole.Infiltrator);
                if (GUI.Button(new Rect(left + 16, 278, 475, 30), "Guards commander — assign checkpoint defenses")) JoinRole(MazeRole.Commander);
                if (GUI.Button(new Rect(left + 16, 316, 475, 30), "Observer — watch the AI factions")) JoinRole(MazeRole.Observer);
                return;
            }
            GUI.Box(new Rect(16, 16, 660, 122), "STEALTH MAZE — " + Role);
            GUI.Label(new Rect(30, 43, 630, 24), Message);
            GUI.Label(new Rect(30, 68, 630, 24), "Checkpoints: " + Scenario.Reached + "/" + Scenario.Checkpoints.Count +
                " • Next: " + (Scenario.AllReached ? "Exit" : "CP " + (Scenario.Reached + 1)) + " • Captures: " + captures);
            GUI.Label(new Rect(30, 93, 630, 24), Role == MazeRole.Infiltrator ?
                "WASD / mouse • Ctrl/C crouch • R restart • Esc cursor • Tab roles" :
                "Yellow: infiltrator • Blue: defending • Amber: searching • Red: sighting");
            if (Role != MazeRole.Infiltrator || Cursor.lockState != CursorLockMode.Locked)
            {
                if (GUI.Button(new Rect(690, 20, 90, 28), "Roles")) ShowRoleMenu();
                if (Role != MazeRole.Observer && GUI.Button(new Rect(690, 55, 90, 28), "Restart")) Restart();
            }
            if (Role == MazeRole.Commander)
            {
                GUI.Box(new Rect(16, 148, 360, 45 + guards.Count * 34), "COMMANDER — persistent checkpoint orders");
                for (int i = 0; i < guards.Count; i++)
                {
                    GUI.Label(new Rect(28, 180 + i * 34, 75, 28), "Guard " + (i + 1));
                    for (int c = 0; c < Scenario.Checkpoints.Count; c++)
                        if (GUI.Button(new Rect(104 + c * 50, 180 + i * 34, 48, 28),
                            (Scenario.DefenseFor(i) == c ? "●" : "") + "CP" + (c + 1))) OrderProtection(i, c);
                }
            }
            Camera active = firstPerson.enabled ? firstPerson : overhead;
            for (int i = 0; i < Scenario.Checkpoints.Count; i++)
            {
                Vector3 p = active.WorldToScreenPoint(CheckpointPosition(i) + Vector3.up * 3.2f);
                if (p.z > 0) GUI.Label(new Rect(p.x - 35, Screen.height - p.y, 100, 24), "CP " + (i + 1) + (i < Scenario.Reached ? " ✓" : ""));
            }
            int row = 0;
            foreach (var guard in guards)
                GUI.Label(new Rect(20, Screen.height - 28 * (guards.Count - row++), 540, 24), guard.name + " [guards] " + guard.Goal +
                    " → CP " + (Scenario.DefenseFor(guard.GuardIndex) + 1) + (guard.Shouting ? " [SHOUT!]" : ""));
            if (Role == MazeRole.Infiltrator && !Won) GUI.Label(new Rect(Screen.width / 2 - 4, Screen.height / 2 - 10, 20, 24), "+");
        }
    }
}
