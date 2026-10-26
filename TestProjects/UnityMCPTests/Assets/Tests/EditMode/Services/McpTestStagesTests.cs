using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    // Keep test-stage enforcement coverage with the MCP implementation it exercises.
    public class McpTestStagesTests
    {
        private object _state;
        private Type _type;

        [SetUp]
        public void SetUp()
        {
            _type = McpType("TestStageState");
            _state = Activator.CreateInstance(_type, true);
            Reconcile("a", "NotRun");
        }

        [Test]
        public void FullSuiteNeedsFocusButDiagnosisIsAlwaysAllowed()
        {
            StringAssert.Contains("test_stage_required", Refusal(false, "EditMode"));
            Assert.IsNull(Refusal(true, "PlayMode"));
        }

        [Test]
        public void PassingFocusUnlocksBothModesForSameInputs()
        {
            Complete(true, "EditMode", "Passed", "a");
            Assert.IsNull(Refusal(false, "EditMode"));
            Assert.IsNull(Refusal(false, "PlayMode"));
        }

        [Test]
        public void FailedFocusDoesNotUnlockSuites()
        {
            Complete(true, "EditMode", "Passed", "a");
            Complete(true, "EditMode", "Failed", "a");
            StringAssert.Contains("test_stage_required", Refusal(false, "PlayMode"));
        }

        [Test]
        public void RunsThatExecutedNoTestLeaveTheStageAsItWas()
        {
            // A run refused or failed before dispatch (unsaved scenes, play mode, init timeout) says
            // nothing about the code under test, so it neither revokes a focused pass nor fails a suite.
            Complete(true, "EditMode", "Passed", "a");
            Complete(true, "EditMode", "NotRun", "a");
            Assert.IsNull(Refusal(false, "EditMode"));
            Complete(false, "PlayMode", "NotRun", "a");
            Assert.IsNull(Refusal(false, "EditMode"), "A suite that never ran must not block other suites");
        }

        [Test]
        public void ARunThatExecutedNoTestDoesNotRepairAFailedSuite()
        {
            Complete(true, "EditMode", "Passed", "a");
            Complete(false, "PlayMode", "Failed", "a");
            Complete(false, "PlayMode", "NotRun", "a");
            StringAssert.Contains("test_stage_failed", Refusal(false, "EditMode"));
        }

        [Test]
        public void ChangingInputsInvalidatesEvenAPassingInFlightRun()
        {
            Complete(true, "EditMode", "Passed", "b");
            StringAssert.Contains("test_stage_required", Refusal(false, "EditMode"));
        }

        [Test]
        public void FailedSuiteMustBeRepairedBeforeAnotherSuite()
        {
            Complete(true, "EditMode", "Passed", "a");
            Complete(false, "EditMode", "Failed", "a");
            Assert.IsNull(Refusal(true, "PlayMode"));
            Complete(true, "PlayMode", "Passed", "a");
            StringAssert.Contains("test_stage_failed", Refusal(false, "PlayMode"));
            Assert.IsNull(Refusal(false, "EditMode"));
            Complete(false, "EditMode", "Passed", "a");
            Assert.IsNull(Refusal(false, "PlayMode"));
        }

        [Test]
        public void InputChangesDoNotEraseAFailedSuite()
        {
            Complete(true, "EditMode", "Passed", "a");
            Complete(false, "EditMode", "Failed", "a");
            Reconcile("b", "NotRun");
            Complete(true, "PlayMode", "Passed", "b");
            StringAssert.Contains("test_stage_failed", Refusal(false, "PlayMode"));
        }

        [Test]
        public void OutcomeIsFailClosedForAMissingJobButNotRunForOneThatExecutedNothing()
        {
            Assert.AreEqual(TestStageOutcome.Failed, TestStageGuard.Outcome(null));
            Assert.AreEqual(TestStageOutcome.NotRun, TestStageGuard.Outcome(new TestJob
            {
                Status = TestJobStatus.Failed,
                Error = "Test job failed to initialize (tests did not start within timeout)",
                Summary = new TestJobSummary()
            }));
            Assert.AreEqual(TestStageOutcome.NotRun, TestStageGuard.Outcome(new TestJob { Status = TestJobStatus.Cancelled }));
            Assert.AreEqual(TestStageOutcome.Failed, TestStageGuard.Outcome(new TestJob
            {
                Status = TestJobStatus.Failed,
                CompletedTests = 3,
                Summary = new TestJobSummary { Total = 3, Passed = 2, Failed = 1 }
            }));
            Assert.AreEqual(TestStageOutcome.Failed, TestStageGuard.Outcome(new TestJob { Status = TestJobStatus.Running }),
                "A job that has not ended cannot be read as evidence either way");
            Assert.AreEqual(TestStageOutcome.Passed, TestStageGuard.Outcome(new TestJob
            {
                Status = TestJobStatus.Succeeded,
                CompletedTests = 2,
                Summary = new TestJobSummary { Total = 2, Passed = 2 }
            }));
        }

        [Test]
        public void AMissingInputIsReportedByName()
        {
            string directory = Path.Combine(Path.GetTempPath(), "cf-stage-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => TestStageGuard.Fingerprint(directory, new[] { "Assets/Gone" }));
                StringAssert.StartsWith("test_stage_input_missing: 'Assets/Gone'", ex.Message);
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void FingerprintTracksContentNamesAndDeletionButIgnoresBuildOutputs()
        {
            string directory = Path.Combine(Path.GetTempPath(), "cf-stage-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string file = Path.Combine(directory, "input.cs");
                File.WriteAllText(file, "one");
                string first = Fingerprint(directory);
                File.WriteAllText(file, "two");
                string second = Fingerprint(directory);
                Assert.AreNotEqual(first, second);
                File.Move(file, Path.Combine(directory, "renamed.cs"));
                string renamed = Fingerprint(directory);
                Assert.AreNotEqual(second, renamed);
                Directory.CreateDirectory(Path.Combine(directory, "obj"));
                File.WriteAllText(Path.Combine(directory, "obj", "output.cs"), "generated");
                Assert.AreEqual(renamed, Fingerprint(directory));
                File.Delete(Path.Combine(directory, "renamed.cs"));
                Assert.AreNotEqual(renamed, Fingerprint(directory));
            }
            finally { Directory.Delete(directory, true); }
        }

        private void Complete(bool focused, string mode, string outcome, string fingerprint)
        {
            _type.GetField("PendingJob").SetValue(_state, "job");
            _type.GetField("PendingFocused").SetValue(_state, focused);
            _type.GetField("PendingMode").SetValue(_state, mode);
            Reconcile(fingerprint, outcome);
        }

        private void Reconcile(string fingerprint, string outcome) =>
            _type.GetMethod("Reconcile").Invoke(_state, new object[]
            {
                fingerprint, Enum.Parse(McpType("TestStageOutcome"), outcome)
            });

        private string Refusal(bool focused, string mode) =>
            (string)_type.GetMethod("Refusal").Invoke(_state, new object[] { focused, mode });

        private static Type McpType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Single(a => a.GetName().Name == "MCPForUnity.Editor")
            .GetType("MCPForUnity.Editor.Services." + name, true);

        private static string Fingerprint(string path) => (string)McpType("TestStageGuard")
            .GetMethod("Fingerprint", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { path, new[] { "." } });
    }
}
