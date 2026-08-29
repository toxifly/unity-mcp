using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.MutationTransactions;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Editor.Tools.GameObjects;
using MCPForUnity.Editor.Tools.Prefabs;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using MCPForUnity.Runtime.Helpers;
using TestNamespace;

namespace MCPForUnityTests.Editor.Services
{
    public class MutationTransactionTests
    {
        private GameObject root;
        private Scene originalScene;
        private Scene testScene;
        private const string ScenePath = "Assets/Temp/MutationTransactionTests.unity";
        private const string AdditiveScenePath = "Assets/Temp/MutationTransactionAdditiveTests.unity";
        private const string PrefabPath = "Assets/Temp/AtomicMutationTest.prefab";
        private const string RenamedPrefabPath = "Assets/Temp/AtomicMutationRenamed.prefab";
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
            AssetDatabase.DeleteAsset(AdditiveScenePath);
            AssetDatabase.DeleteAsset(PrefabPath);
            AssetDatabase.DeleteAsset(RenamedPrefabPath);
            AssetDatabase.DeleteAsset(DirtyAssetPath);
            AssetDatabase.DeleteAsset("Assets/Temp/DryRunPrefab");
        }

        // SetActiveScene answers "did the active scene change", not "did it succeed":
        // NewScene(..., Additive) already leaves the new scene active, so asking for that
        // same scene again reports false. Assert the state the caller needs instead.
        private static void MakeActiveScene(Scene scene)
        {
            EditorSceneManager.SetActiveScene(scene);
            Assert.AreEqual(scene, SceneManager.GetActiveScene());
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
        public void Rollback_DiscardsLedgerRecordsFromTheRolledBackMutation()
        {
            var preexisting = new GameObject("LedgerPreexisting");
            try
            {
                // Saving assigns a fileID (Record skips unsaved objects) and clears the ledger.
                Assert.IsTrue(EditorSceneManager.SaveScene(testScene));
                string preexistingId = GlobalObjectId.GetGlobalObjectIdSlow(preexisting).ToString();
                SceneMutationLedger.Record(preexisting);

                using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
                {
                    root.SetActive(false);
                    SceneMutationLedger.Record(root);
                    transaction.Rollback();
                }

                var (touched, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.AreEquivalent(new[] { preexistingId }, touched,
                    "Rollback must discard the mutation's ledger records but keep pre-transaction ones.");
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
                Object.DestroyImmediate(preexisting);
            }
        }

        [Test]
        public void CommitWithoutSave_KeepsLedgerRecordsFromTheMutation()
        {
            string rootId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
            try
            {
                using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
                {
                    root.SetActive(false);
                    SceneMutationLedger.Record(root);
                    transaction.Commit(save: false);
                }

                var (touched, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.Contains(touched, rootId);
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
            }
        }

        [Test]
        public void UnityUndoAndRedo_InvalidateLedgerRecordsFromCommittedMutation()
        {
            string rootId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
            try
            {
                using (MutationTransaction transaction = MutationTransaction.Begin(new Object[] { root }))
                {
                    root.SetActive(false);
                    SceneMutationLedger.Record(root);
                    transaction.Commit(save: false);
                }

                var (beforeUndo, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.Contains(beforeUndo, rootId);

                Undo.PerformUndo();

                Assert.IsTrue(root.activeSelf, "the committed mutation should be undoable");
                var (afterUndo, deleted) = SceneMutationLedger.SnapshotForScene(testScene.path);
                Assert.IsEmpty(afterUndo, "Undo must invalidate stale touched scopes");
                Assert.IsEmpty(deleted, "Undo must invalidate stale deletion scopes");

                SceneMutationLedger.Record(root);
                CollectionAssert.Contains(
                    SceneMutationLedger.SnapshotForScene(testScene.path).touched, rootId);

                Undo.PerformRedo();

                Assert.IsFalse(root.activeSelf, "the undone mutation should be redoable");
                var (afterRedo, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                Assert.IsEmpty(afterRedo, "Redo must invalidate stale touched scopes");
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
            }
        }

        [Test]
        public void ManageGameObject_ModifyGameObjectFieldOnly_DoesNotLedgerTransform()
        {
            string rootId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
            string transformId = GlobalObjectId.GetGlobalObjectIdSlow(root.transform).ToString();
            try
            {
                JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "modify",
                    ["target"] = root.GetInstanceIDCompat(),
                    ["searchMethod"] = "by_id",
                    ["setActive"] = false
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                var (touched, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.Contains(touched, rootId);
                CollectionAssert.DoesNotContain(touched, transformId,
                    "A GameObject-field-only modify must not mark the Transform block as intentional, " +
                    "or the scoped save bakes the Transform's ambient in-memory drift.");
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
            }
        }

        [Test]
        public void ComponentOps_NoOpAssignment_DoesNotLedgerComponent()
        {
            var audioSource = root.AddComponent<AudioSource>();
            audioSource.volume = 0.5f;
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene));
            string audioSourceId = GlobalObjectId.GetGlobalObjectIdSlow(audioSource).ToString();

            try
            {
                bool success = ComponentOps.SetProperty(
                    audioSource, "volume", new JValue(0.5f), out string error);

                Assert.IsTrue(success, error);
                var (touched, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.DoesNotContain(touched, audioSourceId,
                    "assigning the existing value must not scope ambient drift in the component block");
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
            }
        }

        [Test]
        public void ManageGameObject_NoOpNestedAssignment_DoesNotLedgerAmbientComponentDrift()
        {
            var collider = root.AddComponent<BoxCollider>();
            collider.center = Vector3.zero;
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene));
            string colliderId = GlobalObjectId.GetGlobalObjectIdSlow(collider).ToString();

            // Simulate unrelated in-memory drift that an exclude-unscoped save must not bake.
            collider.size = new Vector3(9f, 8f, 7f);

            try
            {
                JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "modify",
                    ["target"] = root.GetInstanceIDCompat(),
                    ["searchMethod"] = "by_id",
                    ["componentProperties"] = new JObject
                    {
                        ["BoxCollider"] = new JObject
                        {
                            ["center.x"] = 0f
                        }
                    }
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                var (touched, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.DoesNotContain(touched, colliderId,
                    "repeating a nested serialized value must not scope unrelated ambient drift");
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
            }
        }

        [Test]
        public void ComponentOps_ChangedAssignment_LedgersComponent()
        {
            var audioSource = root.AddComponent<AudioSource>();
            audioSource.volume = 0.5f;
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene));
            string audioSourceId = GlobalObjectId.GetGlobalObjectIdSlow(audioSource).ToString();

            try
            {
                bool success = ComponentOps.SetProperty(
                    audioSource, "volume", new JValue(0.25f), out string error);

                Assert.IsTrue(success, error);
                var (touched, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.Contains(touched, audioSourceId,
                    "a serialized value change must still be scoped");
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
            }
        }

        [Test]
        public void ManageGameObject_ModifyTransformOnly_DoesNotLedgerGameObject()
        {
            string rootId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
            string transformId = GlobalObjectId.GetGlobalObjectIdSlow(root.transform).ToString();
            try
            {
                JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "modify",
                    ["target"] = root.GetInstanceIDCompat(),
                    ["searchMethod"] = "by_id",
                    ["position"] = new JArray { 1.0f, 2.0f, 3.0f }
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                var (touched, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.Contains(touched, transformId);
                CollectionAssert.DoesNotContain(touched, rootId,
                    "A transform-only modify must not mark the GameObject block as intentional, " +
                    "or the scoped save bakes the GameObject block's ambient in-memory drift.");
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
            }
        }

        [Test]
        public void ManageGameObject_NestedComponentPropertyPath_LedgersTheTraversedObject()
        {
            var collider = root.AddComponent<BoxCollider>();
            // Re-save so the collider gets a fileID (Record skips unsaved objects); saving clears the ledger.
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene));
            string colliderId = GlobalObjectId.GetGlobalObjectIdSlow(collider).ToString();
            string transformId = GlobalObjectId.GetGlobalObjectIdSlow(root.transform).ToString();
            try
            {
                JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "modify",
                    ["target"] = root.GetInstanceIDCompat(),
                    ["searchMethod"] = "by_id",
                    ["componentProperties"] = new JObject
                    {
                        ["BoxCollider"] = new JObject
                        {
                            ["transform.localPosition"] = new JArray { 4.0f, 5.0f, 6.0f }
                        }
                    }
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                Assert.AreEqual(new Vector3(4f, 5f, 6f), root.transform.localPosition);
                var (touched, _) = SceneMutationLedger.SnapshotForScene(testScene.path);
                CollectionAssert.Contains(touched, transformId,
                    "A nested path that traverses to another object must ledger the object the write lands in.");
                CollectionAssert.DoesNotContain(touched, colliderId,
                    "The component the path started from was not serialized-changed and must not be ledgered.");
            }
            finally
            {
                SceneMutationLedger.Clear(testScene.path);
            }
        }

        [Test]
        public void ManageGameObject_NestedComponentPropertyPathDryRun_RollsBackTraversedObject()
        {
            root.AddComponent<BoxCollider>();
            // Re-save so the scene starts clean for the post-rollback dirty check.
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene));
            Vector3 originalPosition = root.transform.localPosition;

            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "modify",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["componentProperties"] = new JObject
                {
                    ["BoxCollider"] = new JObject
                    {
                        ["transform.localPosition"] = new JArray { 7.0f, 8.0f, 9.0f }
                    }
                },
                ["dryRun"] = true
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.IsTrue(response["data"]["change_preview"]["rolled_back"].Value<bool>());
            Assert.AreEqual(originalPosition, root.transform.localPosition,
                "Rolling back a dry-run must restore objects written through nested property paths.");
            Assert.IsFalse(testScene.isDirty);
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
        public void ManageGameObject_DeleteDryRunTracksEveryAdditiveSceneMatch()
        {
            var sourceMarker = root.AddComponent<AtomicPrefabReferenceHolder>();
            Scene additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var additiveMatch = new GameObject(root.name);
            SceneManager.MoveGameObjectToScene(additiveMatch, additiveScene);
            additiveMatch.AddComponent<AtomicPrefabReferenceHolder>();

            try
            {
                Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));
                Assert.IsTrue(EditorSceneManager.SaveScene(additiveScene, AdditiveScenePath));
                string sourceId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
                string additiveId = GlobalObjectId.GetGlobalObjectIdSlow(additiveMatch).ToString();

                JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "delete",
                    ["target"] = typeof(AtomicPrefabReferenceHolder).FullName,
                    ["searchMethod"] = "by_component",
                    ["dryRun"] = true
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                JToken changes = response["data"]["change_preview"]["changes"];
                Assert.IsTrue(changes.Any(change => change["ObjectId"].Value<string>() == sourceId));
                Assert.IsTrue(changes.Any(change => change["ObjectId"].Value<string>() == additiveId));
                Assert.IsNotNull(root);
                Assert.IsNotNull(additiveMatch);
                Assert.IsFalse(testScene.isDirty);
                Assert.IsFalse(additiveScene.isDirty);
            }
            finally
            {
                if (sourceMarker != null)
                    Object.DestroyImmediate(sourceMarker);
                if (additiveMatch != null)
                    Object.DestroyImmediate(additiveMatch);
                if (additiveScene.IsValid() && additiveScene.isLoaded)
                    EditorSceneManager.CloseScene(additiveScene, true);
                AssetDatabase.DeleteAsset(AdditiveScenePath);
            }
        }

        [Test]
        public void ManageGameObject_ModifyDryRunTracksCrossSceneDestinationParent()
        {
            var sourceMarker = root.AddComponent<AtomicPrefabReferenceHolder>();
            Scene additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var destinationParent = new GameObject("CrossSceneDestinationParent");
            SceneManager.MoveGameObjectToScene(destinationParent, additiveScene);

            try
            {
                Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));
                Assert.IsTrue(EditorSceneManager.SaveScene(additiveScene, AdditiveScenePath));
                MakeActiveScene(additiveScene);
                string parentTransformId = GlobalObjectId.GetGlobalObjectIdSlow(destinationParent.transform).ToString();

                JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "modify",
                    ["target"] = typeof(AtomicPrefabReferenceHolder).FullName,
                    ["searchMethod"] = "by_component",
                    ["parent"] = destinationParent.GetInstanceIDCompat(),
                    ["dryRun"] = true
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                Assert.IsTrue(response["data"]["change_preview"]["changes"].Any(change =>
                    change["ObjectId"].Value<string>() == parentTransformId
                    && change["Property"].Value<string>().StartsWith("m_Children", System.StringComparison.Ordinal)));
                Assert.IsNull(root.transform.parent);
                Assert.AreEqual(testScene, root.scene);
                Assert.AreEqual(0, destinationParent.transform.childCount);
                Assert.IsFalse(testScene.isDirty);
                Assert.IsFalse(additiveScene.isDirty);
            }
            finally
            {
                if (sourceMarker != null)
                    Object.DestroyImmediate(sourceMarker);
                if (destinationParent != null)
                    Object.DestroyImmediate(destinationParent);
                if (testScene.IsValid() && testScene.isLoaded)
                    EditorSceneManager.SetActiveScene(testScene);
                if (additiveScene.IsValid() && additiveScene.isLoaded)
                    EditorSceneManager.CloseScene(additiveScene, true);
                AssetDatabase.DeleteAsset(AdditiveScenePath);
            }
        }

        [Test]
        public void ManageGameObject_DuplicateDryRunTracksCrossSceneDestinationParent()
        {
            var sourceMarker = root.AddComponent<AtomicPrefabReferenceHolder>();
            Scene additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var destinationParent = new GameObject("CrossSceneDuplicateParent");
            SceneManager.MoveGameObjectToScene(destinationParent, additiveScene);

            try
            {
                Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));
                Assert.IsTrue(EditorSceneManager.SaveScene(additiveScene, AdditiveScenePath));
                MakeActiveScene(additiveScene);
                string parentTransformId = GlobalObjectId.GetGlobalObjectIdSlow(destinationParent.transform).ToString();

                JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "duplicate",
                    ["target"] = typeof(AtomicPrefabReferenceHolder).FullName,
                    ["searchMethod"] = "by_component",
                    ["parent"] = destinationParent.GetInstanceIDCompat(),
                    ["new_name"] = "CrossSceneCopy",
                    ["dryRun"] = true
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                JToken changes = response["data"]["change_preview"]["changes"];
                Assert.IsTrue(changes.Any(change =>
                    change["ObjectId"].Value<string>() == parentTransformId
                    && change["Property"].Value<string>().StartsWith("m_Children", System.StringComparison.Ordinal)));
                Assert.IsTrue(changes.Any(change =>
                    change["Kind"].Value<string>() == "added"
                    && change["ObjectPath"].Value<string>().EndsWith("/CrossSceneCopy", System.StringComparison.Ordinal)));
                Assert.AreEqual(0, destinationParent.transform.childCount);
                Assert.IsFalse(testScene.isDirty);
                Assert.IsFalse(additiveScene.isDirty);
            }
            finally
            {
                if (sourceMarker != null)
                    Object.DestroyImmediate(sourceMarker);
                if (destinationParent != null)
                    Object.DestroyImmediate(destinationParent);
                if (testScene.IsValid() && testScene.isLoaded)
                    EditorSceneManager.SetActiveScene(testScene);
                if (additiveScene.IsValid() && additiveScene.isLoaded)
                    EditorSceneManager.CloseScene(additiveScene, true);
                AssetDatabase.DeleteAsset(AdditiveScenePath);
            }
        }

        [Test]
        public void ManageGameObject_DryRunRejectsUnknownTagBeforeProjectSettingsMutation()
        {
            string unknownTag = "MCPGuardedTag_" + System.Guid.NewGuid().ToString("N");
            Assert.IsFalse(InternalEditorUtility.tags.Contains(unknownTag));

            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["name"] = "UnknownTagPreviewObject",
                ["tag"] = unknownTag,
                ["dryRun"] = true
            }));

            Assert.IsFalse(response["success"].Value<bool>(), response.ToString());
            Assert.AreEqual("UNTRACKED_PROJECT_SETTINGS_MUTATION", response["code"].Value<string>());
            Assert.IsFalse(InternalEditorUtility.tags.Contains(unknownTag));
            Assert.IsNull(GameObject.Find("UnknownTagPreviewObject"));
            Assert.IsFalse(testScene.isDirty);
        }

        [Test]
        public void ManageGameObject_GuardRejectsUnknownTagBeforeProjectSettingsMutation()
        {
            string unknownTag = "MCPGuardedTag_" + System.Guid.NewGuid().ToString("N");
            Assert.IsFalse(InternalEditorUtility.tags.Contains(unknownTag));

            JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "modify",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["tag"] = unknownTag,
                ["changeGuard"] = new JObject
                {
                    ["mode"] = "reject_unexpected",
                    ["expected_objects"] = new JArray(root.name)
                }
            }));

            Assert.IsFalse(response["success"].Value<bool>(), response.ToString());
            Assert.AreEqual("UNTRACKED_PROJECT_SETTINGS_MUTATION", response["code"].Value<string>());
            Assert.AreEqual("Untagged", root.tag);
            Assert.IsFalse(InternalEditorUtility.tags.Contains(unknownTag));
            Assert.IsFalse(testScene.isDirty);
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
        public void ManageGameObject_RenamePrefabStageRootDryRunRestoresAssetMoveAndGuid()
        {
            var prefabSource = new GameObject("AtomicMutationTest");
            PrefabUtility.SaveAsPrefabAsset(prefabSource, PrefabPath);
            Object.DestroyImmediate(prefabSource);
            string originalGuid = AssetDatabase.AssetPathToGUID(PrefabPath);
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;

            try
            {
                var prefabStage = PrefabStageUtility.OpenPrefab(PrefabPath);
                Assert.IsNotNull(prefabStage);
                GameObject prefabRoot = prefabStage.prefabContentsRoot;

                JObject response = JObject.FromObject(ManageGameObject.HandleCommand(new JObject
                {
                    ["action"] = "modify",
                    ["target"] = prefabRoot.GetInstanceIDCompat(),
                    ["searchMethod"] = "by_id",
                    ["name"] = "AtomicMutationRenamed",
                    ["dryRun"] = true
                }));

                Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
                Assert.IsTrue(response["data"]["change_preview"]["changes"].Any(change =>
                    change["AssetPath"].Value<string>() == PrefabPath
                    || change["AssetPath"].Value<string>() == RenamedPrefabPath));
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(RenamedPrefabPath));
                Assert.AreEqual(originalGuid, AssetDatabase.AssetPathToGUID(PrefabPath));
                Assert.AreEqual(PrefabPath, AssetDatabase.GUIDToAssetPath(originalGuid));
                Assert.AreEqual(PrefabPath, PrefabStageUtility.GetCurrentPrefabStage()?.assetPath);
                Assert.AreEqual("AtomicMutationTest", PrefabStageUtility.GetCurrentPrefabStage()?.prefabContentsRoot.name);
                Assert.IsFalse(File.Exists(Path.Combine(projectRoot, RenamedPrefabPath + ".meta")));
            }
            finally
            {
                StageUtility.GoToMainStage();
                AssetDatabase.DeleteAsset(RenamedPrefabPath);
            }
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
        public void ManagePrefabs_CreateAndReplaceNestedWithoutReferencePreservationAllowsParentChildSlot()
        {
            var parent = new GameObject("AtomicNestedParent");
            var before = new GameObject("AtomicNestedBefore");
            var after = new GameObject("AtomicNestedAfter");
            before.transform.SetParent(parent.transform, false);
            root.transform.SetParent(parent.transform, false);
            after.transform.SetParent(parent.transform, false);
            int originalIndex = root.transform.GetSiblingIndex();
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));
            string parentTransformId = GlobalObjectId.GetGlobalObjectIdSlow(parent.transform).ToString();

            JObject response = JObject.FromObject(ManagePrefabs.HandleCommand(new JObject
            {
                ["action"] = "create_and_replace",
                ["target"] = root.GetInstanceIDCompat(),
                ["searchMethod"] = "by_id",
                ["prefabPath"] = PrefabPath,
                ["preserveSceneReferences"] = false
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            root = parent.transform.GetChild(originalIndex).gameObject;
            Assert.AreEqual(3, parent.transform.childCount);
            Assert.AreEqual("AtomicNestedBefore", parent.transform.GetChild(0).name);
            Assert.AreEqual("MutationTransactionRoot", root.name);
            Assert.AreEqual("AtomicNestedAfter", parent.transform.GetChild(2).name);
            Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(root));
            Assert.IsTrue(response["data"]["changes"].Any(change =>
                change["ObjectId"].Value<string>() == parentTransformId
                && change["Property"].Value<string>() == $"m_Children.Array.data[{originalIndex}]"));
            Assert.IsFalse(testScene.isDirty);
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
        public void SaveSceneScoped_ExcludeWritesLedgeredChangeAndSuppressesDrift()
        {
            var drift = new GameObject("DriftObject");
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));

            root.name = "ScopedRenamedRoot";
            SceneMutationLedger.Record(root);
            drift.name = "DriftRenamedObject";
            EditorSceneManager.MarkSceneDirty(testScene);

            JObject response = JObject.FromObject(SaveSceneScoped.HandleCommand(new JObject
            {
                ["scenePath"] = ScenePath,
                ["unscopedChanges"] = "exclude"
            }));

            Assert.IsTrue(response["success"].Value<bool>(), response.ToString());
            Assert.AreEqual("merge_scoped", response["data"]["save_mode"].Value<string>());
            Assert.AreEqual(ScenePath, response["data"]["assets_saved"][0].Value<string>());
            Assert.AreEqual(1, response["data"]["blocks"]["scoped"].Value<int>());
            Assert.GreaterOrEqual(response["data"]["blocks"]["drift_suppressed"].Value<int>(), 1);
            Assert.IsTrue(response["data"]["scene_still_dirty_in_memory"].Value<bool>());

            string savedText = File.ReadAllText(ScenePath);
            StringAssert.Contains("ScopedRenamedRoot", savedText);
            StringAssert.DoesNotContain("DriftRenamedObject", savedText);
            StringAssert.Contains("DriftObject", savedText);

            var (touched, _) = SceneMutationLedger.SnapshotForScene(ScenePath);
            Assert.IsEmpty(touched, "A merge save must clear the scene's ledger entries.");
        }

        [Test]
        public void SaveSceneScoped_RejectFailsWhenUnscopedDriftExists()
        {
            var drift = new GameObject("DriftObject");
            Assert.IsTrue(EditorSceneManager.SaveScene(testScene, ScenePath));

            drift.name = "DriftRenamedObject";
            EditorSceneManager.MarkSceneDirty(testScene);

            JObject response = JObject.FromObject(SaveSceneScoped.HandleCommand(new JObject
            {
                ["scenePath"] = ScenePath,
                ["unscopedChanges"] = "reject"
            }));

            Assert.IsFalse(response["success"].Value<bool>(), response.ToString());
            Assert.AreEqual("UNSCOPED_CHANGES", response["code"].Value<string>());
            StringAssert.DoesNotContain("DriftRenamedObject", File.ReadAllText(ScenePath));
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

}
