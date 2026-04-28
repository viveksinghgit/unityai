using NUnit.Framework;
using NpcSoulEngine.Functions.Models;
using NpcSoulEngine.Functions.Services;

namespace NpcSoulEngine.Tests
{
    /// <summary>
    /// Tests the server-side EmotionalWeightCalculator.
    /// These run as plain NUnit tests (no Unity runtime needed).
    /// </summary>
    [TestFixture]
    public sealed class EmotionalWeightTests
    {
        private EmotionalWeightCalculator _calc;

        [SetUp] public void SetUp() => _calc = new EmotionalWeightCalculator();

        private static MemoryEventPayload MakePayload(
            ActionType action, StakeLevel stakes = StakeLevel.Medium,
            float publicness = 0.5f, int witnessCount = 0)
        {
            var witnesses = new string[witnessCount];
            for (int i = 0; i < witnessCount; i++) witnesses[i] = $"npc_{i}";
            return new MemoryEventPayload
            {
                NpcId    = "npc_a",
                PlayerId = "p1",
                ActionType = action,
                Context  = new ActionContext { Stakes = stakes, Publicness = publicness, Summary = "test" },
                WitnessIds = witnesses
            };
        }

        // ─── Sign checks ──────────────────────────────────────────────────────

        [Test]
        public void Betrayal_ProducesNegativeWeight()
        {
            var w = _calc.ComputeWeight(MakePayload(ActionType.Betrayal));
            Assert.Less(w, 0f);
        }

        [Test]
        public void Kindness_ProducesPositiveWeight()
        {
            var w = _calc.ComputeWeight(MakePayload(ActionType.Kindness));
            Assert.Greater(w, 0f);
        }

        // ─── Stakes multiplier ────────────────────────────────────────────────

        [Test]
        public void LifeOrDeath_OutweighsLow_ForSameAction()
        {
            var low  = _calc.ComputeWeight(MakePayload(ActionType.Betrayal, StakeLevel.Low));
            var lod  = _calc.ComputeWeight(MakePayload(ActionType.Betrayal, StakeLevel.LifeOrDeath));
            Assert.Greater(System.Math.Abs(lod), System.Math.Abs(low));
        }

        // ─── Witness multiplier ───────────────────────────────────────────────

        [Test]
        public void MoreWitnesses_IncreasesMagnitude()
        {
            var noWitness   = System.Math.Abs(_calc.ComputeWeight(MakePayload(ActionType.Betrayal, witnessCount: 0)));
            var fiveWitness = System.Math.Abs(_calc.ComputeWeight(MakePayload(ActionType.Betrayal, witnessCount: 5)));
            Assert.Greater(fiveWitness, noWitness);
        }

        // ─── Score recalculation ──────────────────────────────────────────────

        [Test]
        public void RecalculateScores_ClampsTo0_100()
        {
            var doc = new NpcMemoryDocument
            {
                Id = "a_p", NpcId = "a", PlayerId = "p",
                SalientEvents = new System.Collections.Generic.List<SalientEvent>
                {
                    new() { ActionType = ActionType.Betrayal, EmotionalWeight = -200f,
                            DecayRate = 0f, Timestamp = System.DateTimeOffset.UtcNow.AddHours(-1) },
                    new() { ActionType = ActionType.Kindness, EmotionalWeight = +200f,
                            DecayRate = 0f, Timestamp = System.DateTimeOffset.UtcNow.AddHours(-1) }
                }
            };
            _calc.RecalculateScores(doc);
            Assert.GreaterOrEqual(doc.TrustScore, 0f);
            Assert.LessOrEqual(doc.TrustScore, 100f);
            Assert.GreaterOrEqual(doc.HostilityScore, 0f);
            Assert.LessOrEqual(doc.HostilityScore, 100f);
        }

        // ─── Behavior override derivation ─────────────────────────────────────

        [Test]
        public void LowTrust_SetsRefuseTrade()
        {
            var doc = new NpcMemoryDocument
            {
                Id = "a_p", NpcId = "a", PlayerId = "p",
                TrustScore    = 10f,
                HostilityScore = 20f,
                SalientEvents = new()
            };
            _calc.UpdateBehaviorOverrides(doc);
            Assert.IsTrue(doc.BehaviorOverrides.RefuseTrade);
        }

        [Test]
        public void HighTrustAndGratitude_SetsGiveDiscount()
        {
            var doc = new NpcMemoryDocument
            {
                Id = "a_p", NpcId = "a", PlayerId = "p",
                TrustScore      = 85f,
                GratitudeScore  = 50f,
                SalientEvents   = new()
            };
            _calc.UpdateBehaviorOverrides(doc);
            Assert.IsTrue(doc.BehaviorOverrides.GiveDiscount);
        }

        // ─── Decay curve ──────────────────────────────────────────────────────

        [Test]
        public void DecayedEvent_HasLessWeight_ThanFreshEvent()
        {
            var now  = System.DateTimeOffset.UtcNow;
            var evt  = new SalientEvent
            {
                EmotionalWeight = -30f,
                DecayRate       = 0.002f,
                Timestamp       = now.AddHours(-500)
            };
            var decayed = evt.CurrentWeight(now);
            Assert.Less(System.Math.Abs(decayed), 30f);
        }

        [Test]
        public void VeryOldEvent_WithSlowDecay_RetainsSomeWeight()
        {
            var now = System.DateTimeOffset.UtcNow;
            var evt = new SalientEvent
            {
                EmotionalWeight = -30f,
                DecayRate       = 0.0005f,  // Endangered-style
                Timestamp       = now.AddHours(-720)   // 30 days
            };
            var remaining = evt.CurrentWeight(now);
            Assert.Greater(System.Math.Abs(remaining), 1f,
                "Life-or-death event should retain meaningful weight after 30 days");
        }
    }
}
