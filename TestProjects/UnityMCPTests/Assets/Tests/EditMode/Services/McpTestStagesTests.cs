using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
            Reconcile("a", null);
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
            Complete(true, "EditMode", true, "a");
            Assert.IsNull(Refusal(false, "EditMode"));
            Assert.IsNull(Refusal(false, "PlayMode"));
        }

        [TestCase(false)]
        [TestCase(null)]
        public void FailedOrMissingFocusDoesNotUnlockSuites(bool? passed)
        {
            Complete(true, "EditMode", true, "a");
            Complete(true, "EditMode", passed, "a");
            StringAssert.Contains("test_stage_required", Refusal(false, "PlayMode"));
        }

        [Test]
        public void ChangingInputsInvalidatesEvenAPassingInFlightRun()
        {
            Complete(true, "EditMode", true, "b");
            StringAssert.Contains("test_stage_required", Refusal(false, "EditMode"));
        }

        [Test]
        public void FailedSuiteMustBeRepairedBeforeAnotherSuite()
        {
            Complete(true, "EditMode", true, "a");
            Complete(false, "EditMode", false, "a");
            Assert.IsNull(Refusal(true, "PlayMode"));
            Complete(true, "PlayMode", true, "a");
            StringAssert.Contains("test_stage_failed", Refusal(false, "PlayMode"));
            Assert.IsNull(Refusal(false, "EditMode"));
            Complete(false, "EditMode", true, "a");
            Assert.IsNull(Refusal(false, "PlayMode"));
        }

        [Test]
        public void InputChangesDoNotEraseAFailedSuite()
        {
            Complete(true, "EditMode", true, "a");
            Complete(false, "EditMode", false, "a");
            Reconcile("b", null);
            Complete(true, "PlayMode", true, "b");
            StringAssert.Contains("test_stage_failed", Refusal(false, "PlayMode"));
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

        private void Complete(bool focused, string mode, bool? passed, string fingerprint)
        {
            _type.GetField("PendingJob").SetValue(_state, "job");
            _type.GetField("PendingFocused").SetValue(_state, focused);
            _type.GetField("PendingMode").SetValue(_state, mode);
            Reconcile(fingerprint, passed);
        }

        private void Reconcile(string fingerprint, bool? passed) =>
            _type.GetMethod("Reconcile").Invoke(_state, new object[] { fingerprint, passed });

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
