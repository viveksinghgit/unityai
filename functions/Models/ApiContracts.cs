using System.Text.Json.Serialization;

namespace NpcSoulEngine.Functions.Models;

// ─── Response: what Unity receives after a memory event is processed ───────────

public sealed record NpcMemoryState
{
    [JsonPropertyName("npcId")]             public required string NpcId { get; init; }
    [JsonPropertyName("playerId")]          public required string PlayerId { get; init; }
    [JsonPropertyName("trustScore")]        public float TrustScore { get; init; }
    [JsonPropertyName("fearScore")]         public float FearScore { get; init; }
    [JsonPropertyName("respectScore")]      public float RespectScore { get; init; }
    [JsonPropertyName("hostilityScore")]    public float HostilityScore { get; init; }
    [JsonPropertyName("currentEmotion")]    public required EmotionVector CurrentEmotion { get; init; }
    [JsonPropertyName("salientEvents")]     public IReadOnlyList<SalientEvent> SalientEvents { get; init; } = Array.Empty<SalientEvent>();
    [JsonPropertyName("memorySummary")]     public string MemorySummary { get; init; } = string.Empty;
    [JsonPropertyName("behaviorOverrides")] public required BehaviorOverrides BehaviorOverrides { get; init; }
    [JsonPropertyName("playerArchetype")]   public string PlayerArchetype { get; init; } = "unknown";
    [JsonPropertyName("archetypeConfidence")] public float ArchetypeConfidence { get; init; }
    [JsonPropertyName("etag")]              public string? ETag { get; init; }

    public static NpcMemoryState FromDocument(NpcMemoryDocument doc) => new()
    {
        NpcId = doc.NpcId,
        PlayerId = doc.PlayerId,
        TrustScore = doc.TrustScore,
        FearScore = doc.FearScore,
        RespectScore = doc.RespectScore,
        HostilityScore = doc.HostilityScore,
        CurrentEmotion = doc.CurrentEmotion,
        SalientEvents = doc.SalientEvents.Where(e => !e.IsConsolidated).Take(8).ToList(),
        MemorySummary = doc.MemorySummaryBlob,
        BehaviorOverrides = doc.BehaviorOverrides,
        PlayerArchetype = doc.PlayerArchetype,
        ArchetypeConfidence = doc.ArchetypeConfidence,
        ETag = doc.ETag
    };
}

// ─── Dialogue ──────────────────────────────────────────────────────────────────

public sealed record DialogueRequest
{
    [JsonPropertyName("npcId")]       public required string NpcId { get; init; }
    [JsonPropertyName("playerId")]    public required string PlayerId { get; init; }
    [JsonPropertyName("utterance")]   public required string PlayerUtterance { get; init; }
    [JsonPropertyName("history")]     public IReadOnlyList<ConversationTurn> History { get; init; } = Array.Empty<ConversationTurn>();
    [JsonPropertyName("npcProfile")]  public NpcProfile? NpcProfile { get; init; }
    [JsonPropertyName("streaming")]   public bool Streaming { get; init; } = true;
}

public sealed record ConversationTurn
{
    [JsonPropertyName("role")]    public required string Role { get; init; }   // "player" | "npc"
    [JsonPropertyName("content")] public required string Content { get; init; }
}

public sealed record NpcProfile
{
    [JsonPropertyName("name")]              public required string Name { get; init; }
    [JsonPropertyName("baseDescription")]   public required string BaseDescription { get; init; }
    [JsonPropertyName("worldName")]         public string WorldName { get; init; } = "the realm";
    [JsonPropertyName("locationName")]      public string LocationName { get; init; } = string.Empty;
}

public sealed record DialogueResponse
{
    [JsonPropertyName("text")]              public required string Text { get; init; }
    [JsonPropertyName("internalEmotion")]   public required string InternalEmotion { get; init; }
    [JsonPropertyName("emotionIntensity")]  public float EmotionIntensity { get; init; }
    [JsonPropertyName("animationHint")]     public required string AnimationHint { get; init; }
    [JsonPropertyName("tradeWillingness")]  public float TradeWillingness { get; init; }
    [JsonPropertyName("subtext")]           public string Subtext { get; init; } = string.Empty;
    [JsonPropertyName("ssmlMarkup")]        public string SsmlMarkup { get; init; } = string.Empty;
}

// ─── Gossip ────────────────────────────────────────────────────────────────────

public sealed record GossipBroadcastRequest
{
    [JsonPropertyName("sourceNpcId")]       public required string SourceNpcId { get; init; }
    [JsonPropertyName("playerId")]          public required string PlayerId { get; init; }
    [JsonPropertyName("originalEventId")]   public required string OriginalEventId { get; init; }
    [JsonPropertyName("zone")]              public string Zone { get; init; } = "zone_default";
    [JsonPropertyName("socialGraphEdges")]  public IReadOnlyList<SocialGraphEdge> SocialGraphEdges { get; init; } = Array.Empty<SocialGraphEdge>();
    [JsonPropertyName("originalEvent")]     public required MemoryEventPayload OriginalEvent { get; init; }
    [JsonPropertyName("emotionalColoring")] public float EmotionalColoring { get; init; }
    [JsonPropertyName("gossipTtlHops")]     public int GossipTtlHops { get; init; } = 3;
    [JsonPropertyName("hopsFromOrigin")]    public int HopsFromOrigin { get; init; }
}

// ─── Memory consolidation (Service Bus message) ────────────────────────────────

public sealed record ConsolidationRequest
{
    [JsonPropertyName("npcId")]     public required string NpcId { get; init; }
    [JsonPropertyName("playerId")]  public required string PlayerId { get; init; }
    [JsonPropertyName("eventIds")]  public required IReadOnlyList<string> EventIds { get; init; }
}

// ─── Player archetype document ─────────────────────────────────────────────────

public sealed class PlayerArchetypeDocument
{
    [JsonPropertyName("id")]            public required string Id { get; set; }
    [JsonPropertyName("playerId")]      public required string PlayerId { get; set; }
    [JsonPropertyName("primaryArchetype")] public string PrimaryArchetype { get; set; } = "unknown";
    [JsonPropertyName("archetypeScores")] public Dictionary<string, float> ArchetypeScores { get; set; } = new();
    [JsonPropertyName("behaviorFeatures")] public BehaviorFeatures BehaviorFeatures { get; set; } = new();
    [JsonPropertyName("lastClassifiedAt")] public DateTimeOffset LastClassifiedAt { get; set; }
    [JsonPropertyName("classificationVersion")] public string ClassificationVersion { get; set; } = "v1.0";
}

public sealed class BehaviorFeatures
{
    [JsonPropertyName("combatInitiationRate")]      public float CombatInitiationRate { get; set; }
    [JsonPropertyName("dialogueChoiceAggression")]  public float DialogueChoiceAggression { get; set; }
    [JsonPropertyName("tradeDeceptionRate")]         public float TradeDeceptionRate { get; set; }
    [JsonPropertyName("promiseBrokenRate")]          public float PromiseBrokenRate { get; set; }
    [JsonPropertyName("averageTimeBetweenActions")] public float AverageTimeBetweenActions { get; set; }
    [JsonPropertyName("reputationAwarenessScore")]  public float ReputationAwarenessScore { get; set; }
}
