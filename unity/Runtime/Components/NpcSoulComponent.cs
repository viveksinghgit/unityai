using System.Collections;
using System.Threading;
using NpcSoulEngine.Runtime.Inference;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Components
{
    /// <summary>
    /// Attach to every NPC GameObject that participates in the Soul Engine.
    /// Exposes current memory state to Behavior Designer via the NPC blackboard,
    /// and drives EmotionAnimationController on state changes.
    /// </summary>
    [RequireComponent(typeof(EmotionAnimationController))]
    public sealed class NpcSoulComponent : MonoBehaviour
    {
        [SerializeField] private string npcId;
        [SerializeField] private NpcProfileData npcProfile;

        public string NpcId => npcId;
        public NpcProfileData Profile => npcProfile;

        // Current memory state — read by Behavior Designer nodes
        public NpcMemoryState CurrentMemory { get; private set; }
        public bool MemoryLoaded { get; private set; }

        private EmotionAnimationController _animController;
        private EmotionClassifier _emotionClassifier;
        private NpcSoulNetworkSync _netSync;   // null when NGO is not used (single-player)
        private CancellationTokenSource _cts;
        private Coroutine _dialogueCoroutine;

        private void Awake()
        {
            _animController    = GetComponent<EmotionAnimationController>();
            _emotionClassifier = new EmotionClassifier();
            _netSync           = GetComponent<NpcSoulNetworkSync>();
            _cts = new CancellationTokenSource();
        }

        private void Start()
        {
            NpcSoulEngineManager.Instance?.RegisterNpc(this);

            if (string.IsNullOrEmpty(npcId))
                Debug.LogError($"[NpcSoulEngine] NpcSoulComponent on {gameObject.name} has no NpcId set!");
        }

        private void OnDestroy()
        {
            _cts.Cancel();
            _emotionClassifier?.Dispose();
            NpcSoulEngineManager.Instance?.UnregisterNpc(this);
        }

        // ─── Memory Load (called by FetchNpcMemoryAction BT node) ─────────────

        public IEnumerator LoadMemoryCoroutine(string playerId)
        {
            // Clients receive state from the server via NpcSoulNetworkSync — no Azure call.
            if (_netSync?.IsNetworkClient == true) yield break;

            MemoryLoaded = false;
            var task = AzureMemoryService.Instance.GetMemoryAsync(npcId, playerId, _cts.Token);
            yield return new WaitUntil(() => task.IsCompleted);

            CurrentMemory = task.IsCompletedSuccessfully
                ? task.Result
                : NpcMemoryState.Neutral(npcId, playerId);

            MemoryLoaded = true;
            _animController.ApplyEmotionState(CurrentMemory.currentEmotion, CurrentMemory.behaviorOverrides);
            _netSync?.BroadcastState(CurrentMemory);
        }

        public void ApplyMemoryState(NpcMemoryState state)
        {
            CurrentMemory = state;
            MemoryLoaded  = true;
            _animController.ApplyEmotionState(state.currentEmotion, state.behaviorOverrides);
            // BroadcastState is safe on clients — NpcSoulNetworkSync checks IsServer internally.
            _netSync?.BroadcastState(state);
        }

        // ─── Dialogue (called by InitiateDialogueAction BT node) ─────────────

        public Coroutine StartDialogue(
            string playerUtterance,
            System.Action<DialogueResponse> onResponse,
            System.Action<string> onToken = null)
        {
            // Clients never initiate dialogue API calls — the server owns all NPC logic.
            if (_netSync?.IsNetworkClient == true)
            {
                onResponse?.Invoke(null);
                return null;
            }

            if (_dialogueCoroutine != null) StopCoroutine(_dialogueCoroutine);
            _dialogueCoroutine = StartCoroutine(DialogueCoroutine(playerUtterance, onResponse, onToken));
            return _dialogueCoroutine;
        }

        private IEnumerator DialogueCoroutine(
            string playerUtterance,
            System.Action<DialogueResponse> onResponse,
            System.Action<string> onToken)
        {
            var manager = NpcSoulEngineManager.Instance;
            if (manager == null || AzureMemoryService.Instance == null)
            {
                onResponse?.Invoke(null);
                yield break;
            }

            // Pre-warm animation from current memory state before the server responds
            if (_emotionClassifier != null && CurrentMemory != null)
            {
                var preWarm = _emotionClassifier.Classify(CurrentMemory);
                _animController.ApplyEmotionState(preWarm, CurrentMemory.behaviorOverrides);
            }

            var request = new DialogueRequest
            {
                npcId     = npcId,
                playerId  = manager.PlayerId,
                utterance = playerUtterance,
                npcProfile = npcProfile,
                streaming = onToken != null
            };

            if (onToken != null)
            {
                yield return AzureMemoryService.Instance.GenerateDialogueStreaming(
                    request, onToken, response =>
                    {
                        if (response != null)
                        {
                            _animController.ApplyDialogueEmotion(response.animationHint, response.emotionIntensity);
                            if (!string.IsNullOrEmpty(response.ssmlMarkup))
                                StartCoroutine(SpeakCoroutine(response.ssmlMarkup, response.emotionIntensity));
                        }
                        onResponse?.Invoke(response);
                    });
            }
            else
            {
                var task = AzureMemoryService.Instance.GenerateDialogueAsync(request, _cts.Token);
                yield return new WaitUntil(() => task.IsCompleted);
                if (task.IsCompletedSuccessfully)
                {
                    var response = task.Result;
                    _animController.ApplyDialogueEmotion(response.animationHint, response.emotionIntensity);
                    if (!string.IsNullOrEmpty(response.ssmlMarkup))
                        StartCoroutine(SpeakCoroutine(response.ssmlMarkup, response.emotionIntensity));
                    onResponse?.Invoke(response);
                }
                else
                {
                    onResponse?.Invoke(null);
                }
            }
        }

        // Synthesizes SSML and drives the mouth rig from word boundaries while audio plays.
        private IEnumerator SpeakCoroutine(string ssml, float emotionIntensity)
        {
            var tts = NpcTtsService.Instance;
            if (tts == null || !tts.IsConfigured) yield break;

            var task = tts.SynthesizeAsync(ssml, _cts.Token);
            yield return new WaitUntil(() => task.IsCompleted);

            if (!task.IsCompletedSuccessfully || !task.Result.IsSuccess || task.Result.Clip == null)
                yield break;

            var result = task.Result;
            var source = GetComponent<AudioSource>();
            if (source == null) source = gameObject.AddComponent<AudioSource>();
            source.clip = result.Clip;
            source.Play();

            var boundaries = result.WordBoundaries;
            while (source.isPlaying)
            {
                var t      = source.time;
                var inWord = false;
                for (var i = 0; i < boundaries.Count; i++)
                {
                    var wb = boundaries[i];
                    if (t >= wb.AudioOffsetSecs && t < wb.AudioOffsetSecs + wb.DurationSecs)
                    { inWord = true; break; }
                }
                _animController.SetMouthWeight(inWord ? emotionIntensity * 0.85f : 0.1f);
                yield return null;
            }

            _animController.OnSpeechEnd();
        }
    }
}
