using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Tools.GameObjects;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TestNamespace;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// manage_gameobject action="duplicate" must behave like Edit > Duplicate: the copy stays in
    /// the source's scene (prefab stage included) right after the source, keeps its local
    /// transform, and a prefab instance stays linked to its asset with its overrides.
    /// </summary>
    public class ManageGameObjectDuplicateTests
    {
        private const string TempDirectory = "Assets/Temp/ManageGameObjectDuplicateTests";
        private const string InnerPrefabPath = TempDirectory + "/DuplicateInner.prefab";
        private const string OuterPrefabPath = TempDirectory + "/DuplicateOuter.prefab";
        private const string SourceScenePath = TempDirectory + "/DuplicateSource.unity";
        private const float Tolerance = 1e-4f;

        private readonly List<GameObject> sceneRoots = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            StageUtility.GoToMainStage();
            EnsureFolder(TempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                stage.ClearDirtiness();
            StageUtility.GoToMainStage();
            foreach (GameObject root in sceneRoots)
            {
                if (root != null)
                    Object.DestroyImmediate(root);
            }
            sceneRoots.Clear();
            if (AssetDatabase.IsValidFolder(TempDirectory))
                AssetDatabase.DeleteAsset(TempDirectory);
            CleanupEmptyParentFolders(TempDirectory);
        }

        [Test]
        public void Duplicate_PlacesDefaultNamedCopyRightAfterSourceWithSameLocalTransform()
        {
            GameObject parent = CreateRoot("DuplicateParent");
            GameObject source = CreateChild("DuplicateSource", parent.transform);
            GameObject sibling = CreateChild("DuplicateSibling", parent.transform);
            source.transform.localPosition = new Vector3(1f, 2f, 3f);
            source.transform.localRotation = Quaternion.Euler(10f, 20f, 30f);
            source.transform.localScale = new Vector3(2f, 3f, 4f);

            GameObject copy = Duplicate(new JObject { ["target"] = source.GetInstanceIDCompat() });

            Assert.AreEqual("DuplicateSource_Copy", copy.name);
            Assert.AreEqual(source.scene, copy.scene);
            Assert.AreSame(parent.transform, copy.transform.parent);
            Assert.AreEqual(source.transform.GetSiblingIndex() + 1, copy.transform.GetSiblingIndex());
            Assert.AreEqual(copy.transform.GetSiblingIndex() + 1, sibling.transform.GetSiblingIndex());
            AssertSameLocalTransform(source.transform, copy.transform);
        }

        [Test]
        public void Duplicate_WithNewName_NamesTheCopy()
        {
            GameObject source = CreateRoot("DuplicateRenameSource");

            GameObject copy = Duplicate(new JObject
            {
                ["target"] = source.GetInstanceIDCompat(),
                ["new_name"] = "RenamedCopy"
            });

            Assert.AreEqual("RenamedCopy", copy.name);
            Assert.AreEqual("DuplicateRenameSource", source.name);
        }

        [Test]
        public void Duplicate_UnderZeroScaleParent_KeepsLocalTransform()
        {
            GameObject parent = CreateRoot("ZeroScaleParent");
            parent.transform.localScale = Vector3.zero;
            GameObject source = CreateChild("ZeroScaleChild", parent.transform);
            source.transform.localPosition = new Vector3(4f, -5f, 6f);
            source.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            source.transform.localScale = new Vector3(1f, 2f, 1f);

            GameObject copy = Duplicate(new JObject { ["target"] = source.GetInstanceIDCompat() });

            Assert.AreSame(parent.transform, copy.transform.parent);
            AssertSameLocalTransform(source.transform, copy.transform);
        }

        [Test]
        public void Duplicate_WithParent_AppendsCopyUnderParentKeepingLocalTransform()
        {
            GameObject sourceParent = CreateRoot("DuplicateSourceParent");
            sourceParent.transform.localScale = new Vector3(2f, 2f, 2f);
            GameObject source = CreateChild("DuplicateMovedSource", sourceParent.transform);
            source.transform.localPosition = new Vector3(1f, 1f, 0f);
            source.transform.localScale = new Vector3(0.5f, 1f, 1f);
            GameObject destination = CreateRoot("DuplicateDestination");
            destination.transform.localScale = Vector3.zero;
            CreateChild("ExistingChild", destination.transform);

            GameObject copy = Duplicate(new JObject
            {
                ["target"] = source.GetInstanceIDCompat(),
                ["parent"] = destination.GetInstanceIDCompat()
            });

            Assert.AreSame(destination.transform, copy.transform.parent);
            Assert.AreEqual(destination.transform.childCount - 1, copy.transform.GetSiblingIndex());
            AssertSameLocalTransform(source.transform, copy.transform);
            Assert.AreEqual(1, sourceParent.transform.childCount);
        }

        [Test]
        public void Duplicate_WithNullParent_MovesCopyToSceneRoot()
        {
            GameObject parent = CreateRoot("DuplicateRootParent");
            GameObject source = CreateChild("DuplicateToRoot", parent.transform);
            source.transform.localPosition = new Vector3(3f, 0f, 0f);

            GameObject copy = Duplicate(new JObject
            {
                ["target"] = source.GetInstanceIDCompat(),
                ["parent"] = JValue.CreateNull()
            });

            Assert.IsNull(copy.transform.parent);
            Assert.AreEqual(source.scene, copy.scene);
            AssertSameLocalTransform(source.transform, copy.transform);
        }

        [Test]
        public void Duplicate_WithOffset_PlacesCopyAtSourceWorldPositionPlusOffset()
        {
            GameObject parent = CreateRoot("DuplicateOffsetParent");
            parent.transform.position = new Vector3(10f, 0f, 0f);
            parent.transform.localScale = new Vector3(2f, 2f, 2f);
            GameObject source = CreateChild("DuplicateOffsetSource", parent.transform);
            source.transform.localPosition = new Vector3(1f, 2f, 3f);

            GameObject copy = Duplicate(new JObject
            {
                ["target"] = source.GetInstanceIDCompat(),
                ["offset"] = new JArray { 5f, 0f, -1f }
            });

            Assert.AreSame(parent.transform, copy.transform.parent);
            AssertVector(source.transform.position + new Vector3(5f, 0f, -1f), copy.transform.position, "position");
        }

        [Test]
        public void Duplicate_WithPosition_PlacesCopyAtWorldPosition()
        {
            GameObject parent = CreateRoot("DuplicatePositionParent");
            parent.transform.position = new Vector3(-4f, 1f, 0f);
            parent.transform.localScale = new Vector3(3f, 3f, 3f);
            GameObject source = CreateChild("DuplicatePositionSource", parent.transform);

            GameObject copy = Duplicate(new JObject
            {
                ["target"] = source.GetInstanceIDCompat(),
                ["position"] = new JArray { 7f, 8f, 9f }
            });

            Assert.AreSame(parent.transform, copy.transform.parent);
            AssertVector(new Vector3(7f, 8f, 9f), copy.transform.position, "position");
        }

        [Test]
        public void Duplicate_WithUnknownParent_FailsWithoutCreatingCopy()
        {
            GameObject parent = CreateRoot("DuplicateUnknownParentHolder");
            GameObject source = CreateChild("DuplicateUnknownParentSource", parent.transform);

            JObject response = ToJObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "duplicate",
                ["target"] = source.GetInstanceIDCompat(),
                ["parent"] = "NoSuchDuplicateParent_9f3c"
            }));

            Assert.IsFalse(response.Value<bool>("success"), response.ToString());
            Assert.AreEqual(1, parent.transform.childCount);
        }

        [Test]
        public void Duplicate_SourceOutsideActiveScene_StaysInSourceScene()
        {
            Scene sourceScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Scene activeScene = default;
            try
            {
                var parent = new GameObject("InactiveSceneParent");
                GameObject source = CreateChild("InactiveSceneSource", parent.transform);
                source.AddComponent<AtomicPrefabReferenceHolder>();
                Assert.IsTrue(EditorSceneManager.SaveScene(sourceScene, SourceScenePath));
                activeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.AreEqual(activeScene, SceneManager.GetActiveScene());

                // Instance-id and name lookups cover only the active scene; component lookup spans scenes.
                GameObject copy = Duplicate(new JObject
                {
                    ["target"] = typeof(AtomicPrefabReferenceHolder).FullName,
                    ["searchMethod"] = "by_component"
                });

                Assert.AreEqual(sourceScene, copy.scene);
                Assert.AreSame(parent.transform, copy.transform.parent);
                Assert.AreEqual(0, activeScene.rootCount);
            }
            finally
            {
                if (activeScene.IsValid() && activeScene.isLoaded)
                    EditorSceneManager.CloseScene(activeScene, true);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void Duplicate_PrefabInstanceInScene_KeepsPrefabLinkAndOverrides()
        {
            GameObject innerAsset = CreateInnerPrefab();
            GameObject parent = CreateRoot("DuplicatePrefabParent");
            var source = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset, parent.transform);
            source.transform.localPosition = new Vector3(2f, 0f, 1f);
            AddOneOverrideOfEachKind(source);

            GameObject copy = Duplicate(new JObject
            {
                ["target"] = source.GetInstanceIDCompat(),
                ["new_name"] = "InnerCopy"
            });

            Assert.AreEqual(source.scene, copy.scene);
            Assert.AreSame(parent.transform, copy.transform.parent);
            Assert.AreEqual(source.transform.GetSiblingIndex() + 1, copy.transform.GetSiblingIndex());
            AssertSameLocalTransform(source.transform, copy.transform);
            AssertLinkedWithOverrides(source, copy);
            Assert.IsTrue(PrefabUtility.GetPropertyModifications(copy).Any(modification =>
                modification.propertyPath == "m_Name" && modification.value == "InnerCopy"),
                "The rename was not recorded as a prefab-instance override.");
        }

        [Test]
        public void Duplicate_NestedPrefabInstanceInPrefabStage_KeepsLinkOverridesAndLocalLayout()
        {
            PrefabStage stage = OpenOuterPrefabStage();
            Transform container = stage.prefabContentsRoot.transform.Find("ZeroScaleContainer");
            GameObject source = container.Find("DuplicateInner").gameObject;
            AddOneOverrideOfEachKind(source);

            GameObject copy = Duplicate(new JObject { ["target"] = source.GetInstanceIDCompat() });

            Assert.AreEqual(stage.scene, copy.scene);
            Assert.AreSame(container, copy.transform.parent);
            Assert.AreEqual(source.transform.GetSiblingIndex() + 1, copy.transform.GetSiblingIndex());
            AssertSameLocalTransform(source.transform, copy.transform);
            AssertLinkedWithOverrides(source, copy);
            Assert.IsTrue(stage.scene.isDirty);
        }

        [Test]
        public void Duplicate_PrefabStageRoot_PutsCopyUnderTheRoot()
        {
            PrefabStage stage = OpenOuterPrefabStage();
            GameObject root = stage.prefabContentsRoot;

            GameObject copy = Duplicate(new JObject { ["target"] = root.GetInstanceIDCompat() });

            Assert.AreEqual(stage.scene, copy.scene);
            Assert.AreSame(root.transform, copy.transform.parent);
            Assert.IsNotNull(copy.transform.Find("ZeroScaleContainer/DuplicateInner"));
        }

        [Test]
        public void Duplicate_InPrefabStageWithNullParent_KeepsCopyInsidePrefabContents()
        {
            PrefabStage stage = OpenOuterPrefabStage();
            GameObject source = stage.prefabContentsRoot.transform.Find("ZeroScaleContainer/DuplicateInner").gameObject;

            GameObject copy = Duplicate(new JObject
            {
                ["target"] = source.GetInstanceIDCompat(),
                ["parent"] = JValue.CreateNull()
            });

            Assert.AreEqual(stage.scene, copy.scene);
            Assert.AreSame(stage.prefabContentsRoot.transform, copy.transform.parent);
            Assert.AreEqual(InnerPrefabPath, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(copy));
            AssertSameLocalTransform(source.transform, copy.transform);
        }

        [Test]
        public void Duplicate_PrefabInstance_UndoesInOneStep()
        {
            GameObject innerAsset = CreateInnerPrefab();
            GameObject parent = CreateRoot("DuplicateUndoParent");
            var source = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset, parent.transform);
            AddOneOverrideOfEachKind(source);
            Undo.IncrementCurrentGroup();

            GameObject copy = Duplicate(new JObject
            {
                ["target"] = source.GetInstanceIDCompat(),
                ["new_name"] = "UndoneCopy",
                ["offset"] = new JArray { 1f, 0f, 0f }
            });
            Assert.AreEqual(2, parent.transform.childCount);

            Undo.PerformUndo();

            Assert.IsTrue(copy == null, "Undo left the duplicate behind.");
            Assert.AreEqual(1, parent.transform.childCount);
            Assert.AreEqual(0.25f, source.GetComponent<CanvasGroup>().alpha);
        }

        [Test]
        public void Duplicate_PrefabInstanceDryRun_PreviewsWithoutKeepingCopy()
        {
            GameObject innerAsset = CreateInnerPrefab();
            GameObject parent = CreateRoot("DuplicateDryRunParent");
            var source = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset, parent.transform);
            AddOneOverrideOfEachKind(source);

            JObject response = ToJObject(ManageGameObject.HandleCommand(new JObject
            {
                ["action"] = "duplicate",
                ["target"] = source.GetInstanceIDCompat(),
                ["new_name"] = "PreviewCopy",
                ["dryRun"] = true
            }));

            Assert.IsTrue(response.Value<bool>("success"), response.ToString());
            Assert.IsTrue(response["data"]["change_preview"]["changes"].Any(change =>
                change["Kind"].Value<string>() == "added"
                && change["ObjectPath"].Value<string>().EndsWith("/PreviewCopy", System.StringComparison.Ordinal)),
                response.ToString());
            Assert.AreEqual(1, parent.transform.childCount);
        }

        private GameObject CreateRoot(string name)
        {
            var go = new GameObject(name);
            sceneRoots.Add(go);
            return go;
        }

        private static GameObject CreateChild(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        private GameObject Duplicate(JObject parameters)
        {
            parameters["action"] = "duplicate";
            JObject response = ToJObject(ManageGameObject.HandleCommand(parameters));
            Assert.IsTrue(response.Value<bool>("success"), response.ToString());
            int copyId = response["data"]["duplicatedObject"]["instanceID"].Value<int>();
            var copy = UnityObjectIdCompat.InstanceIDToObjectCompat(copyId) as GameObject;
            Assert.IsNotNull(copy, response.ToString());
            if (copy.transform.parent == null)
                sceneRoots.Add(copy);
            return copy;
        }

        private static GameObject CreateInnerPrefab()
        {
            var root = new GameObject("DuplicateInner", typeof(RectTransform));
            try
            {
                root.AddComponent<CanvasGroup>();
                root.AddComponent<AtomicPrefabReferenceHolder>();
                root.AddComponent<BoxCollider>();
                var removable = new GameObject("Removable", typeof(RectTransform));
                removable.transform.SetParent(root.transform, false);
                return PrefabUtility.SaveAsPrefabAsset(root, InnerPrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // A UI prefab whose nested instance sits under a zero-scale container, so any
        // world-space round trip during duplication would corrupt the copy's local layout.
        private static PrefabStage OpenOuterPrefabStage()
        {
            GameObject innerAsset = CreateInnerPrefab();
            var root = new GameObject("DuplicateOuter", typeof(RectTransform));
            try
            {
                ((RectTransform)root.transform).sizeDelta = Vector2.zero;
                var container = new GameObject("ZeroScaleContainer", typeof(RectTransform));
                container.transform.SetParent(root.transform, false);
                container.transform.localScale = Vector3.zero;
                var nested = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset, container.transform);
                var rect = (RectTransform)nested.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(12f, -34f);
                rect.sizeDelta = new Vector2(120f, 48f);
                rect.localScale = new Vector3(0.5f, 0.75f, 1f);
                Assert.IsNotNull(PrefabUtility.SaveAsPrefabAsset(root, OuterPrefabPath));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            PrefabStage stage = PrefabStageUtility.OpenPrefab(OuterPrefabPath);
            Assert.IsNotNull(stage);
            return stage;
        }

        // One override of each kind Edit > Duplicate carries over, including a reference to
        // an added object that must be remapped to the copy's own added object.
        private static void AddOneOverrideOfEachKind(GameObject instance)
        {
            var canvasGroup = new SerializedObject(instance.GetComponent<CanvasGroup>());
            canvasGroup.FindProperty("m_Alpha").floatValue = 0.25f;
            canvasGroup.ApplyModifiedProperties();

            var added = new GameObject("AddedChild", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(added, "Add child");
            Undo.SetTransformParent(added.transform, instance.transform, "Add child");
            var holder = new SerializedObject(instance.GetComponent<AtomicPrefabReferenceHolder>());
            holder.FindProperty("Target").objectReferenceValue = added;
            holder.ApplyModifiedProperties();

            Undo.AddComponent<SphereCollider>(instance);
            Undo.DestroyObjectImmediate(instance.GetComponent<BoxCollider>());
#if UNITY_2022_2_OR_NEWER
            Undo.DestroyObjectImmediate(instance.transform.Find("Removable").gameObject);
#endif
        }

        private static void AssertLinkedWithOverrides(GameObject source, GameObject copy)
        {
            Assert.IsTrue(PrefabUtility.IsAnyPrefabInstanceRoot(copy), "The copy is not a prefab instance root.");
            Assert.AreEqual(InnerPrefabPath, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(copy));
            Assert.AreSame(
                PrefabUtility.GetCorrespondingObjectFromSource(source),
                PrefabUtility.GetCorrespondingObjectFromSource(copy));
            Assert.AreNotSame(
                PrefabUtility.GetPrefabInstanceHandle(source),
                PrefabUtility.GetPrefabInstanceHandle(copy));

            Assert.AreEqual(0.25f, copy.GetComponent<CanvasGroup>().alpha);
            Assert.IsTrue(PrefabUtility.GetPropertyModifications(copy).Any(modification =>
                modification.propertyPath == "m_Alpha" && modification.value == "0.25"),
                "The property override was not carried over.");

            Transform addedChild = copy.transform.Find("AddedChild");
            Assert.IsNotNull(addedChild);
            Assert.AreEqual(1, PrefabUtility.GetAddedGameObjects(copy).Count);
            Assert.AreSame(addedChild.gameObject, copy.GetComponent<AtomicPrefabReferenceHolder>().Target,
                "The internal reference still points at the source's object.");

            Assert.AreEqual(1, PrefabUtility.GetAddedComponents(copy).Count);
            Assert.IsNotNull(copy.GetComponent<SphereCollider>());
            Assert.AreEqual(1, PrefabUtility.GetRemovedComponents(copy).Count);
            Assert.IsNull(copy.GetComponent<BoxCollider>());
#if UNITY_2022_2_OR_NEWER
            Assert.AreEqual(1, PrefabUtility.GetRemovedGameObjects(copy).Count);
            Assert.IsNull(copy.transform.Find("Removable"));
#endif
        }

        // Compares the serialized local values. A RectTransform's localPosition is derived from
        // its parent's rect, so under a different parent its anchored layout is what must match.
        private static void AssertSameLocalTransform(Transform expected, Transform actual)
        {
            AssertVector(expected.localScale, actual.localScale, "localScale");
            Assert.Less(Quaternion.Angle(expected.localRotation, actual.localRotation), 0.01f, "localRotation");
            if (!(expected is RectTransform expectedRect))
            {
                AssertVector(expected.localPosition, actual.localPosition, "localPosition");
            }
            else
            {
                var actualRect = (RectTransform)actual;
                AssertVector(expectedRect.anchoredPosition3D, actualRect.anchoredPosition3D, "anchoredPosition3D");
                AssertVector(expectedRect.sizeDelta, actualRect.sizeDelta, "sizeDelta");
                AssertVector(expectedRect.anchorMin, actualRect.anchorMin, "anchorMin");
                AssertVector(expectedRect.anchorMax, actualRect.anchorMax, "anchorMax");
                AssertVector(expectedRect.pivot, actualRect.pivot, "pivot");
            }
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string label)
        {
            Assert.Less(Vector3.Distance(expected, actual), Tolerance, $"{label}: expected {expected}, got {actual}");
        }
    }
}
