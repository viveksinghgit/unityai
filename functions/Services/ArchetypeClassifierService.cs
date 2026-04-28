using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NpcSoulEngine.Functions.Models;

namespace NpcSoulEngine.Functions.Services;

/// <summary>
/// Feature vector extracted from a player's cross-NPC memory documents.
/// All values are normalized to [0, 1].
/// </summary>
public sealed record PlayerFeatureVector
{
    public float AvgTrust                 { get; init; }
    public float AvgFear                  { get; init; }
    public float AvgHostility             { get; init; }
    public float AvgRespect               { get; init; }
    public float CombatInitiationRate     { get; init; }
    public float DialogueChoiceAggression { get; init; }
    public float TradeDeceptionRate       { get; init; }
    public float PromiseBrokenRate        { get; init; }
    public float AvgTimeBetweenActions    { get; init; }   // normalized: 3600s → 1.0
    public float ReputationAwarenessScore { get; init; }

    public float[] ToArray() => new[]
    {
        AvgTrust, AvgFear, AvgHostility, AvgRespect,
        CombatInitiationRate, DialogueChoiceAggression,
        TradeDeceptionRate, PromiseBrokenRate,
        AvgTimeBetweenActions, ReputationAwarenessScore
    };

    public static PlayerFeatureVector FromDocuments(
        IReadOnlyList<NpcMemoryDocument> npcDocs,
        BehaviorFeatures? behavior)
    {
        if (npcDocs.Count == 0) return new PlayerFeatureVector();

        var avgTrust     = npcDocs.Average(d => d.TrustScore)     / 100f;
        var avgFear      = npcDocs.Average(d => d.FearScore)      / 100f;
        var avgHostility = npcDocs.Average(d => d.HostilityScore) / 100f;
        var avgRespect   = npcDocs.Average(d => d.RespectScore)   / 100f;

        var b        = behavior ?? new BehaviorFeatures();
        var normTime = Math.Clamp(b.AverageTimeBetweenActions / 3600f, 0f, 1f);

        return new PlayerFeatureVector
        {
            AvgTrust                 = Clamp01(avgTrust),
            AvgFear                  = Clamp01(avgFear),
            AvgHostility             = Clamp01(avgHostility),
            AvgRespect               = Clamp01(avgRespect),
            CombatInitiationRate     = Clamp01(b.CombatInitiationRate),
            DialogueChoiceAggression = Clamp01(b.DialogueChoiceAggression),
            TradeDeceptionRate       = Clamp01(b.TradeDeceptionRate),
            PromiseBrokenRate        = Clamp01(b.PromiseBrokenRate),
            AvgTimeBetweenActions    = normTime,
            ReputationAwarenessScore = Clamp01(b.ReputationAwarenessScore)
        };
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);
}

public sealed record ArchetypeResult
{
    public string Archetype { get; init; } = "neutral";
    public float Confidence { get; init; }
    public IReadOnlyDictionary<string, float> Scores { get; init; } = new Dictionary<string, float>();
}

public interface IArchetypeClassifierService
{
    Task<ArchetypeResult> ClassifyAsync(PlayerFeatureVector features, CancellationToken ct = default);
}

public sealed class AzureMLArchetypeClassifier : IArchetypeClassifierService
{
    // Must match the label order produced by the training pipeline (alphabetical).
    internal static readonly string[] Labels =
        { "aggressor", "benefactor", "diplomat", "hero", "neutral", "trickster" };

    private readonly HttpClient _http;
    private readonly ILogger<AzureMLArchetypeClassifier> _log;
    private readonly string? _endpointUri;

    public AzureMLArchetypeClassifier(
        HttpClient http,
        IOptions<FunctionConfig> opts,
        ILogger<AzureMLArchetypeClassifier> log)
    {
        _http        = http;
        _log         = log;
        _endpointUri = opts.Value.AzureMLEndpointUri;

        if (!string.IsNullOrEmpty(opts.Value.AzureMLEndpointKey))
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {opts.Value.AzureMLEndpointKey}");
    }

    public async Task<ArchetypeResult> ClassifyAsync(PlayerFeatureVector features, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_endpointUri))
            return RuleBasedClassify(features);

        try
        {
            var payload = new { features = new[] { features.ToArray() } };
            using var response = await _http.PostAsJsonAsync(_endpointUri, payload, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MLEndpointResponse>(ct);
            if (result?.Archetypes is null || result.Archetypes.Count == 0)
                return RuleBasedClassify(features);

            var archetype = result.Archetypes[0];
            var probs     = result.Probabilities?.Count > 0 ? result.Probabilities[0] : Array.Empty<float>();
            var scores    = BuildScores(probs);
            scores.TryGetValue(archetype, out var confidence);

            return new ArchetypeResult { Archetype = archetype, Confidence = confidence, Scores = scores };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Azure ML endpoint unavailable, using rule-based archetype fallback");
            return RuleBasedClassify(features);
        }
    }

    // Priority: aggressor > trickster > hero > diplomat > benefactor > neutral
    internal static ArchetypeResult RuleBasedClassify(PlayerFeatureVector f)
    {
        string label;
        float  confidence;

        if (f.CombatInitiationRate > 0.55f || (f.AvgHostility > 0.65f && f.DialogueChoiceAggression > 0.50f))
        {
            label      = "aggressor";
            confidence = MathF.Max(f.CombatInitiationRate, f.AvgHostility);
        }
        else if (f.TradeDeceptionRate > 0.45f || f.PromiseBrokenRate > 0.50f)
        {
            label      = "trickster";
            confidence = MathF.Max(f.TradeDeceptionRate, f.PromiseBrokenRate);
        }
        else if (f.AvgTrust > 0.75f && f.AvgHostility < 0.25f && f.ReputationAwarenessScore > 0.60f)
        {
            label      = "hero";
            confidence = (f.AvgTrust + f.ReputationAwarenessScore) / 2f;
        }
        else if (f.DialogueChoiceAggression < 0.25f && f.ReputationAwarenessScore > 0.60f)
        {
            label      = "diplomat";
            confidence = f.ReputationAwarenessScore;
        }
        else if (f.AvgTrust > 0.65f && f.AvgHostility < 0.30f)
        {
            label      = "benefactor";
            confidence = f.AvgTrust;
        }
        else
        {
            label      = "neutral";
            confidence = 0.5f;
        }

        var spread = (1f - confidence) / (Labels.Length - 1);
        var scores = Labels.ToDictionary(l => l, l => l == label ? confidence : spread);
        return new ArchetypeResult { Archetype = label, Confidence = confidence, Scores = scores };
    }

    private static IReadOnlyDictionary<string, float> BuildScores(IReadOnlyList<float> probs)
    {
        var dict = new Dictionary<string, float>(Labels.Length);
        for (var i = 0; i < Labels.Length; i++)
            dict[Labels[i]] = i < probs.Count ? probs[i] : 0f;
        return dict;
    }

    private sealed class MLEndpointResponse
    {
        [JsonPropertyName("archetypes")]
        public IReadOnlyList<string>? Archetypes { get; init; }

        [JsonPropertyName("probabilities")]
        public IReadOnlyList<IReadOnlyList<float>>? Probabilities { get; init; }
    }
}
