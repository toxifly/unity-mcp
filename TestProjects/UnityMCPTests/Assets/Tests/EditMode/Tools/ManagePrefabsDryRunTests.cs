using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MCPForUnity.Editor.Tools.Prefabs;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// A dry run must never write: modify_contents previews its changes in memory, and actions
    /// without a preview path reject dryRun instead of running for real.
    /// </summary>
    public class ManagePrefabsDryRunTests
    {
        private const string TempDirectory = "Assets/Temp/ManagePrefabsDryRunTests";

        [SetUp]
        public void SetUp()
        {
            StageUtility.GoToMainStage();
            EnsureFolder(TempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            StageUtility.GoToMainStage();
            if (AssetDatabase.IsValidFolder(TempDirectory))
            {
                AssetDatabase.DeleteAsset(TempDirectory);
            }
            CleanupEmptyParentFolders(TempDirectory);
        }

        [Test]
        public void ModifyContents_DryRun_ReportsChangesWithoutSaving()
        {
            string prefabPath = CreateCubePrefab("DryRunModify");
            byte[] before = File.ReadAllBytes(prefabPath);

            var result = ToJObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "modify_contents",
                ["prefabPath"] = prefabPath,
                ["position"] = new JArray(1f, 2f, 3f),
                ["dryRun"] = true
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var preview = result["data"]?["change_preview"] as JObject;
            Assert.IsNotNull(preview, result.ToString());
            Assert.IsTrue(preview.Value<bool>("dry_run"));
            Assert.IsFalse(preview.Value<bool>("committed"));
            var changes = preview["changes"] as JArray;
            Assert.IsNotNull(changes);
            Assert.IsTrue(changes.Any(c => (c.Value<string>("Property") ?? "").StartsWith("m_LocalPosition", StringComparison.Ordinal)),
                $"Expected a m_LocalPosition change, got: {changes}");

            CollectionAssert.AreEqual(before, File.ReadAllBytes(prefabPath), "A dry run must leave the prefab file untouched.");
            GameObject reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.AreEqual(Vector3.zero, reloaded.transform.localPosition);
        }

        [Test]
        public void ModifyContents_WithoutDryRun_StillSaves()
        {
            string prefabPath = CreateCubePrefab("RealModify");

            var result = ToJObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "modify_contents",
                ["prefabPath"] = prefabPath,
                ["position"] = new JArray(1f, 2f, 3f),
                ["dryRun"] = false
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNull(result["data"]?["change_preview"]);
            GameObject reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), reloaded.transform.localPosition);
        }

        [Test]
        public void OpenPrefabStage_DryRun_IsRejectedWithoutOpeningStage()
        {
            string prefabPath = CreateCubePrefab("DryRunStage");

            var result = ToJObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "open_prefab_stage",
                ["prefabPath"] = prefabPath,
                ["dryRun"] = true
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.AreEqual("DRY_RUN_UNSUPPORTED", result.Value<string>("code"));
            Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage());
        }

        [Test]
        public void CreateFromGameObject_DryRun_IsRejectedWithoutCreatingAsset()
        {
            string prefabPath = Path.Combine(TempDirectory, "NotCreated.prefab").Replace('\\', '/');
            GameObject sceneObject = new GameObject("DryRunSource");

            try
            {
                var result = ToJObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "create_from_gameobject",
                    ["target"] = sceneObject.name,
                    ["prefabPath"] = prefabPath,
                    ["dryRun"] = true
                }));

                Assert.IsFalse(result.Value<bool>("success"));
                Assert.AreEqual("DRY_RUN_UNSUPPORTED", result.Value<string>("code"));
                Assert.IsFalse(File.Exists(prefabPath));
                Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(sceneObject));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sceneObject);
            }
        }

        private static string CreateCubePrefab(string name)
        {
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            temp.name = name;
            string path = Path.Combine(TempDirectory, name + ".prefab").Replace('\\', '/');
            PrefabUtility.SaveAsPrefabAsset(temp, path, out bool success);
            UnityEngine.Object.DestroyImmediate(temp);
            AssetDatabase.Refresh();
            if (!success) throw new Exception($"Failed to create test prefab at {path}");
            return path;
        }
    }
}
