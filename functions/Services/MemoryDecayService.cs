using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NpcSoulEngine.Functions.Models;
using System.Text.Json;

namespace NpcSoulEngine.Functions.Services;

public interface IMemoryDecayService
{
    Task ProcessDecayBatchAsync(IReadOnlyList<NpcMemoryDocument> documents, CancellationToken ct = default);
    Task ScheduleConsolidationsAsync(IReadOnlyList<NpcMemoryDocument> documents, CancellationToken ct = default);
}

public sealed class MemoryDecayService : IMemoryDecayService, IAsyncDisposable
{
    private readonly ICosmosMemoryStore _store;
    private readonly IEmotionalWeightCalculator _calc;
    private readonly ServiceBusSender _consolidationSender;
    private readonly ServiceBusClient _sbClient;
    private readonly ILogger<MemoryDecayService> _log;

    // Event eligible for consolidation when current weight < 5% of original
    internal const float ConsolidationThreshold = 0.05f;
    // Enqueue consolidation work when >= 20 eligible events accumulate
    internal const int ConsolidationBatchSize = 20;

    public MemoryDecayService(
        ICosmosMemoryStore store,
        IEmotionalWeightCalculator calc,
        ILogger<MemoryDecayService> log,
        Microsoft.Extensions.Configuration.IConfiguration cfg)
    {
        _store = store;
        _calc  = calc;
        _log   = log;
        var connStr = cfg["ServiceBusConnectionString"]
            ?? throw new InvalidOperationException("ServiceBusConnectionString not configured");
        _sbClient = new ServiceBusClient(connStr);
        _consolidationSender = _sbClient.CreateSender(cfg["ConsolidationQueueName"] ?? "memory-consolidation");
    }

    public async Task ProcessDecayBatchAsync(IReadOnlyList<NpcMemoryDocument> documents, CancellationToken ct = default)
    {
        foreach (var doc in documents)
        {
            _calc.RecalculateScores(doc);
            _calc.UpdateBehaviorOverrides(doc);
            await _store.UpsertMemoryAsync(doc, ct);
        }

        await ScheduleConsolidationsAsync(documents, ct);
        _log.LogInformation("Processed decay for {Count} documents", documents.Count);
    }

    public async Task ScheduleConsolidationsAsync(IReadOnlyList<NpcMemoryDocument> documents, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var requests = new List<ConsolidationRequest>();

        foreach (var doc in documents)
        {
            var eligible = GetEligibleEventIds(doc, now);
            if (eligible.Count >= ConsolidationBatchSize)
                requests.Add(new ConsolidationRequest
                {
                    NpcId    = doc.NpcId,
                    PlayerId = doc.PlayerId,
                    EventIds = eligible
                });
        }

        if (requests.Count == 0) return;

        using var batch = await _consolidationSender.CreateMessageBatchAsync(ct);
        foreach (var req in requests)
        {
            var msg = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(req))
            {
                ContentType = "application/json"
            };
            batch.TryAddMessage(msg);
        }
        await _consolidationSender.SendMessagesAsync(batch, ct);
        _log.LogInformation("Enqueued {Count} consolidation requests", requests.Count);
    }

    // Exposed internal for testing — pure, no side effects
    internal static IReadOnlyList<string> GetEligibleEventIds(NpcMemoryDocument doc, DateTimeOffset now) =>
        doc.SalientEvents
            .Where(e => !e.IsConsolidated)
            .Where(e => Math.Abs(e.CurrentWeight(now)) / (Math.Abs(e.EmotionalWeight) + 0.001f) < ConsolidationThreshold)
            .Select(e => e.EventId)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        await _consolidationSender.DisposeAsync();
        await _sbClient.DisposeAsync();
    }
}
