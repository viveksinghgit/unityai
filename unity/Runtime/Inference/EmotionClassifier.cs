using System;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;
using Unity.Sentis;

namespace NpcSoulEngine.Runtime.Inference
{
    /// <summary>
    /// On-device emotion classifier using Unity Sentis (ONNX).
    /// Input:  [trustNorm, fearNorm, hostilityNorm, respectNorm] — all in [0, 1]
    /// Output: softmax over 9 emotion classes → argmax gives primary emotion label.
    ///
    /// Used to pre-warm EmotionAnimationController before the GPT response arrives,
    /// so the NPC's face already reflects their emotional state mid-response.
    ///
    /// Falls back to a deterministic rule-based classifier when the model is absent.
    /// Model path: StreamingAssets/Models/emotion_classifier.onnx
    /// </summary>
    public sealed class EmotionClassifier : IDisposable
    {
        private Model _runtimeModel;
        private Worker _worker;
        private Tensor<float> _inputTensor;
        private bool _modelLoaded;

        private const string ModelPath = "Models/emotion_classifier.onnx";
        private const int InputSize  = 4;
        private const int OutputSize = 9;

        // Indices match EmotionAnimationController.EmotionIndex
        private static readonly string[] Labels =
        {
            "neutral",          // 0
            "warm_content",     // 1
            "joyful_excited",   // 2
            "cautious_warmth",  // 3
            "friendly_alert",   // 4
            "cold_dismissive",  // 5
            "hostile_aggressive", // 6
            "fearful_tense",    // 7
            "suspicious_wary"   // 8
        };

        public EmotionClassifier() => TryLoadModel();

        private void TryLoadModel()
        {
            try
            {
                var path = System.IO.Path.Combine(Application.streamingAssetsPath, ModelPath);
                if (!System.IO.File.Exists(path)) return;

                _runtimeModel = ModelLoader.Load(path);
                _worker       = new Worker(_runtimeModel, BackendType.CPU);
                _inputTensor  = new Tensor<float>(new TensorShape(1, InputSize));
                _modelLoaded  = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcSoulEngine] EmotionClassifier model load failed: {ex.Message}. Using rule-based classifier.");
            }
        }

        /// <summary>
        /// Returns an EmotionVector pre-computed from current memory scores.
        /// Call on the main thread only (Sentis requirement).
        /// </summary>
        public EmotionVector Classify(NpcMemoryState memory)
        {
            var trust     = memory.trustScore     / 100f;
            var fear      = memory.fearScore      / 100f;
            var hostility = memory.hostilityScore / 100f;
            var respect   = memory.respectScore   / 100f;

            if (!_modelLoaded)
                return RuleBasedClassify(trust, fear, hostility, respect);

            try
            {
                _inputTensor[0] = trust;
                _inputTensor[1] = fear;
                _inputTensor[2] = hostility;
                _inputTensor[3] = respect;

                _worker.Schedule(_inputTensor);
                var output = _worker.PeekOutput() as Tensor<float>;
                if (output == null)
                    return RuleBasedClassify(trust, fear, hostility, respect);

                var maxIdx = 0;
                var maxVal = output[0];
                for (var i = 1; i < OutputSize; i++)
                {
                    if (output[i] > maxVal) { maxVal = output[i]; maxIdx = i; }
                }

                return MakeVector(Labels[maxIdx], Mathf.Clamp01(maxVal), trust, fear, hostility);
            }
            catch
            {
                return RuleBasedClassify(trust, fear, hostility, respect);
            }
        }

        // Exposed internal for testing — pure, no side effects
        internal static EmotionVector RuleBasedClassify(float trust, float fear, float hostility, float respect)
        {
            string label;
            float intensity;

            if      (hostility > 0.75f) { label = "hostile_aggressive"; intensity = hostility; }
            else if (fear > 0.60f)      { label = "fearful_tense";       intensity = fear; }
            else if (trust > 0.80f)     { label = "warm_content";        intensity = trust * 0.8f; }
            else if (trust > 0.60f)     { label = "friendly_alert";      intensity = trust * 0.6f; }
            else if (hostility > 0.40f) { label = "suspicious_wary";     intensity = hostility; }
            else if (trust < 0.20f)     { label = "cold_dismissive";     intensity = 1f - trust; }
            else if (trust < 0.35f)     { label = "cautious_warmth";     intensity = 0.4f; }
            else                        { label = "neutral";              intensity = 0f; }

            return MakeVector(label, Mathf.Clamp01(intensity), trust, fear, hostility);
        }

        private static EmotionVector MakeVector(string label, float intensity, float trust, float fear, float hostility) =>
            new EmotionVector
            {
                primary   = label,
                intensity = intensity,
                valence   = trust - hostility,
                arousal   = Mathf.Max(fear, hostility),
                dominance = trust - fear
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
