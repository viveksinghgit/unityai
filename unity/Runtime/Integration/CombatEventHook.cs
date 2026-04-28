using System.Collections.Generic;
using NpcSoulEngine.Runtime.Components;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Integration
{
    /// <summary>
    /// Attach to any NPC that can be involved in combat with the player.
    /// Wire Unity events or call static methods from your combat system.
    ///
    /// Example (from CombatSystem.cs):
    ///   CombatEventHook.NotifyCombatStarted(npc.Id, npc.transform.position,
    ///       playerInitiated: true, witnesses: nearbyNpcIds, zone: currentZone);
    /// </summary>
    public sealed class CombatEventHook : MonoBehaviour
    {
        [SerializeField] private string npcId;
        [SerializeField] private string zone = "zone_default";
        [SerializeField] private float witnessRadius = 15f;

        private void Awake()
        {
            // Wire TriggerAlertGuardsAction's event to record a secondary fear event
            // on each guard that responds
            AvoidPlayerAction.TriggerAlertGuardsAction.OnGuardAlerted += OnGuardAlerted;
        }

        private void OnDestroy()
        {
            AvoidPlayerAction.TriggerAlertGuardsAction.OnGuardAlerted -= OnGuardAlerted;
        }

        private void OnGuardAlerted(Transform alertSource, float radius)
        {
            // Any NPC within the alert radius sees this as a threat to public safety
            if (Vector3.Distance(transform.position, alertSource.position) <= radius)
            {
                SoulEventBus.Record(npcId, ActionType.WitnessedViolence,
                    new ActionContext
                    {
                        summary    = $"Guards alerted near {npcId}'s location",
                        stakes     = "Medium",
                        publicness = 0.9f
                    }, zone: zone);
            }
        }

        // ─── Static API ────────────────────────────────────────────────────────

        public static void NotifyCombatStarted(
            string npcId,
            Vector3 location,
            bool playerInitiated,
            List<string> witnessIds = null,
            string zone = "zone_default")
        {
            SoulEventBus.RecordCombat(npcId, playerInitiated, npcDied: false,
                witnessIds, location.ToString(), zone);
        }

        public static void NotifyNpcKilled(
            string npcId,
            Vector3 location,
            List<string> witnessIds = null,
            string zone = "zone_default")
        {
            // Death is a LifeOrDeath-stakes event — bypasses buffer, fires immediately
            SoulEventBus.Record(npcId, ActionType.Endangered,
                new ActionContext
                {
                    summary    = $"Player killed {npcId}",
                    stakes     = "LifeOrDeath",
                    publicness = witnessIds?.Count > 0 ? 1.0f : 0.3f
                }, witnessIds, location.ToString(), zone);

            // Record fear on all witnesses
            if (witnessIds != null)
            {
                foreach (var witnessId in witnessIds)
                {
                    SoulEventBus.Record(witnessId, ActionType.Endangered,
                        new ActionContext
                        {
                            summary    = $"Witnessed player kill {npcId}",
                            stakes     = "High",
                            publicness = 1.0f
                        }, zone: zone);
                }
            }
        }

        public static void NotifyPlayerDefended(
            string npcId,
            string zone = "zone_default")
        {
            SoulEventBus.RecordCombat(npcId, playerInitiated: false, npcDied: false,
                witnessIds: null, zone: zone);
        }

        /// <summary>
        /// Collects witness NPC IDs within radius using a Physics.OverlapSphere.
        /// Call from your combat system just before NotifyCombatStarted.
        /// </summary>
        public static List<string> CollectNearbyNpcWitnesses(Vector3 position, float radius,
            string excludeNpcId = null)
        {
            var witnesses = new List<string>();
            var hits = Physics.OverlapSphere(position, radius);
            foreach (var hit in hits)
            {
                var soul = hit.GetComponentInParent<NpcSoulComponent>();
                if (soul == null || soul.NpcId == excludeNpcId) continue;
                witnesses.Add(soul.NpcId);
            }
            return witnesses;
        }
    }
}
