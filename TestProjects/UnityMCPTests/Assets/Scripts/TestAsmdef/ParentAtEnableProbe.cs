using UnityEngine;

namespace TestNamespace
{
    // Records the parent seen by the first OnEnable, so a test can tell an object instantiated
    // under its parent from one created at the scene root and reparented once already enabled.
    [ExecuteAlways]
    public sealed class ParentAtEnableProbe : MonoBehaviour
    {
        public bool WasEnabled { get; private set; }
        public Transform ParentAtFirstEnable { get; private set; }

        private void OnEnable()
        {
            if (WasEnabled) return;
            WasEnabled = true;
            ParentAtFirstEnable = transform.parent;
        }
    }
}
