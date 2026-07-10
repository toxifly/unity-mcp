using UnityEngine;

namespace TestNamespace
{
    public class SerializedMissingReferenceFixture : ScriptableObject
    {
        [SerializeField] private Object reference;

        public Object Reference
        {
            get => reference;
            set => reference = value;
        }
    }
}
