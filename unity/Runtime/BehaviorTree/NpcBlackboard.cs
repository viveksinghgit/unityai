using System.Collections.Generic;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;

namespace NpcSoulEngine.Runtime.BehaviorTree
{
    /// <summary>
    /// Component-based blackboard attached to every NPC participating in the Soul Engine.
    /// BT nodes write and read through this — never access NpcSoulComponent directly from BT.
    ///
    /// Attach to the same GameObject as NpcSoulComponent and the BT controller.
    /// </summary>
    public sealed class NpcBlackboard : MonoBehaviour
    {
        // ─── Memory state (written by FetchNpcMemoryAction) ───────────────────

        [Header("Memory State (runtime — do not set in Inspector)")]
        [SerializeField] [ReadOnly] private float _trustScore;
        [SerializeField] [ReadOnly] private float _fearScore;
        [SerializeField] [ReadOnly] private float _hostilityScore;
        [SerializeField] [ReadOnly] private string _currentEmotion;
        [SerializeField] [ReadOnly] private string _playerArchetype;

        public float TrustScore        { get => _trustScore;      private set => _trustScore = value; }
        public float FearScore         { get => _fearScore;       private set => _fearScore = value; }
        public float HostilityScore    { get => _hostilityScore;  private set => _hostilityScore = value; }
        public string CurrentEmotion   { get => _currentEmotion;  private set => _currentEmotion = value; }
        public string PlayerArchetype  { get => _playerArchetype; private set => _playerArchetype = value; }

        // Full state for BT nodes that need fine-grained access
        public NpcMemoryState MemoryState { get; private set; }

        // ─── Behavior flags (derived from BehaviorOverrides) ──────────────────
        public bool RefuseTrade  { get; private set; }
        public bool AlertGuards  { get; private set; }
        public bool GiveDiscount { get; private set; }
        public bool AvoidPlayer  { get; private set; }
        public bool SeekHelp     { get; private set; }

        // ─── Dialogue state (written by InitiateDialogueAction) ───────────────
        public string LastPlayerUtterance   { get; set; }
        public string LastNpcResponse       { get; set; }
        public DialogueResponse LastDialogueResponse { get; set; }
        public readonly List<ConversationTurn> ConversationHistory = new();

        // ─── Navigation target ────────────────────────────────────────────────
        public Transform PlayerTransform { get; set; }
        public Transform FleeTarget      { get; set; }  // set by AvoidPlayerAction

        // ─── Personality drift accumulators (cross-session) ───────────────────
        // Deltas applied since last drift evaluation; cleared after TriggerPersonalityDriftAction runs
        public float TrustDeltaAccumulated      { get; set; }
        public float HostilityDeltaAccumulated  { get; set; }
        public int   SessionsSinceLastDrift     { get; set; }

        // ─── Write methods ────────────────────────────────────────────────────

        public void ApplyMemoryState(NpcMemoryState state)
        {
            MemoryState     = state;
            TrustScore      = state.trustScore;
            FearScore       = state.fearScore;
            HostilityScore  = state.hostilityScore;
            CurrentEmotion  = state.currentEmotion?.primary ?? "neutral";
            PlayerArchetype = state.playerArchetype;

            if (state.behaviorOverrides != null)
            {
                RefuseTrade  = state.behaviorOverrides.refuseTrade;
                AlertGuards  = state.behaviorOverrides.alertGuards;
                GiveDiscount = state.behaviorOverrides.giveDiscount;
                AvoidPlayer  = state.behaviorOverrides.avoidPlayer;
                SeekHelp     = state.behaviorOverrides.seekHelp;
            }

            // Track drift accumulators for TriggerPersonalityDriftAction
            TrustDeltaAccumulated     += state.trustScore - 50f;
            HostilityDeltaAccumulated += state.hostilityScore;
        }

        public void PushConversationTurn(string role, string content)
        {
            ConversationHistory.Add(new ConversationTurn { role = role, content = content });
            // Keep last 6 turns only (matches server-side context window budget)
            while (ConversationHistory.Count > 6)
                ConversationHistory.RemoveAt(0);
        }

        public void ClearConversation()
        {
            ConversationHistory.Clear();
            LastPlayerUtterance = null;
            LastNpcResponse     = null;
            LastDialogueResponse = null;
        }

        public void ResetDriftAccumulators()
        {
            TrustDeltaAccumulated     = 0f;
            HostilityDeltaAccumulated = 0f;
            SessionsSinceLastDrift    = 0;
        }
    }

    // Unity editor attribute — shows field as read-only in Inspector
    public sealed class ReadOnlyAttribute : PropertyAttribute { }
}
