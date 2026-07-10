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

        [SetUp]
        public void SetUp()
        {
            originalScene = EditorSceneManager.GetActiveScene();
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
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
                EditorSceneManager.CloseScene(testScene, true);
            AssetDatabase.DeleteAsset(ScenePath);
            AssetDatabase.DeleteAsset(PrefabPath);
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
}
