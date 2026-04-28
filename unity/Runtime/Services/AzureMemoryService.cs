using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;
using UnityEngine.Networking;

namespace NpcSoulEngine.Runtime.Services
{
    /// <summary>
    /// Singleton facade — all Azure calls go through here.
    /// Call Create() once at game startup, Dispose() on quit.
    /// </summary>
    public sealed class AzureMemoryService : IDisposable
    {
        public static AzureMemoryService Instance { get; private set; }

        public event Action<NpcMemoryState> OnMemoryStateUpdated;

        private readonly AzureSoulEngineConfig _config;
        private readonly NpcMemoryCache _cache;
        private readonly CircuitBreaker _breaker;

        public CircuitBreaker Breaker => _breaker;
        public NpcMemoryCache Cache   => _cache;

        // Pending dirty flush tracking
        private float _lastSyncTime;

        private AzureMemoryService(AzureSoulEngineConfig config)
        {
            _config  = config;
            _cache   = new NpcMemoryCache(config.localCacheCapacity);
            _breaker = new CircuitBreaker(config.circuitBreakerFailureThreshold, config.circuitBreakerResetSeconds);
        }

        public static AzureMemoryService Create(AzureSoulEngineConfig config)
        {
            if (Instance != null) return Instance;
            Instance = new AzureMemoryService(config);
            return Instance;
        }

        // ─── Memory Read ──────────────────────────────────────────────────────

        public async Task<NpcMemoryState> GetMemoryAsync(string npcId, string playerId, CancellationToken ct = default)
        {
            if (_cache.TryGet(npcId, playerId, out var cached))
                return cached;

            if (_breaker.IsOpen)
                return NpcMemoryState.Neutral(npcId, playerId);

            try
            {
                var url = $"{_config.functionsBaseUrl}/api/memory/{Uri.EscapeDataString(npcId)}/{Uri.EscapeDataString(playerId)}";
                var state = await GetJsonAsync<NpcMemoryState>(url, _config.memoryReadTimeoutSeconds, ct);
                _breaker.RecordSuccess();
                _cache.Put(npcId, playerId, state);
                return state;
            }
            catch (Exception ex)
            {
                _breaker.RecordFailure();
                Debug.LogWarning($"[NpcSoulEngine] Memory read failed: {ex.Message}. Returning neutral state.");
                return NpcMemoryState.Neutral(npcId, playerId);
            }
        }

        // ─── Memory Event ─────────────────────────────────────────────────────

        public async Task<NpcMemoryState> ProcessEventAsync(MemoryEventPayload payload, CancellationToken ct = default)
        {
            if (_breaker.IsOpen)
            {
                Debug.LogWarning("[NpcSoulEngine] Circuit open — event dropped, using cached/neutral state");
                return _cache.TryGet(payload.npcId, payload.playerId, out var cached)
                    ? cached
                    : NpcMemoryState.Neutral(payload.npcId, payload.playerId);
            }

            try
            {
                var url  = $"{_config.functionsBaseUrl}/api/memory/process-event";
                var json = JsonConvert.SerializeObject(payload);
                var state = await PostJsonAsync<NpcMemoryState>(url, json, _config.memoryEventTimeoutSeconds, ct);

                _breaker.RecordSuccess();
                _cache.Put(payload.npcId, payload.playerId, state);
                OnMemoryStateUpdated?.Invoke(state);
                return state;
            }
            catch (Exception ex)
            {
                _breaker.RecordFailure();
                Debug.LogError($"[NpcSoulEngine] ProcessEvent failed: {ex.Message}");
                throw;
            }
        }

        // ─── Dialogue ─────────────────────────────────────────────────────────

        /// <summary>
        /// Non-streaming dialogue call. Returns the full <see cref="DialogueResponse"/>.
        /// </summary>
        public async Task<DialogueResponse> GenerateDialogueAsync(DialogueRequest request, CancellationToken ct = default)
        {
            if (_breaker.IsOpen)
                return FallbackDialogue(request.npcId);

            request.streaming = false;
            try
            {
                var url = $"{_config.functionsBaseUrl}/api/dialogue/generate";
                var json = JsonConvert.SerializeObject(request);
                var response = await PostJsonAsync<DialogueResponse>(url, json, _config.dialogueTimeoutSeconds, ct);
                _breaker.RecordSuccess();
                return response;
            }
            catch (Exception ex)
            {
                _breaker.RecordFailure();
                Debug.LogWarning($"[NpcSoulEngine] Dialogue generation failed: {ex.Message}");
                return FallbackDialogue(request.npcId);
            }
        }

        /// <summary>
        /// Streaming dialogue — invokes <paramref name="onToken"/> for each token as it arrives,
        /// then <paramref name="onComplete"/> with the final parsed response.
        /// </summary>
        public IEnumerator GenerateDialogueStreaming(
            DialogueRequest request,
            Action<string> onToken,
            Action<DialogueResponse> onComplete,
            Action<string> onError = null)
        {
            if (_breaker.IsOpen)
            {
                onComplete?.Invoke(FallbackDialogue(request.npcId));
                yield break;
            }

            request.streaming = true;
            var url  = $"{_config.functionsBaseUrl}/api/dialogue/generate";
            var body = JsonConvert.SerializeObject(request);
            var bytes = Encoding.UTF8.GetBytes(body);

            using var www = new UnityWebRequest(url, "POST");
            www.uploadHandler   = new UploadHandlerRaw(bytes);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Accept", "text/event-stream");
            www.SetRequestHeader("x-functions-key", _config.functionsHostKey);
            www.timeout = (int)_config.dialogueTimeoutSeconds;

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                _breaker.RecordFailure();
                onError?.Invoke(www.error);
                onComplete?.Invoke(FallbackDialogue(request.npcId));
                yield break;
            }

            _breaker.RecordSuccess();

            // Parse SSE lines from the buffered response
            var rawText = www.downloadHandler.text;
            DialogueResponse finalResponse = null;

            foreach (var line in rawText.Split('\n'))
            {
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..].Trim();
                if (string.IsNullOrEmpty(data)) continue;

                try
                {
                    var packet = JsonConvert.DeserializeObject<SsePacket>(data);
                    if (packet?.delta != null)
                        onToken?.Invoke(packet.delta);
                    if (packet?.done == true && packet.full != null)
                        finalResponse = packet.full;
                }
                catch { /* skip malformed SSE line */ }
            }

            onComplete?.Invoke(finalResponse ?? FallbackDialogue(request.npcId));
        }

        // ─── Cache sync ───────────────────────────────────────────────────────

        public async Task FlushDirtyCacheAsync(CancellationToken ct = default)
        {
            foreach (var entry in _cache.GetDirtyEntries())
            {
                try
                {
                    // Re-read from Azure to pull any server-side changes (decay, gossip updates)
                    var refreshed = await GetJsonAsync<NpcMemoryState>(
                        $"{_config.functionsBaseUrl}/api/memory/{Uri.EscapeDataString(entry.NpcId)}/{Uri.EscapeDataString(entry.PlayerId)}",
                        _config.memoryReadTimeoutSeconds, ct);
                    _cache.Put(entry.NpcId, entry.PlayerId, refreshed, dirty: false);
                    _cache.ClearDirtyFlag(entry.NpcId, entry.PlayerId);
                    OnMemoryStateUpdated?.Invoke(refreshed);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[NpcSoulEngine] Cache flush failed for {entry.NpcId}: {ex.Message}");
                }
            }
        }

        // ─── Internal HTTP helpers ────────────────────────────────────────────

        private async Task<T> GetJsonAsync<T>(string url, float timeoutSecs, CancellationToken ct)
        {
            using var www = UnityWebRequest.Get(url);
            www.SetRequestHeader("x-functions-key", _config.functionsHostKey);
            www.timeout = (int)timeoutSecs;

            var op = www.SendWebRequest();
            while (!op.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (www.result != UnityWebRequest.Result.Success)
                throw new Exception($"HTTP {www.responseCode}: {www.error}");

            return JsonConvert.DeserializeObject<T>(www.downloadHandler.text);
        }

        private async Task<T> PostJsonAsync<T>(string url, string json, float timeoutSecs, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using var www = new UnityWebRequest(url, "POST")
            {
                uploadHandler   = new UploadHandlerRaw(bytes),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout         = (int)timeoutSecs
            };
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("x-functions-key", _config.functionsHostKey);

            var op = www.SendWebRequest();
            while (!op.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (www.result != UnityWebRequest.Result.Success)
                throw new Exception($"HTTP {www.responseCode}: {www.error}");

            return JsonConvert.DeserializeObject<T>(www.downloadHandler.text);
        }

        private static DialogueResponse FallbackDialogue(string npcId) => new()
        {
            text          = "...",
            internalEmotion = "neutral",
            emotionIntensity = 0f,
            animationHint = "idle",
            tradeWillingness = 0.5f
        };

        public void Dispose() { }

        // SSE packet shapes
        private sealed class SsePacket
        {
            public string delta;
            public bool done;
            public DialogueResponse full;
        }
    }
}
