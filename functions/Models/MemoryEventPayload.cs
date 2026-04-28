using System.Text.Json.Serialization;

namespace NpcSoulEngine.Functions.Models;

public sealed record MemoryEventPayload
{
    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    [JsonPropertyName("playerId")]
    public required string PlayerId { get; init; }

    [JsonPropertyName("actionType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ActionType ActionType { get; init; }

    [JsonPropertyName("context")]
    public required ActionContext Context { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("witnessIds")]
    public IReadOnlyList<string> WitnessIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("witnessPlayerIds")]
    public IReadOnlyList<string> WitnessPlayerIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    [JsonPropertyName("zone")]
    public string Zone { get; init; } = "zone_default";

    // Pre-computed local significance hint from Unity Sentis (0–1). Server re-validates.
    [JsonPropertyName("significanceHint")]
    public float SignificanceHint { get; init; }
}

public sealed record ActionContext
{
    [JsonPropertyName("sceneName")]
    public string SceneName { get; init; } = string.Empty;

    [JsonPropertyName("stakes")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StakeLevel Stakes { get; init; } = StakeLevel.Low;

    [JsonPropertyName("publicness")]
    public float Publicness { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("itemsInvolved")]
    public IReadOnlyList<string> ItemsInvolved { get; init; } = Array.Empty<string>();
}
