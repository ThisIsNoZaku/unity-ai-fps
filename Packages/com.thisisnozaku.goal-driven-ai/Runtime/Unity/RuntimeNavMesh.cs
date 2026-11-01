using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace GoalDrivenAI.Unity
{
    /// <summary>Game-configured clearance and build precision, independent of actor roles.</summary>
    public sealed class NavMeshAgentProfile
    {
        public float Height { get; }
        public float Radius { get; }
        public float Climb { get; }
        public float Slope { get; }
        public float VoxelSize { get; }
        public int TileSize { get; }
        public float MinimumRegionArea { get; }
        public NavMeshAgentProfile(float height, float radius, float climb = .1f, float slope = 45,
            float voxelSize = .04f, int tileSize = 256, float minimumRegionArea = .1f)
        {
            if (!Positive(height) || !Positive(radius) || !Positive(voxelSize) ||
                !Finite(climb) || climb < 0 || climb >= height || !Finite(slope) || slope < 0 || slope > 60 ||
                tileSize < 16 || tileSize > 1024 || !Finite(minimumRegionArea) || minimumRegionArea < 0)
                throw new ArgumentOutOfRangeException(nameof(height), "Invalid NavMesh agent profile.");
            Height = height; Radius = radius; Climb = climb; Slope = slope;
            VoxelSize = voxelSize; TileSize = tileSize; MinimumRegionArea = minimumRegionArea;
        }
        private static bool Positive(float value) => Finite(value) && value > 0;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>Owns one runtime bake, registration and temporary agent type. Use on Unity's main thread.</summary>
    public sealed class RuntimeNavMesh : IDisposable
    {
        private NavMeshData data;
        private NavMeshDataInstance instance;
        private int agentType = -1;
        public NavMeshQueryFilter Filter { get; private set; }
        public bool IsValid => instance.valid;

        public RuntimeNavMesh(IEnumerable<NavMeshBuildSource> sources, Bounds bounds, NavMeshAgentProfile profile)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var copy = sources.ToList();
            if (copy.Count == 0) throw new ArgumentException("No navigation build sources.", nameof(sources));
            for (int axis = 0; axis < 3; axis++)
                if (float.IsNaN(bounds.center[axis]) || float.IsInfinity(bounds.center[axis]) ||
                    float.IsNaN(bounds.size[axis]) || float.IsInfinity(bounds.size[axis]) || bounds.size[axis] <= 0)
                    throw new ArgumentOutOfRangeException(nameof(bounds));
            try
            {
                var settings = NavMesh.CreateSettings(); agentType = settings.agentTypeID;
                settings.agentHeight = profile.Height; settings.agentRadius = profile.Radius;
                settings.agentClimb = profile.Climb; settings.agentSlope = profile.Slope;
                settings.overrideVoxelSize = true; settings.voxelSize = profile.VoxelSize;
                settings.overrideTileSize = true; settings.tileSize = profile.TileSize;
                settings.minRegionArea = profile.MinimumRegionArea;
                data = NavMeshBuilder.BuildNavMeshData(settings, copy, bounds, Vector3.zero, Quaternion.identity);
                if (data == null) throw new InvalidOperationException("Could not build NavMesh data.");
                instance = NavMesh.AddNavMeshData(data);
                if (!instance.valid) throw new InvalidOperationException("Could not register NavMesh data.");
                Filter = new NavMeshQueryFilter { agentTypeID = agentType, areaMask = NavMesh.AllAreas };
            }
            catch { Dispose(); throw; }
        }
        /// <summary>Collect from a caller-owned static hierarchy before spawning actors or decorative colliders.</summary>
        public static List<NavMeshBuildSource> CollectSources(Transform root, int layerMask = ~0,
            NavMeshCollectGeometry geometry = NavMeshCollectGeometry.PhysicsColliders,
            int defaultArea = 0, IEnumerable<NavMeshBuildMarkup> markups = null)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (defaultArea < 0 || defaultArea > 31) throw new ArgumentOutOfRangeException(nameof(defaultArea));
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(root, layerMask, geometry, defaultArea,
                markups == null ? new List<NavMeshBuildMarkup>() : markups.ToList(), sources);
            return sources;
        }
        public void Dispose()
        {
            if (instance.valid) instance.Remove();
            instance = default;
            if (data != null) { UnityEngine.Object.Destroy(data); data = null; }
            if (agentType != -1) { NavMesh.RemoveSettings(agentType); agentType = -1; }
        }
    }
}
