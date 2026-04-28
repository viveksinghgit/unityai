using NUnit.Framework;
using NpcSoulEngine.Functions.Models;
using NpcSoulEngine.Functions.Services;

namespace NpcSoulEngine.Functions.Tests;

[TestFixture]
public sealed class ArchetypeClassifierTests
{
    // ─── Feature extraction ───────────────────────────────────────────────────

    [Test]
    public void FromDocuments_AveragesEmotionalScores()
    {
        var docs = new List<NpcMemoryDocument>
        {
            MakeDoc(trust: 80f, fear: 10f, hostility: 5f,  respect: 70f),
            MakeDoc(trust: 60f, fear: 20f, hostility: 15f, respect: 50f),
        };
        var f = PlayerFeatureVector.FromDocuments(docs, null);

        Assert.AreEqual(0.70f, f.AvgTrust,     delta: 0.001f);
        Assert.AreEqual(0.15f, f.AvgFear,      delta: 0.001f);
        Assert.AreEqual(0.10f, f.AvgHostility, delta: 0.001f);
        Assert.AreEqual(0.60f, f.AvgRespect,   delta: 0.001f);
    }

    [Test]
    public void FromDocuments_EmptyList_ReturnsZeroVector()
    {
        var f = PlayerFeatureVector.FromDocuments(Array.Empty<NpcMemoryDocument>(), null);
        Assert.AreEqual(0f, f.AvgTrust);
        Assert.AreEqual(0f, f.AvgHostility);
    }

    [Test]
    public void FromDocuments_NullBehavior_UsesBehaviorDefaults()
    {
        var docs = new List<NpcMemoryDocument> { MakeDoc() };
        var f = PlayerFeatureVector.FromDocuments(docs, null);
        Assert.AreEqual(0f, f.CombatInitiationRate);
        Assert.AreEqual(0f, f.TradeDeceptionRate);
    }

    [Test]
    public void FromDocuments_CapsTimeBetweenActionsAt3600()
    {
        var docs     = new List<NpcMemoryDocument> { MakeDoc() };
        var behavior = new BehaviorFeatures { AverageTimeBetweenActions = 7200f };
        var f = PlayerFeatureVector.FromDocuments(docs, behavior);
        Assert.AreEqual(1.0f, f.AvgTimeBetweenActions, delta: 0.001f);
    }

    [Test]
    public void FromDocuments_ClampsScoresAbove100()
    {
        var docs = new List<NpcMemoryDocument> { MakeDoc(trust: 150f) };
        var f = PlayerFeatureVector.FromDocuments(docs, null);
        Assert.LessOrEqual(f.AvgTrust, 1.0f);
    }

    [Test]
    public void ToArray_Has10Elements()
    {
        Assert.AreEqual(10, new PlayerFeatureVector().ToArray().Length);
    }

    [Test]
    public void ToArray_ElementsMatchProperties()
    {
        var f = new PlayerFeatureVector
        {
            AvgTrust = 0.7f, AvgFear = 0.1f, AvgHostility = 0.2f, AvgRespect = 0.6f,
            CombatInitiationRate = 0.3f, DialogueChoiceAggression = 0.4f,
            TradeDeceptionRate = 0.05f, PromiseBrokenRate = 0.1f,
            AvgTimeBetweenActions = 0.5f, ReputationAwarenessScore = 0.8f,
        };
        var a = f.ToArray();
        Assert.AreEqual(f.AvgTrust,                 a[0], delta: 0.0001f);
        Assert.AreEqual(f.ReputationAwarenessScore, a[9], delta: 0.0001f);
    }

    // ─── Rule-based classification — label routing ────────────────────────────

    [Test]
    public void RuleBasedClassify_HighCombatRate_ReturnsAggressor()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(
            new PlayerFeatureVector { CombatInitiationRate = 0.7f });
        Assert.AreEqual("aggressor", r.Archetype);
    }

    [Test]
    public void RuleBasedClassify_HighHostilityAndDialogueAggression_ReturnsAggressor()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(
            new PlayerFeatureVector { AvgHostility = 0.7f, DialogueChoiceAggression = 0.6f });
        Assert.AreEqual("aggressor", r.Archetype);
    }

    [Test]
    public void RuleBasedClassify_HighTradeDeception_ReturnsTrickster()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(
            new PlayerFeatureVector { TradeDeceptionRate = 0.6f });
        Assert.AreEqual("trickster", r.Archetype);
    }

    [Test]
    public void RuleBasedClassify_HighBrokenPromises_ReturnsTrickster()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(
            new PlayerFeatureVector { PromiseBrokenRate = 0.6f });
        Assert.AreEqual("trickster", r.Archetype);
    }

    [Test]
    public void RuleBasedClassify_HighTrustLowHostilityHighReputation_ReturnsHero()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(new PlayerFeatureVector
        {
            AvgTrust = 0.85f, AvgHostility = 0.1f, ReputationAwarenessScore = 0.8f,
        });
        Assert.AreEqual("hero", r.Archetype);
    }

    [Test]
    public void RuleBasedClassify_LowAggressionHighReputation_ReturnsDiplomat()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(new PlayerFeatureVector
        {
            DialogueChoiceAggression = 0.1f, ReputationAwarenessScore = 0.75f,
        });
        Assert.AreEqual("diplomat", r.Archetype);
    }

    [Test]
    public void RuleBasedClassify_ModerateTrustLowHostility_ReturnsBenefactor()
    {
        // trust > 0.65 but NOT hero threshold (0.75), hostility < 0.30
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(new PlayerFeatureVector
        {
            AvgTrust = 0.70f, AvgHostility = 0.1f,
        });
        Assert.AreEqual("benefactor", r.Archetype);
    }

    [Test]
    public void RuleBasedClassify_BalancedScores_ReturnsNeutral()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(new PlayerFeatureVector
        {
            AvgTrust = 0.5f, AvgHostility = 0.3f,
        });
        Assert.AreEqual("neutral", r.Archetype);
    }

    // ─── Priority ordering ────────────────────────────────────────────────────

    [Test]
    public void RuleBasedClassify_Aggressor_TakesPriorityOverTrickster()
    {
        // combat > 0.55 and deception > 0.45 — aggressor is checked first
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(new PlayerFeatureVector
        {
            CombatInitiationRate = 0.6f, TradeDeceptionRate = 0.5f,
        });
        Assert.AreEqual("aggressor", r.Archetype);
    }

    [Test]
    public void RuleBasedClassify_Trickster_TakesPriorityOverDiplomat()
    {
        // deception > 0.45 but also low aggression + high reputation
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(new PlayerFeatureVector
        {
            TradeDeceptionRate = 0.5f,
            DialogueChoiceAggression = 0.1f,
            ReputationAwarenessScore = 0.8f,
        });
        Assert.AreEqual("trickster", r.Archetype);
    }

    // ─── Confidence and scores integrity ─────────────────────────────────────

    [Test]
    public void RuleBasedClassify_Confidence_IsInUnitRange_ForAllBranches()
    {
        var fixtures = new[]
        {
            new PlayerFeatureVector { CombatInitiationRate = 0.8f },
            new PlayerFeatureVector { TradeDeceptionRate = 0.7f },
            new PlayerFeatureVector { AvgTrust = 0.9f, AvgHostility = 0.1f, ReputationAwarenessScore = 0.9f },
            new PlayerFeatureVector { DialogueChoiceAggression = 0.1f, ReputationAwarenessScore = 0.8f },
            new PlayerFeatureVector { AvgTrust = 0.7f, AvgHostility = 0.1f },
            new PlayerFeatureVector { AvgTrust = 0.5f },
        };
        foreach (var f in fixtures)
        {
            var r = AzureMLArchetypeClassifier.RuleBasedClassify(f);
            Assert.GreaterOrEqual(r.Confidence, 0f, $"below 0 for {r.Archetype}");
            Assert.LessOrEqual(r.Confidence,    1f, $"above 1 for {r.Archetype}");
        }
    }

    [Test]
    public void RuleBasedClassify_Scores_SumToOne()
    {
        var r   = AzureMLArchetypeClassifier.RuleBasedClassify(
            new PlayerFeatureVector { AvgTrust = 0.8f, AvgHostility = 0.1f });
        var sum = r.Scores.Values.Sum();
        Assert.AreEqual(1f, sum, delta: 0.005f);
    }

    [Test]
    public void RuleBasedClassify_Scores_ContainAllLabels()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(new PlayerFeatureVector());
        CollectionAssert.AreEquivalent(AzureMLArchetypeClassifier.Labels, r.Scores.Keys);
    }

    [Test]
    public void RuleBasedClassify_WinningLabel_HasHighestScore()
    {
        var r = AzureMLArchetypeClassifier.RuleBasedClassify(
            new PlayerFeatureVector { CombatInitiationRate = 0.9f });
        var maxLabel = r.Scores.MaxBy(kv => kv.Value).Key;
        Assert.AreEqual(r.Archetype, maxLabel);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static NpcMemoryDocument MakeDoc(
        float trust = 50f, float fear = 0f,
        float hostility = 0f, float respect = 50f) =>
        new()
        {
            Id       = Guid.NewGuid().ToString("N"),
            NpcId    = "npc_test",
            PlayerId = "player_test",
            TrustScore     = trust,
            FearScore      = fear,
            HostilityScore = hostility,
            RespectScore   = respect,
        };
}
