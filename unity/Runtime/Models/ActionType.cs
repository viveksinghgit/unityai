namespace NpcSoulEngine.Runtime.Models
{
    public enum ActionType
    {
        Kindness,
        Betrayal,
        Threat,
        Bribe,
        TradeFair,
        TradeDeceptive,
        CombatInitiated,
        CombatDefended,
        QuestHelped,
        QuestAbandoned,
        PromiseKept,
        PromiseBroken,
        Trespass,
        Gift,
        Insult,
        Compliment,
        Rescued,
        Endangered,
        WitnessedViolence,
        NeutralInteraction
    }

    public enum StakeLevel
    {
        Low,
        Medium,
        High,
        LifeOrDeath
    }
}
