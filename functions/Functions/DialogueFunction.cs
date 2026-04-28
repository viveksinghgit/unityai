using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NpcSoulEngine.Functions.Models;
using NpcSoulEngine.Functions.Services;
using OpenAI.Chat;
using System.Text;
using System.Text.Json;

namespace NpcSoulEngine.Functions.Functions;

/// <summary>
/// POST /api/dialogue/generate
///
/// Request pipeline:
///   1. Sanitize + injection-check player utterance
///   2. Load NPC memory from Cosmos (or cache)
///   3. Semantic response cache lookup — skip GPT on hit
///   4. Build messages, enforce token budget (truncate history if needed)
///   5. Generate response via gpt-4o or gpt-4o-mini
///   6. Post-generation: ValidateResponse + Content Safety check
///   7. SSE streaming emits sentence events for TTS pipelining before done event
/// </summary>
public sealed class DialogueFunction
{
    private readonly ICosmosMemoryStore _store;
    private readonly IDialoguePromptBuilder _promptBuilder;
    private readonly IPromptInjectionGuard _injectionGuard;
    private readonly ITokenBudgetTracker _budgetTracker;
    private readonly ISemanticResponseCache _responseCache;
    private readonly IContentSafetyValidator _contentSafety;
    private readonly AzureOpenAIClient _openAI;
    private readonly FunctionConfig _cfg;
    private readonly ILogger<DialogueFunction> _log;

    private static readonly JsonSerializerOptions JsonIn = new() { PropertyNameCaseInsensitive = true };

    public DialogueFunction(
        ICosmosMemoryStore store,
        IDialoguePromptBuilder promptBuilder,
        IPromptInjectionGuard injectionGuard,
        ITokenBudgetTracker budgetTracker,
        ISemanticResponseCache responseCache,
        IContentSafetyValidator contentSafety,
        AzureOpenAIClient openAI,
        IOptions<FunctionConfig> cfg,
        ILogger<DialogueFunction> log)
    {
        _store         = store;
        _promptBuilder = promptBuilder;
        _injectionGuard = injectionGuard;
        _budgetTracker  = budgetTracker;
        _responseCache  = responseCache;
        _contentSafety  = contentSafety;
        _openAI        = openAI;
        _cfg           = cfg.Value;
        _log           = log;
    }

    [Function("DialogueGenerate")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "dialogue/generate")] HttpRequest req,
        CancellationToken ct)
    {
        // ── 1. Deserialize ────────────────────────────────────────────────────
        DialogueRequest request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<DialogueRequest>(req.Body, JsonIn, ct)
                ?? throw new ArgumentException("Empty body");
        }
        catch (Exception ex)
        {
            return new BadRequestObjectResult(new { error = "Invalid request", detail = ex.Message });
        }

        // ── 2. Injection guard ────────────────────────────────────────────────
        var sanitized = _injectionGuard.Sanitize(request.PlayerUtterance);
        if (!_injectionGuard.IsSafe(sanitized, out var violation))
        {
            _log.LogWarning("Injection attempt blocked for NPC {NpcId}: {Reason}", request.NpcId, violation);
            // Return in-character deflection rather than revealing the guard
            var deflect = new DialogueResponse
            {
                Text             = "I'm not sure I understand what you mean.",
                InternalEmotion  = "confused",
                EmotionIntensity = 0.3f,
                AnimationHint    = "idle",
                TradeWillingness = 0.5f
            };
            return new OkObjectResult(deflect);
        }

        // Rebuild request with sanitized utterance
        request = request with { PlayerUtterance = sanitized };

        // ── 3. Load memory ────────────────────────────────────────────────────
        var memory = await _store.GetOrCreateMemoryAsync(request.NpcId, request.PlayerId, ct);

        // ── 4. Semantic cache check ───────────────────────────────────────────
        if (_responseCache.TryGet(request.NpcId, request.PlayerId, memory,
                                   request.PlayerUtterance, out var cached) && cached is not null)
        {
            _log.LogDebug("Semantic cache hit for NPC {NpcId}", request.NpcId);
            if (request.Streaming)
                return await StreamCachedResponseAsync(req, cached, ct);
            return new OkObjectResult(cached);
        }

        // ── 5. Build messages + enforce token budget ──────────────────────────
        var messages = BuildMessagesWithBudget(request, memory);

        // ── 6. Route to gpt-4o or gpt-4o-mini ────────────────────────────────
        var deploymentName = _promptBuilder.IsHighSignificance(memory)
            ? _cfg.Gpt4oDeploymentName
            : _cfg.Gpt4oMiniDeploymentName;

        _log.LogInformation(
            "Generating dialogue for NPC {NpcId} via {Model} (trust={Trust:F0})",
            request.NpcId, deploymentName, memory.TrustScore);

        var chatClient = _openAI.GetChatClient(deploymentName);
        var chatOptions = new ChatCompletionOptions
        {
            Temperature         = 0.7f,
            TopP                = 0.9f,
            MaxOutputTokenCount = _cfg.MaxOutputTokensPerDialogueCall,
        };

        return request.Streaming
            ? await StreamResponseAsync(req, request, chatClient, messages, chatOptions, memory, ct)
            : await NonStreamingResponseAsync(request, chatClient, messages, chatOptions, memory, ct);
    }

    // ── Streaming path ────────────────────────────────────────────────────────

    private async Task<IActionResult> StreamResponseAsync(
        HttpRequest req,
        DialogueRequest request,
        ChatClient chatClient,
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionOptions options,
        NpcMemoryDocument memory,
        CancellationToken ct)
    {
        req.HttpContext.Response.Headers["Content-Type"]     = "text/event-stream";
        req.HttpContext.Response.Headers["Cache-Control"]    = "no-cache";
        req.HttpContext.Response.Headers["X-Accel-Buffering"] = "no";

        var sb = new StringBuilder();
        await foreach (var chunk in chatClient.CompleteChatStreamingAsync(messages, options, ct))
        {
            foreach (var part in chunk.ContentUpdate)
            {
                sb.Append(part.Text);
                await WriteSSE(req, new { delta = part.Text }, ct);
            }
        }

        var rawText = sb.ToString();
        var response = await ValidateAndFinaliseAsync(rawText, memory, request, ct);

        // Emit sentence events so Unity can pipeline TTS before the done event arrives
        foreach (var sentence in SplitSentences(response.Text))
            await WriteSSE(req, new { sentence }, ct);

        var ssml = _promptBuilder.BuildSsml(response.Text, memory.CurrentEmotion);
        await WriteSSE(req, new { done = true, full = response with { SsmlMarkup = ssml } }, ct);

        return new EmptyResult();
    }

    private static async Task StreamCachedResponseAsync(
        HttpRequest req, DialogueResponse cached, CancellationToken ct)
    {
        req.HttpContext.Response.Headers["Content-Type"]     = "text/event-stream";
        req.HttpContext.Response.Headers["Cache-Control"]    = "no-cache";
        req.HttpContext.Response.Headers["X-Accel-Buffering"] = "no";

        foreach (var sentence in SplitSentences(cached.Text))
            await WriteSSE(req, new { sentence }, ct);

        await WriteSSE(req, new { done = true, full = cached }, ct);
    }

    // ── Non-streaming path ────────────────────────────────────────────────────

    private async Task<IActionResult> NonStreamingResponseAsync(
        DialogueRequest request,
        ChatClient chatClient,
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionOptions options,
        NpcMemoryDocument memory,
        CancellationToken ct)
    {
        var completion = await chatClient.CompleteChatAsync(messages, options, ct);
        var rawText = completion.Value.Content[0].Text;
        var response = await ValidateAndFinaliseAsync(rawText, memory, request, ct);
        var ssml = _promptBuilder.BuildSsml(response.Text, memory.CurrentEmotion);
        return new OkObjectResult(response with { SsmlMarkup = ssml });
    }

    // ── Shared validation + cache write ──────────────────────────────────────

    private async Task<DialogueResponse> ValidateAndFinaliseAsync(
        string rawText, NpcMemoryDocument memory, DialogueRequest request, CancellationToken ct)
    {
        var response = TryParseResponse(rawText, memory);

        // Post-generation structural validation
        var validation = _promptBuilder.ValidateResponse(rawText, request.NpcId);
        if (!validation.IsValid)
        {
            _log.LogWarning("Response validation failed for NPC {NpcId}: {Reason}",
                request.NpcId, validation.Reason);
            response = FallbackResponse(memory);
        }

        // Content Safety check (async — runs after streaming starts, before done event)
        var safety = await _contentSafety.ValidateAsync(response.Text, ct);
        if (!safety.IsSafe)
        {
            _log.LogWarning("Content Safety blocked NPC {NpcId} response: {Category}",
                request.NpcId, safety.ViolationCategory);
            response = FallbackResponse(memory);
        }

        // Cache the validated response for future similar requests
        _responseCache.Set(request.NpcId, request.PlayerId, memory,
                            request.PlayerUtterance, response);

        return response;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyList<ChatMessage> BuildMessagesWithBudget(
        DialogueRequest request, NpcMemoryDocument memory)
    {
        var messages = _promptBuilder.BuildMessages(request, memory);
        var budget = _budgetTracker.Measure(messages, _cfg.MaxOutputTokensPerDialogueCall);

        if (!budget.ExceedsBudget) return messages;

        // Truncate conversation history from 6 turns down to 2 and rebuild
        _log.LogWarning(
            "Token budget exceeded ({Total} tokens) for NPC {NpcId} — truncating history",
            budget.EstimatedInputTokens + budget.MaxOutputTokens, request.NpcId);

        var truncated = request with
        {
            History = request.History.TakeLast(2).ToArray()
        };
        return _promptBuilder.BuildMessages(truncated, memory);
    }

    private static async Task WriteSSE(HttpRequest req, object payload, CancellationToken ct)
    {
        var line = $"data: {JsonSerializer.Serialize(payload)}\n\n";
        await req.HttpContext.Response.WriteAsync(line, ct);
        await req.HttpContext.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Splits NPC dialogue text into sentences for TTS pipelining.
    /// Treats a sentence as text ending in .!? followed by a space or end of string.
    /// </summary>
    internal static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return sentences;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;
            // Sentence ends here if followed by space+capital, space, or end of string
            var atEnd = i + 1 >= text.Length;
            var followedBySpace = i + 1 < text.Length && text[i + 1] == ' ';
            if (atEnd || followedBySpace)
            {
                var sentence = text[start..(i + 1)].Trim();
                if (sentence.Length > 0) sentences.Add(sentence);
                start = i + 2;
            }
        }
        // Trailing text without terminal punctuation
        if (start < text.Length)
        {
            var tail = text[start..].Trim();
            if (tail.Length > 0) sentences.Add(tail);
        }
        return sentences;
    }

    private static DialogueResponse TryParseResponse(string raw, NpcMemoryDocument memory)
    {
        var json = raw.Trim();
        if (json.StartsWith("```")) json = json[(json.IndexOf('\n') + 1)..];
        if (json.EndsWith("```"))   json = json[..json.LastIndexOf("```")];
        json = json.Trim();

        try
        {
            var parsed = JsonSerializer.Deserialize<DialogueResponseRaw>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed?.Text is not null)
            {
                return new DialogueResponse
                {
                    Text             = parsed.Text,
                    InternalEmotion  = parsed.InternalEmotion ?? memory.CurrentEmotion.Primary,
                    EmotionIntensity = parsed.EmotionIntensity,
                    AnimationHint    = parsed.AnimationHint ?? "idle",
                    TradeWillingness = parsed.TradeWillingness,
                    Subtext          = parsed.Subtext ?? string.Empty
                };
            }
        }
        catch { /* fall through */ }

        return FallbackResponse(memory, raw);
    }

    private static DialogueResponse FallbackResponse(NpcMemoryDocument memory, string? rawText = null) =>
        new()
        {
            Text             = rawText is not null && !rawText.StartsWith('{') ? rawText : "[...]",
            InternalEmotion  = memory.CurrentEmotion.Primary,
            EmotionIntensity = memory.CurrentEmotion.Intensity,
            AnimationHint    = "idle",
            TradeWillingness = 0.5f
        };

    private sealed record DialogueResponseRaw
    {
        public string? Text { get; init; }
        public string? InternalEmotion { get; init; }
        public float EmotionIntensity { get; init; }
        public string? AnimationHint { get; init; }
        public float TradeWillingness { get; init; }
        public string? Subtext { get; init; }
    }
}
