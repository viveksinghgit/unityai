using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NpcSoulEngine.Functions.Models;
using NpcSoulEngine.Functions.Services;
using System.Globalization;
using System.Text.Json;

namespace NpcSoulEngine.Functions.Functions;

/// <summary>
/// POST /api/memory/process-event
/// Processes a player-NPC interaction event, updates the NPC's emotional memory,
/// and returns the updated memory state. May trigger gossip if witnesses present.
/// </summary>
public sealed class MemoryProcessFunction
{
    private readonly ICosmosMemoryStore _store;
    private readonly IEmotionalWeightCalculator _calc;
    private readonly IGossipService _gossip;
    private readonly ILogger<MemoryProcessFunction> _log;

    public MemoryProcessFunction(
        ICosmosMemoryStore store,
        IEmotionalWeightCalculator calc,
        IGossipService gossip,
        ILogger<MemoryProcessFunction> log)
    {
        _store  = store;
        _calc   = calc;
        _gossip = gossip;
        _log    = log;
    }

    [Function("MemoryProcessEvent")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "memory/process-event")] HttpRequest req,
        CancellationToken ct)
    {
        MemoryEventPayload payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<MemoryEventPayload>(
                req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct) ?? throw new ArgumentException("Empty body");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invalid memory event payload");
            return new BadRequestObjectResult(new { error = "Invalid payload", detail = ex.Message });
        }

        _log.LogInformation("Processing memory event {ActionType} for NPC {NpcId} / Player {PlayerId}",
            payload.ActionType, payload.NpcId, payload.PlayerId);

        var doc = await _store.GetOrCreateMemoryAsync(payload.NpcId, payload.PlayerId, ct);

        // Build new salient event
        var weight   = _calc.ComputeWeight(payload);
        var decayRate = _calc.GetDecayRate(payload.ActionType, payload.Context.Stakes);

        var newEvent = new SalientEvent
        {
            ActionType     = payload.ActionType,
            Description    = payload.Context.Summary,
            EmotionalWeight = weight,
            DecayRate      = decayRate,
            Timestamp      = payload.Timestamp,
            WitnessIds     = payload.WitnessIds,
            Location       = payload.Location
        };

        doc.SalientEvents.Add(newEvent);

        // Cap at 50 active events (FIFO eviction of lowest-weight events beyond cap)
        if (doc.SalientEvents.Count(e => !e.IsConsolidated) > 50)
        {
            var toRemove = doc.SalientEvents
                .Where(e => !e.IsConsolidated)
                .OrderBy(e => Math.Abs(e.CurrentWeight(DateTimeOffset.UtcNow)))
                .First();
            doc.SalientEvents.Remove(toRemove);
        }

        // Seed social graph edges from EDGE: tokens embedded in witnessIds
        foreach (var edge in ParseEdgesFromWitnessIds(payload.NpcId, payload.WitnessIds))
            MergeEdge(doc, edge);

        _calc.RecalculateScores(doc);
        _calc.UpdateBehaviorOverrides(doc);
        doc.SessionCount++;

        await _store.UpsertMemoryAsync(doc, ct);

        // Broadcast gossip if this event had witnesses
        if (payload.WitnessIds.Count > 0 && doc.SocialGraphEdges.Count > 0)
        {
            _ = _gossip.BroadcastAsync(new GossipBroadcastRequest
            {
                SourceNpcId     = payload.NpcId,
                PlayerId        = payload.PlayerId,
                OriginalEventId = newEvent.EventId,
                Zone            = payload.Zone,
                SocialGraphEdges = doc.SocialGraphEdges,
                OriginalEvent   = payload,
                EmotionalColoring = weight,
                GossipTtlHops   = 3,
                HopsFromOrigin  = 0
            }, ct);
        }

        return new OkObjectResult(NpcMemoryState.FromDocument(doc));
    }

    // Format: EDGE:{src}>{tgt}:{strength}:{trust}
    internal static IReadOnlyList<SocialGraphEdge> ParseEdgesFromWitnessIds(
        string sourceNpcId, IReadOnlyList<string> witnessIds)
    {
        var edges = new List<SocialGraphEdge>();
        foreach (var w in witnessIds)
        {
            if (!w.StartsWith("EDGE:", StringComparison.Ordinal)) continue;
            var parts = w[5..].Split(new[] { '>', ':' });
            if (parts.Length != 4) continue;
            if (parts[0] != sourceNpcId) continue;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var strength)) continue;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var trust)) continue;
            edges.Add(new SocialGraphEdge
            {
                TargetNpcId          = parts[1],
                RelationshipStrength = Math.Clamp(strength, 0f, 1f),
                TrustInRelationship  = Math.Clamp(trust, 0f, 1f)
            });
        }
        return edges;
    }

    internal static void MergeEdge(NpcMemoryDocument doc, SocialGraphEdge edge)
    {
        var idx = doc.SocialGraphEdges.FindIndex(e => e.TargetNpcId == edge.TargetNpcId);
        if (idx >= 0)
            doc.SocialGraphEdges[idx] = edge;
        else
            doc.SocialGraphEdges.Add(edge);
    }
}

/// <summary>
/// GET /api/memory/{npcId}/{playerId}
/// Fast Cosmos point-read — no GPT-4o call. Target: &lt;15ms.
/// </summary>
public sealed class MemoryReadFunction
{
    private readonly ICosmosMemoryStore _store;
    private readonly ILogger<MemoryReadFunction> _log;

    public MemoryReadFunction(ICosmosMemoryStore store, ILogger<MemoryReadFunction> log)
    {
        _store = store;
        _log   = log;
    }

    [Function("MemoryRead")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "memory/{npcId}/{playerId}")] HttpRequest req,
        string npcId,
        string playerId,
        CancellationToken ct)
    {
        var doc = await _store.GetMemoryAsync(npcId, playerId, ct);
        if (doc is null)
        {
            // Return neutral state for an NPC that has never met this player
            return new OkObjectResult(new NpcMemoryState
            {
                NpcId           = npcId,
                PlayerId        = playerId,
                TrustScore      = 50f,
                CurrentEmotion  = EmotionVector.Neutral,
                BehaviorOverrides = new BehaviorOverrides(),
                PlayerArchetype = "unknown"
            });
        }

        return new OkObjectResult(NpcMemoryState.FromDocument(doc));
    }
}
