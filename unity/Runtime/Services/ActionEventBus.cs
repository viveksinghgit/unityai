using System;
using System.Collections.Generic;
using NpcSoulEngine.Runtime.Components;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Services
{
    /// <summary>
    /// Static event bus. All game systems (trade, combat, dialogue, quest) fire
    /// actions here instead of coupling directly to PlayerActionMonitor.
    /// Zero dependencies on Unity scene hierarchy from calling code.
    ///
    /// Usage:
    ///   SoulEventBus.Record("npc_merchant_elara", ActionType.TradeDeceptive,
    ///       new ActionContext { stakes = "Medium", publicness = 0.9f,
    ///           summary = "Player sold forged map knowing it was worthless" },
    ///       location: "Thornwick Market", zone: "zone_market");
    /// </summary>
    public static class SoulEventBus
    {
        // Fired on the Unity main thread after Azure confirms the memory update.
        public static event Action<string, NpcMemoryState> OnNpcMemoryUpdated;

        // Fired locally (immediately, before Azure round-trip) for optimistic UI.
        public static event Action<MemoryEventPayload> OnActionRecorded;

        // ─── Core recording methods ───────────────────────────────────────────

        public static void Record(
            string npcId,
            ActionType actionType,
            ActionContext context,
            List<string> witnessNpcIds = null,
            string location = "",
            string zone = "zone_default")
        {
            var monitor = PlayerActionMonitor.Instance;
            if (monitor == null)
            {
                Debug.LogWarning("[SoulEventBus] PlayerActionMonitor not in scene — action dropped");
                return;
            }

            // Build and fire local notification before the Azure round-trip
            var payload = MemoryEventPayload.Create(
                npcId, NpcSoulEngineManager.Instance?.PlayerId ?? "unknown",
                actionType, context, location, zone);
            if (witnessNpcIds != null) payload.witnessIds.AddRange(witnessNpcIds);
            OnActionRecorded?.Invoke(payload);

            monitor.Record(npcId, actionType, context, witnessNpcIds, location, zone);
        }

        // ─── Convenience overloads ────────────────────────────────────────────

        public static void RecordTrade(
            string npcId, bool wasDeceptive, float tradeValue,
            string itemName, bool playerWasWitnesed, string zone = "zone_market")
        {
            var actionType = wasDeceptive ? ActionType.TradeDeceptive : ActionType.TradeFair;
            var stakes = tradeValue > 500f ? "High" : tradeValue > 100f ? "Medium" : "Low";
            Record(npcId, actionType, new ActionContext
            {
                summary     = wasDeceptive
                    ? $"Player traded deceptively ({itemName}, value {tradeValue:F0}g)"
                    : $"Player completed fair trade ({itemName})",
                stakes      = stakes,
                publicness  = playerWasWitnesed ? 0.8f : 0.1f,
                itemsInvolved = new List<string> { itemName }
            }, zone: zone);
        }

        public static void RecordCombat(
            string npcId, bool playerInitiated, bool npcDied,
            List<string> witnessIds = null, string location = "", string zone = "zone_default")
        {
            var actionType = playerInitiated ? ActionType.CombatInitiated : ActionType.CombatDefended;
            var stakes = npcDied ? "LifeOrDeath" : "High";
            Record(npcId, actionType, new ActionContext
            {
                summary    = playerInitiated
                    ? $"Player attacked {npcId}{(npcDied ? " (lethal)" : "")}"
                    : $"Player defended against {npcId}",
                stakes     = stakes,
                publicness = witnessIds?.Count > 0 ? 0.9f : 0.2f
            }, witnessIds, location, zone);
        }

        public static void RecordPromise(
            string npcId, bool kept, string promiseDescription,
            string zone = "zone_default")
        {
            Record(npcId, kept ? ActionType.PromiseKept : ActionType.PromiseBroken,
                new ActionContext
                {
                    summary   = kept
                        ? $"Player kept promise: {promiseDescription}"
                        : $"Player broke promise: {promiseDescription}",
                    stakes    = "Medium",
                    publicness = 0.3f
                }, zone: zone);
        }

        public static void RecordRescue(
            string npcId, string situationDescription, List<string> witnesses = null,
            string location = "", string zone = "zone_default")
        {
            Record(npcId, ActionType.Rescued, new ActionContext
            {
                summary   = $"Player rescued {npcId}: {situationDescription}",
                stakes    = "LifeOrDeath",
                publicness = witnesses?.Count > 0 ? 0.85f : 0.4f
            }, witnesses, location, zone);
        }

        // ─── Internal callback wired from PlayerActionMonitor ─────────────────

        internal static void RaiseMemoryUpdated(string npcId, NpcMemoryState state)
            => OnNpcMemoryUpdated?.Invoke(npcId, state);
    }
}
