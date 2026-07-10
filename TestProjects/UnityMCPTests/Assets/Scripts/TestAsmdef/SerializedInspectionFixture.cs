using UnityEngine;

namespace TestNamespace
{
    public class SerializedInspectionFixture : MonoBehaviour
    {
        [SerializeField] private GameObject reference;
        [SerializeField] private string label = "fixture";

        public GameObject Reference
        {
            get => reference;
            set => reference = value;
        }
    }
}
