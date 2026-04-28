using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NpcSoulEngine.Functions.Models;
using System.Text.Json;

namespace NpcSoulEngine.Functions.Services;

public interface IGossipService
{
    Task BroadcastAsync(GossipBroadcastRequest request, CancellationToken ct = default);
}

public sealed class GossipService : IGossipService, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;
    private readonly ServiceBusClient _client;
    private readonly FunctionConfig _cfg;
    private readonly ILogger<GossipService> _log;

    // Gossip emotional weight degrades per hop — 60% of original per hop
    private const float GossipDecayFactor = 0.6f;

    // Cap NPCs affected per gossip broadcast in multiplayer (cost control)
    private const int MaxTargetsPerBroadcast = 10;

    public GossipService(IOptions<FunctionConfig> opts, ILogger<GossipService> log,
        Microsoft.Extensions.Configuration.IConfiguration cfg)
    {
        _cfg = opts.Value;
        _log = log;
        var connStr = cfg["ServiceBusConnectionString"]
            ?? throw new InvalidOperationException("ServiceBusConnectionString not configured");
        _client = new ServiceBusClient(connStr);
        _sender = _client.CreateSender(cfg["GossipTopicName"] ?? "npc-gossip");
    }

    public async Task BroadcastAsync(GossipBroadcastRequest request, CancellationToken ct = default)
    {
        if (request.GossipTtlHops <= 0)
        {
            _log.LogDebug("Gossip TTL exhausted for event {EventId}", request.OriginalEventId);
            return;
        }

        var targets = request.SocialGraphEdges.Take(MaxTargetsPerBroadcast).ToList();
        if (targets.Count == 0) return;

        // Emotional weight degrades with hops — e.g., hop 2 = 60% of original
        var degradedColoring = request.EmotionalColoring
            * MathF.Pow(GossipDecayFactor, request.HopsFromOrigin);

        var messages = targets.Select(edge =>
        {
            var payload = new GossipBroadcastRequest
            {
                SourceNpcId     = request.SourceNpcId,
                PlayerId        = request.PlayerId,
                OriginalEventId = request.OriginalEventId,
                Zone            = request.Zone,
                SocialGraphEdges = Array.Empty<SocialGraphEdge>(),  // second-order edges loaded by target NPC
                OriginalEvent   = request.OriginalEvent,
                EmotionalColoring = degradedColoring,
                GossipTtlHops   = request.GossipTtlHops - 1,
                HopsFromOrigin  = request.HopsFromOrigin + 1
            };

            var body = JsonSerializer.SerializeToUtf8Bytes(payload);
            var msg = new ServiceBusMessage(body)
            {
                MessageId         = $"gossip_{request.OriginalEventId}_{edge.TargetNpcId}",
                Subject           = "npc-gossip",
                ContentType       = "application/json",
                TimeToLive        = TimeSpan.FromDays(7)
            };
            // Route to the correct zone subscription filter
            msg.ApplicationProperties["zone"] = request.Zone;
            msg.ApplicationProperties["targetNpcId"] = edge.TargetNpcId;
            return msg;
        }).ToList();

        // Batch send for efficiency
        using var batch = await _sender.CreateMessageBatchAsync(ct);
        foreach (var msg in messages)
        {
            if (!batch.TryAddMessage(msg))
            {
                // Batch full — send what we have, start a new batch
                await _sender.SendMessagesAsync(batch, ct);
                _log.LogWarning("Gossip batch was full, some messages sent in overflow");
            }
        }
        if (batch.Count > 0)
            await _sender.SendMessagesAsync(batch, ct);

        _log.LogInformation("Broadcast gossip for event {EventId} to {Count} targets (hop {Hop})",
            request.OriginalEventId, targets.Count, request.HopsFromOrigin + 1);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
