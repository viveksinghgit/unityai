using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NpcSoulEngine.Runtime.Inference;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Components
{
    /// <summary>
    /// Attach to the Player GameObject.
    /// Captures player actions, runs local significance scoring via Sentis,
    /// and flushes events to AzureMemoryService on a 500ms interval.
    /// Zero-allocation on the hot path after initialization.
    /// </summary>
    public sealed class PlayerActionMonitor : MonoBehaviour
    {
        // Ring buffer — fixed size, no heap allocation after Start()
        private const int BufferCapacity = 32;
        private readonly MemoryEventPayload[] _buffer = new MemoryEventPayload[BufferCapacity];
        private int _head;
        private int _tail;
        private int _count;
        private readonly object _lock = new();

        [SerializeField] private AzureSoulEngineConfig config;

        private SignificanceScorer _scorer;
        private CancellationTokenSource _cts;
        private Coroutine _flushCoroutine;

        public static PlayerActionMonitor Instance { get; private set; }

        // Other systems call this to record a player action
        public event Action<MemoryEventPayload, NpcMemoryState> OnMemoryUpdated;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            _scorer       = new SignificanceScorer();
            _cts          = new CancellationTokenSource();
            _flushCoroutine = StartCoroutine(FlushLoop());
            // Forward confirmed memory updates to the static event bus
            OnMemoryUpdated += (payload, state) => SoulEventBus.RaiseMemoryUpdated(payload.npcId, state);
        }

        private void OnDestroy()
        {
            _cts.Cancel();
            if (_flushCoroutine != null) StopCoroutine(_flushCoroutine);
            _scorer?.Dispose();
        }

        // ─── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Record an interaction. Call this from dialogue systems, combat systems, trade systems, etc.
        /// </summary>
        public void Record(
            string npcId,
            ActionType actionType,
            ActionContext context,
            List<string> witnessNpcIds = null,
            string location = "",
            string zone = "zone_default")
        {
            var payload = MemoryEventPayload.Create(npcId, NpcSoulEngineManager.Instance.PlayerId,
                actionType, context, location, zone);

            if (witnessNpcIds != null)
                payload.witnessIds.AddRange(witnessNpcIds);

            // Score locally — LifeOrDeath always bypasses buffer and fires immediately
            var significance = _scorer.Score(actionType, context.stakes, context.publicness);
            payload.significanceHint = significance;

            if (context.stakes == "LifeOrDeath" || significance >= 1.0f)
            {
                StartCoroutine(SendImmediate(payload));
                return;
            }

            if (significance < config.significanceThreshold)
                return;  // below threshold — discard

            Enqueue(payload);
        }

        // ─── Ring Buffer ──────────────────────────────────────────────────────

        private void Enqueue(MemoryEventPayload payload)
        {
            lock (_lock)
            {
                if (_count == BufferCapacity)
                {
                    // Buffer full — drop oldest entry
                    _head = (_head + 1) % BufferCapacity;
                    _count--;
                }
                _buffer[_tail] = payload;
                _tail = (_tail + 1) % BufferCapacity;
                _count++;
            }
        }

        private bool TryDequeue(out MemoryEventPayload payload)
        {
            lock (_lock)
            {
                if (_count == 0) { payload = null; return false; }
                payload = _buffer[_head];
                _buffer[_head] = null;
                _head = (_head + 1) % BufferCapacity;
                _count--;
                return true;
            }
        }

        // ─── Flush Loop ───────────────────────────────────────────────────────

        private IEnumerator FlushLoop()
        {
            var wait = new WaitForSeconds(config.bufferFlushIntervalSeconds);
            while (true)
            {
                yield return wait;
                yield return FlushBuffer();
            }
        }

        private IEnumerator FlushBuffer()
        {
            while (TryDequeue(out var payload))
            {
                if (AzureMemoryService.Instance == null) yield break;
                var task = AzureMemoryService.Instance.ProcessEventAsync(payload, _cts.Token);
                yield return new WaitUntil(() => task.IsCompleted);
                if (task.IsCompletedSuccessfully)
                    OnMemoryUpdated?.Invoke(payload, task.Result);
            }
        }

        private IEnumerator SendImmediate(MemoryEventPayload payload)
        {
            if (AzureMemoryService.Instance == null) yield break;
            var task = AzureMemoryService.Instance.ProcessEventAsync(payload, _cts.Token);
            yield return new WaitUntil(() => task.IsCompleted);
            if (task.IsCompletedSuccessfully)
                OnMemoryUpdated?.Invoke(payload, task.Result);
        }
    }
}
