using System;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;
using Unity.Sentis;

namespace NpcSoulEngine.Runtime.Inference
{
    /// <summary>
    /// On-device significance scorer using Unity Sentis (ONNX).
    /// Input: [actionTypeEncoded, stakesEncoded, publicness, currentTrustDelta]
    /// Output: significance score 0–1.
    ///
    /// If the ONNX model is not present (e.g. editor without StreamingAssets),
    /// falls back to a deterministic rule-based scorer that never allocates.
    ///
    /// Model path: StreamingAssets/Models/significance_scorer.onnx
    /// </summary>
    public sealed class SignificanceScorer : IDisposable
    {
        private Model _runtimeModel;
        private Worker _worker;
        private Tensor<float> _inputTensor;
        private bool _modelLoaded;

        private const string ModelPath = "Models/significance_scorer.onnx";
        private const int InputSize = 4;

        public SignificanceScorer()
        {
            TryLoadModel();
        }

        private void TryLoadModel()
        {
            try
            {
                var path = System.IO.Path.Combine(Application.streamingAssetsPath, ModelPath);
                if (!System.IO.File.Exists(path)) return;

                _runtimeModel  = ModelLoader.Load(path);
                _worker        = new Worker(_runtimeModel, BackendType.CPU);
                _inputTensor   = new Tensor<float>(new TensorShape(1, InputSize));
                _modelLoaded   = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcSoulEngine] Sentis model load failed: {ex.Message}. Using rule-based scorer.");
            }
        }

        /// <summary>
        /// Returns a significance score (0–1) for the given action.
        /// Thread-safe: only call from the main thread (Sentis requirement).
        /// </summary>
        public float Score(ActionType actionType, string stakesString, float publicness)
        {
            var stakes = ParseStakes(stakesString);

            if (!_modelLoaded)
                return RuleBasedScore(actionType, stakes, publicness);

            try
            {
                _inputTensor[0] = EncodeActionType(actionType);
                _inputTensor[1] = EncodeStakes(stakes);
                _inputTensor[2] = publicness;
                _inputTensor[3] = 0f;  // trust delta — not known at record time

                _worker.Schedule(_inputTensor);
                var output = (_worker.PeekOutput() as Tensor<float>);
                var score  = output?[0] ?? RuleBasedScore(actionType, stakes, publicness);
                return Mathf.Clamp01(score);
            }
            catch
            {
                return RuleBasedScore(actionType, stakes, publicness);
            }
        }

        // ─── Fallback: deterministic rule-based scoring ───────────────────────

        private static float RuleBasedScore(ActionType actionType, StakeLevel stakes, float publicness)
        {
            var baseScore = actionType switch
            {
                ActionType.Betrayal          => 0.95f,
                ActionType.Endangered        => 1.00f,
                ActionType.Rescued           => 0.90f,
                ActionType.PromiseBroken     => 0.85f,
                ActionType.PromiseKept       => 0.75f,
                ActionType.CombatInitiated   => 0.80f,
                ActionType.TradeDeceptive    => 0.78f,
                ActionType.Threat            => 0.70f,
                ActionType.Gift              => 0.65f,
                ActionType.QuestHelped       => 0.70f,
                ActionType.QuestAbandoned    => 0.65f,
                ActionType.Kindness          => 0.55f,
                ActionType.Insult            => 0.50f,
                ActionType.Compliment        => 0.35f,
                ActionType.TradeFair         => 0.30f,
                ActionType.Trespass          => 0.40f,
                ActionType.WitnessedViolence => 0.45f,
                ActionType.Bribe             => 0.40f,
                ActionType.CombatDefended    => 0.35f,
                ActionType.NeutralInteraction => 0.10f,
                _                            => 0.20f
            };

            var stakesMultiplier = stakes switch
            {
                StakeLevel.LifeOrDeath => 1.5f,
                StakeLevel.High        => 1.2f,
                StakeLevel.Medium      => 1.0f,
                _                      => 0.7f
            };

            return Mathf.Clamp01(baseScore * stakesMultiplier + publicness * 0.1f);
        }

        // ─── Feature encoding ─────────────────────────────────────────────────

        private static float EncodeActionType(ActionType t) => (float)t / 20f;
        private static float EncodeStakes(StakeLevel s) => (float)s / 4f;

        private static StakeLevel ParseStakes(string s) => s?.ToLowerInvariant() switch
        {
            "lifeordeath" => StakeLevel.LifeOrDeath,
            "high"        => StakeLevel.High,
            "medium"      => StakeLevel.Medium,
            _             => StakeLevel.Low
        };

        public void Dispose()
        {
            _worker?.Dispose();
            _inputTensor?.Dispose();
            _runtimeModel = null;
            _modelLoaded  = false;
        }
    }
}
