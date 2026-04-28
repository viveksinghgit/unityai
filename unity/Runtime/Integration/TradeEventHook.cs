using System.Collections.Generic;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;
using UnityEngine.Events;

namespace NpcSoulEngine.Runtime.Integration
{
    /// <summary>
    /// Attach to a trading NPC or the trade UI controller.
    /// Wire your game's trade completion events to the UnityEvents exposed here,
    /// or call the static methods directly from your TradeSystem class.
    ///
    /// Example (from your TradeSystem.cs):
    ///   TradeEventHook.NotifyTradeCompleted("npc_merchant_elara", itemName,
    ///       tradeValue, wasDeceptive, wasPublic, zone);
    /// </summary>
    public sealed class TradeEventHook : MonoBehaviour
    {
        [Header("NPC this hook is attached to")]
        [SerializeField] private string npcId;

        [Header("Zone")]
        [SerializeField] private string zone = "zone_market";

        [Header("Witness NPCs (set at design time)")]
        [SerializeField] private List<string> defaultWitnessNpcIds = new();

        [Header("Unity Events (wire from Inspector)")]
        [Tooltip("Call with (itemName, goldValue, wasDeceptive, wasPublic)")]
        public UnityEvent<string, float, bool, bool> onTradeCompleted;

        private void Awake()
        {
            // Allow Inspector-wired events to call through to the static path
            onTradeCompleted.AddListener((item, value, deceptive, isPublic) =>
                NotifyTradeCompleted(npcId, item, value, deceptive,
                    isPublic ? defaultWitnessNpcIds : null, zone));
        }

        // ─── Static API — call from any game system ────────────────────────────

        public static void NotifyTradeCompleted(
            string npcId,
            string itemName,
            float goldValue,
            bool wasDeceptive,
            List<string> witnessIds = null,
            string zone = "zone_market")
        {
            SoulEventBus.RecordTrade(npcId, wasDeceptive, goldValue, itemName,
                witnessIds?.Count > 0, zone);

            if (witnessIds != null && witnessIds.Count > 0 && wasDeceptive)
            {
                // Secondary events: witnesses saw the deception
                foreach (var witnessId in witnessIds)
                {
                    SoulEventBus.Record(witnessId, ActionType.WitnessedViolence,
                        new ActionContext
                        {
                            summary    = $"Witnessed player deceive {npcId} with {itemName}",
                            stakes     = "Low",
                            publicness = 1.0f
                        }, zone: zone);
                }
            }
        }

        public static void NotifyTradeRefused(string npcId, string zone = "zone_market")
        {
            // NPC refusing to trade is a signal of distrust — no Azure call needed here,
            // but log it for analytics
            Debug.Log($"[TradeEventHook] Trade refused by {npcId} — trust likely below threshold");
        }
    }
}
