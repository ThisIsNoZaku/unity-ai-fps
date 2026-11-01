using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoalDrivenAI.Unity
{
    public readonly struct PerceivedSound
    {
        public readonly SoundBus.Sound Sound;
        public readonly float Gain;
        public PerceivedSound(SoundBus.Sound sound, float gain)
        {
            if (float.IsNaN(gain) || float.IsInfinity(gain) || gain < 0 || gain > 1)
                throw new ArgumentOutOfRangeException(nameof(gain));
            Sound = sound; Gain = gain;
        }
    }

    /// <summary>Expose the agent's actual eye and accepted hearing events, not a second debug sensor.</summary>
    public interface IAgentPerspectiveSource
    {
        Transform Eye { get; }
        bool VisionAvailable { get; }
        float VisionRange { get; }
        float VisionFieldOfView { get; }
        int VisionMask { get; }
        event Action<PerceivedSound> Heard;
    }

    /// <summary>Optional observer camera and bounded playback of sounds accepted by the selected agent.</summary>
    public sealed class PerspectiveDebug : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour senseSource;
        [SerializeField] private bool isolateAudio = true;
        [SerializeField, Range(1, 32)] private int voiceCount = 8;
        [SerializeField, Min(.01f)] private float maximumSoundAge = 2;
        [SerializeField] private bool hideOwnRenderers = true;
        private IAgentPerspectiveSource source;
        private Renderer[] ownRenderers;
        private readonly Dictionary<Renderer, bool> hiddenRenderers = new Dictionary<Renderer, bool>();
        private Camera view;
        private AudioListener listener;
        private AudioSource[] voices;
        private int nextVoice;
        private readonly Queue<string> recentIds = new Queue<string>();
        private readonly HashSet<string> playedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<AudioListener> displacedListeners = new List<AudioListener>();
        private bool previousPause, ownsPause;
        private float lastRange = -1;
        private readonly float[] cullDistances = new float[32];
        public static PerspectiveDebug Active { get; private set; }
        public bool IsObserving { get; private set; }
        public Camera ViewCamera { get { EnsureView(); return view; } }
        public IClock Clock { get; set; } = new UnityClock();
        public int PlaybackCount { get; private set; }
        public AudioSource LastPlaybackSource { get; private set; }
        public event Action<PerceivedSound> SoundPlayed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetObserver() { if (Active != null) Active.StopObserving(); Active = null; }

        public void Bind(MonoBehaviour component)
        {
            if (!(component is IAgentPerspectiveSource))
                throw new ArgumentException("Sense source must implement IAgentPerspectiveSource.", nameof(component));
            StopObserving(); senseSource = component; source = (IAgentPerspectiveSource)component;
        }
        [ContextMenu("Observe this agent")]
        public void BeginObserving() => BeginObserving(null);
        public void BeginObserving(RenderTexture target)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Observe agents in Play Mode.");
            if (!isActiveAndEnabled) throw new InvalidOperationException("PerspectiveDebug is disabled.");
            if (source == null)
            {
                if (senseSource == null) senseSource = GetComponents<MonoBehaviour>().FirstOrDefault(c => c is IAgentPerspectiveSource);
                if (!(senseSource is IAgentPerspectiveSource selected)) throw new InvalidOperationException("No agent perspective source configured.");
                source = selected;
            }
            if (IsObserving && view == null) StopObserving();
            if (IsObserving) { view.targetTexture = target; RefreshView(); return; }
            if (Active != null) Active.StopObserving();
            EnsureView();
            foreach (var other in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                if (other != listener && other.enabled) { displacedListeners.Add(other); other.enabled = false; }
            if (isolateAudio) { previousPause = AudioListener.pause; AudioListener.pause = true; ownsPause = true; }
            IsObserving = true; Active = this; source.Heard += PlayHeard;
            ownRenderers = GetComponentsInChildren<Renderer>();
            Camera.onPreCull += HideForView; Camera.onPostRender += RestoreAfterView;
            RenderPipelineManager.beginCameraRendering += BeforePipelineCamera;
            RenderPipelineManager.endCameraRendering += AfterPipelineCamera;
            view.targetTexture = target; view.enabled = true; listener.enabled = true; RefreshView();
        }
        [ContextMenu("Stop observing")]
        public void StopObserving()
        {
            if (!IsObserving) return;
            IsObserving = false;
            Camera.onPreCull -= HideForView; Camera.onPostRender -= RestoreAfterView;
            RenderPipelineManager.beginCameraRendering -= BeforePipelineCamera;
            RenderPipelineManager.endCameraRendering -= AfterPipelineCamera;
            RestoreRenderers();
            if (source != null) source.Heard -= PlayHeard;
            if (view != null) { view.enabled = false; view.targetTexture = null; }
            if (listener != null) listener.enabled = false;
            if (voices != null) foreach (var voice in voices) if (voice != null) voice.Stop();
            foreach (var other in displacedListeners) if (other != null) other.enabled = true;
            displacedListeners.Clear();
            if (ownsPause) { AudioListener.pause = previousPause; ownsPause = false; }
            if (Active == this) Active = null;
        }
        private void EnsureView()
        {
            if (view != null) return;
            var cameraObject = new GameObject("Agent perspective camera"); cameraObject.SetActive(false); cameraObject.transform.SetParent(transform, false);
            view = cameraObject.AddComponent<Camera>(); view.enabled = false;
            view.depth = 100; view.nearClipPlane = .03f; view.clearFlags = CameraClearFlags.SolidColor; view.backgroundColor = Color.black;
            view.layerCullSpherical = true;
            listener = cameraObject.AddComponent<AudioListener>(); listener.enabled = false;
            voices = new AudioSource[Mathf.Clamp(voiceCount, 1, 32)];
            for (int i = 0; i < voices.Length; i++)
            {
                voices[i] = cameraObject.AddComponent<AudioSource>();
                voices[i].playOnAwake = false; voices[i].spatialBlend = 0; voices[i].ignoreListenerPause = true;
            }
            cameraObject.SetActive(true);
        }
        public void RefreshView()
        {
            if (!IsObserving) return;
            if (view == null) { StopObserving(); return; }
            if (senseSource == null || source == null || source.Eye == null) { StopObserving(); return; }
            view.transform.SetPositionAndRotation(source.Eye.position, source.Eye.rotation);
            bool sees = source.VisionAvailable && source.VisionRange > 0;
            view.cullingMask = sees ? source.VisionMask : 0;
            float sourceRange = source.VisionRange;
            float cone = source.VisionFieldOfView;
            if (float.IsNaN(sourceRange) || float.IsInfinity(sourceRange) || float.IsNaN(cone) || float.IsInfinity(cone))
            { view.cullingMask = 0; sourceRange = .04f; cone = 1; }
            float range = Mathf.Max(.04f, sourceRange);
            view.nearClipPlane = Mathf.Min(.03f, range * .5f); view.farClipPlane = range;
            // Fit the rectangular camera inside the sensor's circular vision cone.
            float half = Mathf.Clamp(cone, 1, 178) * Mathf.Deg2Rad * .5f;
            view.fieldOfView = 2 * Mathf.Atan(Mathf.Tan(half) / Mathf.Sqrt(1 + view.aspect * view.aspect)) * Mathf.Rad2Deg;
            if (!Mathf.Approximately(lastRange, range))
            {
                for (int i = 0; i < cullDistances.Length; i++) cullDistances[i] = range;
                view.layerCullDistances = cullDistances; lastRange = range;
            }
        }
        private void PlayHeard(PerceivedSound heard)
        {
            try { PlayHeardCore(heard); }
            catch (Exception)
            {
                StopObserving();
                Debug.LogWarning("Perspective audio observer stopped after a playback failure.", this);
            }
        }
        private void PlayHeardCore(PerceivedSound heard)
        {
            if (!IsObserving || senseSource == null || source.Eye == null || heard.Sound.Clip == null || heard.Gain <= 0 ||
                string.IsNullOrEmpty(heard.Sound.Id) || playedIds.Contains(heard.Sound.Id)) return;
            double age = (Clock?.Now ?? Time.timeAsDouble) - heard.Sound.ObservedAt;
            if (age < -.1 || age > maximumSoundAge) return;
            playedIds.Add(heard.Sound.Id); recentIds.Enqueue(heard.Sound.Id);
            while (recentIds.Count > 64) playedIds.Remove(recentIds.Dequeue());
            var voice = voices[nextVoice]; nextVoice = (nextVoice + 1) % voices.Length; voice.Stop();
            voice.clip = heard.Sound.Clip; voice.volume = Mathf.Clamp01(heard.Sound.Volume * heard.Gain); voice.pitch = heard.Sound.Pitch;
            Vector3 local = source.Eye.InverseTransformDirection(heard.Sound.Position - source.Eye.position);
            voice.panStereo = local.sqrMagnitude > .0001f ? Mathf.Clamp(local.normalized.x, -1, 1) : 0;
            voice.Play(); LastPlaybackSource = voice; PlaybackCount++; SoundPlayed?.Invoke(heard);
        }
        private void HideForView(Camera camera)
        {
            if (!IsObserving || camera != view || !hideOwnRenderers || hiddenRenderers.Count != 0) return;
            foreach (var renderer in ownRenderers)
                if (renderer != null) { hiddenRenderers[renderer] = renderer.forceRenderingOff; renderer.forceRenderingOff = true; }
        }
        private void RestoreAfterView(Camera camera) { if (camera == view) RestoreRenderers(); }
        private void BeforePipelineCamera(ScriptableRenderContext context, Camera camera) => HideForView(camera);
        private void AfterPipelineCamera(ScriptableRenderContext context, Camera camera) => RestoreAfterView(camera);
        private void RestoreRenderers()
        {
            foreach (var pair in hiddenRenderers) if (pair.Key != null) pair.Key.forceRenderingOff = pair.Value;
            hiddenRenderers.Clear();
        }
        private void LateUpdate() => RefreshView();
        private void OnDisable() => StopObserving();
        private void OnDestroy() => StopObserving();
    }
}
