using System.Collections;
using GoalDrivenAI.Tactics;
using NUnit.Framework;
using UnityEngine;
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
            foreach (var guard in demo.GetComponentsInChildren<MazeGuard>()) Assert.That(guard.Goal, Is.EqualTo("patrol"));
            Assert.That(demo.Won, Is.False);
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
