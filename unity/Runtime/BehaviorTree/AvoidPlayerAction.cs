using NpcSoulEngine.Runtime.Components;
using UnityEngine;
using UnityEngine.AI;

namespace NpcSoulEngine.Runtime.BehaviorTree
{
    /// <summary>
    /// BT Action: moves the NPC away from the player when behaviorOverrides.avoidPlayer is set.
    /// Uses NavMeshAgent to path to the furthest reachable point within flee radius.
    ///
    /// Returns Success once the NPC has reached its flee destination.
    /// Returns Failure if the NPC has no NavMeshAgent or the player is null.
    /// Runs continuously while AvoidPlayer remains true.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class AvoidPlayerAction : BtAction
    {
        [SerializeField] [Range(5f, 40f)] private float fleeRadius = 15f;
        [SerializeField] [Range(1f, 5f)]  private float acceptanceRadius = 2f;
        [SerializeField] private int      fleeSampleCount = 8;  // candidate flee points to evaluate

        private NavMeshAgent _agent;
        private NpcBlackboard _blackboard;
        private Transform _playerTransform;
        private bool _destinationSet;

        public override void OnStart()
        {
            _agent      = GetComponent<NavMeshAgent>();
            _blackboard = GetComponentInParent<NpcBlackboard>();
            _playerTransform = _blackboard?.PlayerTransform
                ?? GameObject.FindGameObjectWithTag("Player")?.transform;
            _destinationSet = false;

            if (_agent == null || _playerTransform == null) return;

            var fleePos = FindFleePosition();
            if (fleePos.HasValue)
            {
                _agent.SetDestination(fleePos.Value);
                if (_blackboard != null) _blackboard.FleeTarget = null;
                _destinationSet = true;
            }
        }

        public override BtTaskStatus OnUpdate()
        {
            if (_agent == null || _playerTransform == null)
                return BtTaskStatus.Failure;

            if (!_destinationSet)
                return BtTaskStatus.Failure;

            // Keep re-evaluating flee position if player is closing in
            var distToPlayer = Vector3.Distance(transform.position, _playerTransform.position);
            if (distToPlayer < acceptanceRadius * 2f)
            {
                var newPos = FindFleePosition();
                if (newPos.HasValue) _agent.SetDestination(newPos.Value);
            }

            // Success once far enough from player and close to destination
            var arrivedAtDest = !_agent.pathPending
                && _agent.remainingDistance <= acceptanceRadius;
            var safeDist = distToPlayer >= fleeRadius * 0.75f;

            return arrivedAtDest && safeDist ? BtTaskStatus.Success : BtTaskStatus.Running;
        }

        public override void OnEnd()
        {
            if (_agent != null && _agent.isOnNavMesh)
                _agent.ResetPath();
        }

        private Vector3? FindFleePosition()
        {
            if (_playerTransform == null) return null;

            var playerPos = _playerTransform.position;
            var npcPos    = transform.position;
            var awayDir   = (npcPos - playerPos).normalized;

            Vector3 best = npcPos;
            float bestScore = -1f;

            for (int i = 0; i < fleeSampleCount; i++)
            {
                // Sample directions away from the player with angular spread
                var angle = Mathf.Lerp(-90f, 90f, (float)i / (fleeSampleCount - 1));
                var sampleDir = Quaternion.Euler(0, angle, 0) * awayDir;
                var candidate = npcPos + sampleDir * fleeRadius;

                if (!NavMesh.SamplePosition(candidate, out var hit, 3f, NavMesh.AllAreas))
                    continue;

                // Score: maximize distance from player, prefer straighter flee angles
                var distFromPlayer = Vector3.Distance(hit.position, playerPos);
                var anglePenalty   = Mathf.Abs(angle) / 90f * 0.2f;
                var score = distFromPlayer - anglePenalty * fleeRadius;

                if (score > bestScore)
                {
                    bestScore = score;
                    best      = hit.position;
                }
            }

            return bestScore > 0f ? best : (Vector3?)null;
        }
    }

    /// <summary>
    /// BT Action: triggers the alertGuards state — plays alert animation,
    /// sets Animator parameter, and fires a scene event for guard AI to respond.
    /// </summary>
    public sealed class TriggerAlertGuardsAction : BtAction
    {
        [SerializeField] private Animator npcAnimator;
        [SerializeField] private string alertAnimTrigger = "AlertGuards";
        [SerializeField] private float alertRadius = 20f;

        public static event System.Action<Transform, float> OnGuardAlerted;

        private bool _done;

        public override void OnStart()
        {
            _done = false;
            npcAnimator ??= GetComponentInChildren<Animator>();
            npcAnimator?.SetTrigger(alertAnimTrigger);
            OnGuardAlerted?.Invoke(transform, alertRadius);
            _done = true;
        }

        public override BtTaskStatus OnUpdate() => _done ? BtTaskStatus.Success : BtTaskStatus.Running;
    }
}
