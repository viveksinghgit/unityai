using Azure.AI.OpenAI;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NpcSoulEngine.Functions.Models;
using NpcSoulEngine.Functions.Services;
using OpenAI.Chat;
using System.Text.Json;
using System.Collections.Generic;

namespace NpcSoulEngine.Functions.Functions;

/// <summary>
/// Timer-triggered every 6 hours.
/// Applies Ebbinghaus decay to all active salient events and flags
/// consolidation-eligible batches. Loops until all stale docs are processed
/// or the per-run cap is hit, relying on UpsertMemory updating
/// lastEncounterTimestamp to naturally advance the query window.
/// </summary>
public sealed class MemoryDecayJob
{
    private readonly ICosmosMemoryStore _store;
    private readonly IMemoryDecayService _decay;
    private readonly ILogger<MemoryDecayJob> _log;

    private const int BatchSize      = 100;
    private const int MaxDocsPerRun  = 1_000;

    public MemoryDecayJob(ICosmosMemoryStore store, IMemoryDecayService decay, ILogger<MemoryDecayJob> log)
    {
        _store = store;
        _decay = decay;
        _log   = log;
    }

    [Function("MemoryDecayJob")]
    public async Task RunAsync(
        [TimerTrigger("0 0 */6 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        _log.LogInformation("Memory decay job starting");
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);

        var processedTotal = 0;
        while (processedTotal < MaxDocsPerRun)
        {
            var documents = await _store.GetMemoriesForDecayAsync(cutoff, BatchSize, ct);
            if (documents.Count == 0) break;

            await _decay.ProcessDecayBatchAsync(documents, ct);
            processedTotal += documents.Count;

            if (documents.Count < BatchSize) break;  // last page
        }

        if (processedTotal == 0)
            _log.LogInformation("No documents require decay processing");
        else
            _log.LogInformation("Decay job completed, processed {Total} documents", processedTotal);
    }
}

/// <summary>
/// Cosmos DB change feed — fires on every write to npc-memory-graphs.
/// Checks whether any changed document has enough faded events to warrant
/// consolidation and enqueues the work immediately, without waiting for the
/// next 6-hour decay timer run.
/// </summary>
public sealed class MemoryChangeFeedFunction
{
    private readonly IMemoryDecayService _decay;
    private readonly ILogger<MemoryChangeFeedFunction> _log;

    public MemoryChangeFeedFunction(IMemoryDecayService decay, ILogger<MemoryChangeFeedFunction> log)
    {
        _decay = decay;
        _log   = log;
    }

    [Function("MemoryChangeFeed")]
    public async Task RunAsync(
        [CosmosDBTrigger(
            databaseName: "%CosmosDatabaseName%",
            containerName: "npc-memory-graphs",
            LeaseContainerName = "npc-memory-leases",
            CreateLeaseContainerIfNotExists = true,
            Connection = "CosmosConnectionString")]
        IReadOnlyList<NpcMemoryDocument> documents,
        CancellationToken ct)
    {
        if (documents.Count == 0) return;
        _log.LogDebug("Change feed: {Count} document(s) changed, checking consolidation eligibility", documents.Count);
        await _decay.ScheduleConsolidationsAsync(documents, ct);
    }
}

/// <summary>
/// Service Bus triggered — consolidates old salient events into the memory summary blob
/// using GPT-4o. Runs at most once per 24h per NPC-player pair.
/// </summary>
public sealed class MemoryConsolidationJob
{
    private readonly ICosmosMemoryStore _store;
    private readonly AzureOpenAIClient _openAI;
    private readonly FunctionConfig _cfg;
    private readonly ILogger<MemoryConsolidationJob> _log;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public MemoryConsolidationJob(
        ICosmosMemoryStore store,
        AzureOpenAIClient openAI,
        IOptions<FunctionConfig> cfg,
        ILogger<MemoryConsolidationJob> log)
    {
        _store  = store;
        _openAI = openAI;
        _cfg    = cfg.Value;
        _log    = log;
    }

    [Function("MemoryConsolidationJob")]
    public async Task RunAsync(
        [ServiceBusTrigger("%ConsolidationQueueName%", Connection = "ServiceBusConnectionString")] string messageBody,
        CancellationToken ct)
    {
        ConsolidationRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<ConsolidationRequest>(messageBody, JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to parse consolidation request");
            return;
        }

        if (req is null) return;

        var doc = await _store.GetMemoryAsync(req.NpcId, req.PlayerId, ct);
        if (doc is null) return;

        var eventsToConsolidate = doc.SalientEvents
            .Where(e => req.EventIds.Contains(e.EventId) && !e.IsConsolidated)
            .OrderBy(e => e.Timestamp)
            .ToList();

        if (eventsToConsolidate.Count == 0) return;

        var summary = await CompressSalientEventsAsync(doc.NpcId, eventsToConsolidate, doc.MemorySummaryBlob, ct);

        var consolidatedIds = eventsToConsolidate.Select(e => e.EventId).ToHashSet();
        doc.SalientEvents = doc.SalientEvents
            .Select(e => consolidatedIds.Contains(e.EventId) ? e with { IsConsolidated = true } : e)
            .ToList();

        doc.MemorySummaryBlob = string.IsNullOrWhiteSpace(doc.MemorySummaryBlob)
            ? summary
            : $"{doc.MemorySummaryBlob} {summary}";

        await _store.UpsertMemoryAsync(doc, ct);
        _log.LogInformation("Consolidated {Count} events for {NpcId}/{PlayerId}", eventsToConsolidate.Count, req.NpcId, req.PlayerId);
    }

    private async Task<string> CompressSalientEventsAsync(
        string npcId,
        List<SalientEvent> events,
        string existingSummary,
        CancellationToken ct)
    {
        var eventList = string.Join("\n", events.Select(e =>
            $"- [{e.ActionType}] {e.Description} (weight: {e.EmotionalWeight:+0.0;-0.0}, {e.Timestamp:yyyy-MM-dd})"));

        var prompt = $"""
            You are summarizing {npcId}'s memories of a player for long-term storage.
            Compress the following {events.Count} historical interactions into at most 150 tokens.
            Preserve: emotional tone, major betrayals/kindnesses, and any recurring patterns.
            Do not list events individually — synthesize them into a narrative summary.

            Interactions to compress:
            {eventList}

            Existing summary to incorporate (may be empty):
            {existingSummary}

            Return only the compressed summary text. No preamble.
            """;

        try
        {
            var chatClient = _openAI.GetChatClient(_cfg.Gpt4oMiniDeploymentName);
            var result = await chatClient.CompleteChatAsync(
                new[] { ChatMessage.CreateUserMessage(prompt) },
                new ChatCompletionOptions { MaxOutputTokenCount = 200, Temperature = 0.3f },
                ct);
            return result.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GPT consolidation failed for {NpcId}", npcId);
            return string.Join("; ", events.Take(5).Select(e => e.Description));
        }
    }
}

/// <summary>
/// Timer-triggered daily at 02:00 UTC.
/// Queries all players active in the last 30 days, builds a feature vector from
/// their cross-NPC memory documents, and calls the Azure ML archetype endpoint
/// (or rule-based fallback) to update PlayerArchetypeDocument and each
/// NpcMemoryDocument.playerArchetype field.
/// </summary>
public sealed class ArchetypeReclassificationJob
{
    private const int MaxPlayersPerRun     = 500;
    private const int ActiveWindowDays     = 30;

    private readonly ICosmosMemoryStore _store;
    private readonly IArchetypeClassifierService _classifier;
    private readonly ILogger<ArchetypeReclassificationJob> _log;

    public ArchetypeReclassificationJob(
        ICosmosMemoryStore store,
        IArchetypeClassifierService classifier,
        ILogger<ArchetypeReclassificationJob> log)
    {
        _store      = store;
        _classifier = classifier;
        _log        = log;
    }

    [Function("ArchetypeReclassificationJob")]
    public async Task RunAsync(
        [TimerTrigger("0 0 2 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        _log.LogInformation("Archetype reclassification job starting");

        var activeSince = DateTimeOffset.UtcNow.AddDays(-ActiveWindowDays);
        var playerIds   = await _store.GetActivePlayerIdsAsync(activeSince, MaxPlayersPerRun, ct);

        if (playerIds.Count == 0)
        {
            _log.LogInformation("No active players found in the last {Days} days", ActiveWindowDays);
            return;
        }

        var updated = 0;
        foreach (var playerId in playerIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ReclassifyPlayerAsync(playerId, ct);
                updated++;
            }
            catch (Exception ex)
            {
                // Isolated failure — log and continue so one bad player doesn't abort the run.
                _log.LogError(ex, "Reclassification failed for player {PlayerId}", playerId);
            }
        }

        _log.LogInformation(
            "Archetype reclassification completed: {Updated}/{Total} players updated",
            updated, playerIds.Count);
    }

    private async Task ReclassifyPlayerAsync(string playerId, CancellationToken ct)
    {
        var npcDocs  = await _store.GetMemoriesByPlayerAsync(playerId, ct);
        var existing = await _store.GetArchetypeAsync(playerId, ct);
        var features = PlayerFeatureVector.FromDocuments(npcDocs, existing?.BehaviorFeatures);
        var result   = await _classifier.ClassifyAsync(features, ct);

        var doc = existing ?? new PlayerArchetypeDocument
        {
            Id       = $"archetype_{playerId}",
            PlayerId = playerId
        };

        doc.PrimaryArchetype      = result.Archetype;
        doc.ArchetypeScores       = new Dictionary<string, float>(result.Scores);
        doc.LastClassifiedAt      = DateTimeOffset.UtcNow;
        doc.ClassificationVersion = "v1.0";
        // BehaviorFeatures are written by other services (ActionEventBus); preserve them here.
        doc.BehaviorFeatures = existing?.BehaviorFeatures ?? new BehaviorFeatures();

        await _store.UpsertArchetypeAsync(doc, ct);
        await _store.BulkUpdatePlayerArchetypeAsync(playerId, result.Archetype, result.Confidence, ct);

        _log.LogDebug(
            "Player {PlayerId} → {Archetype} (confidence {Confidence:P0})",
            playerId, result.Archetype, result.Confidence);
    }
}
