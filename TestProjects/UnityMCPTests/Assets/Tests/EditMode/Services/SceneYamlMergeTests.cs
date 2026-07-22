using System.Collections.Generic;
using MCPForUnity.Editor.Services.MutationTransactions;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class SceneYamlMergeTests
    {
        private const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        private static string GameObjectBlock(long id, string name) =>
            $"--- !u!1 &{id}\nGameObject:\n  m_Name: {name}\n  m_IsActive: 1\n";

        private static string GameObjectWithComponentsBlock(
            long id, string name, IEnumerable<long> componentIds)
        {
            string componentYaml = "  m_Component: []\n";
            var list = new List<long>(componentIds ?? new long[0]);
            if (list.Count > 0)
            {
                componentYaml = "  m_Component:\n";
                foreach (long componentId in list)
                    componentYaml += $"  - component: {{fileID: {componentId}}}\n";
            }
            return $"--- !u!1 &{id}\nGameObject:\n{componentYaml}  m_Name: {name}\n  m_IsActive: 1\n";
        }

        private static string ComponentBlock(long id, long ownerId) =>
            $"--- !u!114 &{id}\nMonoBehaviour:\n  m_GameObject: {{fileID: {ownerId}}}\n";

        private static string ReferenceHolderBlock(long id, long ownerId, long targetId, string ambientValue) =>
            $"--- !u!114 &{id}\nMonoBehaviour:\n  m_GameObject: {{fileID: {ownerId}}}\n" +
            $"  m_Target: {{fileID: {targetId}}}\n  m_AmbientValue: {ambientValue}\n";

        private static string TransformBlock(long id, long ownerId, long fatherId, IEnumerable<long> children)
        {
            string childYaml = "  m_Children: []\n";
            var list = new List<long>(children ?? new long[0]);
            if (list.Count > 0)
            {
                childYaml = "  m_Children:\n";
                foreach (long child in list)
                    childYaml += $"  - {{fileID: {child}}}\n";
            }
            return $"--- !u!4 &{id}\nTransform:\n  m_GameObject: {{fileID: {ownerId}}}\n{childYaml}  m_Father: {{fileID: {fatherId}}}\n";
        }

        private static string SceneRootsBlock(params long[] roots)
        {
            string rootYaml = roots.Length == 0 ? "  m_Roots: []\n" : "  m_Roots:\n";
            foreach (long root in roots)
                rootYaml += $"  - {{fileID: {root}}}\n";
            return $"--- !u!1660057539 &9223372036854775807\nSceneRoots:\n  m_ObjectHideFlags: 0\n{rootYaml}";
        }

        private static readonly HashSet<long> None = new HashSet<long>();

        [Test]
        public void UnscopedDrift_KeepsDiskBytes_AndReportsIt()
        {
            string disk = Header + GameObjectBlock(100, "Label") + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);
            string temp = Header + GameObjectBlock(100, "DriftedPreviewText") + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);

            var result = SceneYamlMerge.Merge(disk, temp, None, None);

            Assert.AreEqual(disk, result.MergedText, "unscoped drift must not reach the file");
            Assert.AreEqual(1, result.BlocksDriftSuppressed);
            CollectionAssert.Contains(result.DriftSuppressedIds, 100L);
        }

        [Test]
        public void ScopedEdit_IsWritten_WhileSiblingDriftIsSuppressed()
        {
            string disk = Header + GameObjectBlock(100, "Edited") + GameObjectBlock(200, "Bystander")
                + TransformBlock(101, 100, 0, null) + TransformBlock(201, 200, 0, null) + SceneRootsBlock(101, 201);
            string temp = Header + GameObjectBlock(100, "EditedNewName") + GameObjectBlock(200, "BystanderDrift")
                + TransformBlock(101, 100, 0, null) + TransformBlock(201, 200, 0, null) + SceneRootsBlock(101, 201);

            var result = SceneYamlMerge.Merge(disk, temp, new HashSet<long> { 100 }, None);

            StringAssert.Contains("EditedNewName", result.MergedText);
            StringAssert.Contains("m_Name: Bystander\n", result.MergedText);
            StringAssert.DoesNotContain("BystanderDrift", result.MergedText);
            Assert.AreEqual(1, result.BlocksScoped);
            Assert.AreEqual(1, result.BlocksDriftSuppressed);
        }

        [Test]
        public void NewObject_IsAdded_AndParentChildListSpliced_WithoutBakingParentDrift()
        {
            // Parent 101 drifted in memory (m_Father comment aside, name change on its GameObject
            // 100) and gained a new child 301. The child must land in the file and appear in the
            // parent's m_Children, while the parent GameObject keeps its disk name.
            string disk = Header + GameObjectBlock(100, "Parent") + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);
            string temp = Header + GameObjectBlock(100, "ParentDrift") + TransformBlock(101, 100, 0, new long[] { 301 })
                + GameObjectBlock(300, "NewChild") + TransformBlock(301, 300, 101, null) + SceneRootsBlock(101);

            var result = SceneYamlMerge.Merge(disk, temp, None, None);

            StringAssert.Contains("m_Name: NewChild", result.MergedText);
            StringAssert.Contains("m_Name: Parent\n", result.MergedText);
            StringAssert.Contains("m_Children:\n  - {fileID: 301}", result.MergedText);
            Assert.AreEqual(2, result.BlocksNew);
            Assert.IsNotEmpty(result.Warnings);
            CollectionAssert.AreEquivalent(new[] { 100L }, result.DriftSuppressedIds,
                "only the parent GameObject's name drifted; the spliced m_Children is not drift");
        }

        [Test]
        public void NewRootObject_IsSplicedIntoSceneRoots()
        {
            string disk = Header + GameObjectBlock(100, "Existing") + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);
            string temp = Header + GameObjectBlock(100, "Existing") + TransformBlock(101, 100, 0, null)
                + GameObjectBlock(400, "NewRoot") + TransformBlock(401, 400, 0, null) + SceneRootsBlock(101, 401);

            var result = SceneYamlMerge.Merge(disk, temp, None, None);

            StringAssert.Contains("m_Name: NewRoot", result.MergedText);
            StringAssert.Contains("- {fileID: 101}\n  - {fileID: 401}", result.MergedText);
            Assert.AreEqual(0, result.BlocksDriftSuppressed,
                "the reconciled SceneRoots splice is intentional, not drift — reject mode must not trip on it");
        }

        [Test]
        public void ScopedDeletion_DropsBlocks_AndParentListEntry()
        {
            string disk = Header + GameObjectBlock(100, "Parent") + TransformBlock(101, 100, 0, new long[] { 301 })
                + GameObjectBlock(300, "Doomed") + TransformBlock(301, 300, 101, null) + SceneRootsBlock(101);
            string temp = Header + GameObjectBlock(100, "Parent") + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);

            var result = SceneYamlMerge.Merge(disk, temp, new HashSet<long> { 101 }, new HashSet<long> { 300, 301 });

            StringAssert.DoesNotContain("Doomed", result.MergedText);
            StringAssert.DoesNotContain("fileID: 301", result.MergedText);
            Assert.AreEqual(2, result.BlocksDeleted);
        }

        [Test]
        public void ScopedDeletion_ClearsReferenceFromUnscopedComponent_WithoutWritingItsDrift()
        {
            string disk = Header
                + GameObjectWithComponentsBlock(100, "Holder", new long[] { 101, 102 })
                + TransformBlock(101, 100, 0, null)
                + ReferenceHolderBlock(102, 100, 300, "disk")
                + GameObjectBlock(300, "Doomed")
                + TransformBlock(301, 300, 0, null)
                + SceneRootsBlock(101, 301);
            string temp = Header
                + GameObjectWithComponentsBlock(100, "Holder", new long[] { 101, 102 })
                + TransformBlock(101, 100, 0, null)
                + ReferenceHolderBlock(102, 100, 0, "ambient-drift")
                + SceneRootsBlock(101);

            var result = SceneYamlMerge.Merge(
                disk, temp, None, new HashSet<long> { 300, 301 });

            StringAssert.DoesNotContain("--- !u!1 &300", result.MergedText);
            StringAssert.DoesNotContain("--- !u!4 &301", result.MergedText);
            StringAssert.Contains("m_Target: {fileID: 0}", result.MergedText,
                "references to scoped deletions must be cleared even on an unscoped owner");
            StringAssert.Contains("m_AmbientValue: disk", result.MergedText);
            StringAssert.DoesNotContain("ambient-drift", result.MergedText);
            CollectionAssert.Contains(result.DriftSuppressedIds, 102L);
        }

        [Test]
        public void UnscopedDeletion_KeepsDiskBlock_AndReportsIt()
        {
            string disk = Header + GameObjectBlock(100, "Survivor") + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);
            string temp = Header + SceneRootsBlock();

            var result = SceneYamlMerge.Merge(disk, temp, None, None);

            StringAssert.Contains("m_Name: Survivor", result.MergedText);
            CollectionAssert.AreEquivalent(new[] { 100L, 101L }, result.UnscopedDeletionsKept);
            // SceneRoots keeps the disk root entry because the survivor's disk block remains.
            StringAssert.Contains("- {fileID: 101}", result.MergedText);
        }

        [Test]
        public void UnscopedComponentDeletion_ReinsertsReferenceIntoScopedGameObject()
        {
            string disk = Header + GameObjectWithComponentsBlock(100, "Owner", new long[] { 101, 102 })
                + TransformBlock(101, 100, 0, null) + ComponentBlock(102, 100) + SceneRootsBlock(101);
            string temp = Header + GameObjectWithComponentsBlock(100, "Renamed", new long[] { 101 })
                + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);

            var result = SceneYamlMerge.Merge(disk, temp, new HashSet<long> { 100 }, None);

            StringAssert.Contains("m_Name: Renamed", result.MergedText,
                "the scoped GameObject edit must still be written");
            StringAssert.Contains("- component: {fileID: 102}", result.MergedText,
                "the retained component block must remain owned by the GameObject");
            StringAssert.Contains("--- !u!114 &102", result.MergedText);
            CollectionAssert.Contains(result.UnscopedDeletionsKept, 102L);
        }

        [Test]
        public void UnscopedChildDeletion_ReinsertsReferenceIntoScopedTransform()
        {
            string disk = Header + GameObjectBlock(100, "Parent")
                + TransformBlock(101, 100, 0, new long[] { 301 })
                + GameObjectBlock(300, "Child") + TransformBlock(301, 300, 101, null)
                + SceneRootsBlock(101);
            string temp = Header + GameObjectBlock(100, "Parent")
                + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);

            var result = SceneYamlMerge.Merge(disk, temp, new HashSet<long> { 101 }, None);

            StringAssert.Contains("m_Children:\n  - {fileID: 301}", result.MergedText,
                "the retained child blocks must remain linked from the scoped Transform");
            StringAssert.Contains("m_Name: Child", result.MergedText);
            CollectionAssert.AreEquivalent(new[] { 300L, 301L }, result.UnscopedDeletionsKept);
        }

        [Test]
        public void UnscopedSiblingReorder_KeepsDiskOrder()
        {
            string blocks(string firstName, string secondName) =>
                GameObjectBlock(100, firstName) + TransformBlock(101, 100, 0, null)
                + GameObjectBlock(200, secondName) + TransformBlock(201, 200, 0, null);
            string disk = Header + blocks("First", "Second") + SceneRootsBlock(101, 201);
            string temp = Header + blocks("First", "Second") + SceneRootsBlock(201, 101);

            var result = SceneYamlMerge.Merge(disk, temp, None, None);

            Assert.AreEqual(disk, result.MergedText, "ambient reorder is drift and must not reach the file");
            Assert.IsEmpty(result.Warnings);
        }

        [Test]
        public void UnscopedDeletion_OfMiddleChild_KeepsDiskPosition()
        {
            // Child 300/301 vanished in memory without a recorded deletion; its block is kept,
            // and its m_Children entry must stay in the middle, not migrate to the end.
            string parentAndSiblings(IEnumerable<long> children) =>
                GameObjectBlock(100, "Parent") + TransformBlock(101, 100, 0, children)
                + GameObjectBlock(200, "KeptA") + TransformBlock(201, 200, 101, null)
                + GameObjectBlock(400, "KeptB") + TransformBlock(401, 400, 101, null);
            string disk = Header + parentAndSiblings(new long[] { 201, 301, 401 })
                + GameObjectBlock(300, "Vanished") + TransformBlock(301, 300, 101, null) + SceneRootsBlock(101);
            string temp = Header + parentAndSiblings(new long[] { 201, 401 }) + SceneRootsBlock(101);

            var result = SceneYamlMerge.Merge(disk, temp, None, None);

            Assert.AreEqual(disk, result.MergedText, "kept child must not move within m_Children");
            CollectionAssert.AreEquivalent(new[] { 300L, 301L }, result.UnscopedDeletionsKept);
        }

        [Test]
        public void ScopedReorder_IsWritten()
        {
            string blocks(IEnumerable<long> children) =>
                GameObjectBlock(100, "Parent") + TransformBlock(101, 100, 0, children)
                + GameObjectBlock(200, "Moved") + TransformBlock(201, 200, 101, null)
                + GameObjectBlock(300, "Sibling") + TransformBlock(301, 300, 101, null);
            string disk = Header + blocks(new long[] { 201, 301 }) + SceneRootsBlock(101);
            string temp = Header + blocks(new long[] { 301, 201 }) + SceneRootsBlock(101);

            var result = SceneYamlMerge.Merge(disk, temp, new HashSet<long> { 201 }, None);

            StringAssert.Contains("m_Children:\n  - {fileID: 301}\n  - {fileID: 201}", result.MergedText);
            Assert.IsNotEmpty(result.Warnings);
            Assert.AreEqual(0, result.BlocksDriftSuppressed,
                "the scoped reorder is fully written, so nothing was suppressed");
        }

        [Test]
        public void ScopedPrefabInstance_TakesStrippedMemberBlocksToo()
        {
            string prefabInstance(long id, string modsName) =>
                $"--- !u!1001 &{id}\nPrefabInstance:\n  m_Modification:\n    value: {modsName}\n";
            string stripped(long id, long instanceId) =>
                $"--- !u!224 &{id} stripped\nRectTransform:\n  m_PrefabInstance: {{fileID: {instanceId}}}\n";

            string disk = Header + prefabInstance(500, "old") + stripped(501, 500) + SceneRootsBlock(501);
            string temp = Header + prefabInstance(500, "new") + stripped(501, 500) + SceneRootsBlock(501);

            var result = SceneYamlMerge.Merge(disk, temp, new HashSet<long> { 500 }, None);

            StringAssert.Contains("value: new", result.MergedText);
            Assert.AreEqual(2, result.BlocksScoped, "the PrefabInstance and its stripped member block are both scoped");
            Assert.AreEqual(0, result.BlocksDriftSuppressed);
        }

        [Test]
        public void NoChanges_ProducesByteIdenticalOutput()
        {
            string disk = Header + GameObjectBlock(100, "Same") + TransformBlock(101, 100, 0, null) + SceneRootsBlock(101);
            var result = SceneYamlMerge.Merge(disk, disk, None, None);
            Assert.AreEqual(disk, result.MergedText);
            Assert.AreEqual(0, result.BlocksDriftSuppressed);
        }
    }
}
