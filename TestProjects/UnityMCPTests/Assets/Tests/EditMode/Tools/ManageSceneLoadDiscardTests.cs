using System.IO;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Tests.EditMode.Tools
{
    /// <summary>
    /// Covers manage_scene load's handling of unsaved changes: a single load must refuse while ANY
    /// loaded scene is dirty, honour discard_unsaved without writing to disk, and every action that
    /// cannot honour the flag must reject it instead of silently ignoring it.
    /// </summary>
    [TestFixture]
    public class ManageSceneLoadDiscardTests
    {
        private const string SourceScene = "Assets/Scenes/SampleScene.unity";
        private const string SceneA = "Assets/Scenes/LoadDiscardTempA.unity";
        private const string SceneB = "Assets/Scenes/LoadDiscardTempB.unity";

        private string _previousScenePath;

        [SetUp]
        public void SetUp()
        {
            if (SceneManager.loadedSceneCount != 1 || SceneManager.GetActiveScene().isDirty)
            {
                Assert.Ignore("Test requires one clean scene so it can restore the prior Editor state.");
            }
            _previousScenePath = SceneManager.GetActiveScene().path;
            foreach (string path in new[] { SceneA, SceneB })
            {
                AssetDatabase.DeleteAsset(path);
                Assert.IsTrue(AssetDatabase.CopyAsset(SourceScene, path), $"Could not copy '{SourceScene}' to '{path}'.");
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrEmpty(_previousScenePath))
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            else
            {
                EditorSceneManager.OpenScene(_previousScenePath, OpenSceneMode.Single);
            }
            AssetDatabase.DeleteAsset(SceneA);
            AssetDatabase.DeleteAsset(SceneB);
        }

        [Test]
        public void ADirtyActiveSceneBlocksALoadThatDoesNotDiscard()
        {
            Scene a = EditorSceneManager.OpenScene(SceneA, OpenSceneMode.Single);
            EditorSceneManager.MarkSceneDirty(a);

            var result = Invoke(Load(SceneB));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains(SceneA, result.Value<string>("error"));
            Assert.AreEqual(SceneA, SceneManager.GetActiveScene().path);
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty, "The refused load must leave the changes in place.");
        }

        [Test]
        public void ADirtyNonActiveSceneAlsoBlocksASingleLoad()
        {
            // A Single-mode load closes every scene, so a dirty additive scene would be lost too.
            Scene a = EditorSceneManager.OpenScene(SceneA, OpenSceneMode.Single);
            Scene b = EditorSceneManager.OpenScene(SceneB, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(a);
            EditorSceneManager.MarkSceneDirty(b);

            var result = Invoke(Load(SourceScene));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains(SceneB, result.Value<string>("error"));
            Assert.AreEqual(2, SceneManager.loadedSceneCount);
        }

        [Test]
        public void DiscardUnsavedDropsTheChangesWithoutWritingThem()
        {
            string fullPath = FullPath(SceneA);
            byte[] before = File.ReadAllBytes(fullPath);
            Scene a = EditorSceneManager.OpenScene(SceneA, OpenSceneMode.Single);
            new GameObject("UnsavedMarker");
            EditorSceneManager.MarkSceneDirty(a);

            var p = Load(SceneB);
            p["discard_unsaved"] = true;
            var result = Invoke(p);

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(SceneB, SceneManager.GetActiveScene().path);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fullPath), "Discarding must not save the scene.");
            CollectionAssert.AreEqual(new[] { SceneA }, result["data"]["discardedUnsaved"].ToObject<string[]>());
        }

        [Test]
        public void AnAdditiveLoadRejectsDiscardUnsaved()
        {
            var p = Load(SceneB);
            p["additive"] = true;
            p["discard_unsaved"] = true;

            var result = Invoke(p);

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("discard_unsaved", result.Value<string>("error"));
            Assert.AreEqual(1, SceneManager.loadedSceneCount);
        }

        [Test]
        public void AnActionThatCannotDiscardRejectsTheFlag()
        {
            var result = Invoke(new JObject { ["action"] = "get_active", ["discard_unsaved"] = true });

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("discard_unsaved", result.Value<string>("error"));
        }

        private static JObject Load(string path) => new JObject { ["action"] = "load", ["path"] = path };

        private static JObject Invoke(JObject p)
        {
            object result = ManageScene.HandleCommand(p);
            return result as JObject ?? JObject.FromObject(result);
        }

        private static string FullPath(string assetPath) => Path.Combine(
            Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length), assetPath);
    }
}
