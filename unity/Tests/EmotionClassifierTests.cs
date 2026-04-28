using NUnit.Framework;
using NpcSoulEngine.Runtime.Inference;

namespace NpcSoulEngine.Tests
{
    /// <summary>
    /// Tests the rule-based fallback path (no ONNX model loaded in tests).
    /// Parameters: (trust, fear, hostility, respect) — all normalized 0–1.
    /// </summary>
    [TestFixture]
    public sealed class EmotionClassifierTests
    {
        // ─── Label routing ────────────────────────────────────────────────────

        [Test]
        public void HighHostility_ReturnsHostileAggressive()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.5f, 0.1f, 0.9f, 0.5f);
            Assert.AreEqual("hostile_aggressive", v.primary);
        }

        [Test]
        public void HighFear_NoHostility_ReturnsFearfulTense()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.4f, 0.8f, 0.1f, 0.4f);
            Assert.AreEqual("fearful_tense", v.primary);
        }

        [Test]
        public void HighTrust_ReturnsWarmContent()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.9f, 0.0f, 0.0f, 0.8f);
            Assert.AreEqual("warm_content", v.primary);
        }

        [Test]
        public void ModerateTrust_ReturnsFriendlyAlert()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.7f, 0.0f, 0.0f, 0.5f);
            Assert.AreEqual("friendly_alert", v.primary);
        }

        [Test]
        public void ModerateHostility_ReturnsSuspiciousWary()
        {
            // hostility=0.5 clears the 0.75 threshold but hits the 0.40 threshold
            var v = EmotionClassifier.RuleBasedClassify(0.5f, 0.1f, 0.5f, 0.5f);
            Assert.AreEqual("suspicious_wary", v.primary);
        }

        [Test]
        public void VeryLowTrust_ReturnsColdDismissive()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.1f, 0.0f, 0.1f, 0.1f);
            Assert.AreEqual("cold_dismissive", v.primary);
        }

        [Test]
        public void SlightlyLowTrust_ReturnsCautiousWarmth()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.28f, 0.0f, 0.0f, 0.3f);
            Assert.AreEqual("cautious_warmth", v.primary);
        }

        [Test]
        public void NeutralScores_ReturnsNeutral()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.5f, 0.0f, 0.0f, 0.5f);
            Assert.AreEqual("neutral", v.primary);
        }

        // ─── Priority ordering ────────────────────────────────────────────────

        [Test]
        public void Hostility_TakesPriorityOver_HighFear()
        {
            // Both above their thresholds — hostility (>0.75) is checked first
            var v = EmotionClassifier.RuleBasedClassify(0.5f, 0.7f, 0.8f, 0.5f);
            Assert.AreEqual("hostile_aggressive", v.primary);
        }

        // ─── Intensity range ──────────────────────────────────────────────────

        [Test]
        public void Intensity_IsClamped0To1_ForAllBranches()
        {
            float[][] inputs =
            {
                new[] { 1.0f, 0.0f, 0.0f, 1.0f },  // warm_content
                new[] { 0.0f, 1.0f, 0.0f, 0.0f },  // fearful_tense
                new[] { 0.0f, 0.0f, 1.0f, 0.0f },  // hostile_aggressive
                new[] { 0.0f, 0.0f, 0.0f, 0.0f },  // cold_dismissive
                new[] { 0.5f, 0.0f, 0.0f, 0.5f },  // neutral
            };

            foreach (var inp in inputs)
            {
                var v = EmotionClassifier.RuleBasedClassify(inp[0], inp[1], inp[2], inp[3]);
                Assert.GreaterOrEqual(v.intensity, 0f, $"intensity below 0 for trust={inp[0]}");
                Assert.LessOrEqual(v.intensity,   1f, $"intensity above 1 for trust={inp[0]}");
            }
        }

        // ─── PAD vector signs ─────────────────────────────────────────────────

        [Test]
        public void HighTrust_ProducesPositiveValence()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.9f, 0.0f, 0.0f, 0.8f);
            Assert.Greater(v.valence, 0f);
        }

        [Test]
        public void HighHostility_ProducesNegativeValence()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.3f, 0.1f, 0.9f, 0.3f);
            Assert.Less(v.valence, 0f);
        }

        [Test]
        public void HighFear_ProducesHighArousal()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.4f, 0.9f, 0.0f, 0.4f);
            Assert.Greater(v.arousal, 0.5f);
        }

        [Test]
        public void HighTrust_LowFear_ProducesPositiveDominance()
        {
            var v = EmotionClassifier.RuleBasedClassify(0.9f, 0.0f, 0.0f, 0.8f);
            Assert.Greater(v.dominance, 0f);
        }
    }
}
