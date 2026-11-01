using System;
using System.Collections;
using GoalDrivenAI.Language;
using GoalDrivenAI.Navigation;
using GoalDrivenAI.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace GoalDrivenAI.Tests
{
    public sealed class NavigationPlayModeTests
    {
        private sealed class Clock : IClock { public double Now { get; set; } }
        private GameObject geometry;
        private RuntimeNavMesh mesh;
        private Bounds Bounds => new Bounds(new Vector3(10, 2, 0), new Vector3(48, 10, 24));
        [SetUp]
        public void SetUp()
        {
            geometry = new GameObject("Another game's navigation geometry");
            Primitive(PrimitiveType.Cube, new Vector3(0, -.2f, 0), new Vector3(20, .4f, 12));
            Primitive(PrimitiveType.Cube, new Vector3(0, 1.5f, 0), new Vector3(1, 3, 8));
            Primitive(PrimitiveType.Sphere, new Vector3(-4, .5f, 3), Vector3.one);
            // Disconnected destination tests partial paths rather than a falsely successful arrival.
            Primitive(PrimitiveType.Cube, new Vector3(30, -.2f, 0), new Vector3(6, .4f, 6));
            Physics.SyncTransforms();
            mesh = new RuntimeNavMesh(RuntimeNavMesh.CollectSources(geometry.transform), Bounds,
                new NavMeshAgentProfile(1.2f, .25f));
        }
        private void Primitive(PrimitiveType type, Vector3 position, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type); go.transform.SetParent(geometry.transform);
            go.transform.position = position; go.transform.localScale = scale;
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        { mesh?.Dispose(); UnityEngine.Object.Destroy(geometry); yield return null; }

        private NavigationProgress Follow(NavMeshPathFollower follower, LocationRegistry locations,
            string target, ref Vector3 position, Clock clock)
        {
            NavigationProgress state = NavigationProgress.Moving;
            for (int i = 0; i < 600; i++)
            {
                state = follower.Next(position, locations, target, out var delta);
                if (state != NavigationProgress.Moving) break;
                // This game owns its own motor, with no maze, CharacterController or actor roles.
                position += Vector3.ClampMagnitude(delta, .15f); clock.Now += .05;
            }
            return state;
        }

        [Test]
        public void ReusableNavigationDetoursAroundGeometryAndReachesAnArbitraryLocation()
        {
            var start = new Vector3(-6, 0, 0); var destination = new Position(6, 0, 0);
            var path = new NavMeshPath();
            Assert.That(NavMesh.CalculatePath(start, destination.ToVector(), mesh.Filter, path), Is.True);
            Assert.That(path.status, Is.EqualTo(NavMeshPathStatus.PathComplete));
            Assert.That(Array.Exists(path.corners, p => Mathf.Abs(p.z) > 4), Is.True, "Route must detour around the wall");
            var locations = new LocationRegistry(new[] { new CatalogHandle("garden", HandleKind.Location, destination) });
            var clock = new Clock(); var follower = new NavMeshPathFollower(mesh.Filter, clock: clock);
            Assert.That(Follow(follower, locations, "garden", ref start, clock), Is.EqualTo(NavigationProgress.Arrived));
            Assert.That(Vector3.Distance(start, destination.ToVector()), Is.LessThan(.1f));
        }

        [Test]
        public void DisconnectedAndMissingDestinationsNeverReportArrival()
        {
            var locations = new LocationRegistry(new[] {
                new CatalogHandle("island", HandleKind.Location, new Position(30, 0, 0)),
                new CatalogHandle("outside", HandleKind.Location, new Position(100, 0, 0)) });
            var clock = new Clock(); var follower = new NavMeshPathFollower(mesh.Filter, clock: clock);
            var position = new Vector3(-6, 0, 0);
            Assert.That(Follow(follower, locations, "island", ref position, clock), Is.EqualTo(NavigationProgress.Partial));
            follower.Clear();
            Assert.That(follower.Next(position, locations, "outside", out var delta), Is.EqualTo(NavigationProgress.Unavailable));
            Assert.That(delta, Is.EqualTo(Vector3.zero));
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => follower.Next(position, locations, "unknown", out _));
        }

        [Test]
        public void SmallDestinationChangesAfterArrivalReplanInsteadOfReportingPartial()
        {
            Assert.That(NavMesh.SamplePosition(new Vector3(6, 0, 0), out var hit, .5f, mesh.Filter), Is.True);
            var position = hit.position; var clock = new Clock();
            var follower = new NavMeshPathFollower(mesh.Filter, clock: clock);
            Assert.That(follower.Next(position, position, out _), Is.EqualTo(NavigationProgress.Arrived));
            var target = position + Vector3.right * .2f; // Below the .25 m regular repath threshold.
            Assert.That(follower.Next(position, target, out _), Is.EqualTo(NavigationProgress.Moving));
            var locations = new LocationRegistry(new[] { new CatalogHandle("moving_marker", HandleKind.Location, target.ToPosition()) });
            Assert.That(Follow(follower, locations, "moving_marker", ref position, clock), Is.EqualTo(NavigationProgress.Arrived));
        }

        [Test]
        public void AgentProfilesAndLifetimesAreIndependent()
        {
            int before = NavMesh.GetSettingsCount();
            var other = new RuntimeNavMesh(RuntimeNavMesh.CollectSources(geometry.transform), Bounds,
                new NavMeshAgentProfile(2.4f, .6f, climb: .2f, voxelSize: .08f));
            try
            {
                Assert.That(other.Filter.agentTypeID, Is.Not.EqualTo(mesh.Filter.agentTypeID));
                Assert.That(NavMesh.GetSettingsCount(), Is.EqualTo(before + 1));
                other.Dispose(); other.Dispose();
                Assert.That(other.IsValid, Is.False);
                Assert.That(NavMesh.GetSettingsCount(), Is.EqualTo(before));
                var path = new NavMeshPath();
                Assert.That(NavMesh.CalculatePath(new Vector3(-6, 0, 0), new Vector3(6, 0, 0), mesh.Filter, path), Is.True);
                Assert.That(path.status, Is.EqualTo(NavMeshPathStatus.PathComplete));
            }
            finally { other.Dispose(); }
        }
    }
}
