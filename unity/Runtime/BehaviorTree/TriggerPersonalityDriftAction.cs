using System.Collections;
using NpcSoulEngine.Runtime.Components;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;

namespace NpcSoulEngine.Runtime.BehaviorTree
{
    /// <summary>
    /// BT Action: applies accumulated interaction history to the NPC's base personality,
    /// then notifies the Azure Functions layer to persist the updated profile.
    ///
    /// Run this once per session-end or when SessionsSinceLastDrift exceeds threshold.
    /// Personality drift is one-way: aggression increases from repeated THREAT actions
    /// and never spontaneously resets — it must be overcome by sustained positive interactions.
    ///
    /// Drift rules (conservative; tuned per game):
    ///   - High sustained hostility  → Vengefulness +, Forgiveness -
    ///   - High sustained trust      → Agreeableness +, Neuroticism -
    ///   - Many betrayals            → Greed +, Loyalty -
    ///   - Many rescues/kindnesses   → Loyalty +, Aggression -
    /// </summary>
    public sealed class TriggerPersonalityDriftAction : BtAction
    {
        [SerializeField] private NpcSoulComponent npcSoulComponent;

        [Header("Drift thresholds")]
        [Tooltip("Minimum absolute trust delta before drift is applied")]
        [SerializeField] private float driftThreshold = 20f;

        [Tooltip("Maximum single-session personality shift (0–1 scale)")]
        [SerializeField] [Range(0f, 0.15f)] private float maxDriftPerSession = 0.05f;

        [Tooltip("Sessions between drift evaluations")]
        [SerializeField] private int driftEvalInterval = 3;

        private NpcBlackboard _blackboard;
        private bool _done;

        public override void OnStart()
        {
            _done = false;

            if (npcSoulComponent == null)
                npcSoulComponent = GetComponentInParent<NpcSoulComponent>();
            if (_blackboard == null)
                _blackboard = GetComponentInParent<NpcBlackboard>();

            if (_blackboard == null || !npcSoulComponent.MemoryLoaded)
            {
                _done = true;
                return;
            }

            _blackboard.SessionsSinceLastDrift++;
            if (_blackboard.SessionsSinceLastDrift < driftEvalInterval)
            {
                _done = true;
                return;
            }

            var trustDelta    = _blackboard.TrustDeltaAccumulated;
            var hostileDelta  = _blackboard.HostilityDeltaAccumulated;

            if (Mathf.Abs(trustDelta) < driftThreshold && hostileDelta < driftThreshold)
            {
                _blackboard.ResetDriftAccumulators();
                _done = true;
                return;
            }

            npcSoulComponent.StartCoroutine(ApplyDrift(trustDelta, hostileDelta));
        }

        private IEnumerator ApplyDrift(float trustDelta, float hostileDelta)
        {
            var memory = npcSoulComponent.CurrentMemory;
            if (memory == null) { _done = true; yield break; }

            // Drift is a small nudge — we update the local NpcSoulComponent and
            // post the updated personality to Azure as a lightweight PUT.
            var driftMagnitude = Mathf.Clamp01(
                (Mathf.Abs(trustDelta) + hostileDelta) / 200f) * maxDriftPerSession;

            var driftEvent = new MemoryEventPayload
            {
                npcId       = npcSoulComponent.NpcId,
                playerId    = NpcSoulEngineManager.Instance?.PlayerId ?? "unknown",
                actionType  = "NeutralInteraction",  // triggers score recalculation without a new event weight
                context     = new ActionContext
                {
                    summary  = $"Session personality drift applied (trustDelta={trustDelta:+0.0;-0.0}, hostile={hostileDelta:F0})",
                    stakes   = "Low",
                    publicness = 0f
                },
                significanceHint = driftMagnitude
            };

            var task = AzureMemoryService.Instance?.ProcessEventAsync(driftEvent);
            if (task != null)
                yield return new WaitUntil(() => task.IsCompleted);

            if (task?.IsCompletedSuccessfully == true)
            {
                npcSoulComponent.ApplyMemoryState(task.Result);
                _blackboard.ApplyMemoryState(task.Result);
            }

            _blackboard.ResetDriftAccumulators();
            _done = true;
        }

        public override BtTaskStatus OnUpdate() => _done ? BtTaskStatus.Success : BtTaskStatus.Running;
    }
}
