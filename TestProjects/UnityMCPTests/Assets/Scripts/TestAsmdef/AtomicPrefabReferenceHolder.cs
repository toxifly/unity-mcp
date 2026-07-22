using UnityEngine;

namespace TestNamespace
{
    // Lives in the runtime TestAsmdef assembly (in its own same-named file) so it gets a
    // MonoScript that AddComponent accepts; editor-assembly MonoBehaviours cannot be attached.
    public sealed class AtomicPrefabReferenceHolder : MonoBehaviour
    {
        public GameObject Target;
    }
}
