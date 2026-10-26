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
            // Regression: the unsaved-scene re-check threw after the run's completion source was
            // created and never ended it, so every later run was refused as already in progress
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
    }
}
