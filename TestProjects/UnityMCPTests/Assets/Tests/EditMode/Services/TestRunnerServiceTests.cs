using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine.SceneManagement;

namespace MCPForUnityTests.Editor.Services
{
    public class TestRunnerServiceTests
    {
        [Test]
        public void ARunRefusedOverUnsavedScenesLeavesNoPendingRun()
        {
            // Regression: the unsaved-scene re-check once threw after the run's completion source
            // was created and never ended it, so every later run was refused as already in progress
            // and clear_stuck could not tell the dead source from a live run.
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
            var service = new TestRunnerService();
            try
            {
                EditorSceneManager.MarkSceneDirty(scene);

                var run = service.RunTestsAsync(
                    TestMode.EditMode, new TestFilterOptions { TestNames = new[] { "No.Such.Test" } });

                Assert.IsTrue(run.IsFaulted, "The refusal must happen before the run is dispatched");
                StringAssert.StartsWith("unsaved_scenes:", run.Exception.GetBaseException().Message);
                Assert.IsFalse(service.HasPendingRun, "A refused run must not hold the runner");
            }
            finally
            {
                service.Dispose();
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

        [Test]
        public void ARunWhoseJobEndedBeforeDispatchLeavesNoPendingRun()
        {
            // The job check throws after the completion source exists, so it exercises the cleanup
            // any throw between creating the source and dispatching the run relies on.
            if (TestRunStatus.IsRunning)
            {
                Assert.Ignore("A live MCP test run owns the run status this test would end.");
            }
            if (TestRunnerService.UnsavedScenes().Count > 0)
            {
                Assert.Ignore("Unsaved scenes refuse the run before its completion source exists.");
            }

            var service = new TestRunnerService();
            try
            {
                var run = service.RunTestsAsync(
                    TestMode.EditMode, new TestFilterOptions { TestNames = new[] { "No.Such.Test" } }, "no-such-job");

                Assert.IsTrue(run.IsFaulted, "The job check must refuse before the run is dispatched");
                StringAssert.Contains("ended before its run was dispatched", run.Exception.GetBaseException().Message);
                Assert.IsFalse(service.HasPendingRun, "A run that never dispatched must not hold the runner");
                Assert.IsFalse(TestRunStatus.IsRunning, "A run that never dispatched must not read as running");
            }
            finally
            {
                service.Dispose();
            }
        }
    }
}
