using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Covers what a test-job poll actually puts on the wire. Two things used to be paid for on
    /// every call and read by nobody: live progress on a job that had already succeeded, and the
    /// full text of every skipped test — a suite with a standing [Explicit] block re-sends the
    /// same handful of sentences on each green run. A failed job is the exception in both: it
    /// carries no result payload, so progress is all the diagnosis there is.
    /// </summary>
    [TestFixture]
    public class TestJobPayloadTests
    {
        [Test]
        public void ASucceededJobCarriesNoProgressBlock()
        {
            JObject payload = Serialize(Job(TestJobStatus.Succeeded), includeSkipped: false);

            AssertJsonNull(payload["progress"], payload);
        }

        [Test]
        public void AFailedJobKeepsProgressBecauseNothingElseSaysWhatWentWrong()
        {
            TestJob job = Job(TestJobStatus.Failed);
            job.CurrentTestFullName = "N.C";
            job.FailuresSoFar = new List<TestJobFailure>
            {
                new TestJobFailure { FullName = "N.C", Message = "boom" },
            };

            JObject payload = Serialize(job, includeSkipped: false);

            AssertJsonNull(payload["result"], payload);
            Assert.AreEqual("N.C", payload["progress"].Value<string>("current_test_full_name"));
            Assert.AreEqual(1, payload["progress"]["failures_so_far"].Count());
        }

        [Test]
        public void ARunningJobStillCarriesProgress()
        {
            JObject payload = Serialize(Job(TestJobStatus.Running), includeSkipped: false);

            Assert.IsNotNull(payload["progress"]?["completed"], payload.ToString());
        }

        [Test]
        public void SkippedTestsAreCountedByReasonRatherThanListed()
        {
            JObject payload = Serialize(Job(TestJobStatus.Succeeded), includeSkipped: false);
            JToken reasons = payload["result"]["skipped_reasons"];

            AssertJsonNull(payload["result"]["results"], payload);
            Assert.AreEqual(1, reasons.Count());
            Assert.AreEqual(2, reasons[0].Value<int>("count"));
            Assert.AreEqual("needs a live server", reasons[0].Value<string>("reason"));
        }

        [Test]
        public void AskingForSkippedDetailStillReturnsTheTests()
        {
            JObject payload = Serialize(Job(TestJobStatus.Succeeded), includeSkipped: true);

            Assert.AreEqual(2, payload["result"]["results"].Count(), payload.ToString());
        }

        [Test]
        public void AskingForFailuresDoesNotDragSkippedTestsAlong()
        {
            // The reason the old include_failed_tests was renamed rather than kept: a skip is not
            // "Passed", so a green run with a standing [Explicit] block answered a request for
            // failures with the whole skipped list.
            JObject payload = Serialize(
                Job(TestJobStatus.Succeeded), includeSkipped: false, includeFailed: true);

            Assert.AreEqual(0, payload["result"]["results"].Count(), payload.ToString());
        }

        /// <summary>
        /// A JSON null is a JValue, not a missing token, and every Value&lt;T&gt; overload on one
        /// hands back something NUnit does not read as null. The token's type is the honest check.
        /// </summary>
        private static void AssertJsonNull(JToken token, JObject payload)
        {
            Assert.IsNotNull(token, "the field is absent rather than null");
            Assert.AreEqual(JTokenType.Null, token.Type, payload.ToString());
        }

        private static JObject Serialize(
            TestJob job, bool includeSkipped, bool includeFailed = false)
        {
            object payload = TestJobManager.ToSerializable(
                job, includeDetails: false, includeFailed: includeFailed, includeSkipped: includeSkipped);
            return JObject.FromObject(payload);
        }

        private static TestJob Job(TestJobStatus status)
        {
            var results = new List<TestRunTestResult>
            {
                // The states NUnit actually reports, qualified with reason and origin. A bare
                // "Skipped" never reaches a caller, and matching on one classified every real
                // skip as a failure.
                new TestRunTestResult("A", "N.A", "Passed", 0.1, null, null, null),
                new TestRunTestResult(
                    "B", "N.B", "Skipped:Explicit(Parent)", 0.0, "needs a live server", null, null),
                new TestRunTestResult(
                    "C", "N.C", "Skipped:Explicit", 0.0, "needs a live server", null, null),
            };

            return new TestJob
            {
                JobId = "j1",
                Status = status,
                Mode = "EditMode",
                CompletedTests = 3,
                TotalTests = 3,
                Result = new TestRunResult(
                    new TestRunSummary(3, 1, 0, 2, 0.1, "Passed"), results),
                Summary = new TestJobSummary
                {
                    Total = 3,
                    Passed = 1,
                    Failed = 0,
                    Skipped = 2,
                    DurationSeconds = 0.1,
                },
            };
        }
    }
}
