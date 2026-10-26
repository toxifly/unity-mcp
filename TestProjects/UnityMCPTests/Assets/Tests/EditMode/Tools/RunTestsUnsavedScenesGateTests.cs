using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// run_tests must refuse unsaved scenes instead of saving them: a silent save bakes editor
    /// preview state into assets, and leaving them lets the Test Framework's save prompt block the
    /// main thread. Exercised through the pure gate and the dirty-scene query, never a real run.
    /// </summary>
    public class RunTestsUnsavedScenesGateTests
    {
        [Test]
        public void CleanScenesAreNotRefused()
        {
            Assert.IsNull(RunTests.UnsavedScenesRefusal(null));
            Assert.IsNull(RunTests.UnsavedScenesRefusal(new string[0]));
        }

        [Test]
        public void RefusalNamesTheScenesAndHowToClearThem()
        {
            var response = RunTests.UnsavedScenesRefusal(new[] { "Assets/Scenes/A.unity", "Assets/Prefabs/B.prefab" });

            Assert.IsInstanceOf<ErrorResponse>(response);
            var refusal = JObject.FromObject(response);
            Assert.AreEqual("unsaved_scenes", (string)refusal["error"]);
            CollectionAssert.AreEqual(
                new[] { "Assets/Scenes/A.unity", "Assets/Prefabs/B.prefab" },
                ((JArray)refusal["data"]["scenes"]).Select(t => (string)t).ToArray());

            string message = (string)refusal["data"]["message"];
            StringAssert.Contains("Assets/Scenes/A.unity, Assets/Prefabs/B.prefab", message);
            StringAssert.Contains("save_scene_scoped", message);
            StringAssert.Contains("discard_unsaved=true", message);
        }

        [Test]
        public void UnsavedScenesListsADirtyLoadedScene()
        {
            // An untitled active scene (batchmode's default) blocks additive scene creation, so
            // replace it instead of adding alongside it.
            Scene active = SceneManager.GetActiveScene();
            bool replaceUntitled = string.IsNullOrEmpty(active.path);
            if (replaceUntitled && active.isDirty)
            {
                Assert.Ignore("Test requires the untitled scene to be clean so it can replace it.");
            }

            int before = TestRunnerService.UnsavedScenes().Count;
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, replaceUntitled ? NewSceneMode.Single : NewSceneMode.Additive);
            try
            {
                Assert.AreEqual(before, TestRunnerService.UnsavedScenes().Count,
                    "A freshly created scene carries no edits yet");

                SceneManager.MoveGameObjectToScene(new GameObject("Edit"), scene);
                EditorSceneManager.MarkSceneDirty(scene);

                var unsaved = TestRunnerService.UnsavedScenes();
                Assert.AreEqual(before + 1, unsaved.Count);
                CollectionAssert.Contains(unsaved, "(untitled scene)");
            }
            finally
            {
                if (SceneManager.sceneCount > 1)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
        }
    }
}
