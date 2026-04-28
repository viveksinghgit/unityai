using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NpcSoulEngine.Functions.Models;

namespace NpcSoulEngine.Functions.Services;

public interface ICosmosMemoryStore
{
    Task<NpcMemoryDocument?> GetMemoryAsync(string npcId, string playerId, CancellationToken ct = default);
    Task<NpcMemoryDocument> UpsertMemoryAsync(NpcMemoryDocument document, CancellationToken ct = default);
    Task<IReadOnlyList<NpcMemoryDocument>> GetMemoriesForDecayAsync(DateTimeOffset olderThan, int maxItems, CancellationToken ct = default);
    Task<PlayerArchetypeDocument?> GetArchetypeAsync(string playerId, CancellationToken ct = default);
    Task UpsertArchetypeAsync(PlayerArchetypeDocument document, CancellationToken ct = default);
    Task<NpcMemoryDocument> GetOrCreateMemoryAsync(string npcId, string playerId, CancellationToken ct = default);

    // ─── Archetype reclassification support ──────────────────────────────────
    Task<IReadOnlyList<string>> GetActivePlayerIdsAsync(DateTimeOffset activeSince, int maxPlayers, CancellationToken ct = default);
    Task<IReadOnlyList<NpcMemoryDocument>> GetMemoriesByPlayerAsync(string playerId, CancellationToken ct = default);
    Task BulkUpdatePlayerArchetypeAsync(string playerId, string archetype, float confidence, CancellationToken ct = default);
}

public sealed class CosmosMemoryStore : ICosmosMemoryStore
{
    private readonly Container _memoryContainer;
    private readonly Container _archetypeContainer;
    private readonly ILogger<CosmosMemoryStore> _log;

    public CosmosMemoryStore(CosmosClient cosmos, IOptions<FunctionConfig> opts, ILogger<CosmosMemoryStore> log)
    {
        var db = cosmos.GetDatabase(opts.Value.CosmosDatabaseName);
        _memoryContainer = db.GetContainer("npc-memory-graphs");
        _archetypeContainer = db.GetContainer("player-archetypes");
        _log = log;
    }

    public async Task<NpcMemoryDocument?> GetMemoryAsync(string npcId, string playerId, CancellationToken ct = default)
    {
        var id = NpcMemoryDocument.MakeId(npcId, playerId);
        try
        {
            var response = await _memoryContainer.ReadItemAsync<NpcMemoryDocument>(
                id,
                new PartitionKey(npcId),   // hierarchical pk: npcId first
                cancellationToken: ct);

            var doc = response.Resource;
            doc.ETag = response.ETag;
            return doc;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<NpcMemoryDocument> GetOrCreateMemoryAsync(string npcId, string playerId, CancellationToken ct = default)
    {
        return await GetMemoryAsync(npcId, playerId, ct) ?? new NpcMemoryDocument
        {
            Id = NpcMemoryDocument.MakeId(npcId, playerId),
            NpcId = npcId,
            PlayerId = playerId
        };
    }

    public async Task<NpcMemoryDocument> UpsertMemoryAsync(NpcMemoryDocument document, CancellationToken ct = default)
    {
        document.LastEncounterTimestamp = DateTimeOffset.UtcNow;

        var options = document.ETag is not null
            ? new ItemRequestOptions { IfMatchEtag = document.ETag }
            : null;

        try
        {
            var response = await _memoryContainer.UpsertItemAsync(
                document,
                new PartitionKey(document.NpcId),
                options,
                ct);

            document.ETag = response.ETag;
            return document;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            // ETag conflict — re-read and merge
            _log.LogWarning("ETag conflict for {NpcId}/{PlayerId}, re-reading and retrying", document.NpcId, document.PlayerId);
            var fresh = await GetMemoryAsync(document.NpcId, document.PlayerId, ct);
            if (fresh is null) throw;

            // Merge new salient events into the fresh document
            fresh.SalientEvents.AddRange(document.SalientEvents.Where(
                e => !fresh.SalientEvents.Any(existing => existing.EventId == e.EventId)));
            fresh.TrustScore = document.TrustScore;
            fresh.CurrentEmotion = document.CurrentEmotion;
            fresh.BehaviorOverrides = document.BehaviorOverrides;

            var retryResponse = await _memoryContainer.UpsertItemAsync(fresh, new PartitionKey(fresh.NpcId), cancellationToken: ct);
            fresh.ETag = retryResponse.ETag;
            return fresh;
        }
    }

    public async Task<IReadOnlyList<NpcMemoryDocument>> GetMemoriesForDecayAsync(
        DateTimeOffset olderThan,
        int maxItems,
        CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.lastEncounterTimestamp < @cutoff AND ARRAY_LENGTH(c.salientEvents) > 0 OFFSET 0 LIMIT @max")
            .WithParameter("@cutoff", olderThan.ToString("O"))
            .WithParameter("@max", maxItems);

        var results = new List<NpcMemoryDocument>();
        using var feed = _memoryContainer.GetItemQueryIterator<NpcMemoryDocument>(query);
        while (feed.HasMoreResults && results.Count < maxItems)
        {
            var page = await feed.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task<PlayerArchetypeDocument?> GetArchetypeAsync(string playerId, CancellationToken ct = default)
    {
        try
        {
            var response = await _archetypeContainer.ReadItemAsync<PlayerArchetypeDocument>(
                $"archetype_{playerId}",
                new PartitionKey(playerId),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpsertArchetypeAsync(PlayerArchetypeDocument document, CancellationToken ct = default)
    {
        await _archetypeContainer.UpsertItemAsync(document, new PartitionKey(document.PlayerId), cancellationToken: ct);
    }

    public async Task<IReadOnlyList<string>> GetActivePlayerIdsAsync(
        DateTimeOffset activeSince,
        int maxPlayers,
        CancellationToken ct = default)
    {
        // Cross-partition distinct scan — acceptable for a daily batch job.
        var query = new QueryDefinition(
            "SELECT DISTINCT VALUE c.playerId FROM c WHERE c.lastEncounterTimestamp >= @since OFFSET 0 LIMIT @max")
            .WithParameter("@since", activeSince.ToString("O"))
            .WithParameter("@max", maxPlayers);

        var results = new List<string>();
        using var feed = _memoryContainer.GetItemQueryIterator<string>(
            query, requestOptions: new QueryRequestOptions { MaxConcurrency = -1 });

        while (feed.HasMoreResults && results.Count < maxPlayers)
        {
            var page = await feed.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task<IReadOnlyList<NpcMemoryDocument>> GetMemoriesByPlayerAsync(
        string playerId,
        CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.playerId = @playerId")
            .WithParameter("@playerId", playerId);

        var results = new List<NpcMemoryDocument>();
        using var feed = _memoryContainer.GetItemQueryIterator<NpcMemoryDocument>(
            query, requestOptions: new QueryRequestOptions { MaxConcurrency = -1 });

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }

    public async Task BulkUpdatePlayerArchetypeAsync(
        string playerId,
        string archetype,
        float confidence,
        CancellationToken ct = default)
    {
        var docs = await GetMemoriesByPlayerAsync(playerId, ct);
        foreach (var doc in docs)
        {
            doc.PlayerArchetype     = archetype;
            doc.ArchetypeConfidence = confidence;
            doc.ETag = null;  // unconditional upsert — background job, no OCC needed
            await UpsertMemoryAsync(doc, ct);
        }
    }
}
