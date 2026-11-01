using System.Collections;
using GoalDrivenAI.Unity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GoalDrivenAI.Tests
{
    public sealed class PerspectiveDebugTests
    {
        private GameObject agent, hostListenerObject, otherAgent;
        private PerceptionSensor sensor;
        private PerspectiveDebug perspective;
        private AudioClip clip;
        private bool previousPause;
        [SetUp]
        public void SetUp()
        {
            previousPause = AudioListener.pause; AudioListener.pause = false;
            hostListenerObject = new GameObject("Host listener"); hostListenerObject.AddComponent<AudioListener>();
            agent = new GameObject("Agent using the framework sensor");
            sensor = agent.AddComponent<PerceptionSensor>(); perspective = agent.AddComponent<PerspectiveDebug>(); perspective.Bind(sensor);
            clip = AudioClip.Create("Test heard sound", 160, 1, 16000, false);
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            perspective.StopObserving();
            if (otherAgent != null) Object.Destroy(otherAgent);
            Object.Destroy(agent); Object.Destroy(hostListenerObject); Object.Destroy(clip);
            AudioListener.pause = previousPause; yield return null;
        }
        [UnityTest]
        public IEnumerator CameraUsesActualEyeConeRangeAndMasksAndBlacksOutWhenSenseIsDisabled()
        {
            int observations = 0; sensor.Observed += _ => observations++;
            agent.transform.SetPositionAndRotation(new Vector3(2, 3, 4), Quaternion.Euler(0, 70, 0));
            perspective.BeginObserving(); yield return null;
            var view = perspective.ViewCamera;
            Assert.That(Vector3.Distance(view.transform.position, sensor.Eye.position), Is.LessThan(.001f));
            Assert.That(Quaternion.Angle(view.transform.rotation, sensor.Eye.rotation), Is.LessThan(.001f));
            Assert.That(view.farClipPlane, Is.EqualTo(sensor.VisionRange));
            Assert.That(view.cullingMask, Is.EqualTo(sensor.VisionMask));
            Assert.That(view.fieldOfView, Is.LessThanOrEqualTo(sensor.VisionFieldOfView));
            Assert.That(observations, Is.Zero, "Debug observation must not perform extra AI sensing");
            sensor.enabled = false; yield return null;
            Assert.That(view.cullingMask, Is.Zero);
            sensor.enabled = true; yield return null; Assert.That(view.cullingMask, Is.EqualTo(sensor.VisionMask));
        }
        [Test]
        public void OnlyAcceptedHeardSoundsPlayWithTheirOriginalClipAndPerceivedGain()
        {
            perspective.BeginObserving();
            SoundBus.Publish(new Vector3(100, 0, 0), 3, Time.timeAsDouble, clip);
            Assert.That(perspective.PlaybackCount, Is.Zero);
            SoundBus.Publish(Vector3.right * 2, 4, Time.timeAsDouble, clip, .6f, 1.2f);
            Assert.That(perspective.PlaybackCount, Is.EqualTo(1));
            Assert.That(perspective.LastPlaybackSource.clip, Is.SameAs(clip));
            Assert.That(perspective.LastPlaybackSource.volume, Is.EqualTo(.3f).Within(.001f));
            Assert.That(perspective.LastPlaybackSource.pitch, Is.EqualTo(1.2f).Within(.001f));
            Assert.That(perspective.LastPlaybackSource.panStereo, Is.GreaterThan(0));
            Assert.That(perspective.LastPlaybackSource.ignoreListenerPause, Is.True);
            sensor.enabled = false;
            SoundBus.Publish(Vector3.zero, 4, Time.timeAsDouble, clip);
            Assert.That(perspective.PlaybackCount, Is.EqualTo(1));
        }
        [Test]
        public void DuplicateStaleAndCliplessEventsDoNotReplayAndVoicesStayBounded()
        {
            perspective.BeginObserving();
            var sound = new SoundBus.Sound(Vector3.zero, 4, Time.timeAsDouble, clip);
            SoundBus.Publish(sound); SoundBus.Publish(sound);
            Assert.That(perspective.PlaybackCount, Is.EqualTo(1));
            SoundBus.Publish(Vector3.zero, 4, Time.timeAsDouble - 3, clip);
            SoundBus.Publish(Vector3.zero, 4, Time.timeAsDouble);
            Assert.That(perspective.PlaybackCount, Is.EqualTo(1));
            for (int i = 0; i < 100; i++) SoundBus.Publish(Vector3.zero, 4, Time.timeAsDouble, clip);
            Assert.That(perspective.PlaybackCount, Is.EqualTo(101));
            Assert.That(agent.GetComponentsInChildren<AudioSource>().Length, Is.EqualTo(8));
        }
        [Test]
        public void DebugSubscriberFailureCannotInterruptTheAgentHearingPipeline()
        {
            int observations = 0; sensor.Observed += _ => observations++;
            perspective.BeginObserving(); perspective.SoundPlayed += _ => throw new System.InvalidOperationException("UI failed");
            LogAssert.Expect(LogType.Warning, "Perspective audio observer stopped after a playback failure.");
            SoundBus.Publish(Vector3.zero, 4, Time.timeAsDouble, clip);
            Assert.That(observations, Is.EqualTo(1));
            Assert.That(perspective.IsObserving, Is.False);
            Assert.That(AudioListener.pause, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void ObservationRestoresTheHostAudioPauseAndListenerState(bool originallyPaused)
        {
            AudioListener.pause = originallyPaused;
            var host = hostListenerObject.GetComponent<AudioListener>();
            perspective.BeginObserving();
            Assert.That(AudioListener.pause, Is.True); Assert.That(host.enabled, Is.False);
            Assert.That(PerspectiveDebug.Active, Is.SameAs(perspective));
            perspective.StopObserving();
            Assert.That(AudioListener.pause, Is.EqualTo(originallyPaused)); Assert.That(host.enabled, Is.True);
            Assert.That(perspective.ViewCamera.enabled, Is.False);
            Assert.That(PerspectiveDebug.Active, Is.Null);
        }
        [Test]
        public void SwitchingAgentsAndDisablingTheDebugViewUnsubscribesTheOldPerspective()
        {
            otherAgent = new GameObject("Another agent"); var otherSensor = otherAgent.AddComponent<PerceptionSensor>();
            var other = otherAgent.AddComponent<PerspectiveDebug>(); other.Bind(otherSensor);
            perspective.BeginObserving(); other.BeginObserving();
            Assert.That(perspective.IsObserving, Is.False); Assert.That(other.IsObserving, Is.True);
            SoundBus.Publish(Vector3.zero, 4, Time.timeAsDouble, clip);
            Assert.That(perspective.PlaybackCount, Is.Zero); Assert.That(other.PlaybackCount, Is.EqualTo(1));
            other.enabled = false;
            Assert.That(PerspectiveDebug.Active, Is.Null); Assert.That(AudioListener.pause, Is.False);
            SoundBus.Publish(Vector3.zero, 4, Time.timeAsDouble, clip);
            Assert.That(other.PlaybackCount, Is.EqualTo(1));
            Assert.That(hostListenerObject.GetComponent<AudioListener>().enabled, Is.True);
        }
    }
}
