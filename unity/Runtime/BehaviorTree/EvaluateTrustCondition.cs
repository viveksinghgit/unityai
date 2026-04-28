using NpcSoulEngine.Runtime.Components;
using UnityEngine;

namespace NpcSoulEngine.Runtime.BehaviorTree
{
    /// <summary>
    /// BT Condition: succeeds when the NPC's current trust score for the player
    /// satisfies the configured comparison.
    /// </summary>
    public sealed class EvaluateTrustCondition : BtCondition
    {
        public enum Comparison { GreaterThan, LessThan, Between }

        [SerializeField] private NpcSoulComponent npcSoulComponent;
        [SerializeField] private Comparison comparison = Comparison.GreaterThan;
        [SerializeField] [Range(0, 100)] private float threshold = 50f;
        [SerializeField] [Range(0, 100)] private float thresholdMax = 100f;  // used for Between

        public override bool OnEvaluate()
        {
            if (npcSoulComponent == null)
                npcSoulComponent = GetComponentInParent<NpcSoulComponent>();

            if (!npcSoulComponent.MemoryLoaded) return false;

            var trust = npcSoulComponent.CurrentMemory?.trustScore ?? 50f;
            return comparison switch
            {
                Comparison.GreaterThan => trust > threshold,
                Comparison.LessThan    => trust < threshold,
                Comparison.Between     => trust >= threshold && trust <= thresholdMax,
                _                      => false
            };
        }
    }

    /// <summary>
    /// BT Condition: succeeds when a specific behavior override is active.
    /// </summary>
    public sealed class BehaviorOverrideCondition : BtCondition
    {
        public enum OverrideFlag { RefuseTrade, AlertGuards, GiveDiscount, AvoidPlayer, SeekHelp }

        [SerializeField] private NpcSoulComponent npcSoulComponent;
        [SerializeField] private OverrideFlag flag;

        public override bool OnEvaluate()
        {
            if (npcSoulComponent == null)
                npcSoulComponent = GetComponentInParent<NpcSoulComponent>();

            if (!npcSoulComponent.MemoryLoaded) return false;
            var ov = npcSoulComponent.CurrentMemory?.behaviorOverrides;
            if (ov == null) return false;

            return flag switch
            {
                OverrideFlag.RefuseTrade  => ov.refuseTrade,
                OverrideFlag.AlertGuards  => ov.alertGuards,
                OverrideFlag.GiveDiscount => ov.giveDiscount,
                OverrideFlag.AvoidPlayer  => ov.avoidPlayer,
                OverrideFlag.SeekHelp     => ov.seekHelp,
                _                          => false
            };
        }
    }
}
