using System.Collections;
using System.Collections.Generic;
using NpcSoulEngine.Runtime.Components;
using NpcSoulEngine.Runtime.Data;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Components
{
    /// <summary>
    /// Reads SocialGraphAsset definitions and writes social edges into NPC memory
    /// documents at scene start. Only the server runs this in multiplayer.
    ///
    /// Attach one per scene that has a social graph. Runs after NpcSoulEngineManager
    /// initialises (DefaultExecutionOrder -50 vs Manager's -100).
    ///
    /// Idempotent: re-running only updates edge weights, never creates duplicates.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class SocialGraphInitializer : MonoBehaviour
    {
        [SerializeField] private List<SocialGraphAsset> graphAssets = new();

        [Tooltip("Wait for all NPCs to be registered before uploading (frames)")]
        [SerializeField] private int initDelayFrames = 2;

        private void Start() => StartCoroutine(InitializeAfterDelay());

        private IEnumerator InitializeAfterDelay()
        {
            for (int i = 0; i < initDelayFrames; i++)
                yield return null;

            foreach (var asset in graphAssets)
            {
                if (asset == null) continue;
                yield return UploadGraph(asset);
            }
        }

        private IEnumerator UploadGraph(SocialGraphAsset asset)
        {
            if (AzureMemoryService.Instance == null) yield break;

            var manager = NpcSoulEngineManager.Instance;
            if (manager == null) yield break;

            Debug.Log($"[SocialGraph] Uploading graph for zone {asset.zoneId} ({asset.edges.Count} edges)");

            // Group edges by source NPC — one read-modify-write per NPC
            var edgesByNpc = new Dictionary<string, List<SocialEdge>>();
            foreach (var edge in asset.edges)
            {
                if (!edgesByNpc.ContainsKey(edge.sourceNpcId))
                    edgesByNpc[edge.sourceNpcId] = new List<SocialEdge>();
                edgesByNpc[edge.sourceNpcId].Add(edge);

                if (edge.bidirectional)
                {
                    if (!edgesByNpc.ContainsKey(edge.targetNpcId))
                        edgesByNpc[edge.targetNpcId] = new List<SocialEdge>();
                    edgesByNpc[edge.targetNpcId].Add(new SocialEdge
                    {
                        sourceNpcId         = edge.targetNpcId,
                        targetNpcId         = edge.sourceNpcId,
                        relationshipStrength = edge.relationshipStrength,
                        trustInRelationship  = edge.trustInRelationship,
                        relationshipType    = edge.relationshipType
                    });
                }
            }

            foreach (var (npcId, edges) in edgesByNpc)
            {
                var task = AzureMemoryService.Instance.GetMemoryAsync(npcId, "_social_seed_", default);
                yield return new WaitUntil(() => task.IsCompleted);

                if (!task.IsCompletedSuccessfully) continue;

                // The social graph is seeded via a special process-event call
                // with ActionType.NeutralInteraction and context carrying the graph data
                var seedPayload = MemoryEventPayload.Create(
                    npcId, "_social_seed_",
                    ActionType.NeutralInteraction,
                    new ActionContext
                    {
                        summary    = $"Social graph seeded for zone {asset.zoneId}",
                        stakes     = "Low",
                        publicness = 0f
                    },
                    zone: asset.zoneId);

                // Attach edge data in witnessIds field (used by Azure Function to update socialGraphEdges)
                foreach (var edge in edges)
                    seedPayload.witnessIds.Add(SerializeEdge(edge));

                var seedTask = AzureMemoryService.Instance.ProcessEventAsync(seedPayload);
                yield return new WaitUntil(() => seedTask.IsCompleted);

                if (!seedTask.IsCompletedSuccessfully)
                    Debug.LogWarning($"[SocialGraph] Failed to seed edges for {npcId}");
            }

            Debug.Log($"[SocialGraph] Zone {asset.zoneId} upload complete");
        }

        private static string SerializeEdge(SocialEdge edge) =>
            $"EDGE:{edge.sourceNpcId}>{edge.targetNpcId}:{edge.relationshipStrength:F2}:{edge.trustInRelationship:F2}";
    }
}
