using System;
using UnityEngine;

namespace GoalDrivenAI.Unity
{
    public sealed class CommandRegion : MonoBehaviour
    {
        [SerializeField] private string regionId;
        [SerializeField] private BoxCollider boundary;
        [SerializeField] private CommandRegion parent;
        public string Id => regionId;
        public CommandRegion Parent => parent;
        public bool Contains(Vector3 point) => boundary != null && boundary.ClosestPoint(point) == point;
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(regionId)) regionId = Guid.NewGuid().ToString("N");
        }
    }
}
