using Azure.AI.OpenAI;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NpcSoulEngine.Functions.Models;
using NpcSoulEngine.Functions.Services;
using OpenAI.Chat;
using System.Text.Json;

namespace NpcSoulEngine.Functions.Functions;

/// <summary>
/// POST /api/gossip/broadcast — returns 202, processes async via Service Bus.
/// </summary>
public sealed class GossipBroadcastHttpFunction
{
    private readonly IGossipService _gossip;
    private readonly ILogger<GossipBroadcastHttpFunction> _log;

    public GossipBroadcastHttpFunction(IGossipService gossip, ILogger<GossipBroadcastHttpFunction> log)
    {
        _gossip = gossip;
        _log    = log;
    }

    [Function("GossipBroadcast")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "gossip/broadcast")] HttpRequest req,
        CancellationToken ct)
    {
        var request = await JsonSerializer.DeserializeAsync<GossipBroadcastRequest>(
            req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            ct);

        if (request is null)
            return new BadRequestObjectResult(new { error = "Invalid payload" });

        // Fire-and-forget — caller gets 202 immediately
        _ = _gossip.BroadcastAsync(request, CancellationToken.None);

        return new AcceptedResult();
    }
}

/// <summary>
/// Service Bus triggered — processes a gossip message, updates the target NPC's memory,
/// then re-enqueues if TTL hops remain.
/// </summary>
public sealed class GossipProcessFunction
{
    private readonly ICosmosMemoryStore _store;
    private readonly IEmotionalWeightCalculator _calc;
    private readonly IGossipService _gossip;
    private readonly AzureOpenAIClient _openAI;
    private readonly FunctionConfig _cfg;
    private readonly ILogger<GossipProcessFunction> _log;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const float StrangerTrustScale = 0.3f;

    public GossipProcessFunction(
        ICosmosMemoryStore store,
        IEmotionalWeightCalculator calc,
        IGossipService gossip,
        AzureOpenAIClient openAI,
        IOptions<FunctionConfig> cfg,
        ILogger<GossipProcessFunction> log)
    {
        _store  = store;
        _calc   = calc;
        _gossip = gossip;
        _openAI = openAI;
        _cfg    = cfg.Value;
        _log    = log;
    }

    [Function("GossipProcess")]
    public async Task RunAsync(
        [ServiceBusTrigger("%GossipTopicName%", "zone_default", Connection = "ServiceBusConnectionString")]
        ServiceBusReceivedMessage message,
        CancellationToken ct)
    {
        if (!message.ApplicationProperties.TryGetValue("targetNpcId", out var targetNpcIdObj)
            || targetNpcIdObj is not string targetNpcId
            || string.IsNullOrEmpty(targetNpcId))
        {
            _log.LogWarning("Gossip message {MessageId} missing targetNpcId application property", message.MessageId);
            return;
        }

        GossipBroadcastRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<GossipBroadcastRequest>(message.Body.ToString(), JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to deserialize gossip message {MessageId}", message.MessageId);
            return;
        }

        if (request is null) return;

        var doc = await _store.GetOrCreateMemoryAsync(targetNpcId, request.PlayerId, ct);

        // Scale emotional impact by how much this NPC trusts the gossip source
        var sourceEdge = doc.SocialGraphEdges.FirstOrDefault(e => e.TargetNpcId == request.SourceNpcId);
        var effectiveColoring = request.EmotionalColoring * (sourceEdge?.TrustInRelationship ?? StrangerTrustScale);

        var gossipResponse = await InterpretGossipAsync(request, doc, ct);

        if (gossipResponse is not null)
        {
            var gossipEvent = new SalientEvent
            {
                ActionType      = request.OriginalEvent.ActionType,
                Description     = $"(Hearsay from {request.SourceNpcId}) {gossipResponse}",
                EmotionalWeight = effectiveColoring,
                DecayRate       = _calc.GetDecayRate(request.OriginalEvent.ActionType, request.OriginalEvent.Context.Stakes) * 1.5f,
                WitnessIds      = new[] { request.SourceNpcId }
            };

            doc.SalientEvents.Add(gossipEvent);
            _calc.RecalculateScores(doc);
            _calc.UpdateBehaviorOverrides(doc);
            await _store.UpsertMemoryAsync(doc, ct);
        }

        // Propagate further if TTL allows and target has social edges
        if (request.GossipTtlHops > 0 && doc.SocialGraphEdges.Count > 0)
        {
            await _gossip.BroadcastAsync(request with
            {
                SourceNpcId      = targetNpcId,
                SocialGraphEdges = doc.SocialGraphEdges,
                HopsFromOrigin   = request.HopsFromOrigin + 1,
                GossipTtlHops    = request.GossipTtlHops - 1
            }, ct);
        }
    }

    private async Task<string?> InterpretGossipAsync(GossipBroadcastRequest request, NpcMemoryDocument listenerDoc, CancellationToken ct)
    {
        var prompt = $"""
            You are {listenerDoc.NpcId}. {request.SourceNpcId} just told you:
            "{request.OriginalEvent.Context.Summary}"
            You trust {request.SourceNpcId} at level {GetTrustLabel(request.EmotionalColoring)}.
            In one sentence, describe how this changes your private opinion of the player. Be concrete.
            """;

        try
        {
            var chatClient = _openAI.GetChatClient(_cfg.Gpt4oMiniDeploymentName);
            var result = await chatClient.CompleteChatAsync(
                new[] { ChatMessage.CreateUserMessage(prompt) },
                new ChatCompletionOptions { MaxOutputTokenCount = 80, Temperature = 0.6f },
                ct);
            return result.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GPT gossip interpretation failed, skipping sentiment update");
            return null;
        }
    }

    internal static string GetTrustLabel(float coloring) => coloring switch
    {
        > 0.5f => "highly",
        > 0f   => "somewhat",
        > -0.5f => "not much",
        _      => "not at all"
    };
}
