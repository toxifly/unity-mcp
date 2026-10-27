using System;
using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace MCPForUnityTests.Editor.Services
{
    public class TestJobManagerLifecycleTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

        [Test]
        public void Snapshot_ReportsActualRunInBackgroundSetting()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var job = new TestJob
            {
                JobId = "lifecycle-job", Mode = "EditMode", Status = TestJobStatus.Running,
                StartedUnixMs = now, LastUpdateUnixMs = now,
                FailuresSoFar = new List<TestJobFailure>()
            };

            var payload = JObject.FromObject(TestJobManager.ToSerializable(job, false, false, false));

            Assert.AreEqual(Application.runInBackground, (bool)payload["progress"]["run_in_background"]);
        }

        [Test]
        public void NoThrottle_ErrorBeforeRunStarted_RestoresCapturedPreferences()
        {
            const string activeKey = "TestRunnerNoThrottle_TestRunActive";
            const string capturedKey = "TestRunnerNoThrottle_SettingsCaptured";
            const string idleKey = "TestRunnerNoThrottle_PrevIdleTime";
            const string modeKey = "TestRunnerNoThrottle_PrevInteractionMode";
            bool active = SessionState.GetBool(activeKey, false);
            bool captured = SessionState.GetBool(capturedKey, false);
            int previousIdle = SessionState.GetInt(idleKey, 4);
            int previousMode = SessionState.GetInt(modeKey, 0);
            bool hadIdle = EditorPrefs.HasKey("ApplicationIdleTime");
            bool hadMode = EditorPrefs.HasKey("InteractionMode");
            int idle = EditorPrefs.GetInt("ApplicationIdleTime", 4);
            int mode = EditorPrefs.GetInt("InteractionMode", 0);
            try
            {
                SessionState.SetBool(capturedKey, false);
                EditorPrefs.SetInt("ApplicationIdleTime", 7);
                EditorPrefs.SetInt("InteractionMode", 0);
                TestRunnerNoThrottle.ApplyNoThrottlingPreemptive();
                Assert.AreEqual(0, EditorPrefs.GetInt("ApplicationIdleTime"));
                Assert.AreEqual(1, EditorPrefs.GetInt("InteractionMode"));

                var callbackType = typeof(TestRunnerNoThrottle).GetNestedType("TestCallbacks", BindingFlags.NonPublic);
                var callback = Activator.CreateInstance(callbackType, true) as IErrorCallbacks;
                Assert.NotNull(callback, "Initialization errors must be observed even before RunStarted.");
                callback.OnError("Prebuild setup failed");

                Assert.AreEqual(7, EditorPrefs.GetInt("ApplicationIdleTime"));
                Assert.AreEqual(0, EditorPrefs.GetInt("InteractionMode"));
                Assert.IsFalse(SessionState.GetBool(activeKey, true));
                Assert.IsFalse(SessionState.GetBool(capturedKey, true));
            }
            finally
            {
                SessionState.SetBool(activeKey, active);
                SessionState.SetBool(capturedKey, captured);
                SessionState.SetInt(idleKey, previousIdle);
                SessionState.SetInt(modeKey, previousMode);
                if (hadIdle) EditorPrefs.SetInt("ApplicationIdleTime", idle);
                else EditorPrefs.DeleteKey("ApplicationIdleTime");
                if (hadMode) EditorPrefs.SetInt("InteractionMode", mode);
                else EditorPrefs.DeleteKey("InteractionMode");
                typeof(TestRunnerNoThrottle).GetMethod("ForceEditorToApplyInteractionPrefs", PrivateStatic).Invoke(null, null);
            }
        }
    }
}
