using NUnit.Framework;
using NpcSoulEngine.Functions.Models;
using NpcSoulEngine.Functions.Services;

namespace NpcSoulEngine.Functions.Tests;

[TestFixture]
public sealed class MemoryDecayTests
{
    // ─── SalientEvent.CurrentWeight (Ebbinghaus formula) ─────────────────────

    [Test]
    public void CurrentWeight_AtTimestamp_EqualsOriginalWeight()
    {
        var evt = new SalientEvent { EmotionalWeight = 20f, DecayRate = 0.003f, Timestamp = DateTimeOffset.UtcNow };
        Assert.AreEqual(20f, evt.CurrentWeight(DateTimeOffset.UtcNow), 0.05f);
    }

    [Test]
    public void CurrentWeight_After24Hours_MatchesFormula()
    {
        // weight * e^(-decayRate * hours)
        var now = DateTimeOffset.UtcNow;
        var evt = new SalientEvent { EmotionalWeight = 20f, DecayRate = 0.003f, Timestamp = now.AddHours(-24) };
        var expected = 20f * MathF.Exp(-0.003f * 24f);
        Assert.AreEqual(expected, evt.CurrentWeight(now), 0.01f);
    }

    [Test]
    public void CurrentWeight_Betrayal_RetainsOver40PercentAfter30Days()
    {
        // Betrayal decayRate = 0.001; e^(-0.001 * 720) = e^(-0.72) ≈ 0.487
        var now = DateTimeOffset.UtcNow;
        var evt = new SalientEvent { EmotionalWeight = -30f, DecayRate = 0.001f, Timestamp = now.AddHours(-720) };
        var ratio = Math.Abs(evt.CurrentWeight(now)) / Math.Abs(evt.EmotionalWeight);
        Assert.Greater(ratio, 0.4f);
    }

    [Test]
    public void CurrentWeight_NeutralInteraction_AlmostGoneAfter200Hours()
    {
        // NeutralInteraction decayRate = 0.02; e^(-0.02 * 200) = e^(-4) ≈ 0.018
        var now = DateTimeOffset.UtcNow;
        var evt = new SalientEvent { EmotionalWeight = 1f, DecayRate = 0.02f, Timestamp = now.AddHours(-200) };
        var ratio = Math.Abs(evt.CurrentWeight(now)) / Math.Abs(evt.EmotionalWeight);
        Assert.Less(ratio, 0.05f);
    }

    [Test]
    public void CurrentWeight_NegativeWeight_StaysNegative()
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new SalientEvent { EmotionalWeight = -20f, DecayRate = 0.003f, Timestamp = now.AddHours(-10) };
        Assert.Less(evt.CurrentWeight(now), 0f);
    }

    // ─── GetEligibleEventIds ─────────────────────────────────────────────────

    [Test]
    public void GetEligibleEventIds_FadedEvent_IsEligible()
    {
        var doc = MakeDoc();
        // decayRate=0.1, after 100h: e^(-10) ≈ 0.0000454 → ratio << 5%
        doc.SalientEvents.Add(new SalientEvent
        {
            EmotionalWeight = 15f,
            DecayRate       = 0.1f,
            Timestamp       = DateTimeOffset.UtcNow.AddHours(-100)
        });

        var eligible = MemoryDecayService.GetEligibleEventIds(doc, DateTimeOffset.UtcNow);
        Assert.AreEqual(1, eligible.Count);
    }

    [Test]
    public void GetEligibleEventIds_FreshEvent_IsNotEligible()
    {
        var doc = MakeDoc();
        doc.SalientEvents.Add(new SalientEvent
        {
            EmotionalWeight = 15f,
            DecayRate       = 0.003f,
            Timestamp       = DateTimeOffset.UtcNow.AddHours(-1)
        });

        var eligible = MemoryDecayService.GetEligibleEventIds(doc, DateTimeOffset.UtcNow);
        Assert.AreEqual(0, eligible.Count);
    }

    [Test]
    public void GetEligibleEventIds_AlreadyConsolidated_Excluded()
    {
        var doc = MakeDoc();
        doc.SalientEvents.Add(new SalientEvent
        {
            EmotionalWeight = 15f,
            DecayRate       = 0.1f,
            Timestamp       = DateTimeOffset.UtcNow.AddHours(-100),
            IsConsolidated  = true
        });

        var eligible = MemoryDecayService.GetEligibleEventIds(doc, DateTimeOffset.UtcNow);
        Assert.AreEqual(0, eligible.Count);
    }

    [Test]
    public void GetEligibleEventIds_Mixed_ReturnsOnlyFaded()
    {
        var doc = MakeDoc();
        doc.SalientEvents.Add(new SalientEvent { EmotionalWeight = 15f, DecayRate = 0.1f,   Timestamp = DateTimeOffset.UtcNow.AddHours(-100) });
        doc.SalientEvents.Add(new SalientEvent { EmotionalWeight = 15f, DecayRate = 0.003f, Timestamp = DateTimeOffset.UtcNow.AddHours(-1) });

        var eligible = MemoryDecayService.GetEligibleEventIds(doc, DateTimeOffset.UtcNow);
        Assert.AreEqual(1, eligible.Count);
    }

    [Test]
    public void GetEligibleEventIds_EmptyDocument_ReturnsEmpty()
    {
        var eligible = MemoryDecayService.GetEligibleEventIds(MakeDoc(), DateTimeOffset.UtcNow);
        Assert.AreEqual(0, eligible.Count);
    }

    // ─── ConsolidationThreshold and ConsolidationBatchSize constants ──────────

    [Test]
    public void ConsolidationThreshold_Is5Percent()
    {
        Assert.AreEqual(0.05f, MemoryDecayService.ConsolidationThreshold, 0.0001f);
    }

    [Test]
    public void ConsolidationBatchSize_Is20()
    {
        Assert.AreEqual(20, MemoryDecayService.ConsolidationBatchSize);
    }

    // ─── EmotionalWeightCalculator.RecalculateScores — all four scores ────────

    [Test]
    public void RecalculateScores_PositiveEvent_IncreasesTrust()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.SalientEvents.Add(new SalientEvent { EmotionalWeight = 25f, DecayRate = 0.002f, Timestamp = DateTimeOffset.UtcNow });

        calc.RecalculateScores(doc);
        Assert.Greater(doc.TrustScore, 50f);
    }

    [Test]
    public void RecalculateScores_ThreatEvent_UpdatesFearScore()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.SalientEvents.Add(new SalientEvent
        {
            ActionType      = ActionType.Threat,
            EmotionalWeight = -20f,
            DecayRate       = 0.002f,
            Timestamp       = DateTimeOffset.UtcNow
        });

        calc.RecalculateScores(doc);
        Assert.Greater(doc.FearScore, 0f);
    }

    [Test]
    public void RecalculateScores_RescuedEvent_UpdatesGratitudeScore()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.SalientEvents.Add(new SalientEvent
        {
            ActionType      = ActionType.Rescued,
            EmotionalWeight = 35f,   // > 15 triggers gratitude
            DecayRate       = 0.0008f,
            Timestamp       = DateTimeOffset.UtcNow
        });

        calc.RecalculateScores(doc);
        Assert.Greater(doc.GratitudeScore, 0f);
    }

    [Test]
    public void RecalculateScores_SetsCurrentEmotion()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.SalientEvents.Add(new SalientEvent { EmotionalWeight = 30f, DecayRate = 0.002f, Timestamp = DateTimeOffset.UtcNow });

        calc.RecalculateScores(doc);
        Assert.IsNotNull(doc.CurrentEmotion);
        Assert.IsNotEmpty(doc.CurrentEmotion.Primary);
    }

    // ─── UpdateBehaviorOverrides ──────────────────────────────────────────────

    [Test]
    public void BehaviorOverrides_LowTrust_SetRefuseTrade()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.TrustScore = 15f;  // < 20

        calc.UpdateBehaviorOverrides(doc);
        Assert.IsTrue(doc.BehaviorOverrides.RefuseTrade);
    }

    [Test]
    public void BehaviorOverrides_HighHostility_AlertsGuards()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.HostilityScore = 80f;  // > 75
        doc.TrustScore     = 50f;

        calc.UpdateBehaviorOverrides(doc);
        Assert.IsTrue(doc.BehaviorOverrides.AlertGuards);
    }

    [Test]
    public void BehaviorOverrides_HighTrustAndGratitude_GivesDiscount()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.TrustScore     = 85f;  // > 80
        doc.GratitudeScore = 45f;  // > 40

        calc.UpdateBehaviorOverrides(doc);
        Assert.IsTrue(doc.BehaviorOverrides.GiveDiscount);
    }

    [Test]
    public void BehaviorOverrides_HighFear_SeeksHelp()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.FearScore  = 85f;  // > 80
        doc.TrustScore = 50f;

        calc.UpdateBehaviorOverrides(doc);
        Assert.IsTrue(doc.BehaviorOverrides.SeekHelp);
        Assert.IsTrue(doc.BehaviorOverrides.AvoidPlayer);  // fear > 50
    }

    [Test]
    public void BehaviorOverrides_NeutralScores_NoOverrides()
    {
        var calc = new EmotionalWeightCalculator();
        var doc  = MakeDoc();
        doc.TrustScore     = 50f;
        doc.FearScore      = 0f;
        doc.HostilityScore = 0f;
        doc.GratitudeScore = 0f;

        calc.UpdateBehaviorOverrides(doc);
        Assert.IsFalse(doc.BehaviorOverrides.RefuseTrade);
        Assert.IsFalse(doc.BehaviorOverrides.AlertGuards);
        Assert.IsFalse(doc.BehaviorOverrides.GiveDiscount);
        Assert.IsFalse(doc.BehaviorOverrides.AvoidPlayer);
        Assert.IsFalse(doc.BehaviorOverrides.SeekHelp);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static NpcMemoryDocument MakeDoc(string npcId = "npc_a", string playerId = "player_1") => new()
    {
        Id       = NpcMemoryDocument.MakeId(npcId, playerId),
        NpcId    = npcId,
        PlayerId = playerId
    };
}
