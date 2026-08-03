using System.Linq;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TestNamespace;
using UnityEditor;
using UnityEditor.SceneManagement;
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
        public void PrefabPath_InspectsOnlyTheHeadlesslyLoadedPrefabHierarchy()
        {
            const string folder = "Assets/Temp/InspectSerializedPrefabAssetTests";
            const string prefabPath = folder + "/SerializedOwner.prefab";
            EnsureFolder(folder);
            var prefabRoot = new GameObject(_owner.name);
            var fixture = prefabRoot.AddComponent<SerializedInspectionFixture>();
            var serializedFixture = new SerializedObject(fixture);
            serializedFixture.FindProperty("label").stringValue = "prefab fixture";
            serializedFixture.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            Object.DestroyImmediate(prefabRoot);

            try
            {
                JObject result = ToJObject(InspectSerialized.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray(new JObject
                    {
                        ["target"] = _owner.name,
                        ["component"] = typeof(SerializedInspectionFixture).FullName
                    }),
                    ["properties"] = new JArray("label"),
                    ["includePrefabProvenance"] = true
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.AreEqual(prefabPath, result["data"].Value<string>("prefab_path"));
                JToken finding = result["data"]["findings"][0];
                Assert.AreEqual("prefab fixture", finding.Value<string>("value"));
                Assert.IsFalse(finding["prefab"].Value<bool>("is_instance"));
                Assert.IsNull(finding["prefab"].Value<string>("source_asset"));
                Assert.IsNull(finding["prefab"].Value<string>("source_global_object_id"));
                Assert.IsFalse(finding["prefab"].Value<bool>("is_override"));

                string stableId = finding.Value<string>("global_object_id");
                Assert.IsNotEmpty(stableId, result.ToString());
                Assert.IsTrue(GlobalObjectId.TryParse(stableId, out GlobalObjectId parsedId));
                UnityEngine.Object stableObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsedId);
                Assert.IsNotNull(stableObject, stableId);
                Assert.AreEqual(prefabPath, AssetDatabase.GetAssetPath(stableObject));

                JObject roundTrip = ToJObject(InspectSerialized.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray(new JObject
                    {
                        ["global_object_id"] = stableId,
                        ["component"] = typeof(SerializedInspectionFixture).FullName
                    }),
                    ["properties"] = new JArray("label")
                }));
                Assert.IsTrue(roundTrip.Value<bool>("success"), roundTrip.ToString());
                Assert.AreEqual("prefab fixture", roundTrip["data"]["findings"][0].Value<string>("value"));
            }
            finally
            {
                SafeDeleteAsset(prefabPath);
                SafeDeleteAsset(folder);
            }
        }

        [Test]
        public void PrefabPath_NestedInstanceReportsRealSourceAndOverride()
        {
            const string folder = "Assets/Temp/InspectSerializedNestedPrefabTests";
            const string nestedPath = folder + "/Nested.prefab";
            const string outerPath = folder + "/Outer.prefab";
            EnsureFolder(folder);

            var nestedRoot = new GameObject("Nested");
            nestedRoot.AddComponent<SerializedInspectionFixture>();
            PrefabUtility.SaveAsPrefabAsset(nestedRoot, nestedPath);
            Object.DestroyImmediate(nestedRoot);

            var outerRoot = new GameObject("Outer");
            var nestedInstance = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(nestedPath));
            nestedInstance.transform.SetParent(outerRoot.transform, false);
            var nestedFixture = nestedInstance.GetComponent<SerializedInspectionFixture>();
            var serializedFixture = new SerializedObject(nestedFixture);
            serializedFixture.FindProperty("label").stringValue = "outer override";
            serializedFixture.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.RecordPrefabInstancePropertyModifications(nestedFixture);
            PrefabUtility.SaveAsPrefabAsset(outerRoot, outerPath);
            Object.DestroyImmediate(outerRoot);

            try
            {
                JObject result = ToJObject(InspectSerialized.HandleCommand(new JObject
                {
                    ["prefabPath"] = outerPath,
                    ["targets"] = new JArray(new JObject
                    {
                        ["target"] = "Nested",
                        ["component"] = typeof(SerializedInspectionFixture).FullName
                    }),
                    ["properties"] = new JArray("label"),
                    ["includePrefabProvenance"] = true
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                JToken finding = result["data"]["findings"][0];
                Assert.AreEqual("outer override", finding.Value<string>("value"));
                Assert.IsTrue(finding["prefab"].Value<bool>("is_instance"), result.ToString());
                Assert.AreEqual(nestedPath, finding["prefab"].Value<string>("source_asset"));
                Assert.IsTrue(finding["prefab"].Value<bool>("is_override"), result.ToString());

                string sourceId = finding["prefab"].Value<string>("source_global_object_id");
                Assert.IsTrue(GlobalObjectId.TryParse(sourceId, out GlobalObjectId parsedSourceId));
                UnityEngine.Object sourceObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsedSourceId);
                Assert.IsNotNull(sourceObject, sourceId);
                Assert.AreEqual(nestedPath, AssetDatabase.GetAssetPath(sourceObject));
            }
            finally
            {
                SafeDeleteAsset(outerPath);
                SafeDeleteAsset(nestedPath);
                SafeDeleteAsset(folder);
            }
        }

        [Test]
        public void PrefabPath_UsesMatchingLivePrefabStageWithUnsavedChanges()
        {
            const string folder = "Assets/Temp/InspectSerializedLiveStageTests";
            const string prefabPath = folder + "/LiveStage.prefab";
            EnsureFolder(folder);
            StageUtility.GoToMainStage();
            var prefabRoot = new GameObject("LiveStage");
            prefabRoot.AddComponent<SerializedInspectionFixture>();
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            Object.DestroyImmediate(prefabRoot);

            try
            {
                var stage = PrefabStageUtility.OpenPrefab(prefabPath);
                Assert.IsNotNull(stage);
                var fixture = stage.prefabContentsRoot.GetComponent<SerializedInspectionFixture>();
                var serializedFixture = new SerializedObject(fixture);
                serializedFixture.FindProperty("label").stringValue = "unsaved stage value";
                serializedFixture.ApplyModifiedPropertiesWithoutUndo();

                JObject result = ToJObject(InspectSerialized.HandleCommand(new JObject
                {
                    ["prefabPath"] = prefabPath,
                    ["targets"] = new JArray(new JObject
                    {
                        ["target"] = "LiveStage",
                        ["component"] = typeof(SerializedInspectionFixture).FullName
                    }),
                    ["properties"] = new JArray("label")
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.AreEqual("unsaved stage value", result["data"]["findings"][0].Value<string>("value"));
                Assert.AreSame(stage, PrefabStageUtility.GetCurrentPrefabStage());
            }
            finally
            {
                StageUtility.GoToMainStage();
                SafeDeleteAsset(prefabPath);
                SafeDeleteAsset(folder);
            }
        }

        [Test]
        public void PrefabPath_DoesNotRewritePackagePathsUnderAssets()
        {
            const string packagePath = "Packages/com.example.missing/Widget.prefab";
            JObject result = ToJObject(InspectSerialized.HandleCommand(new JObject
            {
                ["prefabPath"] = packagePath,
                ["targets"] = new JArray("Widget"),
                ["properties"] = new JArray("m_Name")
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.AreEqual("PREFAB_NOT_FOUND", result.Value<string>("code"));
            StringAssert.Contains(packagePath, result.ToString());
            StringAssert.DoesNotContain("Assets/Packages", result.ToString());
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
