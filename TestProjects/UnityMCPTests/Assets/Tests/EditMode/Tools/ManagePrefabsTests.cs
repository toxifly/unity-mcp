using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using MCPForUnity.Editor.Tools.Prefabs;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManagePrefabsTests
    {
        private const string TempDirectory = "Assets/Temp/ManagePrefabsTests";

        [SetUp]
        public void SetUp()
        {
            StageUtility.GoToMainStage();
            EnsureTempDirectoryExists();
        }

        [TearDown]
        public void TearDown()
        {
            StageUtility.GoToMainStage();
            
            // Clean up temp directory after each test
            if (AssetDatabase.IsValidFolder(TempDirectory))
            {
                AssetDatabase.DeleteAsset(TempDirectory);
            }
            
            // Clean up empty parent folders to avoid debris
            CleanupEmptyParentFolders(TempDirectory);
        }

        [Test]
        public void OpenStage_OpensPrefabInIsolation()
        {
            string prefabPath = CreateTestPrefab("OpenStageCube");

            try
            {
                var openParams = new JObject
                {
                    ["action"] = "open_stage",
                    ["prefabPath"] = prefabPath
                };

                var openResult = ToJObject(ManagePrefabs.HandleCommand(openParams));

                Assert.IsTrue(openResult.Value<bool>("success"), "open_stage should succeed for a valid prefab.");

                UnityEditor.SceneManagement.PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                Assert.IsNotNull(stage, "Prefab stage should be open after open_stage.");
                Assert.AreEqual(prefabPath, stage.assetPath, "Opened stage should match prefab path.");

                var stageInfo = ToJObject(MCPForUnity.Editor.Resources.Editor.PrefabStage.HandleCommand(new JObject()));
                Assert.IsTrue(stageInfo.Value<bool>("success"), "get_prefab_stage should succeed when stage is open.");

                var data = stageInfo["data"] as JObject;
                Assert.IsNotNull(data, "Stage info should include data payload.");
                Assert.IsTrue(data.Value<bool>("isOpen"));
                Assert.AreEqual(prefabPath, data.Value<string>("assetPath"));
            }
            finally
            {
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(prefabPath);
            }
        }

        [Test]
        public void CloseStage_ReturnsSuccess_WhenNoStageOpen()
        {
            StageUtility.GoToMainStage();
            var closeResult = ToJObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "close_stage"
            }));

            Assert.IsTrue(closeResult.Value<bool>("success"), "close_stage should succeed even if no stage is open.");
        }

        [Test]
        public void CloseStage_ClosesOpenPrefabStage()
        {
            string prefabPath = CreateTestPrefab("CloseStageCube");

            try
            {
                ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "open_stage",
                    ["prefabPath"] = prefabPath
                });

                var closeResult = ToJObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "close_stage"
                }));

                Assert.IsTrue(closeResult.Value<bool>("success"), "close_stage should succeed when stage is open.");
                StringAssert.Contains(prefabPath, closeResult.Value<string>("message"), "close_stage message should include the prefab path.");
                Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage(), "Prefab stage should be closed after close_stage.");
            }
            finally
            {
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(prefabPath);
            }
        }

        [Test]
        public void ApplyInstanceOverrides_AppliesChangesToPrefabAsset()
        {
            EnsureTempDirectoryExists();
            StageUtility.GoToMainStage();

            string prefabPath = CreateTestPrefab("ApplyOverridesCube");
            GameObject instance = null;

            try
            {
                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.IsNotNull(prefabAsset, "Prefab asset should load for instantiation.");

                instance = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                Assert.IsNotNull(instance, "PrefabUtility.InstantiatePrefab should return a GameObject instance.");

                instance.transform.localScale = new Vector3(3f, 3f, 3f);

                var applyResult = ToJObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "apply_instance_overrides",
                    ["target"] = instance.GetInstanceID(),
                }));

                Assert.IsTrue(applyResult.Value<bool>("success"), applyResult.ToString());

                var reloadedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.IsNotNull(reloadedPrefab, "Prefab asset should still exist after apply.");
                Assert.AreEqual(new Vector3(3f, 3f, 3f), reloadedPrefab.transform.localScale, "Prefab asset should reflect applied overrides.");
            }
            finally
            {
                if (instance != null)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(instance))
                    {
                        PrefabUtility.UnpackPrefabInstance(
                            instance,
                            PrefabUnpackMode.Completely,
                            InteractionMode.AutomatedAction
                        );
                    }
                    UnityEngine.Object.DestroyImmediate(instance, true);
                }

                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(prefabPath) != null)
                {
                    AssetDatabase.DeleteAsset(prefabPath);
                }
            }
        }

        [Test]
        public void RevertInstanceOverrides_RevertsChangesOnInstance()
        {
            EnsureTempDirectoryExists();
            StageUtility.GoToMainStage();

            string prefabPath = CreateTestPrefab("RevertOverridesCube");
            GameObject instance = null;

            try
            {
                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                instance = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                Assert.IsNotNull(instance);

                instance.transform.localScale = new Vector3(2f, 2f, 2f);
                Assert.AreEqual(new Vector3(2f, 2f, 2f), instance.transform.localScale);

                var revertResult = ToJObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "revert_instance_overrides",
                    ["target"] = instance.GetInstanceID(),
                }));

                Assert.IsTrue(revertResult.Value<bool>("success"), revertResult.ToString());
                Assert.AreEqual(Vector3.one, instance.transform.localScale, "Instance scale should revert to prefab defaults.");
            }
            finally
            {
                if (instance != null)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(instance))
                    {
                        PrefabUtility.UnpackPrefabInstance(
                            instance,
                            PrefabUnpackMode.Completely,
                            InteractionMode.AutomatedAction
                        );
                    }
                    UnityEngine.Object.DestroyImmediate(instance, true);
                }

                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(prefabPath) != null)
                {
                    AssetDatabase.DeleteAsset(prefabPath);
                }
            }
        }

        [Test]
        public void UnpackInstance_UnpacksPrefabInstance()
        {
            EnsureTempDirectoryExists();
            StageUtility.GoToMainStage();

            string prefabPath = CreateTestPrefab("UnpackInstanceCube");
            GameObject instance = null;

            try
            {
                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                instance = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                Assert.IsNotNull(instance);
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(instance), "Instance should initially be a prefab instance.");

                var unpackResult = ToJObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "unpack_instance",
                    ["target"] = instance.GetInstanceID(),
                    ["unpackMode"] = "Completely",
                }));

                Assert.IsTrue(unpackResult.Value<bool>("success"), unpackResult.ToString());
                Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(instance), "Instance should no longer be part of a prefab instance after unpack.");
            }
            finally
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance, true);
                }

                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(prefabPath) != null)
                {
                    AssetDatabase.DeleteAsset(prefabPath);
                }
            }
        }

        [Test]
        public void SaveOpenStage_SavesDirtyChanges()
        {
            string prefabPath = CreateTestPrefab("SaveStageCube");

            try
            {
                ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "open_stage",
                    ["prefabPath"] = prefabPath
                });

                UnityEditor.SceneManagement.PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                Assert.IsNotNull(stage, "Stage should be open before modifying.");

                stage.prefabContentsRoot.transform.localScale = new Vector3(2f, 2f, 2f);

                var saveResult = ToJObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "save_open_stage"
                }));

                Assert.IsTrue(saveResult.Value<bool>("success"), "save_open_stage should succeed when stage is open.");
                Assert.IsFalse(stage.scene.isDirty, "Stage scene should not be dirty after saving.");

                GameObject reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.AreEqual(new Vector3(2f, 2f, 2f), reloaded.transform.localScale, "Saved prefab asset should include changes from open stage.");
            }
            finally
            {
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(prefabPath);
            }
        }

        [Test]
        public void SaveOpenStage_ReturnsError_WhenNoStageOpen()
        {
            StageUtility.GoToMainStage();

            var saveResult = ToJObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "save_open_stage"
            }));

            Assert.IsFalse(saveResult.Value<bool>("success"), "save_open_stage should fail when no stage is open.");
        }

        [Test]
        public void CreateFromGameObject_CreatesPrefabAndLinksInstance()
        {
            EnsureTempDirectoryExists();
            StageUtility.GoToMainStage();

            string prefabPath = Path.Combine(TempDirectory, "SceneObjectSaved.prefab").Replace('\\', '/');
            GameObject sceneObject = new GameObject("ScenePrefabSource");

            try
            {
                var result = ToJObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "create_from_gameobject",
                    ["target"] = sceneObject.name,
                    ["prefabPath"] = prefabPath
                }));

                Assert.IsTrue(result.Value<bool>("success"), "create_from_gameobject should succeed for a valid scene object.");

                var data = result["data"] as JObject;
                Assert.IsNotNull(data, "Response data should include prefab information.");

                string savedPath = data.Value<string>("prefabPath");
                Assert.AreEqual(prefabPath, savedPath, "Returned prefab path should match the requested path.");

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(savedPath);
                Assert.IsNotNull(prefabAsset, "Prefab asset should exist at the saved path.");

                int instanceId = data.Value<int>("instanceId");
                var linkedInstance = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                Assert.IsNotNull(linkedInstance, "Linked instance should resolve from instanceId.");
                Assert.AreEqual(savedPath, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(linkedInstance), "Instance should be connected to the new prefab.");

                sceneObject = linkedInstance;
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(prefabPath) != null)
                {
                    AssetDatabase.DeleteAsset(prefabPath);
                }

                if (sceneObject != null)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(sceneObject))
                    {
                        PrefabUtility.UnpackPrefabInstance(
                            sceneObject,
                            PrefabUnpackMode.Completely,
                            InteractionMode.AutomatedAction
                        );
                    }
                    UnityEngine.Object.DestroyImmediate(sceneObject, true);
                }
            }
        }

        [Test]
        public void CreateFromGameObject_SupportsInstanceIdTarget()
        {
            EnsureTempDirectoryExists();
            StageUtility.GoToMainStage();

            string prefabPath = Path.Combine(TempDirectory, "SceneObjectSavedById.prefab").Replace('\\', '/');
            GameObject sceneObject = new GameObject("ScenePrefabSourceById");

            try
            {
                var result = ToJObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "create_from_gameobject",
                    ["target"] = sceneObject.GetInstanceID(),
                    ["prefabPath"] = prefabPath
                }));

                Assert.IsTrue(result.Value<bool>("success"), "create_from_gameobject should succeed for an instance ID target.");

                var data = result["data"] as JObject;
                Assert.IsNotNull(data, "Response data should include prefab information.");

                string savedPath = data.Value<string>("prefabPath");
                Assert.AreEqual(prefabPath, savedPath, "Returned prefab path should match the requested path.");

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(savedPath);
                Assert.IsNotNull(prefabAsset, "Prefab asset should exist at the saved path.");

                int instanceId = data.Value<int>("instanceId");
                var linkedInstance = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                Assert.IsNotNull(linkedInstance, "Linked instance should resolve from instanceId.");
                Assert.AreEqual(savedPath, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(linkedInstance), "Instance should be connected to the new prefab.");

                sceneObject = linkedInstance;
            }
            finally
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(prefabPath) != null)
                {
                    AssetDatabase.DeleteAsset(prefabPath);
                }

                if (sceneObject != null)
                {
                    if (PrefabUtility.IsPartOfPrefabInstance(sceneObject))
                    {
                        PrefabUtility.UnpackPrefabInstance(
                            sceneObject,
                            PrefabUnpackMode.Completely,
                            InteractionMode.AutomatedAction
                        );
                    }
                    UnityEngine.Object.DestroyImmediate(sceneObject, true);
                }
            }
        }

        private static string CreateTestPrefab(string name)
        {
            EnsureTempDirectoryExists();

            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            temp.name = name;

            string path = Path.Combine(TempDirectory, name + ".prefab").Replace('\\', '/');
            PrefabUtility.SaveAsPrefabAsset(temp, path, out bool success);
            UnityEngine.Object.DestroyImmediate(temp);

            Assert.IsTrue(success, "PrefabUtility.SaveAsPrefabAsset should succeed for test prefab.");
            return path;
        }

        private static void EnsureTempDirectoryExists()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
            {
                AssetDatabase.CreateFolder("Assets", "Temp");
            }

            if (!AssetDatabase.IsValidFolder(TempDirectory))
            {
                AssetDatabase.CreateFolder("Assets/Temp", "ManagePrefabsTests");
            }
        }
    }
}
