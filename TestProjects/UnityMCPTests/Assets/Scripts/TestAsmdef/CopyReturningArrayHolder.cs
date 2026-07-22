using UnityEngine;

namespace TestNamespace
{
    // Lives in the runtime TestAsmdef assembly (in its own same-named file) so it gets a
    // MonoScript that AddComponent accepts; editor-assembly MonoBehaviours cannot be attached.
    // Mirrors Unity properties like renderer.sharedMaterials / mesh.vertices whose getters
    // return a fresh copy of the underlying array, to exercise nested-path write-back.
    public sealed class CopyReturningArrayHolder : MonoBehaviour
    {
        [SerializeField] private Vector3[] points = { Vector3.zero, Vector3.zero };

        public Vector3[] Points
        {
            get => (Vector3[])points.Clone();
            set => points = value;
        }

        public Vector3[] ReadOnlyPoints => (Vector3[])points.Clone();
    }
}
