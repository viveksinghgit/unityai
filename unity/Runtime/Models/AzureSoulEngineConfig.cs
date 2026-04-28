using UnityEngine;

namespace NpcSoulEngine.Runtime.Models
{
    [CreateAssetMenu(menuName = "NPC Soul Engine/Config", fileName = "AzureSoulEngineConfig")]
    public sealed class AzureSoulEngineConfig : ScriptableObject
    {
        [Header("Azure Functions Endpoint")]
        [Tooltip("Base URL of the deployed Azure Functions app, e.g. https://func-npc-soul-dev.azurewebsites.net")]
        public string functionsBaseUrl;

        [Tooltip("Azure Functions host key (stored in KeyVault in production — use local.settings.json in dev)")]
        public string functionsHostKey;

        [Header("Timeouts")]
        [Tooltip("HTTP request timeout in seconds for memory events")]
        public float memoryEventTimeoutSeconds = 8f;

        [Tooltip("HTTP request timeout in seconds for fast memory reads")]
        public float memoryReadTimeoutSeconds = 2f;

        [Tooltip("HTTP request timeout in seconds for dialogue generation")]
        public float dialogueTimeoutSeconds = 15f;

        [Header("Local Cache")]
        [Tooltip("Maximum number of NPC-player pairs held in the local LRU cache")]
        public int localCacheCapacity = 50;

        [Tooltip("How often dirty cache entries are flushed to Azure (seconds)")]
        public float cacheSyncIntervalSeconds = 60f;

        [Header("Circuit Breaker")]
        [Tooltip("Consecutive Azure failures before entering open state")]
        public int circuitBreakerFailureThreshold = 10;

        [Tooltip("Seconds to wait in open state before attempting a probe")]
        public float circuitBreakerResetSeconds = 30f;

        [Header("Significance Filter")]
        [Tooltip("Minimum significance score (0–1) for an event to be sent to Azure")]
        public float significanceThreshold = 0.3f;

        [Tooltip("Buffer flush interval in seconds")]
        public float bufferFlushIntervalSeconds = 0.5f;

        [Header("NPC Prefetch")]
        [Tooltip("Distance (metres) at which the engine speculatively prefetches an NPC's memory")]
        public float prefetchRadiusMetres = 30f;

        [Header("Azure TTS")]
        [Tooltip("Azure Speech Services subscription key. Required for voiced NPC dialogue.")]
        public string speechSubscriptionKey;

        [Tooltip("Azure Speech Services region, e.g. eastus")]
        public string speechRegion;

        [Tooltip("Enable Azure TTS for NPC dialogue. Requires SPEECH_SDK_AVAILABLE define and Speech SDK plugin.")]
        public bool enableTts = true;
    }
}
