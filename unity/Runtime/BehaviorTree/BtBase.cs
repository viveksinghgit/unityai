using UnityEngine;

namespace NpcSoulEngine.Runtime.BehaviorTree
{
    // Minimal stub base classes so the BT node scripts compile without
    // the Behavior Designer package. Replace with BehaviorDesigner.Runtime.Tasks
    // equivalents when the package is imported into the Unity project.

    public enum BtTaskStatus { Failure, Success, Running }

    public abstract class BtAction : MonoBehaviour
    {
        public virtual void OnStart() { }
        public abstract BtTaskStatus OnUpdate();
        public virtual void OnEnd() { }
    }

    public abstract class BtCondition : MonoBehaviour
    {
        public abstract bool OnEvaluate();
    }
}
