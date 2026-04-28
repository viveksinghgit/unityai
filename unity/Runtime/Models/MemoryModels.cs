using System;
using System.Collections.Generic;

namespace NpcSoulEngine.Runtime.Models
{
    // ─── Outbound: Unity → Azure ──────────────────────────────────────────────

    [Serializable]
    public sealed class MemoryEventPayload
    {
        public string npcId;
        public string playerId;
        public string actionType;       // serialized as string for JSON compat
        public ActionContext context;
        public string timestamp;        // ISO 8601
        public List<string> witnessIds = new();
        public List<string> witnessPlayerIds = new();
        public string location;
        public string zone = "zone_default";
        public float significanceHint;

        public static MemoryEventPayload Create(
            string npcId, string playerId,
            ActionType actionType, ActionContext context,
            string location = "", string zone = "zone_default")
        {
            return new MemoryEventPayload
            {
                npcId       = npcId,
                playerId    = playerId,
                actionType  = actionType.ToString(),
                context     = context,
                timestamp   = DateTimeOffset.UtcNow.ToString("O"),
                location    = location,
                zone        = zone
            };
        }
    }

    [Serializable]
    public sealed class ActionContext
    {
        public string sceneName;
        public string stakes = "Low";
        public float publicness;
        public string summary;
        public List<string> itemsInvolved = new();
    }

    // ─── Inbound: Azure → Unity ───────────────────────────────────────────────

    [Serializable]
    public sealed class NpcMemoryState
    {
        public string npcId;
        public string playerId;
        public float trustScore;
        public float fearScore;
        public float respectScore;
        public float hostilityScore;
        public EmotionVector currentEmotion;
        public List<SalientEventSummary> salientEvents = new();
        public string memorySummary;
        public BehaviorOverrides behaviorOverrides;
        public string playerArchetype;
        public float archetypeConfidence;
        public string etag;

        public static NpcMemoryState Neutral(string npcId, string playerId) => new()
        {
            npcId           = npcId,
            playerId        = playerId,
            trustScore      = 50f,
            currentEmotion  = EmotionVector.Neutral(),
            behaviorOverrides = new BehaviorOverrides(),
            playerArchetype = "unknown"
        };
    }

    [Serializable]
    public sealed class EmotionVector
    {
        public string primary = "neutral";
        public float intensity;
        public float valence;
        public float arousal;
        public float dominance;

        public static EmotionVector Neutral() => new()
        {
            primary   = "neutral",
            intensity = 0f,
            valence   = 0f,
            arousal   = 0f,
            dominance = 0f
        };
    }

    [Serializable]
    public sealed class SalientEventSummary
    {
        public string eventId;
        public string actionType;
        public string description;
        public float emotionalWeight;
        public string timestamp;
        public string location;
    }

    [Serializable]
    public sealed class BehaviorOverrides
    {
        public bool refuseTrade;
        public bool alertGuards;
        public bool giveDiscount;
        public bool avoidPlayer;
        public bool seekHelp;
    }

    // ─── Dialogue ─────────────────────────────────────────────────────────────

    [Serializable]
    public sealed class DialogueRequest
    {
        public string npcId;
        public string playerId;
        public string utterance;
        public List<ConversationTurn> history = new();
        public NpcProfileData npcProfile;
        public bool streaming = true;
    }

    [Serializable]
    public sealed class NpcProfileData
    {
        public string name;
        public string baseDescription;
        public string worldName = "the realm";
        public string locationName;
    }

    [Serializable]
    public sealed class ConversationTurn
    {
        public string role;     // "player" | "npc"
        public string content;
    }

    [Serializable]
    public sealed class DialogueResponse
    {
        public string text;
        public string internalEmotion;
        public float emotionIntensity;
        public string animationHint;
        public float tradeWillingness;
        public string subtext;
        public string ssmlMarkup;
    }
}
