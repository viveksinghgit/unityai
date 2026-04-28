using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NpcSoulEngine.Runtime.Models;
using NpcSoulEngine.Runtime.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NpcSoulEngine.Runtime.Components
{
    /// <summary>
    /// Singleton MonoBehaviour. DontDestroyOnLoad.
    /// Owns the AzureMemoryService lifecycle, scene NPC registration,
    /// proximity-based prefetch, and dirty-cache sync on scene unload.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class NpcSoulEngineManager : MonoBehaviour
    {
        [SerializeField] private AzureSoulEngineConfig config;

        [Tooltip("Override player ID (leave blank to use PlayerIdentityService / PlayerPrefs)")]
        [SerializeField] private string playerIdOverride;

        public static NpcSoulEngineManager Instance { get; private set; }
        public static AzureMemoryService Service => AzureMemoryService.Instance;
        public string PlayerId => PlayerIdentityService.PlayerId;

        private readonly List<NpcSoulComponent> _registeredNpcs = new();
        private readonly HashSet<string> _prefetchedNpcIds = new();
        private Transform _playerTransform;
        private CancellationTokenSource _cts;
        private Coroutine _cacheFlushCoroutine;
        private Coroutine _prefetchCoroutine;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Allow inspector override (e.g. during QA testing with a fixed ID)
            if (!string.IsNullOrEmpty(playerIdOverride))
                PlayerIdentityService.SetPlayerId(playerIdOverride);

            AzureMemoryService.Create(config);
            if (config.enableTts)
                NpcTtsService.Create(config);
            _cts = new CancellationTokenSource();
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded   += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded   -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        private void Start()
        {
            _playerTransform = GameObject.FindGameObjectWithTag("Player")?.transform;
            _cacheFlushCoroutine = StartCoroutine(CacheFlushLoop());
            _prefetchCoroutine   = StartCoroutine(PrefetchLoop());
        }

        private void OnApplicationQuit()
        {
            _cts.Cancel();
            NpcTtsService.Instance?.Dispose();
            // Sync flush with tight timeout on quit
            var flushTask = Service?.FlushDirtyCacheAsync();
            flushTask?.Wait(TimeSpan.FromSeconds(3));
        }

        // ─── NPC Registration ─────────────────────────────────────────────────

        public void RegisterNpc(NpcSoulComponent npc)
        {
            if (!_registeredNpcs.Contains(npc))
                _registeredNpcs.Add(npc);
        }

        public void UnregisterNpc(NpcSoulComponent npc) => _registeredNpcs.Remove(npc);

        // ─── Scene Lifecycle ──────────────────────────────────────────────────

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _prefetchedNpcIds.Clear();
            _playerTransform = GameObject.FindGameObjectWithTag("Player")?.transform;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            _ = Service?.FlushDirtyCacheAsync(_cts.Token);
            _registeredNpcs.Clear();
            _prefetchedNpcIds.Clear();
        }

        // ─── Proximity Prefetch ───────────────────────────────────────────────

        private IEnumerator PrefetchLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(1f);
                if (_playerTransform == null || Service == null) continue;

                foreach (var npc in _registeredNpcs)
                {
                    if (npc == null) continue;
                    var dist = Vector3.Distance(_playerTransform.position, npc.transform.position);
                    if (dist <= config.prefetchRadiusMetres && _prefetchedNpcIds.Add(npc.NpcId))
                    {
                        // Fire-and-forget prefetch — warms the local cache before the player speaks
                        StartCoroutine(PrefetchNpcMemory(npc.NpcId));
                    }
                }
            }
        }

        private IEnumerator PrefetchNpcMemory(string npcId)
        {
            var task = Service.GetMemoryAsync(npcId, PlayerId, _cts.Token);
            yield return new WaitUntil(() => task.IsCompleted);
            if (task.Exception != null)
                Debug.LogWarning($"[NpcSoulEngine] Prefetch failed for {npcId}: {task.Exception.InnerException?.Message}");
        }

        // ─── Periodic Cache Flush ─────────────────────────────────────────────

        private IEnumerator CacheFlushLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(config.cacheSyncIntervalSeconds);
                if (Service == null) continue;
                var task = Service.FlushDirtyCacheAsync(_cts.Token);
                yield return new WaitUntil(() => task.IsCompleted);
            }
        }
    }
}
