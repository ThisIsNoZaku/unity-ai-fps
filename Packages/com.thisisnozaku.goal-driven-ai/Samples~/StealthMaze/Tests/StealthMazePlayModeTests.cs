using System.Collections;
using GoalDrivenAI.Tactics;
using GoalDrivenAI.Stealth;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using GoalDrivenAI.Unity;
using UnityEngine.TestTools;

namespace GoalDrivenAI.Samples.StealthMaze.Tests
{
    public sealed class StealthMazePlayModeTests
    {
        private MazeDemo demo;
        private GameObject blocker;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            demo = new GameObject("Test maze").AddComponent<MazeDemo>();
            yield return null;
            demo.JoinRole(MazeRole.Infiltrator);
            demo.Player.enabled = false;
            foreach (var guard in demo.GetComponentsInChildren<MazeGuard>()) guard.enabled = false;
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (blocker != null) Object.Destroy(blocker);
            Object.Destroy(demo.gameObject); yield return null;
        }
        private static void Teleport(Component actor, Vector3 position)
        {
            var motor = actor.GetComponent<CharacterController>();
            motor.enabled = false; actor.transform.position = position; motor.enabled = true;
            Physics.SyncTransforms();
        }

        [UnityTest]
        public IEnumerator SceneBootstrapsAndCaptureClearsTheEncounter()
        {
            Assert.That(demo.GetComponentsInChildren<MazeGuard>().Length, Is.EqualTo(4));
            Assert.That(demo.GetComponentInChildren<Camera>(), Is.Not.Null);
            Assert.That(demo.Map.Route((1, 1), demo.Cell(demo.Exit)).Count, Is.GreaterThan(0));
            bool crawlFound = false;
            for (int y = 0; y < demo.Map.Height; y++) for (int x = 0; x < demo.Map.Width; x++)
                if (demo.Map[x, y] == 'c')
                {
                    crawlFound = true;
                    Assert.That(Physics.CheckSphere(demo.Point((x, y)) + Vector3.up * 1.5f, .2f), Is.True);
                    Assert.That(Physics.CheckSphere(demo.Point((x, y)) + Vector3.up * .5f, .2f), Is.False);
                }
            Assert.That(crawlFound, Is.True);
            yield return new WaitForSeconds(1.1f);
            Teleport(demo.Player, demo.Exit);
            var captor = demo.GetComponentsInChildren<MazeGuard>()[0];
            Teleport(captor, demo.Exit + Vector3.forward);
            captor.GetComponent<CharacterController>().Move(Vector3.back);
            yield return null;
            Assert.That(Vector3.Distance(demo.Player.transform.position, demo.Point((1, 1))), Is.LessThan(.01f));
            foreach (var guard in demo.GetComponentsInChildren<MazeGuard>()) Assert.That(guard.Goal, Is.EqualTo("defend"));
            Assert.That(demo.Won, Is.False);
        }

        [Test]
        public void RoleSelectionChangesCameraAndRejectsObserverOrdersAndRestarts()
        {
            demo.JoinRole(MazeRole.Commander);
            Assert.That(demo.OrderProtection(0, 2), Is.True);
            Assert.That(demo.Scenario.DefenseFor(0), Is.EqualTo(2));
            demo.Scenario.Visit(MazeScenario.InfiltratorId, demo.Scenario.Checkpoints[0]);
            demo.JoinRole(MazeRole.Observer);
            Assert.That(demo.OrderProtection(0, 1), Is.False);
            demo.Restart();
            Assert.That(demo.Scenario.Reached, Is.EqualTo(1));
            Assert.That(demo.Scenario.DefenseFor(0), Is.EqualTo(2));
            foreach (var camera in demo.Player.GetComponentsInChildren<Camera>()) Assert.That(camera.enabled, Is.False);
            demo.JoinRole(MazeRole.Infiltrator);
            Assert.That(demo.Player.GetComponentInChildren<Camera>().enabled, Is.True);
            Assert.That(demo.Player.Faction, Is.EqualTo("infiltrator"));
            foreach (var guard in demo.GetComponentsInChildren<MazeGuard>()) Assert.That(guard.Faction, Is.EqualTo("guards"));
        }

        [Test]
        public void ObserverCanViewAgentSensesWithoutGainingCommandControls()
        {
            Assert.That(demo.ObserveGuard(0), Is.False);
            demo.JoinRole(MazeRole.Observer);
            Assert.That(demo.ObserveGuard(0), Is.True);
            Assert.That(PerspectiveDebug.Active, Is.SameAs(demo.GetComponentsInChildren<MazeGuard>()[0].GetComponent<PerspectiveDebug>()));
            Assert.That(demo.OrderPatrol(0), Is.False);
            Assert.That(demo.OrderProtection(0, 1), Is.False);
            demo.ShowRoleMenu();
            Assert.That(PerspectiveDebug.Active, Is.Null);
            demo.JoinRole(MazeRole.Observer); demo.ObserveGuard(1); demo.EndPerspective();
            Assert.That(PerspectiveDebug.Active, Is.Null);
        }

        [UnityTest]
        public IEnumerator AutonomousInfiltratorMovesInObserverModeAndPausesInRoleMenu()
        {
            demo.JoinRole(MazeRole.Observer);
            demo.Player.enabled = true;
            Vector3 before = demo.Player.transform.position;
            float deadline = Time.time + 2;
            while (Vector3.Distance(before, demo.Player.transform.position) <= .2f && Time.time < deadline)
                yield return null;
            Assert.That(Vector3.Distance(before, demo.Player.transform.position), Is.GreaterThan(.2f));
            demo.ShowRoleMenu(); before = demo.Player.transform.position;
            yield return new WaitForSeconds(.2f);
            Assert.That(Vector3.Distance(before, demo.Player.transform.position), Is.LessThan(.001f));
        }

        [Test]
        public void NavMeshesIncludeCoverAndRespectCrawlClearance()
        {
            var path = new NavMeshPath();
            foreach (var guard in demo.GetComponentsInChildren<MazeGuard>())
                for (int i = 0; i < demo.Scenario.Checkpoints.Count; i++)
                {
                    Assert.That(NavMesh.CalculatePath(guard.transform.position, demo.CheckpointPosition(i),
                        demo.GuardNavigation.Filter, path), Is.True);
                    Assert.That(path.status, Is.EqualTo(NavMeshPathStatus.PathComplete),
                        guard.name + " cannot reach checkpoint " + (i + 1));
                }
            bool found = false;
            for (int y = 0; y < demo.Map.Height; y++) for (int x = 0; x < demo.Map.Width; x++)
                if (demo.Map[x, y] == 'c')
                {
                    found = true;
                    Assert.That(NavMesh.SamplePosition(demo.Point((x, y)), out _, .1f,
                        demo.InfiltratorNavigation.Filter), Is.True, "Crouched infiltrator needs a crawl route");
                    Assert.That(NavMesh.SamplePosition(demo.Point((x, y)), out _, .1f,
                        demo.GuardNavigation.Filter), Is.False, "Standing guards must not enter crawl passages");
                }
            Assert.That(found, Is.True);
        }

        [UnityTest]
        public IEnumerator AutonomousInfiltratorNavigatesAroundCoverAndEscapes()
        {
            // Keep the actual generated colliders, but isolate navigation from capture/balance.
            foreach (var guard in demo.GetComponentsInChildren<MazeGuard>()) guard.gameObject.SetActive(false);
            demo.JoinRole(MazeRole.Observer); demo.Player.enabled = true;
            float previousScale = Time.timeScale;
            try
            {
                Time.timeScale = 5;
                float deadline = Time.time + 240;
                while (!demo.Won && Time.time < deadline) yield return null;
                Assert.That(demo.Scenario.Reached, Is.EqualTo(demo.Scenario.Checkpoints.Count));
                Assert.That(demo.Won, Is.True, "AI must pass crates, visit all checkpoints, and reach the exit");
            }
            finally { Time.timeScale = previousScale; }
        }

        [UnityTest]
        public IEnumerator GuardsReachAssignedPostsAroundCover()
        {
            demo.JoinRole(MazeRole.Commander);
            var guards = demo.GetComponentsInChildren<MazeGuard>();
            demo.Player.GetComponent<CharacterController>().enabled = false;
            for (int i = 0; i < guards.Length; i++)
            {
                foreach (var sense in guards[i].GetComponents<MazeSense>()) sense.enabled = false;
                Assert.That(demo.OrderProtection(i, i % demo.Scenario.Checkpoints.Count), Is.True);
                guards[i].enabled = true;
            }
            float previousScale = Time.timeScale;
            try
            {
                Time.timeScale = 5;
                float deadline = Time.time + 180;
                bool arrived = false;
                while (!arrived && Time.time < deadline)
                {
                    arrived = true;
                    for (int i = 0; i < guards.Length; i++)
                        if (Vector3.Distance(guards[i].transform.position,
                            demo.CheckpointPosition(demo.Scenario.DefenseFor(i))) > .3f) arrived = false;
                    yield return null;
                }
                Assert.That(arrived, Is.True, "All guards must reach their posts, rather than stall at cover");
            }
            finally { Time.timeScale = previousScale; }
        }

        [UnityTest]
        public IEnumerator GuardsPatrolEveryCheckpointAndContinueAfterALap()
        {
            demo.JoinRole(MazeRole.Commander);
            demo.Player.GetComponent<CharacterController>().enabled = false;
            var guards = demo.GetComponentsInChildren<MazeGuard>();
            var visits = new int[guards.Length];
            var expected = new int[guards.Length];
            for (int i = 0; i < guards.Length; i++)
            {
                foreach (var sense in guards[i].GetComponents<MazeSense>()) sense.enabled = false;
                Assert.That(demo.OrderPatrol(i), Is.True);
                expected[i] = guards[i].PatrolCheckpointIndex;
                guards[i].enabled = true;
            }
            float previousScale = Time.timeScale;
            try
            {
                Time.timeScale = 5;
                float deadline = Time.time + 300;
                bool finished = false;
                while (!finished && Time.time < deadline)
                {
                    finished = true;
                    for (int i = 0; i < guards.Length; i++)
                    {
                        var guard = guards[i];
                        if (guard.PatrolVisits != visits[i])
                        {
                            Assert.That(guard.PatrolVisits, Is.EqualTo(visits[i] + 1), guard.name);
                            Assert.That(Vector3.Distance(guard.transform.position,
                                demo.CheckpointPosition(expected[i])), Is.LessThan(.3f), guard.name + " skipped a checkpoint");
                            expected[i] = (expected[i] + 1) % demo.Scenario.CheckpointLocations.Count;
                            Assert.That(guard.PatrolCheckpointIndex, Is.EqualTo(expected[i]));
                            visits[i] = guard.PatrolVisits;
                        }
                        if (visits[i] <= demo.Scenario.CheckpointLocations.Count) finished = false;
                    }
                    yield return null;
                }
                for (int i = 0; i < guards.Length; i++)
                    Assert.That(visits[i], Is.GreaterThan(demo.Scenario.CheckpointLocations.Count),
                        guards[i].name + " did not complete a lap and continue");
                Assert.That(demo.OrderProtection(0, 2), Is.True);
                Assert.That(demo.Scenario.DefenseFor(0), Is.EqualTo(2));
                demo.JoinRole(MazeRole.Observer);
                Assert.That(demo.OrderPatrol(0), Is.False);
            }
            finally { Time.timeScale = previousScale; }
        }

        [Test]
        public void ExitCannotWinBeforeAllCheckpointsAreVisited()
        {
            Teleport(demo.Player, demo.Exit); demo.Win(); Assert.That(demo.Won, Is.False);
            foreach (var checkpoint in demo.Scenario.Checkpoints)
                demo.Scenario.Visit(MazeScenario.InfiltratorId, checkpoint);
            demo.Win(); Assert.That(demo.Won, Is.True);
            demo.Restart(); Assert.That(demo.Won, Is.False); Assert.That(demo.Scenario.Reached, Is.Zero);
        }

        [Test]
        public void CrawlPassagePreventsStandingAndLeavingItAllowsStanding()
        {
            var player = demo.Player;
            for (int y = 0; y < demo.Map.Height; y++) for (int x = 0; x < demo.Map.Width; x++)
                if (demo.Map[x, y] == 'c')
                {
                    player.SetCrouching(true); Teleport(player, demo.Point((x, y)));
                    player.SetCrouching(false);
                    Assert.That(player.Crouching, Is.True);
                    Assert.That(player.GetComponent<CharacterController>().height, Is.EqualTo(.9f));
                    Teleport(player, demo.Point((1, 1))); player.SetCrouching(false);
                    Assert.That(player.Crouching, Is.False);
                    Assert.That(player.GetComponent<CharacterController>().height, Is.EqualTo(1.8f));
                    return;
                }
            Assert.Fail("Expected a crawl passage");
        }

        [Test]
        public void ShoutsUseSpeakerRangeAndPreserveCapturedSighting()
        {
            var guards = demo.GetComponentsInChildren<MazeGuard>();
            var source = guards[0]; var receiver = guards[1];
            Teleport(source, demo.Point((1, 1))); Teleport(receiver, demo.Point((3, 1)));
            var hearing = receiver.GetComponent<MazeHearing>(); hearing.Clear(); hearing.range = 8; hearing.sensitivity = 1;
            var report = new MazeObservation(demo.Exit, Time.timeAsDouble, EvidenceKind.Communication, 1, "original-sighting");
            demo.Shout(source, report);
            Assert.That(hearing.Observe(receiver, demo.Player, out var heard), Is.True);
            Assert.That(heard.Id, Is.EqualTo(report.Id)); Assert.That(heard.Time, Is.EqualTo(report.Time));
            Assert.That(heard.Position, Is.EqualTo(report.Position));
            Assert.That(heard.Kind, Is.EqualTo(EvidenceKind.Communication));
            Teleport(receiver, demo.Point((5, 1))); demo.Shout(source, report);
            Assert.That(hearing.Observe(receiver, demo.Player, out _), Is.False);
            Teleport(receiver, demo.Point((3, 1))); hearing.sensitivity = 0; demo.Shout(source, report);
            Assert.That(hearing.Observe(receiver, demo.Player, out _), Is.False);
        }

        [Test]
        public void SightRequiresRecognitionAndFailsBehindOccludersOrOutsideRange()
        {
            var guard = demo.GetComponentsInChildren<MazeGuard>()[0];
            // Isolate the ray above maze geometry; preserve real character colliders.
            Teleport(guard, new Vector3(0, 5, 0)); guard.transform.rotation = Quaternion.identity;
            Teleport(demo.Player, new Vector3(0, 5, 5));
            var sight = guard.GetComponent<MazeSight>(); sight.Clear(); sight.range = 10; sight.sensitivity = 1;
            Assert.That(sight.Observe(guard, demo.Player, out _), Is.False);
            for (int i = 0; i < 5; i++) sight.Observe(guard, demo.Player, out _);
            Assert.That(sight.Observe(guard, demo.Player, out var seen), Is.True);
            Assert.That(seen.Kind, Is.EqualTo(EvidenceKind.Vision));
            blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blocker.transform.position = new Vector3(0, 6, 2.5f); blocker.transform.localScale = new Vector3(2, 3, 1);
            Physics.SyncTransforms();
            Assert.That(sight.Observe(guard, demo.Player, out _), Is.False);
            blocker.SetActive(false); sight.range = 3;
            Assert.That(sight.Observe(guard, demo.Player, out _), Is.False);
            sight.range = 10; sight.sensitivity = 0;
            Assert.That(sight.Observe(guard, demo.Player, out _), Is.False);
        }
    }
}
