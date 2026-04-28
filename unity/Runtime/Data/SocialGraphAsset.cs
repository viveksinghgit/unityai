using System;
using System.Collections.Generic;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Data
{
    /// <summary>
    /// ScriptableObject for designing NPC social relationships in the Unity editor.
    /// One asset per location zone (e.g. Thornwick_SocialGraph, Castle_SocialGraph).
    ///
    /// At scene start, SocialGraphInitializer reads these assets and writes the
    /// edge data into each NPC's Cosmos DB memory document.
    ///
    /// Create: Assets > Create > NPC Soul Engine > Social Graph
    /// </summary>
    [CreateAssetMenu(menuName = "NPC Soul Engine/Social Graph", fileName = "NewSocialGraph")]
    public sealed class SocialGraphAsset : ScriptableObject
    {
        [Tooltip("Zone identifier matching the zone field on MemoryEventPayload (e.g. zone_market)")]
        public string zoneId = "zone_default";

        [Tooltip("Human-readable zone name for editor clarity")]
        public string zoneName;

        public List<NpcNode> nodes = new();
        public List<SocialEdge> edges = new();

        public List<SocialEdge> GetEdgesForNpc(string npcId)
        {
            var result = new List<SocialEdge>();
            foreach (var edge in edges)
            {
                if (edge.sourceNpcId == npcId || (edge.bidirectional && edge.targetNpcId == npcId))
                    result.Add(edge);
            }
            return result;
        }
    }

    [Serializable]
    public sealed class NpcNode
    {
        [Tooltip("Must match the npcId on the NpcSoulComponent")]
        public string npcId;

        [Tooltip("Human-readable label for the graph editor")]
        public string displayName;

        [Tooltip("Position in the editor canvas (set by SocialGraphEditorWindow)")]
        public Vector2 editorPosition;

        [Tooltip("NPC type influences gossip behaviour")]
        public NpcRole role = NpcRole.Civilian;
    }

    [Serializable]
    public sealed class SocialEdge
    {
        public string sourceNpcId;
        public string targetNpcId;

        [Range(0f, 1f)]
        [Tooltip("How strongly source influences target's gossip reception")]
        public float relationshipStrength = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("How much target trusts what source says")]
        public float trustInRelationship = 0.5f;

        [Tooltip("Whether gossip also flows from target back to source")]
        public bool bidirectional = true;

        [Tooltip("Type of social bond — affects gossip content framing")]
        public RelationshipType relationshipType = RelationshipType.Acquaintance;
    }

    public enum NpcRole { Civilian, Merchant, Guard, Noble, Innkeeper, Criminal, Clergy }

    public enum RelationshipType
    {
        Acquaintance,
        Friend,
        Rival,
        Family,
        Colleague,
        Employer,
        Employee,
        Enemy
    }
}
