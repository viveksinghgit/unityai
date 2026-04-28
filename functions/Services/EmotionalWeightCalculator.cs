using NpcSoulEngine.Functions.Models;

namespace NpcSoulEngine.Functions.Services;

public interface IEmotionalWeightCalculator
{
    float ComputeWeight(MemoryEventPayload payload);
    float GetDecayRate(ActionType actionType, StakeLevel stakes);
    void RecalculateScores(NpcMemoryDocument doc);
    void UpdateBehaviorOverrides(NpcMemoryDocument doc);
}

public sealed class EmotionalWeightCalculator : IEmotionalWeightCalculator
{
    // Base emotional weight per action type (positive = good for relationship, negative = bad)
    private static readonly Dictionary<ActionType, float> BaseWeights = new()
    {
        [ActionType.Kindness]           =  15f,
        [ActionType.Gift]               =  20f,
        [ActionType.Compliment]         =   8f,
        [ActionType.Rescued]            =  35f,
        [ActionType.PromiseKept]        =  18f,
        [ActionType.QuestHelped]        =  25f,
        [ActionType.TradeFair]          =   5f,

        [ActionType.Betrayal]           = -30f,
        [ActionType.Threat]             = -20f,
        [ActionType.Insult]             = -12f,
        [ActionType.TradeDeceptive]     = -25f,
        [ActionType.PromiseBroken]      = -22f,
        [ActionType.QuestAbandoned]     = -15f,
        [ActionType.CombatInitiated]    = -28f,
        [ActionType.Trespass]           = -10f,
        [ActionType.Endangered]         = -40f,
        [ActionType.WitnessedViolence]  = -18f,
        [ActionType.Bribe]              =  -5f,  // erodes respect even if it works

        [ActionType.CombatDefended]     =   0f,  // neutral — context-dependent
        [ActionType.NeutralInteraction] =   1f,
    };

    // Decay rate per action type (Ebbinghaus coefficient, per-hour)
    private static readonly Dictionary<ActionType, float> DecayRates = new()
    {
        [ActionType.Betrayal]           = 0.001f,   // almost permanent
        [ActionType.Endangered]         = 0.0005f,  // permanent
        [ActionType.Rescued]            = 0.0008f,
        [ActionType.Threat]             = 0.002f,
        [ActionType.PromiseBroken]      = 0.0015f,
        [ActionType.PromiseKept]        = 0.003f,
        [ActionType.Kindness]           = 0.003f,
        [ActionType.Gift]               = 0.002f,
        [ActionType.TradeDeceptive]     = 0.002f,
        [ActionType.TradeFair]          = 0.005f,
        [ActionType.QuestHelped]        = 0.002f,
        [ActionType.QuestAbandoned]     = 0.003f,
        [ActionType.CombatInitiated]    = 0.004f,
        [ActionType.Insult]             = 0.005f,
        [ActionType.Compliment]         = 0.008f,
        [ActionType.NeutralInteraction] = 0.02f,
        [ActionType.Bribe]              = 0.006f,
        [ActionType.Trespass]           = 0.01f,
        [ActionType.WitnessedViolence]  = 0.004f,
        [ActionType.CombatDefended]     = 0.01f,
    };

    public float ComputeWeight(MemoryEventPayload payload)
    {
        var base_ = BaseWeights.GetValueOrDefault(payload.ActionType, 0f);
        var stakesMultiplier = payload.Context.Stakes switch
        {
            StakeLevel.Low          => 0.5f,
            StakeLevel.Medium       => 1.0f,
            StakeLevel.High         => 1.5f,
            StakeLevel.LifeOrDeath  => 2.5f,
            _                       => 1.0f
        };
        var witnessMultiplier = 1f + (payload.WitnessIds.Count * 0.1f);  // public acts hit harder
        return base_ * stakesMultiplier * witnessMultiplier;
    }

    public float GetDecayRate(ActionType actionType, StakeLevel stakes)
    {
        var rate = DecayRates.GetValueOrDefault(actionType, 0.01f);
        // High-stakes events decay more slowly
        return stakes switch
        {
            StakeLevel.LifeOrDeath => rate * 0.5f,
            StakeLevel.High        => rate * 0.7f,
            _                      => rate
        };
    }

    public void RecalculateScores(NpcMemoryDocument doc)
    {
        var now = DateTimeOffset.UtcNow;
        var activeEvents = doc.SalientEvents.Where(e => !e.IsConsolidated).ToList();

        doc.TrustScore = Math.Clamp(
            50f + activeEvents.Sum(e => e.CurrentWeight(now)),
            0f, 100f);

        doc.HostilityScore = Math.Clamp(
            activeEvents.Where(e => e.EmotionalWeight < 0).Sum(e => Math.Abs(e.CurrentWeight(now))),
            0f, 100f);

        doc.GratitudeScore = Math.Clamp(
            activeEvents.Where(e => e.EmotionalWeight > 15).Sum(e => e.CurrentWeight(now)),
            0f, 100f);

        doc.FearScore = Math.Clamp(
            activeEvents.Where(e => e.ActionType is ActionType.Threat or ActionType.CombatInitiated or ActionType.Endangered)
                        .Sum(e => Math.Abs(e.CurrentWeight(now))),
            0f, 100f);

        doc.CurrentEmotion = ComputeEmotionVector(doc);
    }

    public void UpdateBehaviorOverrides(NpcMemoryDocument doc)
    {
        doc.BehaviorOverrides.RefuseTrade   = doc.TrustScore < 20 || doc.HostilityScore > 60;
        doc.BehaviorOverrides.AlertGuards   = doc.HostilityScore > 75 || doc.FearScore > 70;
        doc.BehaviorOverrides.GiveDiscount  = doc.TrustScore > 80 && doc.GratitudeScore > 40;
        doc.BehaviorOverrides.AvoidPlayer   = doc.FearScore > 50 || doc.HostilityScore > 50;
        doc.BehaviorOverrides.SeekHelp      = doc.FearScore > 80;
    }

    private static EmotionVector ComputeEmotionVector(NpcMemoryDocument doc)
    {
        // PAD (Pleasure-Arousal-Dominance) model
        var valence   = (doc.TrustScore - 50f) / 50f;                    // -1 to +1
        var arousal   = Math.Clamp((doc.FearScore + doc.HostilityScore) / 100f, 0f, 1f);
        var dominance = Math.Clamp((doc.RespectScore - doc.FearScore) / 100f, -1f, 1f);

        var primary = (valence, arousal) switch
        {
            ( > 0.4f, < 0.3f)  => "warm_content",
            ( > 0.4f, >= 0.3f) => "joyful_excited",
            ( > 0.1f, < 0.3f)  => "cautious_warmth",
            ( > 0.1f, >= 0.3f) => "friendly_alert",
            ( < -0.4f, < 0.3f) => "cold_dismissive",
            ( < -0.4f, >= 0.5f) => "hostile_aggressive",
            ( < -0.1f, >= 0.4f) => "fearful_tense",
            ( < -0.1f, < 0.4f) => "suspicious_wary",
            _                   => "neutral"
        };

        var intensity = Math.Clamp(Math.Abs(valence) + arousal * 0.5f, 0f, 1f);

        return new EmotionVector
        {
            Primary   = primary,
            Intensity = intensity,
            Valence   = valence,
            Arousal   = arousal,
            Dominance = dominance
        };
    }
}
