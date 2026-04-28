using NUnit.Framework;
using NpcSoulEngine.Runtime.Inference;
using NpcSoulEngine.Runtime.Models;

namespace NpcSoulEngine.Tests
{
    /// <summary>
    /// Tests the rule-based fallback path (no ONNX model loaded in tests).
    /// All scores must be in [0, 1].
    /// </summary>
    [TestFixture]
    public sealed class SignificanceScorerTests
    {
        private SignificanceScorer _scorer;

        [SetUp] public void SetUp() => _scorer = new SignificanceScorer();
        [TearDown] public void TearDown() => _scorer?.Dispose();

        // ─── Range checks ─────────────────────────────────────────────────────

        [TestCase(ActionType.Betrayal,          "High",         0.8f)]
        [TestCase(ActionType.Endangered,        "LifeOrDeath",  0.9f)]
        [TestCase(ActionType.NeutralInteraction,"Low",          0.0f)]
        [TestCase(ActionType.Kindness,          "Medium",       0.3f)]
        public void Score_IsClamped0To1(ActionType action, string stakes, float minExpected)
        {
            var score = _scorer.Score(action, stakes, 0f);
            Assert.GreaterOrEqual(score, 0f, "Score below 0");
            Assert.LessOrEqual(score, 1f, "Score above 1");
            Assert.GreaterOrEqual(score, minExpected,
                $"Expected {action}/{stakes} to score at least {minExpected}, got {score}");
        }

        // ─── Ordering ─────────────────────────────────────────────────────────

        [Test]
        public void LifeOrDeath_ScoredHigherThanLow_ForSameAction()
        {
            var low  = _scorer.Score(ActionType.Betrayal, "Low",         0f);
            var high = _scorer.Score(ActionType.Betrayal, "LifeOrDeath", 0f);
            Assert.Greater(high, low);
        }

        [Test]
        public void Betrayal_ScoredHigherThan_NeutralInteraction()
        {
            var betrayal = _scorer.Score(ActionType.Betrayal,          "Medium", 0f);
            var neutral  = _scorer.Score(ActionType.NeutralInteraction, "Medium", 0f);
            Assert.Greater(betrayal, neutral);
        }

        [Test]
        public void Endangered_MaximumScore()
        {
            var score = _scorer.Score(ActionType.Endangered, "LifeOrDeath", 1.0f);
            Assert.AreEqual(1f, score, 0.0001f);
        }

        // ─── Publicness contribution ──────────────────────────────────────────

        [Test]
        public void PublicAction_ScoredHigherThan_PrivateAction()
        {
            var priv   = _scorer.Score(ActionType.TradeDeceptive, "Medium", 0f);
            var public_ = _scorer.Score(ActionType.TradeDeceptive, "Medium", 1f);
            Assert.GreaterOrEqual(public_, priv);
        }

        // ─── Threshold filtering ──────────────────────────────────────────────

        [Test]
        public void NeutralLow_BelowDefaultThreshold()
        {
            var score = _scorer.Score(ActionType.NeutralInteraction, "Low", 0f);
            Assert.Less(score, 0.3f, "Neutral/Low should fall below the default significance threshold");
        }
    }
}
