using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using MCPForUnity.Editor.Services;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Tests for TestJobManager's per-job InitTimeoutMs feature.
    /// Uses reflection to manipulate internal state since StartJob triggers a real test run.
    /// </summary>
    public class TestJobManagerInitTimeoutTests
    {
        private FieldInfo _jobsField;
        private FieldInfo _currentJobIdField;
        private MethodInfo _getJobMethod;
        private MethodInfo _persistMethod;
        private MethodInfo _restoreMethod;
        private Type _testJobType;
        private FieldInfo _stallEndedField;
        private FieldInfo _loopTickField;

        private string _originalJobId;
        private long _originalStallEnded;
        private long _originalLoopTick;

        [SetUp]
        public void SetUp()
        {
            var asm = typeof(MCPServiceLocator).Assembly;
            var managerType = asm.GetType("MCPForUnity.Editor.Services.TestJobManager");
            Assert.NotNull(managerType, "Could not find TestJobManager");

            _testJobType = asm.GetType("MCPForUnity.Editor.Services.TestJob");
            Assert.NotNull(_testJobType, "Could not find TestJob");

            _jobsField = managerType.GetField("Jobs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_jobsField, "Could not find Jobs field");

            _currentJobIdField = managerType.GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_currentJobIdField, "Could not find _currentJobId field");

            _getJobMethod = managerType.GetMethod("GetJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_getJobMethod, "Could not find GetJob method");

            _persistMethod = managerType.GetMethod("PersistToSessionState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_persistMethod, "Could not find PersistToSessionState method");

            _restoreMethod = managerType.GetMethod("TryRestoreFromSessionState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_restoreMethod, "Could not find TryRestoreFromSessionState method");

            _stallEndedField = managerType.GetField("_lastStallEndedUnixMs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_stallEndedField, "Could not find _lastStallEndedUnixMs field");

            _loopTickField = managerType.GetField("_lastLoopTickUnixMs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_loopTickField, "Could not find _lastLoopTickUnixMs field");

            // Snapshot original state
            _originalJobId = _currentJobIdField.GetValue(null) as string;
            _originalStallEnded = (long)_stallEndedField.GetValue(null);
            _originalLoopTick = (long)_loopTickField.GetValue(null);

            // These tests fabricate a job's idle time, so they have to fabricate the editor-loop
            // liveness the watchdog weighs it against too: it refuses to charge idleness that
            // elapsed while the loop was frozen, and it counts a domain load as one such freeze --
            // which in a batch run happened seconds ago, so every fabricated age would be excused
            // and no job could auto-fail. A live, unstalled loop is the premise each test is about.
            _stallEndedField.SetValue(null, 0L);
            _loopTickField.SetValue(null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // We'll restore _currentJobId in TearDown; Jobs dictionary is shared static state
        }

        [TearDown]
        public void TearDown()
        {
            // Restore original state
            _currentJobIdField.SetValue(null, _originalJobId);
            _stallEndedField.SetValue(null, _originalStallEnded);
            _loopTickField.SetValue(null, _originalLoopTick);
            // Clean up any test jobs we inserted
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            jobs?.Remove("test-init-timeout-job");
            jobs?.Remove("test-init-timeout-default");
            jobs?.Remove("test-init-timeout-persist");
            jobs?.Remove("test-init-timeout-runstarted-stall");
            jobs?.Remove("test-init-timeout-running-test");
            jobs?.Remove("test-midrun-wedge");
            jobs?.Remove("test-midrun-alive");
            jobs?.Remove("test-midrun-editmode");
            // Flush cleaned state to SessionState so synthetic jobs don't survive domain reloads.
            // The persist test writes to SessionState; without this, the stub job would be
            // restored on the next [InitializeOnLoadMethod] and pollute later test runs.
            _persistMethod.Invoke(null, new object[] { true });
        }

        [Test]
        public void GetJob_WithCustomInitTimeout_UsesPerJobTimeout()
        {
            // Arrange: insert a job with a custom init timeout and a start time far enough in the
            // past to exceed the default 15s but within the custom 120s.
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-job");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 30_000); // 30s ago
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 30_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, null); // Not initialized yet
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 120_000L); // 120s custom timeout
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-job"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-job");

            // Act: GetJob should NOT auto-fail because 30s < 120s custom timeout
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-job" });

            // Assert: job should still be running
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Running, status,
                "Job with 120s custom timeout should not auto-fail after 30s");
        }

        [Test]
        public void GetJob_WithDefaultTimeout_AutoFailsAfter15Seconds()
        {
            // Arrange: insert a job with InitTimeoutMs=0 (use default) and start time 20s ago
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-default");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "EditMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 20_000); // 20s ago
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 20_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, null);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 0L); // Use default
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-default"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-default");

            // Act: GetJob should auto-fail because 20s > 15s default
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-default" });

            // Assert: job should be failed
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Failed, status,
                "Job with default timeout should auto-fail after 20s");
        }

        [Test]
        public void GetJob_WhenRunStartedButNoTestBegan_AutoFailsAfterTimeout()
        {
            // Regression: the Unity Test Framework runner can throw a NullReference AFTER RunStarted
            // (which sets TotalTests) but BEFORE the first TestStarted / RunFinished. RunFinished never
            // fires and the awaited task never completes, so nothing clears _currentJobId and the job
            // wedges "running" forever — every later run_tests is rejected with "tests_running".
            // The watchdog must catch this even though TotalTests is non-null.
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-runstarted-stall");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 200_000);
            // RunStarted fired ~195s ago (announced a total), then the runner died: no further updates.
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 195_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, 4); // RunStarted set a total
            _testJobType.GetProperty("CompletedTests").SetValue(job, 0); // ...but no test ran
            _testJobType.GetProperty("CurrentTestFullName").SetValue(job, null); // ...and none started
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 120_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-runstarted-stall"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-runstarted-stall");

            // Act
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-runstarted-stall" });

            // Assert: job auto-failed and _currentJobId cleared so new runs can start.
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Failed, status,
                "Job that got RunStarted but never ran a test should auto-fail past the init timeout");
            Assert.IsNull(_currentJobIdField.GetValue(null) as string,
                "_currentJobId must be cleared so subsequent run_tests is not rejected with tests_running");
        }

        [Test]
        public void GetJob_WhenTestActuallyRunning_DoesNotAutoFail()
        {
            // Guard: a genuinely slow test (CurrentTestFullName set) must never be auto-failed by the
            // pre-first-test watchdog, even long past the init timeout.
            // Uses an EditMode job: a stalled PlayMode job with the editor OUT of play mode is now
            // legitimately auto-failed by the mid-run wedge watchdog (a real in-flight PlayMode test
            // keeps the editor in play mode, which cannot be synthesized from this EditMode test).
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-running-test");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "EditMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 200_000);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 195_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, 4);
            _testJobType.GetProperty("CompletedTests").SetValue(job, 0);
            _testJobType.GetProperty("CurrentTestFullName").SetValue(job, "Some.Slow.Test"); // a test is executing
            _testJobType.GetProperty("CurrentTestStartedUnixMs").SetValue(job, now - 195_000);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 120_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-running-test"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-running-test");

            // Act
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-running-test" });

            // Assert: still running — the watchdog must not kill an in-flight test.
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Running, status,
                "A job with a test actively executing must not be auto-failed by the init watchdog");
        }

        [Test]
        public void GetJob_PlayModeWedgedMidRun_AutoFailsAndClearsCurrentJob()
        {
            // Regression (2026-07-05): the Unity Test Framework runner NRE'd in PlayModeRunTask
            // MID-RUN (44/81 tests done, a current test recorded). Play mode exited, RunFinished was
            // never delivered, and the awaited run task never completed — the job stayed "running"
            // forever. The mid-run watchdog must fail it: tests began + PlayMode job + editor not in
            // play mode (guaranteed here: this is an EditMode test) + quiet past the stuck threshold.
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-midrun-wedge");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 300_000);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 90_000); // quiet 90s > 60s threshold
            _testJobType.GetProperty("TotalTests").SetValue(job, 81);
            _testJobType.GetProperty("CompletedTests").SetValue(job, 44); // run clearly began
            _testJobType.GetProperty("CurrentTestFullName").SetValue(job, "Some.Test.That.Never.Finished");
            _testJobType.GetProperty("CurrentTestStartedUnixMs").SetValue(job, now - 90_000);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 120_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-midrun-wedge"] = job;
            _currentJobIdField.SetValue(null, "test-midrun-wedge");

            var result = _getJobMethod.Invoke(null, new object[] { "test-midrun-wedge" });

            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Failed, status,
                "A PlayMode job that went quiet mid-run with the editor out of play mode must auto-fail");
            Assert.IsNull(_currentJobIdField.GetValue(null) as string,
                "_currentJobId must be cleared so subsequent run_tests is not rejected with tests_running");
        }

        [Test]
        public void GetJob_PlayModeMidRun_RecentUpdate_DoesNotAutoFail()
        {
            // Guard: a PlayMode run that is making progress must not be touched even though the
            // editor is not in play mode from this test's perspective — the staleness bound is what
            // separates "just exited play, RunFinished on its way" from a dead runner.
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-midrun-alive");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 300_000);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 5_000); // updated 5s ago
            _testJobType.GetProperty("TotalTests").SetValue(job, 81);
            _testJobType.GetProperty("CompletedTests").SetValue(job, 80);
            _testJobType.GetProperty("CurrentTestFullName").SetValue(job, "Some.Final.Test");
            _testJobType.GetProperty("CurrentTestStartedUnixMs").SetValue(job, now - 5_000);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 120_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-midrun-alive"] = job;
            _currentJobIdField.SetValue(null, "test-midrun-alive");

            var result = _getJobMethod.Invoke(null, new object[] { "test-midrun-alive" });

            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Running, status,
                "A recently-updated PlayMode job must not be auto-failed by the mid-run watchdog");
        }

        [Test]
        public void GetJob_EditModeMidRunStall_DoesNotAutoFail()
        {
            // Guard: EditMode jobs are deliberately outside the mid-run watchdog — there is no
            // play-mode signal to distinguish a dead runner from a legitimately slow test.
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-midrun-editmode");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "EditMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 300_000);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 90_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, 10);
            _testJobType.GetProperty("CompletedTests").SetValue(job, 4);
            _testJobType.GetProperty("CurrentTestFullName").SetValue(job, "Some.Slow.EditMode.Test");
            _testJobType.GetProperty("CurrentTestStartedUnixMs").SetValue(job, now - 90_000);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 120_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-midrun-editmode"] = job;
            _currentJobIdField.SetValue(null, "test-midrun-editmode");

            var result = _getJobMethod.Invoke(null, new object[] { "test-midrun-editmode" });

            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Running, status,
                "A stalled EditMode job must not be auto-failed by the PlayMode mid-run watchdog");
        }

        [Test]
        public void InitTimeoutMs_SurvivesPersistAndRestore()
        {
            // Arrange: insert a job with custom InitTimeoutMs
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-persist");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now);
            _testJobType.GetProperty("TotalTests").SetValue(job, null);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 90_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-persist"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-persist");

            // Act: persist then restore (simulates domain reload)
            _persistMethod.Invoke(null, new object[] { true });
            // Clear in-memory state
            jobs.Remove("test-init-timeout-persist");
            _currentJobIdField.SetValue(null, null);
            // Restore from SessionState
            _restoreMethod.Invoke(null, null);

            // Assert: restored job should have the same InitTimeoutMs
            var restoredJobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            Assert.IsTrue(restoredJobs.Contains("test-init-timeout-persist"),
                "Job should be restored from SessionState");

            var restoredJob = restoredJobs["test-init-timeout-persist"];
            var restoredTimeout = (long)_testJobType.GetProperty("InitTimeoutMs").GetValue(restoredJob);
            Assert.AreEqual(90_000L, restoredTimeout,
                "InitTimeoutMs should survive persist/restore cycle");
        }
    }
}
