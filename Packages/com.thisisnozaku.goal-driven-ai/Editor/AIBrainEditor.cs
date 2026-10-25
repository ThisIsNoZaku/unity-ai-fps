using UnityEditor;
using UnityEngine;

namespace GoalDrivenAI.Unity.Editor
{
    [CustomEditor(typeof(AIBrain))]
    public sealed class AIBrainEditor : UnityEditor.Editor
    {
        private void OnEnable()
        {
            EditorApplication.update += RepaintWhilePlaying;
        }

        private void OnDisable()
        {
            EditorApplication.update -= RepaintWhilePlaying;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime Goal / Plan Stack", EditorStyles.boldLabel);
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to inspect the active goal and plan.", MessageType.Info);
                return;
            }

            var brain = (AIBrain)target;
            EditorGUILayout.LabelField("Active goal", Display(brain.DebugActiveGoal));
            EditorGUILayout.LabelField("Current action", Display(brain.DebugRunningAction));
            EditorGUILayout.LabelField("Planning status", brain.DebugPlanningStatus);
            EditorGUILayout.LabelField("Expanded nodes", brain.DebugExpandedNodes.ToString());

            EditorGUILayout.LabelField("Queued plan steps", EditorStyles.boldLabel);
            var queuedActions = brain.DebugQueuedActions;
            if (queuedActions.Count == 0)
            {
                EditorGUILayout.LabelField("None");
            }
            else
            {
                EditorGUI.indentLevel++;
                for (var i = 0; i < queuedActions.Count; i++)
                    EditorGUILayout.LabelField((i + 1) + ". " + queuedActions[i]);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.LabelField("Last failure", Display(brain.LastFailure));
        }

        private void RepaintWhilePlaying()
        {
            if (Application.isPlaying)
                Repaint();
        }

        private static string Display(string value) =>
            string.IsNullOrEmpty(value) ? "None" : value;
    }
}
