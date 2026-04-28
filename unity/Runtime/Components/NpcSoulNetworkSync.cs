using Unity.Collections;
using Unity.Netcode;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;

namespace NpcSoulEngine.Runtime.Components
{
    /// <summary>
    /// NGO network sync layer for NpcSoulComponent. Server authority only.
    ///
    /// Server drives all Azure calls via NpcSoulComponent as normal.
    /// Clients receive an NpcMemoryStateSyncData NetworkVariable and apply
    /// animations/behavior without touching any remote service.
    ///
    /// Usage: add this component alongside NpcSoulComponent on NPC prefabs
    /// that need multiplayer state replication. No changes to NpcSoulComponent
    /// are required on single-player projects — this component is safely inert
    /// when the object is never spawned through NGO.
    /// </summary>
    [RequireComponent(typeof(NpcSoulComponent))]
    public sealed class NpcSoulNetworkSync : NetworkBehaviour
    {
        private readonly NetworkVariable<NpcMemoryStateSyncData> _syncedState = new(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NpcSoulComponent _soul;

        /// <summary>
        /// True on connected clients (not the server/host).
        /// When true, NpcSoulComponent skips Azure API calls — state arrives from the server.
        /// </summary>
        public bool IsNetworkClient => IsSpawned && !IsServer;

        public override void OnNetworkSpawn()
        {
            _soul = GetComponent<NpcSoulComponent>();
            if (!IsServer)
                _syncedState.OnValueChanged += OnStateChangedOnClient;
        }

        public override void OnNetworkDespawn()
        {
            if (!IsServer)
                _syncedState.OnValueChanged -= OnStateChangedOnClient;
        }

        /// <summary>
        /// Called by NpcSoulComponent on the server whenever memory state changes,
        /// to broadcast the visual subset to all connected clients.
        /// No-op on clients or before the object is spawned.
        /// </summary>
        public void BroadcastState(NpcMemoryState state)
        {
            if (!IsSpawned || !IsServer) return;
            _syncedState.Value = NpcMemoryStateSyncData.FromMemoryState(state);
        }

        // Client-only: apply the incoming state as pure visuals — no API call.
        private void OnStateChangedOnClient(NpcMemoryStateSyncData _, NpcMemoryStateSyncData next)
        {
            _soul?.ApplyMemoryState(next.ToMemoryState());
        }
    }

    /// <summary>
    /// Serializable subset of NpcMemoryState sent over the wire.
    /// Only fields needed for client-side animation and behavior are included.
    /// </summary>
    public struct NpcMemoryStateSyncData : INetworkSerializable
    {
        public float TrustScore;
        public float FearScore;
        public float HostilityScore;
        public float RespectScore;
        public FixedString64Bytes CurrentEmotionPrimary;  // max 63 bytes — labels are ≤20 chars
        public float EmotionIntensity;
        public float EmotionValence;
        public float EmotionArousal;
        public bool  RefuseTrade;
        public bool  AlertGuards;
        public bool  GiveDiscount;
        public bool  AvoidPlayer;
        public bool  SeekHelp;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref TrustScore);
            serializer.SerializeValue(ref FearScore);
            serializer.SerializeValue(ref HostilityScore);
            serializer.SerializeValue(ref RespectScore);
            serializer.SerializeValue(ref CurrentEmotionPrimary);
            serializer.SerializeValue(ref EmotionIntensity);
            serializer.SerializeValue(ref EmotionValence);
            serializer.SerializeValue(ref EmotionArousal);
            serializer.SerializeValue(ref RefuseTrade);
            serializer.SerializeValue(ref AlertGuards);
            serializer.SerializeValue(ref GiveDiscount);
            serializer.SerializeValue(ref AvoidPlayer);
            serializer.SerializeValue(ref SeekHelp);
        }

        public static NpcMemoryStateSyncData FromMemoryState(NpcMemoryState s) => new()
        {
            TrustScore            = s.trustScore,
            FearScore             = s.fearScore,
            HostilityScore        = s.hostilityScore,
            RespectScore          = s.respectScore,
            CurrentEmotionPrimary = s.currentEmotion?.primary ?? "neutral",
            EmotionIntensity      = s.currentEmotion?.intensity ?? 0f,
            EmotionValence        = s.currentEmotion?.valence   ?? 0f,
            EmotionArousal        = s.currentEmotion?.arousal   ?? 0f,
            RefuseTrade           = s.behaviorOverrides?.refuseTrade  ?? false,
            AlertGuards           = s.behaviorOverrides?.alertGuards  ?? false,
            GiveDiscount          = s.behaviorOverrides?.giveDiscount ?? false,
            AvoidPlayer           = s.behaviorOverrides?.avoidPlayer  ?? false,
            SeekHelp              = s.behaviorOverrides?.seekHelp     ?? false,
        };

        public NpcMemoryState ToMemoryState() => new()
        {
            trustScore     = TrustScore,
            fearScore      = FearScore,
            hostilityScore = HostilityScore,
            respectScore   = RespectScore,
            currentEmotion = new EmotionVector
            {
                primary   = CurrentEmotionPrimary.ToString(),
                intensity = EmotionIntensity,
                valence   = EmotionValence,
                arousal   = EmotionArousal,
            },
            behaviorOverrides = new BehaviorOverrides
            {
                refuseTrade  = RefuseTrade,
                alertGuards  = AlertGuards,
                giveDiscount = GiveDiscount,
                avoidPlayer  = AvoidPlayer,
                seekHelp     = SeekHelp,
            }
        };
    }
}
