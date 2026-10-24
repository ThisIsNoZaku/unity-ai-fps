using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GoalDrivenAI.Commands;
using GoalDrivenAI.Language;
using UnityEngine;

namespace GoalDrivenAI.Unity
{
    public sealed class CommandInputController : MonoBehaviour
    {
        [SerializeField] private CommandHost host;
        [SerializeField] private Actor[] selectedRecipients = Array.Empty<Actor>();
        [SerializeField] private Transform marker;
        [SerializeField] private CommandRegion holdRegion;
        [SerializeField] private bool showPrototypeMenu = true;
        private CommandInterpretationService interpretation;
        private CommandServiceAdapter plans;
        private CapturedContextSource contexts;
        private ISpeechRecognizer speech;
        private CancellationTokenSource microphone;
        private Task<FinalTranscript> transcript;
        private CommandContext voiceSnapshot;
        private string text = "";
        private long lastOutcome;
        public string Phase { get; private set; } = "Ready";
        public string Feedback { get; private set; } = "";
        public BoundedPlanJournal Journal { get; private set; }

        // Configure on the main thread with a knowledge-filtered context source and an explicit provider.
        public void Configure(ILanguageCommandModel model, ICommandContextSource contextSource,
            CommandCatalog catalog, ISpeechRecognizer recognizer = null)
        {
            CancelPending();
            interpretation?.Dispose();
            contexts = new CapturedContextSource(contextSource);
            var clock = new UnityClock();
            var thread = new CapturedThreadScheduler();
            Journal = new BoundedPlanJournal();
            plans = new CommandServiceAdapter(host.Service, contexts, clock, thread, Journal,
                report: message => Feedback = message);
            interpretation = new CommandInterpretationService(clock, contexts, model, new PlanCompiler(catalog),
                plans, thread, Journal);
            speech = recognizer;
            lastOutcome = 0;
        }

        private string[] Recipients()
        {
            var ids = new List<string>();
            foreach (Actor actor in selectedRecipients)
                if (actor != null && !ids.Contains(actor.Id)) ids.Add(actor.Id);
            return ids.ToArray();
        }

        public void SubmitText(string utterance)
        {
            CancelPending();
            if (string.IsNullOrWhiteSpace(utterance) || utterance.Length > 4096)
            { Feedback = "Enter a command of at most 4096 characters."; return; }
            if (interpretation == null) { Feedback = "No language provider configured; use the command menu."; return; }
            contexts.Next = contexts.Source.Capture(host.PlayerIdentity, Recipients());
            Begin(new FinalTranscript(utterance), contexts.Next);
        }

        public void BeginVoice()
        {
            CancelPending();
            if (interpretation == null || speech == null) { Feedback = "No speech recognizer configured."; return; }
            voiceSnapshot = contexts.Source.Capture(host.PlayerIdentity, Recipients());
            microphone = new CancellationTokenSource();
            Phase = "Listening";
            try { transcript = speech.RecognizeFinalAsync(microphone.Token); }
            catch (Exception)
            {
                microphone.Dispose();
                microphone = null;
                Phase = "Failed";
                Feedback = "Speech recognizer unavailable.";
            }
        }

        private void Begin(FinalTranscript final, CommandContext snapshot)
        {
            contexts.Next = snapshot;
            interpretation.Begin(final, snapshot.Issuer, snapshot.Recipients, 2.5);
            Phase = "Interpreting";
        }

        private void Update()
        {
            if (transcript != null && transcript.IsCompleted)
            {
                Task<FinalTranscript> completed = transcript;
                transcript = null;
                if (completed.Status == TaskStatus.RanToCompletion && completed.Result != null
                    && microphone != null && !microphone.IsCancellationRequested
                    && contexts.Source.IsCurrent(voiceSnapshot))
                    Begin(completed.Result, voiceSnapshot);
                else { Phase = "Failed"; Feedback = "Transcription cancelled, stale or unavailable."; }
                microphone?.Dispose();
                microphone = null;
            }
            interpretation?.Pump();
            plans?.Pump();
            if (interpretation != null && interpretation.Outcomes.Count > 0)
            {
                var outcome = interpretation.Outcomes[interpretation.Outcomes.Count - 1];
                if (outcome.Generation > lastOutcome)
                {
                    lastOutcome = outcome.Generation;
                    Phase = outcome.Status.ToString();
                    Feedback = outcome.Detail;
                }
            }
        }

        public void CancelPending()
        {
            microphone?.Cancel();
            microphone?.Dispose();
            microphone = null;
            if (transcript != null)
                _ = transcript.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            transcript = null;
            if (interpretation != null) interpretation.Cancel(host.PlayerIdentity, Recipients());
        }

        public void Stop()
        {
            CancelPending();
            foreach (string recipient in Recipients()) Feedback = host.StopPlayerOrder(recipient).Reason;
        }

        private void OnDisable()
        {
            CancelPending();
            interpretation?.Dispose();
            interpretation = null;
        }

        private void OnGUI()
        {
            if (!showPrototypeMenu || host == null) return;
            GUILayout.BeginArea(new Rect(12, 12, 420, 300), GUI.skin.box);
            GUILayout.Label("Goal-driven AI: " + Phase);
            text = GUILayout.TextField(text, 4096);
            if (GUILayout.Button("Interpret text")) SubmitText(text);
            if (GUILayout.Button("Move to captured marker") && marker != null)
                foreach (string id in Recipients()) Feedback = host.IssuePlayerOrder(id, OrderType.Move,
                    marker.position.ToPosition()).Reason;
            if (GUILayout.Button("Hold authored area") && marker != null && holdRegion != null)
                foreach (string id in Recipients()) Feedback = host.IssuePlayerOrder(id, OrderType.HoldArea,
                    marker.position.ToPosition(), regionId: holdRegion.Id).Reason;
            if (GUILayout.Button("Hold fire"))
                foreach (string id in Recipients()) Feedback = host.Service.SetPolicy(host.PlayerIdentity,
                    new PolicyRequest(Guid.NewGuid().ToString("N"), id, new OrderConstraints(holdFire: true))).Reason;
            if (GUILayout.Button("Stop / cancel current order")) Stop();
            GUILayout.Label(Feedback);
            foreach (string id in Recipients())
            {
                OrderRecord order = host.Service.GetPrimary(id);
                GUILayout.Label(id + ": " + (order == null ? "autonomous" : order.Request.Type + " / " + order.Status));
            }
            GUILayout.EndArea();
        }

        private sealed class CapturedContextSource : ICommandContextSource
        {
            public ICommandContextSource Source { get; }
            public CommandContext Next { get; set; }
            public CapturedContextSource(ICommandContextSource source) => Source = source;
            public CommandContext Capture(string issuer, IReadOnlyList<string> recipients)
            {
                CommandContext snapshot = Next ?? Source.Capture(issuer, recipients);
                Next = null;
                return snapshot;
            }
            public bool IsCurrent(CommandContext snapshot) => Source.IsCurrent(snapshot);
        }
    }
}
