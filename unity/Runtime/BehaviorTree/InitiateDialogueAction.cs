using System.Collections;
using NpcSoulEngine.Runtime.Components;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;
using UnityEngine.Events;

namespace NpcSoulEngine.Runtime.BehaviorTree
{
    /// <summary>
    /// BT Action: triggers GPT-4o dialogue generation for the NPC.
    /// Fires UnityEvents for integration with dialogue UI and TTS systems.
    /// </summary>
    public sealed class InitiateDialogueAction : BtAction
    {
        [SerializeField] private NpcSoulComponent npcSoulComponent;

        [Header("Input")]
        [Tooltip("Set this from your dialogue UI before the BT evaluates this node")]
        public string playerUtterance;

        [Header("Callbacks")]
        public UnityEvent<string>          onTokenReceived;   // streaming — each token
        public UnityEvent<DialogueResponse> onResponseReady;   // final response
        public UnityEvent                  onDialogueError;

        private bool _done;
        private bool _success;
        private Coroutine _coroutine;

        public override void OnStart()
        {
            _done = _success = false;

            if (npcSoulComponent == null)
                npcSoulComponent = GetComponentInParent<NpcSoulComponent>();

            if (npcSoulComponent == null || string.IsNullOrEmpty(playerUtterance))
            {
                _done = true; return;
            }

            _coroutine = npcSoulComponent.StartDialogue(
                playerUtterance,
                onResponse: response =>
                {
                    if (response != null)
                    {
                        onResponseReady?.Invoke(response);
                        _success = true;
                    }
                    else
                    {
                        onDialogueError?.Invoke();
                    }
                    _done = true;
                },
                onToken: token => onTokenReceived?.Invoke(token));
        }

        public override BtTaskStatus OnUpdate()
        {
            if (!_done) return BtTaskStatus.Running;
            return _success ? BtTaskStatus.Success : BtTaskStatus.Failure;
        }

        public override void OnEnd()
        {
            if (_coroutine != null && npcSoulComponent != null)
                npcSoulComponent.StopCoroutine(_coroutine);
        }
    }
}
