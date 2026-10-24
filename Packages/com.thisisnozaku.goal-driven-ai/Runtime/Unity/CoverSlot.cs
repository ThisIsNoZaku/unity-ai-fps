using UnityEngine;

namespace GoalDrivenAI.Unity
{
    public sealed class CoverSlot : MonoBehaviour
    {
        [SerializeField] private string slotId;
        [SerializeField] private Transform refuge;
        [SerializeField] private Transform exposure;
        [SerializeField, Min(1)] private int capacity = 1;
        private int revision;
        public string Id => slotId;
        public int Capacity => capacity;
        public int Revision => revision;
        public Vector3 Refuge => refuge == null ? transform.position : refuge.position;
        public Vector3 Exposure => exposure == null ? transform.position : exposure.position;
        public bool UsableFrom(Vector3 knownThreat) => isActiveAndEnabled
            && Vector3.Dot(transform.forward, knownThreat - transform.position) > 0;
        public void Invalidate() { revision++; enabled = false; }
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(slotId)) slotId = System.Guid.NewGuid().ToString("N");
        }
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(Refuge, 0.15f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward);
        }
    }
}
