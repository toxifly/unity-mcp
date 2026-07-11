using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TestNamespace;
using UnityEditor;
using UnityEngine;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class InspectSerializedTests
    {
        private GameObject _owner;
        private GameObject _referenced;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject("SerializedOwner");
            _referenced = new GameObject("SerializedReference");
            _owner.AddComponent<SerializedInspectionFixture>().Reference = _referenced;
        }

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) Object.DestroyImmediate(_owner);
            if (_referenced != null) Object.DestroyImmediate(_referenced);
        }

        [Test]
        public void ObjectReference_ReportsIdentityAndNullState()
        {
            JObject result = Inspect("reference");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            JToken finding = result["data"]["findings"][0];
            Assert.AreEqual("object_reference", finding.Value<string>("value_kind"));
            Assert.AreEqual("SerializedReference", finding.Value<string>("referenced_object"));
            Assert.IsFalse(finding.Value<bool>("is_null"));
            Assert.IsFalse(finding.Value<bool>("missing"));
            Assert.IsNotEmpty(finding.Value<string>("referenced_global_object_id"));
        }

        [Test]
        public void NullReference_IsDistinctFromBrokenReference()
        {
            _owner.GetComponent<SerializedInspectionFixture>().Reference = null;
            JObject result = Inspect("reference");

            JToken finding = result["data"]["findings"][0];
            Assert.IsTrue(finding.Value<bool>("is_null"));
            Assert.IsFalse(finding.Value<bool>("missing"));
        }

        [Test]
        public void PropertyWhitelist_DoesNotDumpUnrequestedFields()
        {
            JObject result = Inspect("label");

            JArray findings = (JArray)result["data"]["findings"];
            Assert.AreEqual(1, findings.Count, result.ToString());
            Assert.AreEqual("label", findings[0].Value<string>("property"));
            Assert.AreEqual("fixture", findings[0].Value<string>("value"));
        }

        [Test]
        public void UnfilteredGameObjectTarget_InspectsGameObjectProperties()
        {
            string globalId = GlobalObjectId.GetGlobalObjectIdSlow(_owner).ToString();
            // On 6000.5+ objects in untitled scenes get the null GlobalObjectId;
            // fall back to name targeting so the inspection itself is still covered.
            if (globalId.EndsWith("-0-0"))
                globalId = _owner.name;
            JObject result = ToJObject(InspectSerialized.HandleCommand(new JObject
            {
                ["targets"] = new JArray(globalId),
                ["properties"] = new JArray("m_Name")
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            JToken finding = result["data"]["findings"].Single(item =>
                item.Value<string>("component") == typeof(GameObject).FullName);
            Assert.AreEqual("m_Name", finding.Value<string>("property"));
            Assert.AreEqual(_owner.name, finding.Value<string>("value"));
        }

        [Test]
        public void DuplicateName_IsRejectedAsAmbiguous()
        {
            var duplicate = new GameObject(_owner.name);
            try
            {
                JObject result = Inspect("reference");
                Assert.IsFalse(result.Value<bool>("success"));
                Assert.AreEqual("TARGET_AMBIGUOUS", result.Value<string>("code"));
            }
            finally
            {
                Object.DestroyImmediate(duplicate);
            }
        }

        [Test]
        public void PrefabInstanceProperty_ReportsSourceAndOverride()
        {
            const string folder = "Assets/Temp/InspectSerializedTests";
            const string prefabPath = folder + "/Fixture.prefab";
            EnsureFolder(folder);
            var prefabSource = new GameObject("SerializedPrefabSource");
            prefabSource.AddComponent<SerializedInspectionFixture>().Reference = _referenced;
            PrefabUtility.SaveAsPrefabAsset(prefabSource, prefabPath);
            Object.DestroyImmediate(prefabSource);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            instance.name = "SerializedPrefabInstance";
            instance.GetComponent<SerializedInspectionFixture>().Reference = _owner;
            PrefabUtility.RecordPrefabInstancePropertyModifications(instance.GetComponent<SerializedInspectionFixture>());
            try
            {
                JObject result = ToJObject(InspectSerialized.HandleCommand(new JObject
                {
                    ["targets"] = new JArray(new JObject
                    {
                        ["target"] = instance.name,
                        ["component"] = typeof(SerializedInspectionFixture).FullName
                    }),
                    ["properties"] = new JArray("reference"),
                    ["includePrefabProvenance"] = true
                }));

                JToken prefab = result["data"]["findings"][0]["prefab"];
                Assert.IsTrue(prefab.Value<bool>("is_instance"), result.ToString());
                Assert.AreEqual(prefabPath, prefab.Value<string>("source_asset"));
                Assert.IsTrue(prefab.Value<bool>("is_override"), result.ToString());
            }
            finally
            {
                Object.DestroyImmediate(instance);
                SafeDeleteAsset(prefabPath);
                SafeDeleteAsset(folder);
            }
        }

        [Test]
        public void BrokenAssetReference_IsReturnedAsStructuredFinding()
        {
            const string folder = "Assets/Temp/InspectSerializedMissingReference";
            const string holderPath = folder + "/Holder.asset";
            const string referencedPath = folder + "/Referenced.asset";
            EnsureFolder(folder);
            var referencedAsset = new Texture2D(1, 1);
            AssetDatabase.CreateAsset(referencedAsset, referencedPath);
            var holder = ScriptableObject.CreateInstance<SerializedMissingReferenceFixture>();
            holder.Reference = referencedAsset;
            AssetDatabase.CreateAsset(holder, holderPath);
            AssetDatabase.SaveAssets();
            string holderId = GlobalObjectId.GetGlobalObjectIdSlow(holder).ToString();

            AssetDatabase.DeleteAsset(referencedPath);
            UnityEngine.Resources.UnloadAsset(holder);
            holder = AssetDatabase.LoadAssetAtPath<SerializedMissingReferenceFixture>(holderPath);
            try
            {
                JObject result = ToJObject(InspectSerialized.HandleCommand(new JObject
                {
                    ["targets"] = new JArray(holderId),
                    ["properties"] = new JArray("reference"),
                    ["includeMissingReferences"] = true
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                JToken finding = result["data"]["findings"][0];
                Assert.AreEqual("object_reference", finding.Value<string>("value_kind"));
                Assert.IsTrue(finding.Value<bool>("missing"), result.ToString());
                Assert.IsFalse(finding.Value<bool>("is_null"), result.ToString());
            }
            finally
            {
                SafeDeleteAsset(holderPath);
                SafeDeleteAsset(referencedPath);
                SafeDeleteAsset(folder);
            }
        }

        private JObject Inspect(string property)
        {
            return ToJObject(InspectSerialized.HandleCommand(new JObject
            {
                ["targets"] = new JArray(new JObject
                {
                    ["target"] = _owner.name,
                    ["component"] = typeof(SerializedInspectionFixture).FullName
                }),
                ["properties"] = new JArray(property),
                ["includePrefabProvenance"] = true
            }));
        }
    }

}
