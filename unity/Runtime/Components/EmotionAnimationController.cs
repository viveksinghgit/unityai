using System.Collections.Generic;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace NpcSoulEngine.Runtime.Components
{
    /// <summary>
    /// Drives facial blend shapes and rig weights from the NPC's emotional state.
    /// Three additive rig layers: brow, mouth/lip-sync, micro-expression.
    /// Transitions are smoothed over 0.3–0.8s to avoid jarring snaps.
    /// </summary>
    public sealed class EmotionAnimationController : MonoBehaviour
    {
        [Header("Skinned Mesh")]
        [SerializeField] private SkinnedMeshRenderer faceRenderer;

        [Header("Blend Shape Indices (set in Inspector)")]
        [SerializeField] private int blendSmile     = 0;
        [SerializeField] private int blendFrown     = 1;
        [SerializeField] private int blendBrowRaise = 2;
        [SerializeField] private int blendBrowFurrow = 3;
        [SerializeField] private int blendNostrilFlare = 4;
        [SerializeField] private int blendEyeSquint = 5;

        [Header("Rig Layers (Animation Rigging)")]
        [SerializeField] private RigBuilder rigBuilder;
        [SerializeField] private Rig browRig;
        [SerializeField] private Rig mouthRig;
        [SerializeField] private Rig microExpressionRig;

        [Header("Animator (body state)")]
        [SerializeField] private Animator bodyAnimator;

        [Header("Transition")]
        [SerializeField] private float transitionSpeed = 2f;  // blend speed per second

        // Target blend shape weights (set by emotion updates, lerped in Update)
        private float _targetSmile;
        private float _targetFrown;
        private float _targetBrowRaise;
        private float _targetBrowFurrow;
        private float _targetNostrilFlare;
        private float _targetEyeSquint;

        // Current weights (lerped towards targets)
        private float _curSmile;
        private float _curFrown;
        private float _curBrowRaise;
        private float _curBrowFurrow;
        private float _curNostrilFlare;
        private float _curEyeSquint;

        // Mouth rig — driven exclusively by TTS word boundaries
        private float _targetMouthWeight;
        private float _curMouthWeight;

        // Animator parameter hashes
        private static readonly int HashEmotion   = Animator.StringToHash("EmotionIndex");
        private static readonly int HashIntensity  = Animator.StringToHash("EmotionIntensity");
        private static readonly int HashTrust      = Animator.StringToHash("TrustScore");

        // Emotion label → animation hint mapping (matches server DialoguePromptBuilder)
        private static readonly Dictionary<string, int> EmotionIndex = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["warm_content"]        = 1,
            ["joyful_excited"]      = 2,
            ["cautious_warmth"]     = 3,
            ["friendly_alert"]      = 4,
            ["cold_dismissive"]     = 5,
            ["hostile_aggressive"]  = 6,
            ["fearful_tense"]       = 7,
            ["suspicious_wary"]     = 8,
            ["neutral"]             = 0
        };

        private void Update()
        {
            if (faceRenderer == null) return;

            var dt = Time.deltaTime * transitionSpeed;
            _curSmile        = Mathf.Lerp(_curSmile, _targetSmile, dt);
            _curFrown        = Mathf.Lerp(_curFrown, _targetFrown, dt);
            _curBrowRaise    = Mathf.Lerp(_curBrowRaise, _targetBrowRaise, dt);
            _curBrowFurrow   = Mathf.Lerp(_curBrowFurrow, _targetBrowFurrow, dt);
            _curNostrilFlare = Mathf.Lerp(_curNostrilFlare, _targetNostrilFlare, dt);
            _curEyeSquint    = Mathf.Lerp(_curEyeSquint, _targetEyeSquint, dt);
            _curMouthWeight  = Mathf.Lerp(_curMouthWeight, _targetMouthWeight, dt);

            faceRenderer.SetBlendShapeWeight(blendSmile,       _curSmile * 100f);
            faceRenderer.SetBlendShapeWeight(blendFrown,       _curFrown * 100f);
            faceRenderer.SetBlendShapeWeight(blendBrowRaise,   _curBrowRaise * 100f);
            faceRenderer.SetBlendShapeWeight(blendBrowFurrow,  _curBrowFurrow * 100f);
            faceRenderer.SetBlendShapeWeight(blendNostrilFlare, _curNostrilFlare * 100f);
            faceRenderer.SetBlendShapeWeight(blendEyeSquint,   _curEyeSquint * 100f);

            if (mouthRig != null)
                mouthRig.weight = _curMouthWeight;
        }

        /// <summary>
        /// Called when NPC memory state changes (e.g. after ProcessEvent returns).
        /// Applies coarse emotion — drives Animator blend trees and rig weights.
        /// </summary>
        public void ApplyEmotionState(EmotionVector emotion, BehaviorOverrides overrides)
        {
            SetBlendTargets(emotion);
            SetRigWeights(emotion);
            SetAnimatorParams(emotion, overrides);
        }

        /// <summary>
        /// Called from dialogue streaming — applies the per-utterance emotion hint.
        /// Fine-grained, overrides coarse memory emotion for the duration of speech.
        /// </summary>
        public void ApplyDialogueEmotion(string animationHint, float intensity)
        {
            // Map hint string to a temporary override blend
            switch (animationHint?.ToLowerInvariant())
            {
                case "warm":        _targetSmile = intensity; _targetFrown = 0; break;
                case "joyful":      _targetSmile = intensity * 1.2f; break;
                case "cold":        _targetFrown = intensity; _targetSmile = 0; break;
                case "aggressive":  _targetBrowFurrow = intensity; _targetNostrilFlare = intensity * 0.5f; break;
                case "fearful":     _targetBrowRaise = intensity; _targetEyeSquint = intensity * 0.3f; break;
                case "grieving":    _targetFrown = intensity * 1.1f; _targetBrowRaise = intensity * 0.4f; break;
                default:            break;
            }
        }

        /// <summary>
        /// Called on each TTS word-boundary event to open the mouth rig.
        /// Weight should be scaled by emotionIntensity (0–1) from the dialogue response.
        /// </summary>
        public void SetMouthWeight(float weight) => _targetMouthWeight = Mathf.Clamp01(weight);

        /// <summary>Called when TTS audio playback ends — resets mouth rig to closed.</summary>
        public void OnSpeechEnd() => _targetMouthWeight = 0f;

        private void SetBlendTargets(EmotionVector emotion)
        {
            var v = emotion.valence;
            var a = emotion.arousal;
            var i = emotion.intensity;

            _targetSmile        = Mathf.Clamp01(v * i);
            _targetFrown        = Mathf.Clamp01(-v * i);
            _targetBrowRaise    = Mathf.Clamp01(a * i * 0.8f);
            _targetBrowFurrow   = Mathf.Clamp01(-v * a * i);
            _targetNostrilFlare = Mathf.Clamp01(a * Mathf.Abs(v) * i * 0.4f);
            _targetEyeSquint    = Mathf.Clamp01(-emotion.dominance * i * 0.3f);
        }

        private void SetRigWeights(EmotionVector emotion)
        {
            if (browRig != null)
                browRig.weight = Mathf.Clamp01(emotion.arousal * 1.2f);
            if (microExpressionRig != null)
                microExpressionRig.weight = Mathf.Clamp01(emotion.intensity * 0.6f);
        }

        private void SetAnimatorParams(EmotionVector emotion, BehaviorOverrides overrides)
        {
            if (bodyAnimator == null) return;
            var idx = EmotionIndex.TryGetValue(emotion.primary, out var i) ? i : 0;
            bodyAnimator.SetInteger(HashEmotion, idx);
            bodyAnimator.SetFloat(HashIntensity, emotion.intensity);
            if (overrides != null)
            {
                // avoidPlayer drives the body away/closed posture layer
                bodyAnimator.SetBool("AvoidPlayer", overrides.avoidPlayer);
                bodyAnimator.SetBool("AlertGuards", overrides.alertGuards);
            }
        }
    }
}
