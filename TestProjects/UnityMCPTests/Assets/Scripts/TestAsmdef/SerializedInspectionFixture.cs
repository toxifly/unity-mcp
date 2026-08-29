using UnityEngine;

namespace TestNamespace
{
    public class SerializedInspectionFixture : MonoBehaviour
    {
        [SerializeField] private GameObject reference;
        [SerializeField] private string label = "fixture";
        [SerializeField] private Color tint = new Color(0.72f, 0.86f, 1f, 0.45f);
        [SerializeField] private Vector2 offset2 = new Vector2(1f, 2f);
        [SerializeField] private Vector3 offset3 = new Vector3(1f, 2f, 3f);
        [SerializeField] private Vector4 tangent = new Vector4(1f, 2f, 3f, 4f);
        [SerializeField] private Quaternion rotation = Quaternion.identity;
        [SerializeField] private Rect area = new Rect(1f, 2f, 3f, 4f);
        [SerializeField] private Bounds volume = new Bounds(Vector3.one, Vector3.one * 2f);
        [SerializeField] private Vector2Int cell2 = new Vector2Int(1, 2);
        [SerializeField] private Vector3Int cell3 = new Vector3Int(1, 2, 3);
        [SerializeField] private RectInt areaInt = new RectInt(1, 2, 3, 4);
        [SerializeField] private BoundsInt volumeInt = new BoundsInt(1, 2, 3, 4, 5, 6);
        [SerializeField] private AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        public GameObject Reference
        {
            get => reference;
            set => reference = value;
        }

        public AnimationCurve Curve
        {
            get => curve;
            set => curve = value;
        }
    }
}
