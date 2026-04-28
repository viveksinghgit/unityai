using System.Collections;
using NpcSoulEngine.Runtime.Components;
using UnityEngine;

// Requires Behavior Designer or Unity Behavior package.
// This file uses a minimal abstract base class approach so it compiles
// without the Behavior Designer package present; swap the base class
// to BehaviorDesigner.Runtime.Tasks.Action when the package is imported.

namespace NpcSoulEngine.Runtime.BehaviorTree
{
    /// <summary>
    /// Behavior Tree Action node.
    /// Fetches the NPC's memory state for the current player and writes it
    /// to the NPC blackboard. Must run before any memory-dependent conditions.
    /// </summary>
    [AddComponentMenu("")]  // hide from Add Component menu — added via BT graph only
    public sealed class FetchNpcMemoryAction : BtAction
    {
        [SerializeField] [Tooltip("NpcSoulComponent on the NPC this BT belongs to")]
        private NpcSoulComponent npcSoulComponent;

        private NpcBlackboard _blackboard;
        private Coroutine _loadCoroutine;
        private bool _done;
        private bool _success;

        public override void OnStart()
        {
            _done    = false;
            _success = false;

            if (npcSoulComponent == null)
                npcSoulComponent = GetComponentInParent<NpcSoulComponent>();

            if (_blackboard == null)
                _blackboard = GetComponentInParent<NpcBlackboard>();

            // Cache the player transform for navigation nodes
            if (_blackboard != null && _blackboard.PlayerTransform == null)
                _blackboard.PlayerTransform = GameObject.FindGameObjectWithTag("Player")?.transform;

            if (npcSoulComponent == null || NpcSoulEngineManager.Instance == null)
            {
                _done = _success = false;
                return;
            }

            _loadCoroutine = npcSoulComponent.StartCoroutine(LoadAndComplete());
        }

        private IEnumerator LoadAndComplete()
        {
            yield return npcSoulComponent.LoadMemoryCoroutine(NpcSoulEngineManager.Instance.PlayerId);
            if (npcSoulComponent.MemoryLoaded && _blackboard != null)
                _blackboard.ApplyMemoryState(npcSoulComponent.CurrentMemory);
            _success = npcSoulComponent.MemoryLoaded;
            _done    = true;
        }

        public override BtTaskStatus OnUpdate()
        {
            if (!_done) return BtTaskStatus.Running;
            return _success ? BtTaskStatus.Success : BtTaskStatus.Failure;
        }

        public override void OnEnd()
        {
            if (_loadCoroutine != null && npcSoulComponent != null)
                npcSoulComponent.StopCoroutine(_loadCoroutine);
        }
    }
}
