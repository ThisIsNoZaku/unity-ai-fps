using UnityEngine;

namespace GoalDrivenAI.Samples.StealthMaze
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class MazePlayer : MonoBehaviour
    {
        [Min(.1f)] public float walkingSpeed = 3.8f;
        [Min(.1f)] public float crouchingSpeed = 1.7f;
        [Min(0)] public float walkingNoiseRadius = 8;
        [Min(0)] public float crouchingNoiseRadius = 1.5f;
        [Min(.1f)] public float mouseSensitivity = 2;
        public bool Crouching { get; private set; }
        private CharacterController controller;
        private Camera view;
        private MazeDemo world;
        private float pitch, nextStep, verticalSpeed;
        private bool inputUnavailable;
        private readonly Collider[] clearance = new Collider[16];
        public void Initialize(MazeDemo demo, Camera camera)
        { world = demo; view = camera; controller = GetComponent<CharacterController>(); }

        private void Update()
        {
            if (world == null || inputUnavailable) return;
            try { ReadInput(); }
            catch (System.InvalidOperationException)
            {
                inputUnavailable = true;
                world.Message = "Enable Active Input Handling: Both (or Input Manager) in Player Settings, then restart the Editor.";
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                Debug.LogError(world.Message);
            }
        }
        private void ReadInput()
        {
            if (Input.GetKeyDown(KeyCode.R)) { world.Restart(); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (Input.GetMouseButtonDown(0) && !world.Won) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            if (world.Won || Cursor.lockState != CursorLockMode.Locked) return;
            transform.Rotate(0, Input.GetAxis("Mouse X") * mouseSensitivity, 0);
            pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * mouseSensitivity, -80, 80);
            view.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
            SetCrouching(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C));
            bool crouch = Crouching;
            Vector3 movement = Vector3.ClampMagnitude(transform.forward * Input.GetAxisRaw("Vertical") +
                transform.right * Input.GetAxisRaw("Horizontal"), 1) * (crouch ? crouchingSpeed : walkingSpeed);
            verticalSpeed = controller.isGrounded ? -2 : verticalSpeed + Physics.gravity.y * Time.deltaTime;
            Vector3 before = transform.position;
            controller.Move((movement + Vector3.up * verticalSpeed) * Time.deltaTime);
            if ((transform.position - before).sqrMagnitude > .000001f && movement.sqrMagnitude > .01f && Time.time >= nextStep)
            {
                nextStep = Time.time + (crouch ? .65f : .42f);
                world.EmitNoise(transform.position, crouch ? crouchingNoiseRadius : walkingNoiseRadius);
            }
            if (Vector3.Distance(transform.position, world.Exit) < 1.1f) world.Win();
        }
        /// <summary>Supports alternate input bindings while preserving standing clearance.</summary>
        public void SetCrouching(bool crouch)
        {
            if (!crouch && Crouching)
            {
                int count = Physics.OverlapCapsuleNonAlloc(transform.position + Vector3.up * 1.2f,
                    transform.position + Vector3.up * 1.5f, .29f, clearance, ~0, QueryTriggerInteraction.Ignore);
                // Never let our own capsule prevent standing. A full buffer conservatively blocks it.
                crouch = count == clearance.Length;
                for (int i = 0; i < count; i++) if (clearance[i] != controller) crouch = true;
            }
            Crouching = crouch;
            controller.height = crouch ? .9f : 1.8f;
            controller.center = Vector3.up * controller.height * .5f;
            view.transform.localPosition = Vector3.up * (crouch ? .72f : 1.6f);
        }
        private void OnControllerColliderHit(ControllerColliderHit hit)
        { if (hit.collider.GetComponent<MazeGuard>() != null) world.Capture(); }
        public void ResetTo(Vector3 position)
        {
            controller.enabled = false; transform.SetPositionAndRotation(position, Quaternion.identity); controller.enabled = true;
            controller.height = 1.8f; controller.center = Vector3.up * .9f;
            Crouching = false; pitch = 0; verticalSpeed = 0; nextStep = Time.time + 1;
            view.transform.localPosition = Vector3.up * 1.6f; view.transform.localRotation = Quaternion.identity;
        }
        private void OnDisable() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
    }
}
