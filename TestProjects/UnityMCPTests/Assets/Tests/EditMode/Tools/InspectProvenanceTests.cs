using MCPForUnity.Editor.Tools;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.Linq;
using TestNamespace;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class InspectProvenanceTests
    {
        private const string Folder = "Assets/Temp/InspectProvenanceTests";
        private const string PrefabPath = Folder + "/Fixture.prefab";

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in UnityFindObjectsCompat.FindAll(typeof(GameObject), true))
            {
                if (root.name.StartsWith("Provenance", System.StringComparison.Ordinal))
                    Object.DestroyImmediate(root);
            }
            SafeDeleteAsset(PrefabPath);
            SafeDeleteAsset(Folder);
        }

        [Test]
        public void SceneObject_ReportsStableIdentityAndScenePath()
        {
            var sceneObject = new GameObject("ProvenanceSceneObject");
            JObject result = Inspect(sceneObject.name);

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            JToken finding = result["data"]["findings"][0];
            Assert.IsNotEmpty(finding.Value<string>("global_object_id"));
            // The tool reports empty scene paths (untitled scenes) as null.
            string activeScenePath = EditorSceneManager.GetActiveScene().path;
            Assert.AreEqual(string.IsNullOrEmpty(activeScenePath) ? null : activeScenePath, finding.Value<string>("scene_path"));
            Assert.IsFalse(finding["prefab"].Value<bool>("is_instance"));
        }

        [Test]
        public void PrefabInstance_ReportsRootsSourceAndPropertyOverridePaths()
        {
            GameObject instance = CreateInstance();
            var component = instance.GetComponent<SerializedInspectionFixture>();
            var serialized = new SerializedObject(component);
            serialized.FindProperty("label").stringValue = "overridden";
            serialized.ApplyModifiedProperties();

            JObject result = Inspect(instance.name);

            JToken finding = result["data"]["findings"][0];
            JToken prefab = finding["prefab"];
            Assert.IsTrue(prefab.Value<bool>("is_instance"), result.ToString());
            Assert.AreEqual(PrefabPath, prefab.Value<string>("source_asset"));
            Assert.IsNotNull(prefab["nearest_instance_root"]);
            Assert.IsNotNull(prefab["outermost_instance_root"]);
            Assert.IsNotNull(prefab["source_object"]);
            Assert.IsTrue(prefab["override_property_paths"].Any(item => item.Value<string>("property") == "label"), result.ToString());
        }

        [Test]
        public void PrefabInstance_DefaultOverridesAreExcludedFromPropertyOverridePaths()
        {
            GameObject instance = CreateInstance();
            PropertyModification[] modifications = PrefabUtility.GetPropertyModifications(instance);

            Assert.IsTrue(modifications.Any(PrefabUtility.IsDefaultOverride), "Fixture should contain Unity default overrides.");
            Assert.IsFalse(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false));

            JObject result = Inspect(instance.name);

            JToken prefab = result["data"]["findings"][0]["prefab"];
            Assert.IsFalse(prefab.Value<bool>("has_any_overrides"), result.ToString());
            Assert.AreEqual(0, prefab.Value<int>("override_property_count"), result.ToString());
            Assert.IsEmpty(prefab["override_property_paths"], result.ToString());
        }

        [Test]
        public void PrefabInstance_ReportsAddedAndRemovedComponents()
        {
            GameObject instance = CreateInstance();
            instance.AddComponent<BoxCollider>();
            SerializedInspectionFixture sourceComponent = AssetDatabase
                .LoadAssetAtPath<GameObject>(PrefabPath)
                .GetComponent<SerializedInspectionFixture>();
            Undo.DestroyObjectImmediate(instance.GetComponent<SerializedInspectionFixture>());

            JObject result = Inspect(instance.name);

            JToken prefab = result["data"]["findings"][0]["prefab"];
            Assert.IsTrue(prefab["added_components"].Any(item => item.Value<string>("type") == typeof(BoxCollider).FullName), result.ToString());
            Assert.IsTrue(prefab["removed_components"].Any(item => item.Value<string>("type") == sourceComponent.GetType().FullName), result.ToString());
        }

        [Test]
        public void AssetPathTarget_ReportsAssetIdentityWithoutComponentDump()
        {
            CreateInstance();
            JObject result = Inspect(PrefabPath);

            JToken finding = result["data"]["findings"][0];
            Assert.AreEqual(PrefabPath, finding.Value<string>("asset_path"));
            Assert.IsNotEmpty(finding.Value<string>("asset_guid"));
            Assert.IsNull(finding["prefab"]["override_property_paths"]?.First);
        }

        private static GameObject CreateInstance()
        {
            EnsureFolder(Folder);
            var source = new GameObject("ProvenancePrefabSource");
            source.AddComponent<SerializedInspectionFixture>();
            PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
            Object.DestroyImmediate(source);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            instance.name = "ProvenancePrefabInstance";
            return instance;
        }

        private static JObject Inspect(string target)
        {
            return ToJObject(InspectProvenance.HandleCommand(new JObject
            {
                ["targets"] = new JArray(target),
                ["includePropertyOverrides"] = true,
                ["includeComponentOverrides"] = true
            }));
        }
    }
}
