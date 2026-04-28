using System.Text.Json.Serialization;

namespace NpcSoulEngine.Functions.Models;

/// <summary>
/// Cosmos DB document stored in the npc-memory-graphs container.
/// Partition key: /npcId + /playerId (hierarchical).
/// </summary>
public sealed class NpcMemoryDocument
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("npcId")]
    public required string NpcId { get; set; }

    [JsonPropertyName("playerId")]
    public required string PlayerId { get; set; }

    [JsonPropertyName("sessionCount")]
    public int SessionCount { get; set; }

    [JsonPropertyName("firstEncounterTimestamp")]
    public DateTimeOffset FirstEncounterTimestamp { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastEncounterTimestamp")]
    public DateTimeOffset LastEncounterTimestamp { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("trustScore")]
    public float TrustScore { get; set; } = 50f;

    [JsonPropertyName("fearScore")]
    public float FearScore { get; set; }

    [JsonPropertyName("respectScore")]
    public float RespectScore { get; set; } = 50f;

    [JsonPropertyName("gratitudeScore")]
    public float GratitudeScore { get; set; }

    [JsonPropertyName("hostilityScore")]
    public float HostilityScore { get; set; }

    [JsonPropertyName("currentEmotion")]
    public EmotionVector CurrentEmotion { get; set; } = EmotionVector.Neutral;

    [JsonPropertyName("salientEvents")]
    public List<SalientEvent> SalientEvents { get; set; } = new();

    [JsonPropertyName("memorySummaryBlob")]
    public string MemorySummaryBlob { get; set; } = string.Empty;

    [JsonPropertyName("personalityVector")]
    public PersonalityVector PersonalityVector { get; set; } = new();

    [JsonPropertyName("socialGraphEdges")]
    public List<SocialGraphEdge> SocialGraphEdges { get; set; } = new();

    [JsonPropertyName("behaviorOverrides")]
    public BehaviorOverrides BehaviorOverrides { get; set; } = new();

    [JsonPropertyName("playerArchetype")]
    public string PlayerArchetype { get; set; } = "unknown";

    [JsonPropertyName("archetypeConfidence")]
    public float ArchetypeConfidence { get; set; }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    public static string MakeId(string npcId, string playerId) => $"{npcId}_{playerId}";
}

public sealed record EmotionVector
{
    [JsonPropertyName("primary")]
    public string Primary { get; init; } = "neutral";

    [JsonPropertyName("intensity")]
    public float Intensity { get; init; }

    [JsonPropertyName("valence")]
    public float Valence { get; init; }

    [JsonPropertyName("arousal")]
    public float Arousal { get; init; }

    [JsonPropertyName("dominance")]
    public float Dominance { get; init; }

    public static readonly EmotionVector Neutral = new()
    {
        Primary = "neutral",
        Intensity = 0f,
        Valence = 0f,
        Arousal = 0f,
        Dominance = 0f
    };
}

public sealed record SalientEvent
{
    [JsonPropertyName("eventId")]
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("actionType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionType ActionType { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("emotionalWeight")]
    public float EmotionalWeight { get; init; }

    [JsonPropertyName("decayRate")]
    public float DecayRate { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("witnessIds")]
    public IReadOnlyList<string> WitnessIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    [JsonPropertyName("isConsolidated")]
    public bool IsConsolidated { get; init; }

    public float CurrentWeight(DateTimeOffset now)
    {
        var hoursSince = (float)(now - Timestamp).TotalHours;
        return EmotionalWeight * MathF.Exp(-DecayRate * hoursSince);
    }
}

public sealed class PersonalityVector
{
    [JsonPropertyName("openness")]          public float Openness { get; set; } = 0.5f;
    [JsonPropertyName("conscientiousness")] public float Conscientiousness { get; set; } = 0.5f;
    [JsonPropertyName("extraversion")]      public float Extraversion { get; set; } = 0.5f;
    [JsonPropertyName("agreeableness")]     public float Agreeableness { get; set; } = 0.5f;
    [JsonPropertyName("neuroticism")]       public float Neuroticism { get; set; } = 0.5f;
    [JsonPropertyName("aggression")]        public float Aggression { get; set; }
    [JsonPropertyName("loyalty")]           public float Loyalty { get; set; } = 0.5f;
    [JsonPropertyName("greed")]             public float Greed { get; set; }
    [JsonPropertyName("fearfulness")]       public float Fearfulness { get; set; }
    [JsonPropertyName("curiosity")]         public float Curiosity { get; set; } = 0.5f;
    [JsonPropertyName("vengefulness")]      public float Vengefulness { get; set; }
    [JsonPropertyName("forgiveness")]       public float Forgiveness { get; set; } = 0.5f;
}

public sealed record SocialGraphEdge
{
    [JsonPropertyName("targetNpcId")]
    public required string TargetNpcId { get; init; }

    [JsonPropertyName("relationshipStrength")]
    public float RelationshipStrength { get; init; }

    [JsonPropertyName("trustInRelationship")]
    public float TrustInRelationship { get; init; }
}

public sealed class BehaviorOverrides
{
    [JsonPropertyName("refuseTrade")]   public bool RefuseTrade { get; set; }
    [JsonPropertyName("alertGuards")]   public bool AlertGuards { get; set; }
    [JsonPropertyName("giveDiscount")]  public bool GiveDiscount { get; set; }
    [JsonPropertyName("avoidPlayer")]   public bool AvoidPlayer { get; set; }
    [JsonPropertyName("seekHelp")]      public bool SeekHelp { get; set; }
}
