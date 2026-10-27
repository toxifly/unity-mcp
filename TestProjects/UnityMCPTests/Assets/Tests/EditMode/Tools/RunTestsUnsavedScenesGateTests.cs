using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
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
            int before = TestRunnerService.UnsavedScenes().Count;
            WithScratchScene(scene =>
            {
                Assert.AreEqual(before, TestRunnerService.UnsavedScenes().Count,
                    "A freshly created scene carries no edits yet");

                SceneManager.MoveGameObjectToScene(new GameObject("Edit"), scene);
                EditorSceneManager.MarkSceneDirty(scene);

                var unsaved = TestRunnerService.UnsavedScenes();
                Assert.AreEqual(before + 1, unsaved.Count);
                CollectionAssert.Contains(unsaved, "(untitled scene)");
            });
        }

        [Test]
        public void ARunStillHoldingTheRunnerIsReportedInsteadOfItsDirtyScenes()
        {
            // A run whose job already ended can still be executing tests, and those tests may be
            // what dirtied the scenes: saving or discarding them is not the caller's fix. Run
            // through run_tests, this suite's own job is current, so the test hides it.
            object originalRunner = RunnerField.GetValue(null);
            object originalJobId = CurrentJobIdField.GetValue(null);
            MCPServiceLocator.Register<ITestRunnerService>(new PendingRunner());
            CurrentJobIdField.SetValue(null, null);
            try
            {
                WithScratchScene(scene =>
                {
                    SceneManager.MoveGameObjectToScene(new GameObject("Edit"), scene);
                    EditorSceneManager.MarkSceneDirty(scene);

                    var response = JObject.FromObject(RunTests.HandleCommand(new JObject()).Result);

                    Assert.AreEqual("tests_running", (string)response["error"]);
                });
            }
            finally
            {
                CurrentJobIdField.SetValue(null, originalJobId);
                RunnerField.SetValue(null, originalRunner);
            }
        }

        private static readonly FieldInfo RunnerField =
            typeof(MCPServiceLocator).GetField("_testRunnerService", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly FieldInfo CurrentJobIdField =
            typeof(TestJobManager).GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);

        private static void WithScratchScene(Action<Scene> body)
        {
            // An untitled active scene (batchmode's default) blocks additive scene creation, so
            // replace it instead of adding alongside it.
            Scene active = SceneManager.GetActiveScene();
            bool replaceUntitled = string.IsNullOrEmpty(active.path);
            if (replaceUntitled && active.isDirty)
            {
                Assert.Ignore("Test requires the untitled scene to be clean so it can replace it.");
            }

            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, replaceUntitled ? NewSceneMode.Single : NewSceneMode.Additive);
            try
            {
                body(scene);
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

        private sealed class PendingRunner : ITestRunnerService
        {
            public bool HasPendingRun => true;

            public Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode) =>
                throw new NotSupportedException();

            public Task<TestRunResult> RunTestsAsync(
                TestMode mode, TestFilterOptions filterOptions = null, string jobId = null) =>
                throw new NotSupportedException();

            public bool TryAbortWedgedRun(string reason) => false;
        }
    }
}
