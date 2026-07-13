using System.IO;
using System.Linq;
using MCPForUnity.Editor.Services.MutationTransactions;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Editor.Tools.GameObjects;
using MCPForUnity.Editor.Tools.Prefabs;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using MCPForUnity.Runtime.Helpers;

namespace MCPForUnityTests.Editor.Services
{
    public class MutationTransactionTests
    {
        private GameObject root;
        private Scene originalScene;
        private Scene testScene;
        private const string ScenePath = "Assets/Temp/MutationTransactionTests.unity";
        private const string PrefabPath = "Assets/Temp/AtomicMutationTest.prefab";
        private const string DryRunPrefabFolder = "Assets/Temp/DryRunPrefab/Nested";
        private const string DryRunPrefabPath = DryRunPrefabFolder + "/Preview.prefab";
        private const string DirtyAssetPath = "Assets/Temp/MutationTransactionDirtyAsset.asset";

        [SetUp]
        public void SetUp()
        {
            originalScene = EditorSceneManager.GetActiveScene();
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (string.IsNullOrEmpty(originalScene.path))
            {
                // An untitled active scene (batchmode's default) blocks additive
                // scene creation, so replace it instead of adding alongside it.
                testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                originalScene = testScene;
            }
            else
            {
                testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            }
            EditorSceneManager.SetActiveScene(testScene);
            root = new GameObject("MutationTransactionRoot");
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null)
                Object.DestroyImmediate(root);
            if (originalScene.IsValid() && originalScene.isLoaded)
                EditorSceneManager.SetActiveScene(originalScene);
            if (testScene.IsValid() && testScene.isLoaded)
            {
                if (SceneManager.sceneCount > 1)
                    EditorSceneManager.CloseScene(testScene, true);
                else
                    // The last loaded scene cannot be closed; replace it so the
                    // saved scene path is released before the asset is deleted.
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            AssetDatabase.DeleteAsset(ScenePath);
            AssetDatabase.DeleteAsset(PrefabPath);
            AssetDatabase.DeleteAsset(DirtyAssetPath);
            AssetDatabase.DeleteAsset("Assets/Temp/DryRunPrefab");
        }

        [Test]
        public void Begin_RejectsPredirtyTargetSceneByDefault()
        {
            EditorSceneManager.MarkSceneDirty(root.scene);

            MutationTransactionException exception = Assert.Throws<MutationTransactionException>(
                () => MutationTransaction.Begin(new Object[] { root }));

            Assert.AreEqual("SCENE_ALREADY_DIRTY", exception.Code);
        }

        [Test]
        public void Changes_ReportsNormalizedModifiedProperty()
        {
            using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
            {
                root.SetActive(false);

                SerializedChange change = transaction.Changes.Single(item =>
                    item.ComponentType == typeof(GameObject).FullName && item.Property == "m_IsActive");
                Assert.AreEqual("modified", change.Kind);
                Assert.AreEqual("true", change.Before);
                Assert.AreEqual("false", change.After);
            }

            Assert.IsTrue(root.activeSelf, "Disposing an uncommitted transaction must roll the edit back.");
            Assert.IsFalse(root.scene.isDirty);
        }

        [Test]
        public void Changes_TreatsRenamedStringAsSingleScalarProperty()
        {
            string originalName = root.name;
            using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
            {
                root.name = "RenamedMutationTransactionRoot";

                SerializedChange[] changes = transaction.Changes
                    .Where(item => item.ComponentType == typeof(GameObject).FullName)
                    .ToArray();

                Assert.AreEqual(1, changes.Length);
                Assert.AreEqual("m_Name", changes[0].Property);
                Assert.AreEqual(originalName, changes[0].Before);
                Assert.AreEqual(root.name, changes[0].After);
            }

            Assert.AreEqual(originalName, root.name);
            Assert.IsFalse(root.scene.isDirty);
        }

        [Test]
        public void Rollback_RemovesUndoRegisteredCreatedObjects()
        {
            using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
            {
                var child = new GameObject("TransactionalChild");
                Undo.RegisterCreatedObjectUndo(child, "Create child");
                child.transform.SetParent(root.transform, false);

                Assert.IsTrue(transaction.Changes.Any(item =>
                    item.Kind == "added" && item.ObjectPath.Contains("TransactionalChild")));
                transaction.Rollback();
            }

            Assert.IsNull(GameObject.Find("TransactionalChild"));
            Assert.AreEqual(0, root.transform.childCount);
            Assert.IsFalse(root.scene.isDirty);
        }

        [Test]
        public void CommitWithoutSave_KeepsMutationAndClosesUndoGroup()
        {
            using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
            {
                root.SetActive(false);
                var changes = transaction.Commit(save: false);
                Assert.IsTrue(changes.Any(item => item.Property == "m_IsActive"));
            }

            Assert.IsFalse(root.activeSelf);
        }

        [Test]
        public void CommitWithSave_AllowsCreatedObjectIdentityToStabilize()
        {
            GameObject child;
            using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
            {
                child = new GameObject("PersistentTransactionalChild");
                Undo.RegisterCreatedObjectUndo(child, "Create persistent child");
                child.transform.SetParent(root.transform, false);

                Assert.DoesNotThrow(() => transaction.Commit(save: true));
            }

            Assert.IsNotNull(child);
            Assert.AreEqual(root.transform, child.transform.parent);
            Assert.IsFalse(root.scene.isDirty);
            Assert.IsFalse(GlobalObjectId.GetGlobalObjectIdSlow(child).ToString().EndsWith("-0-0"));
        }

        [Test]
        public void Commit_WhenValidatorRejects_RollsBackWithStableCode()
        {
            using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
            {
                root.SetActive(false);

                MutationTransactionException exception = Assert.Throws<MutationTransactionException>(
                    () => transaction.Commit(changes => false, save: false));
                Assert.AreEqual("UNEXPECTED_SERIALIZED_CHANGES", exception.Code);
            }

            Assert.IsTrue(root.activeSelf);
            Assert.IsFalse(root.scene.isDirty);
        }

        [Test]
        public void ManageComponents_ChangeGuardAcceptsExpectedObject()
        {
            JObject response = JObject.FromObject(ManageComponents.HandleCommand(new JObject
            {
                ["action"] = "add",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["componentType"] = "BoxCollider",
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_objects"] = new JArray(root.name),
                    ["max_changed_objects"] = 2
                }
            }));

            Assert.IsTrue(response["success"].Value<bool>());
            Assert.IsNotNull(root.GetComponent<BoxCollider>());
            Assert.IsTrue(response["data"]["change_guard"]["committed"].Value<bool>());
        }

        [Test]
        public void ManageComponents_ChangeGuardRejectsAndRollsBackUnexpectedObject()
        {
            JObject response = JObject.FromObject(ManageComponents.HandleCommand(new JObject
            {
                ["action"] = "add",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["componentType"] = "BoxCollider",
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_objects"] = new JArray("SomeOtherObject")
                }
            }));

            Assert.IsFalse(response["success"].Value<bool>());
            Assert.AreEqual("UNEXPECTED_SERIALIZED_CHANGES", response["code"].Value<string>());
            Assert.IsNull(root.GetComponent<BoxCollider>());
            Assert.IsTrue(response["data"]["rolled_back"].Value<bool>());
            Assert.IsFalse(root.scene.isDirty);
        }

        [Test]
        public void ManageGameObject_CreateGuardRejectsAndRemovesCreatedObject()
        {
            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["name"] = "GuardedCreatedObject",
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_objects"] = new JArray("A different object")
                }
            }));

            Assert.IsFalse(response["success"].Value<bool>());
            Assert.AreEqual("UNEXPECTED_SERIALIZED_CHANGES", response["code"].Value<string>());
            Assert.IsNull(GameObject.Find("GuardedCreatedObject"));
            Assert.IsFalse(root.scene.isDirty);
        }

        [Test]
        public void ManageGameObject_DeleteDryRunRestoresDeletedObject()
        {
            int instanceId = root.GetInstanceIDCompat();

            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "delete",
                ["target"] = instanceId,
                ["searchMethod"] = "by_id",
                ["dryRun"] = true
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.IsTrue(response["data"]["change_preview"]["rolled_back"].Value<bool>());
            Assert.IsNotNull(GameObject.Find("MutationTransactionRoot"));
            Assert.IsFalse(testScene.isDirty);
        }

        [Test]
        public void ManageGameObject_DeleteGuardRejectionRestoresDeletedObject()
        {
            int instanceId = root.GetInstanceIDCompat();

            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "delete",
                ["target"] = instanceId,
                ["searchMethod"] = "by_id",
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_objects"] = new JArray("SomeOtherObject")
                }
            }));

            Assert.IsFalse(response["success"].Value<bool>(), response.ToString());
            Assert.AreEqual("UNEXPECTED_SERIALIZED_CHANGES", response["code"].Value<string>());
            Assert.IsTrue(response["data"]["rolled_back"].Value<bool>());
            Assert.IsNotNull(GameObject.Find("MutationTransactionRoot"));
            Assert.IsFalse(testScene.isDirty);
        }

        [Test]
        public void ManageGameObject_PropertyScopeAcceptsExpectedProperty()
        {
            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "modify",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["setActive"] = false,
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_properties"] = new JArray(root.name + ".GameObject.m_IsActive")
                }
            }));

            Assert.IsTrue(response["success"].Value<bool>());
            Assert.IsFalse(root.activeSelf);
            Assert.AreEqual(1, response["data"]["change_guard"]["changed_objects"].Value<int>());
        }

        [Test]
        public void ManageGameObject_ChangeGuardRejectsUnexpectedPropertyOnExpectedObject()
        {
            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "modify",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["setActive"] = false,
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_objects"] = new JArray(root.name),
                    ["expected_properties"] = new JArray("m_Name")
                }
            }));

            Assert.IsFalse(response["success"].Value<bool>(), response.ToString());
            Assert.AreEqual("UNEXPECTED_SERIALIZED_CHANGES", response["code"].Value<string>());
            Assert.IsTrue(root.activeSelf);
        }

        [Test]
        public void ManageGameObject_ChangeGuardRejectsExpectedPropertyOnUnexpectedObject()
        {
            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "modify",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["setActive"] = false,
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_objects"] = new JArray("SomeOtherObject"),
                    ["expected_properties"] = new JArray(root.name + ".GameObject.m_IsActive")
                }
            }));

            Assert.IsFalse(response["success"].Value<bool>(), response.ToString());
            Assert.AreEqual("UNEXPECTED_SERIALIZED_CHANGES", response["code"].Value<string>());
            Assert.IsTrue(root.activeSelf);
        }

        [Test]
        public void BeginAssets_FingerprintsPrefabChildComponents()
        {
            var prefabSource = new GameObject("FingerprintPrefabRoot");
            var child = new GameObject("FingerprintPrefabChild");
            child.transform.SetParent(prefabSource.transform, false);
            child.AddComponent<BoxCollider>();
            PrefabUtility.SaveAsPrefabAsset(prefabSource, PrefabPath);
            Object.DestroyImmediate(prefabSource);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            BoxCollider collider = prefab.transform.GetChild(0).GetComponent<BoxCollider>();
            using (MutationTransaction transaction = MutationTransaction.BeginAssets(new[] { PrefabPath }))
            {
                collider.enabled = false;

                SerializedChange change = transaction.Changes.Single(item =>
                    item.ComponentType == typeof(BoxCollider).FullName && item.Property == "m_Enabled");
                Assert.AreEqual("true", change.Before);
                Assert.AreEqual("false", change.After);
            }

            Assert.IsTrue(collider.enabled, "Rolling back must restore child component properties.");
        }

        [Test]
        public void Rollback_AfterAssetWasSaved_RestoresPredirtyInMemoryContents()
        {
            var asset = ScriptableObject.CreateInstance<MutationTransactionDirtyAsset>();
            asset.Value = "persisted value";
            AssetDatabase.CreateAsset(asset, DirtyAssetPath);
            AssetDatabase.SaveAssetIfDirty(asset);
            string fullPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, DirtyAssetPath);
            byte[] persistedBytes = File.ReadAllBytes(fullPath);

            asset.Value = "unsaved value";
            EditorUtility.SetDirty(asset);

            using (MutationTransaction transaction = MutationTransaction.BeginAssets(
                new[] { DirtyAssetPath },
                new MutationTransactionOptions { AllowDirtyAssets = true }))
            {
                asset.Value = "transaction value";
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
                transaction.Rollback();
            }

            Assert.AreEqual("unsaved value", asset.Value);
            Assert.IsTrue(EditorUtility.IsDirty(asset));
            CollectionAssert.AreEqual(persistedBytes, File.ReadAllBytes(fullPath));
        }

        [Test]
        public void ManageGameObject_DryRunReturnsChangesAndRollsBack()
        {
            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "modify",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["setActive"] = false,
                ["dryRun"] = true
            }));

            Assert.IsTrue(response["success"].Value<bool>());
            Assert.IsTrue(response["data"]["change_preview"]["dry_run"].Value<bool>());
            Assert.IsTrue(response["data"]["change_preview"]["rolled_back"].Value<bool>());
            Assert.IsTrue(response["data"]["change_preview"]["changes"].Any(change =>
                change["Property"].Value<string>() == "m_IsActive"));
            Assert.IsTrue(root.activeSelf);
            Assert.IsFalse(root.scene.isDirty);
        }

        [Test]
        public void ManageGameObject_CreatePrefabDryRunRemovesNewParentFolders()
        {
            Assert.IsFalse(AssetDatabase.IsValidFolder(DryRunPrefabFolder));

            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["name"] = "PrefabPreview",
                ["saveAsPrefab"] = true,
                ["prefabPath"] = DryRunPrefabFolder + "/Preview",
                ["dryRun"] = true
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.IsFalse(AssetDatabase.IsValidFolder("Assets/Temp/DryRunPrefab"));
            Assert.IsFalse(AssetDatabase.IsValidFolder(DryRunPrefabFolder));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(DryRunPrefabPath));
        }

        [Test]
        public void ManageGameObject_RenamePrefabInstanceDryRunRestoresCleanScene()
        {
            var prefabSource = new GameObject("SelectionManager");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(prefabSource, PrefabPath);
            Object.DestroyImmediate(prefabSource);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, testScene);
            // 6000.5+ renames prefab roots to the asset filename, so capture the
            // actual instance name instead of assuming the source object's name.
            string instanceName = instance.name;
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));
            Assert.IsFalse(testScene.isDirty);

            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "modify",
                ["target"] = instance.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["name"] = "RenamedSelectionManager",
                ["dryRun"] = true
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.AreEqual(instanceName, instance.name);
            Assert.IsFalse(
                testScene.isDirty,
                "Rolling back a prefab-instance override must restore the scene's initial clean state.");
        }

        [Test]
        public void ManageComponents_DryRunPreservesPredirtyScene()
        {
            EditorSceneManager.MarkSceneDirty(root.scene);

            JObject response = JObject.FromObject(ManageComponents.HandleCommand(new JObject
            {
                ["action"] = "add",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["componentType"] = "BoxCollider",
                ["dryRun"] = true
            }));

            Assert.IsTrue(response["success"].Value<bool>());
            Assert.IsNull(root.GetComponent<BoxCollider>());
            Assert.IsTrue(root.scene.isDirty);
            Assert.IsTrue(response["data"]["change_preview"]["changes"].Any());
        }

        [Test]
        public void ManagePrefabs_CreateAndReplacePreservesReferencesTransformAndOrder()
        {
            var before = new GameObject("BeforeAtomicTarget");
            var owner = new GameObject("AtomicReferenceOwner");
            var reference = owner.AddComponent<AtomicPrefabReferenceHolder>();
            reference.Target = root;
            root.transform.position = new Vector3(4f, 5f, 6f);
            root.transform.SetSiblingIndex(1);
            int siblingIndex = root.transform.GetSiblingIndex();
            Vector3 position = root.transform.position;
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));

            JObject response = JObject.FromObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "create_and_replace",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["prefabPath"] = PrefabPath
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            root = GameObject.Find("MutationTransactionRoot");
            Assert.IsNotNull(root);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(root));
            Assert.AreEqual(root, reference.Target);
            Assert.AreEqual(siblingIndex, root.transform.GetSiblingIndex());
            Assert.AreEqual(position, root.transform.position);
            Assert.IsFalse(testScene.isDirty);
            Object.DestroyImmediate(before);
            Object.DestroyImmediate(owner);
        }

        [Test]
        public void ManagePrefabs_CreateAndReplacePreservesReferenceFromAdditiveScene()
        {
            Scene referenceScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var owner = new GameObject("AdditiveSceneReferenceOwner");
            SceneManager.MoveGameObjectToScene(owner, referenceScene);
            var reference = owner.AddComponent<AtomicPrefabReferenceHolder>();
            reference.Target = root;

            try
            {
                JObject response = JObject.FromObject(ManagePrefabs.HandleCommand(new JObject
                {
                    ["action"] = "create_and_replace",
                    ["target"] = root.GetInstanceIDCompat(),
                    ["searchMethod"] = "by_id",
                    ["prefabPath"] = PrefabPath,
                    ["dirtyScenePolicy"] = "allow",
                    ["saveScene"] = false
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                root = testScene.GetRootGameObjects().Single(item => item.name == "MutationTransactionRoot");
                Assert.AreEqual(root, reference.Target);
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(root));
            }
            finally
            {
                if (referenceScene.IsValid() && referenceScene.isLoaded)
                    EditorSceneManager.CloseScene(referenceScene, true);
            }
        }

        [Test]
        public void ManagePrefabs_CreateAndReplaceDryRunRemovesPrefabAndRestoresScene()
        {
            JObject response = JObject.FromObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "create_and_replace",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["prefabPath"] = PrefabPath,
                ["dryRun"] = true
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.IsTrue(response["data"]["change_preview"]["rolled_back"].Value<bool>());
            Assert.AreNotEqual(0, response["data"]["instanceId"].Value<int>());
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(root));
            Assert.IsFalse(testScene.isDirty);
        }

        [Test]
        public void ManagePrefabs_CreateAndReplaceGuardFailureRollsBackBothAssetAndScene()
        {
            JObject response = JObject.FromObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "create_and_replace",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["prefabPath"] = PrefabPath,
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_objects"] = new JArray("SomeOtherObject")
                }
            }));

            Assert.IsFalse(response["success"].Value<bool>());
            Assert.AreEqual("UNEXPECTED_SERIALIZED_CHANGES", response["code"].Value<string>());
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(root));
            Assert.IsFalse(testScene.isDirty);
        }

        [Test]
        public void ManagePrefabs_CreateAndReplaceCanCreateUnlinkedAssetWithoutDirtyingScene()
        {
            JObject response = JObject.FromObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "create_and_replace",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["prefabPath"] = PrefabPath,
                ["linkSceneInstance"] = false
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            Assert.IsFalse(PrefabUtility.IsPartOfPrefabInstance(root));
            Assert.IsFalse(testScene.isDirty);
            Assert.IsFalse(response["data"]["linkedSceneInstance"].Value<bool>());
        }

        [Test]
        public void SaveSceneScoped_SavesOnlyRequestedDirtyScene()
        {
            root.SetActive(false);
            EditorSceneManager.MarkSceneDirty(testScene);

            JObject response = JObject.FromObject(SaveSceneScoped.HandleCommand(new JObject
            {
                ["scenePath"] = ScenePath
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.IsFalse(testScene.isDirty);
            Assert.AreEqual(ScenePath, response["data"]["assets_saved"][0].Value<string>());
            Assert.IsFalse(response["data"]["rolled_back"].Value<bool>());
        }

        [Test]
        public void PreviewAssetChanges_LeavesDirtySceneUnsaved()
        {
            root.SetActive(false);
            EditorSceneManager.MarkSceneDirty(testScene);

            JObject response = JObject.FromObject(PreviewAssetChanges.HandleCommand(new JObject
            {
                ["scenePath"] = ScenePath
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.IsTrue(testScene.isDirty);
            Assert.IsEmpty(response["data"]["assets_saved"]);
            Assert.IsTrue(response["data"]["preview"].Value<bool>());
            Assert.IsTrue(response["data"]["objects_changed"].Any());
        }
    }

    public sealed class AtomicPrefabReferenceHolder : MonoBehaviour
    {
        public GameObject Target;
    }

    public sealed class MutationTransactionDirtyAsset : ScriptableObject
    {
        public string Value;
    }
}
