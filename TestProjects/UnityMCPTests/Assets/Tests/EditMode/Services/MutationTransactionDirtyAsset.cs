using UnityEngine;

namespace MCPForUnityTests.Editor.Services
{
    // Must live in a file matching the class name: the dirty-asset rollback test reimports
    // a .asset of this type from disk, which fails to rebind without its own MonoScript.
    public sealed class MutationTransactionDirtyAsset : ScriptableObject
    {
        public string Value;
    }
}
